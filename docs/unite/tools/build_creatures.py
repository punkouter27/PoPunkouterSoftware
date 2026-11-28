"""Build PoPunkouterSoftware/docs/unite/creatures from the repos' existing MJCFs.

    python tools/build_creatures.py            # build every creature + INDEX.md
    python tools/build_creatures.py go2 r1     # build a subset
    python tools/build_creatures.py --check    # exit 1 if anything on disk is stale

Each creature is written as a PORTABLE PREFAB:

    creatures/<id>/creature.xml   flat, self-contained, no <option>, no floor, no light,
                                  <compiler angle="radian">, meshes referenced as "meshes/x.obj"
                                  with NO meshdir (meshdir is dropped by MuJoCo's <include>,
                                  verified on 4.0.0; so is every other compiler attribute)
    creatures/<id>/scene.xml      <include creature.xml> + the training <option> + floor + light
    creatures/<id>/role_map.json  joint -> canonical role, the human-editable input
    creatures/<id>/contract.json  emitted by pocontract
    creatures/<id>/meshes/        vendored mesh files, basenames only

The split is verified by tools/check_creatures.py against the original source model.
"""

import argparse
import json
import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402

ROOT = L.UNITE.parents[2]  # _CODEMATT (UNITE = PoPunkouterSoftware/docs/unite)
TRAIN = ROOT / "PoRaceUnreal" / "training"
MJCF = ROOT / "PoRobotTrain" / "UnrealProject" / "MJCF"
BB = ROOT / "PoBasketball" / "sim" / "assets"

# id -> source, kind, meshdir override, root body, hardware flag
REGISTRY = {
    "unitree_r1_a5": dict(
        source=MJCF / "r1_twin.xml", kind="humanoid", root_body="pelvis", hardware=True,
        hardware_yaml=ROOT / "PoRobotTrain" / "RobotControl" / "configs" / "r1_joint_map.yaml",
        label="PoRobotTrain/UnrealProject/MJCF/r1_twin.xml",
        legacy=[
            {"name": "r1_level0_upperbody", "n_obs": 23, "n_action": 7,
             "source": "PoRobotTrain/RobotControl/models/r1_level0_upperbody/deploy.yaml", "status": "live"},
            {"name": "r1_head_skill", "n_obs": 9, "n_action": 2,
             "source": "PoRobotTrain/RobotControl/models/head_skills/look_at/deploy.json", "status": "live"},
            {"name": "r1_head_skill_single", "n_obs": 6, "n_action": 1,
             "source": "PoRobotTrain/RobotControl/models/head_skills/nod_yes/deploy.json", "status": "live"},
            {"name": "r1_level2_velocity", "n_obs": 83, "n_action": 24,
             "source": "PoRobotTrain/docs/level2_walk.md (mjlab sandbox, MuJoCo 3.5.0)", "status": "bridge"},
        ],
        notes="Hardware-backed: this contract is the one the physical Unitree R1 A5 is driven with. "
              "Blueprints and the deploy gate live in PoRobotTrain; this folder is the portable twin. "
              "Any brain exported here must pass pocontract verify + the PoRobotTrain gate before it is copied on. "
              "Earth and timing verified: gravity [0,0,-9.81], SI metres/radians, 5 ms implicitfast, 50 Hz policy. "
              "KNOWN GAPS vs the physical A5 (from PoRobotTrain/docs/level2_walk.md and "
              "configs/sim_calibration.yaml, tracked in this folder's README): twin mass 30.15 kg vs real "
              "28.93 kg (1.2 kg extra above the pelvis); contacts condim 6 / friction 1.0 vs training condim 3 / "
              "0.6 randomised 0.3-1.6; sim_calibration last_calibrated_at is null and parity_rms_deg is null, "
              "so the actuator chain has never been identified against the robot; the twin applies no domain "
              "randomisation. Do not treat this contract alone as proof of sim-to-real fidelity.",
    ),
    "go2": dict(
        source=TRAIN / "assets" / "go2" / "go2_porace.xml",
        scene_source=TRAIN / "assets" / "go2" / "scene_porace.xml",
        kind="quadruped", root_body="base",
        label="PoRaceUnreal/training/assets/go2/go2_porace.xml",
        notes="Unitree Go2 quadruped. Legs map positionally into the humanoid limb block "
              "(hip->hip_pitch, thigh->hip_roll, calf->hip_yaw) because a quadruped has no humanoid roles; "
              "the mask and morphology one-hot tell a shared brain what it is driving.",
    ),
    "matthew": dict(
        source=TRAIN / "matt" / "assets" / "matt_porace.xml", kind="humanoid", root_body="pelvis",
        label="PoRaceUnreal/training/matt/assets/matt_porace.xml",
        notes="The Matt human racer (G1 joint layout). Source is a full scene; the floor, light, option and "
              "keyframe are split out into scene.xml.",
    ),
    "grandma": dict(
        source=TRAIN / "grandma" / "assets" / "grandma_porace.xml", kind="humanoid", root_body="pelvis",
        label="PoRaceUnreal/training/grandma/assets/grandma_porace.xml",
    ),
    "g1": dict(
        source=TRAIN / "g1" / "assets" / "g1_porace.xml", kind="humanoid", root_body="pelvis",
        label="PoRaceUnreal/training/g1/assets/g1_porace.xml",
        notes="KNOWN DEFECT: only 2 of 13 geoms collide (left_foot, right_foot, both thin meshes), so "
              "the creature sinks through the floor within ~6 s of falling (pelvis z +0.43 -> -0.74). "
              "It is usable for kinematic/render work but not for physics training until collision "
              "geoms are added to the body links.",
    ),
    "streetball_humanoid": dict(
        source=BB / "humanoid.xml", scene_source=BB / "scene.xml",
        kind="humanoid", root_body="pelvis", angle_from="degree",
        label="PoBasketball/sim/assets/humanoid.xml",
        notes="The basketball mannequin. Source uses compiler angle=degree; every angle is converted to "
              "radians here so one unit system holds across the family (a host's <compiler> wins over an "
              "included file's, so mixed units would silently corrupt the model).",
    ),
}

# Entities that are scene furniture, not the creature.
FURNITURE_DROP = {"option", "statistic", "visual", "size", "keyframe"}

ANGLE_ATTRS = ("range", "ref", "springref")


def localname(tag):
    return tag.split("}")[-1]


def convert_degrees_to_radians(root):
    import math

    for el in root.iter():
        if localname(el.tag) != "joint":
            continue
        for attr in ANGLE_ATTRS:
            if attr in el.attrib:
                vals = [float(v) * math.pi / 180.0 for v in el.attrib[attr].split()]
                el.set(attr, " ".join(f"{v:.10g}" for v in vals))


def collect_meshes(root, src_dir, meshdir):
    """Every <mesh file=...> resolved against the source, returned as [(element, abs_path)]."""
    out = []
    for el in root.iter():
        if localname(el.tag) != "mesh":
            continue
        f = el.get("file")
        if not f:
            continue
        cand = (src_dir / meshdir / f) if meshdir else (src_dir / f)
        if not cand.exists():
            cand = src_dir / f
        if not cand.exists():
            raise SystemExit(f"mesh {f} not found next to {src_dir}")
        out.append((el, cand, f))
    return out


def build_one(cid, spec, out_root):
    import mujoco

    src = Path(spec["source"])
    src_dir = src.parent
    tree = ET.parse(src)
    root = tree.getroot()

    if spec.get("angle_from") == "degree":
        convert_degrees_to_radians(root)

    # ---- assets: vendored meshes, no meshdir
    comp = root.find("compiler")
    meshdir = ""
    if comp is not None:
        meshdir = comp.get("meshdir") or ""
        for attr in ("meshdir", "assetdir", "texturedir"):
            comp.attrib.pop(attr, None)
        comp.set("angle", "radian")
        comp.set("autolimits", "true")
    else:
        comp = ET.Element("compiler", {"angle": "radian", "autolimits": "true"})
        root.insert(0, comp)

    cdir = out_root / cid
    mesh_out = cdir / "meshes"
    meshes = collect_meshes(root, src_dir, meshdir)
    if meshes:
        mesh_out.mkdir(parents=True, exist_ok=True)
    for el, path, _f in meshes:
        shutil.copy2(path, mesh_out / path.name)
        el.set("file", f"meshes/{path.name}")

    # ---- drop scene furniture
    for child in list(root):
        if localname(child.tag) in FURNITURE_DROP:
            root.remove(child)

    # ---- keep only the creature's root body in worldbody
    wb = root.find("worldbody")
    if wb is None:
        raise SystemExit(f"{cid}: no worldbody")
    root_body = spec.get("root_body")
    keep = None
    for child in list(wb):
        tag = localname(child.tag)
        if tag == "body" and child.get("name") == root_body:
            keep = child
            continue
        wb.remove(child)
    if keep is None:
        raise SystemExit(f"{cid}: root body {root_body!r} not in worldbody")

    # ---- strip ground-only assets that the floor no longer uses
    asset = root.find("asset")
    if asset is not None:
        for child in list(asset):
            if localname(child.tag) in ("texture", "material"):
                nm = (child.get("name") or "").lower()
                if "ground" in nm or "floor" in nm or child.get("type") == "skybox":
                    asset.remove(child)

    # ---- anything that referenced a dropped name (floor, stand, pooled props) is scene-level:
    #      move it to scene.xml instead of leaving a dangling reference in the creature.
    present = {e.get("name") for e in root.iter() if e.get("name")}
    scene_extra = []
    for section in ("sensor", "contact", "equality"):
        sec = root.find(section)
        if sec is None:
            continue
        for child in list(sec):
            vals = {v for v in child.attrib.values()}
            if vals - present:
                scene_extra.append(ET.tostring(child, encoding="unicode").strip())
                sec.remove(child)
        if len(sec) == 0:
            root.remove(sec)

    # ---- creature.xml
    cdir.mkdir(parents=True, exist_ok=True)
    ET.indent(tree, space="  ")
    creature_xml = cdir / "creature.xml"
    tree.write(creature_xml, encoding="utf-8", xml_declaration=True)
    _prepend_note(creature_xml, cid, "portable prefab")

    # ---- scene.xml (keeps the training option + keyframe from the source)
    _m_probe = mujoco.MjModel.from_xml_path(str(creature_xml))
    scene = _scene_xml(spec.get("scene_source", src), cid, scene_extra, _m_probe.nq, _m_probe.nu, creature_xml)
    (cdir / "scene.xml").write_text(scene, encoding="utf-8")

    # ---- role map: explicit overrides plus the auto map, so the file shows every decision
    m = mujoco.MjModel.from_xml_path(str(creature_xml))
    role_map = {}
    for j in range(m.njnt):
        if int(m.jnt_type[j]) == int(mujoco.mjtJoint.mjJNT_FREE):
            continue
        jn = mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_JOINT, j)
        if jn:
            role_map[jn] = L.auto_role(jn, spec["kind"])
    for jn, role in spec.get("role_overrides", {}).items():
        if jn in role_map:
            role_map[jn] = role
    role_map = L.dedupe_roles(role_map)
    L.write_json(cdir / "role_map.json", {
        "_comment": "joint -> canonical role. null means held outside the brain (PD-held at default)."
                    " Edit this file, then rerun tools/build_creatures.py.",
        "kind": spec["kind"],
        "roles": role_map,
    })

    # ---- contract
    hardware = None
    if spec.get("hardware"):
        hw_path = cdir / "hardware.json"
        if spec.get("hardware_yaml"):
            L.write_json(hw_path, _r1_hardware(spec["hardware_yaml"]))
        hardware = json.loads(hw_path.read_text(encoding="utf-8"))

    contract = L.build_contract(
        cid=cid,
        # Compile the SCENE, not the stripped creature: <option> lives in the scene (it is scene
        # furniture), so compiling the creature alone reports MuJoCo's defaults (2 ms, Euler,
        # pyramidal cone) instead of the training solver. model_numbers() filters to the creature
        # subtree by root_body, so the floor and lights contribute nothing but the correct option.
        xml_path=cdir / "scene.xml",
        role_map=role_map,
        kind=spec["kind"],
        mujoco_version=mujoco.__version__,
        root_body=root_body,
        hardware=hardware,
        notes=spec.get("notes", ""),
        source_label=spec.get("label", str(src)),
        morphology_id=sorted(REGISTRY).index(cid),
        legacy=spec.get("legacy"),
    )
    if hardware:
        _apply_motor_slots(contract, hardware)
    L.write_json(cdir / "contract.json", contract)
    return contract


def _r1_hardware(yaml_path):
    """The physical bus contract, read from PoRobotTrain's joint map (the hardware source of truth)."""
    import yaml

    spec = yaml.safe_load(Path(yaml_path).read_text(encoding="utf-8"))
    slots, joints = {}, {}
    for row in spec["joints"]:
        slots[row["name"]] = int(row["motor_slot"])
        joints[row["name"]] = {
            "default": float(row["default"]),
            "kp": float(row["kp"]),
            "kd": float(row["kd"]),
            "effort": float(row["effort"]),
            "action_scale": float(row["action_scale"]),
            "limits": [float(x) for x in row["limits"]],
            "motor_slot": int(row["motor_slot"]),
            "policy_index": int(row["policy_index"]),
        }
    return {
        "_comment": "Physical bus contract for the Unitree R1 A5. Generated from "
                    "PoRobotTrain/RobotControl/configs/r1_joint_map.yaml, which stays the source of truth.",
        "robot": spec.get("robot"),
        "kind": "hardware",
        "owner_app": "PoRobotTrain",
        "num_joints": int(spec["num_joints"]),
        "bus": {
            "msg_type": spec.get("msg_type"),
            "lowcmd_topic": spec.get("lowcmd_topic"),
            "lowstate_topic": spec.get("lowstate_topic"),
            "num_slots": int(spec["lowcmd_num_slots"]),
            "unused_slots": sorted(set(range(int(spec["lowcmd_num_slots"]))) - set(slots.values())),
            "imu_body": spec.get("imu_body"),
        },
        "deploy_gate": {
            "required": True,
            "app": "PoRobotTrain",
            "sidecar": "RobotControl/models/<skill>/deploy.json",
            "checks": ["policy_sha256 matches the ONNX", "validation.passed is true",
                       "joint_map_sha256 matches this creature's role map"],
        },
        "policy_rate_hz": round(1.0 / 0.02, 3),
        "motor_slot": slots,
        "joints": joints,
    }


def _apply_motor_slots(contract, hardware):
    """Attach the physical bus index to each joint so a sim and the robot share one action vector."""
    slots = hardware.get("motor_slot", {})
    for j in contract["morphology"]["joints"]:
        if j["name"] in slots:
            j["motor_slot"] = int(slots[j["name"]])
    # defaults + gains come from the hardware joint map, which is the source of truth on hardware
    for j in contract["morphology"]["joints"]:
        hw = hardware.get("joints", {}).get(j["name"])
        if not hw:
            continue
        j["default"] = float(hw.get("default", j.get("default") or 0.0))
        for k in ("kp", "kd", "effort", "action_scale"):
            if hw.get(k) is not None:
                j[k] = float(hw[k])
    contract["hashes"]["morphology_sha256"] = L.sha(contract["morphology"])


def _prepend_note(path, cid, what):
    text = path.read_text(encoding="utf-8")
    note = (
        f"<!-- {what} for {cid}. GENERATED by tools/build_creatures.py from the source recorded in\n"
        f"     contract.json -> generated.source. Do not edit: rerun the builder.\n"
        f"     Portable: no meshdir, no floor, no light, no option, compiler angle=radian.\n"
        f"     Mount from a host scene with:  <compiler angle=\"radian\" autolimits=\"true\"/>  then\n"
        f"     <include file=\".../creatures/{cid}/creature.xml\"/> and provide <geom name=\"floor\"/>. -->\n"
    )
    if text.startswith("<?xml"):
        head, _, rest = text.partition("\n")
        text = head + "\n" + note + rest
    else:
        text = note + text
    path.write_text(text, encoding="utf-8")


def _home_keyframe(scene_src, creature_xml):
    """Build a creature-only `home` keyframe from the source scene's first keyframe.

    A source scene's keyframe qpos may interleave props with the creature (the Streetball scene's
    key holds the ball's free joint first, so a positional slice shifts every joint). This maps by
    *name*: each creature joint is looked up in the source model, its qpos is copied, and the result is
    assembled in the creature's own qpos order. Returns None when the source has no keyframe.
    """
    import mujoco

    ms = mujoco.MjModel.from_xml_path(str(scene_src))
    if ms.nkey == 0:
        return None
    mc = mujoco.MjModel.from_xml_path(str(creature_xml))
    src_key = ms.key_qpos[0]
    T = mujoco.mjtObj

    out = np.zeros(mc.nq, dtype=np.float64)
    for j in range(mc.njnt):
        name = mujoco.mj_id2name(mc, T.mjOBJ_JOINT, j)
        qadr = int(mc.jnt_qposadr[j])
        n = {int(mujoco.mjtJoint.mjJNT_FREE): 7, int(mujoco.mjtJoint.mjJNT_BALL): 4}.get(int(mc.jnt_type[j]), 1)
        if int(mc.jnt_type[j]) == int(mujoco.mjtJoint.mjJNT_FREE):
            body = mujoco.mj_id2name(mc, T.mjOBJ_BODY, int(mc.jnt_bodyid[j]))
            js = next((k for k in range(ms.njnt)
                       if int(ms.jnt_type[k]) == int(mujoco.mjtJoint.mjJNT_FREE)
                       and mujoco.mj_id2name(ms, T.mjOBJ_BODY, int(ms.jnt_bodyid[k])) == body), None)
            if js is None:
                return None
            out[qadr:qadr + 7] = src_key[int(ms.jnt_qposadr[js]):int(ms.jnt_qposadr[js]) + 7]
            continue
        if not name:
            continue
        js = mujoco.mj_name2id(ms, T.mjOBJ_JOINT, name)
        if js < 0:
            return None
        out[qadr:qadr + n] = src_key[int(ms.jnt_qposadr[js]):int(ms.jnt_qposadr[js]) + n]
    return out


def _scene_xml(src, cid, scene_extra=(), nq=None, nu=None, creature_xml=None):
    """The source's own <option> and <size>, a creature-only home keyframe, a floor and a light."""
    tree = ET.parse(src)
    root = tree.getroot()
    parts = []
    for el in root:
        tag = localname(el.tag)
        if tag in ("option", "size"):
            parts.append(ET.tostring(el, encoding="unicode"))

    home = _home_keyframe(src, creature_xml) if creature_xml is not None else None
    if home is not None and nq is not None and len(home) == nq:
        qpos = " ".join(f"{v:.8g}" for v in home)
        parts.append(f'  <keyframe>\n    <key name="home" qpos="{qpos}"/>\n  </keyframe>')
    body = "\n".join(parts)
    extra = "\n".join(scene_extra)
    extra_block = ""
    if extra:
        extra_block = (
            "\n  <!-- scene-level sensors / contacts moved out of creature.xml because they reference\n"
            "       the floor or scene props that a portable creature does not own. -->\n  <sensor>\n    "
            + extra.replace("\n", "\n    ")
            + "\n  </sensor>\n"
        )
    return f"""<mujoco model="{cid}_scene">
  <!-- standalone scene for {cid}. GENERATED by tools/build_creatures.py.
       The option, keyframe and size blocks are the training scene's, copied verbatim. -->
  <compiler angle="radian" autolimits="true"/>
  <include file="creature.xml"/>
{body}{extra_block}
  <asset>
    <texture type="2d" name="groundplane" builtin="checker" mark="edge" rgb1="0.25 0.27 0.3" rgb2="0.15 0.16 0.18"
      markrgb="1 1 1" width="300" height="300"/>
    <material name="groundplane" texture="groundplane" texuniform="true" texrepeat="5 5" reflectance="0"/>
  </asset>
  <worldbody>
    <light name="sun" pos="0 0 3" dir="0 0 -1" directional="true" diffuse="0.8 0.8 0.8"/>
    <geom name="floor" type="plane" size="0 0 0.05" material="groundplane" priority="1"
      friction="0.6" condim="3" contype="1" conaffinity="0"/>
  </worldbody>
</mujoco>
"""


def write_index(contracts):
    lines = [
        "# Creature index",
        "",
        "Generated by `tools/build_creatures.py`. Drop-in prefabs for every Po MuJoCo simulation.",
        "",
        "| id | kind | joints | roles mapped | n_obs | n_action | hardware | source |",
        "|---|---|---|---|---|---|---|---|",
    ]
    for cid in sorted(contracts):
        c = contracts[cid]
        mapped = sum(c["roles"]["mask"])
        hw = "**yes**" if "hardware" in c else "no"
        src = c["generated"]["source"]
        lines.append(
            f"| [{cid}](./{cid}/creature.xml) | {c['kind']} | {len(c['morphology']['joints'])} | "
            f"{mapped}/{L.N_ROLES} | {c['observation']['n_obs']} | {c['action']['n_action']} | {hw} | `{src}` |"
        )
    lines += [
        "",
        "## Mounting a creature in any MuJoCo scene",
        "",
        "```xml",
        '<mujoco model="my game">',
        '  <compiler angle="radian" autolimits="true"/>   <!-- required: it wins over the included file -->',
        '  <include file="../../PoPunkouterSoftware/docs/unite/creatures/go2/creature.xml"/>',
        "  <worldbody>",
        '    <geom name="floor" type="plane" size="0 0 0.05"/>   <!-- required: creatures assume a "floor" -->',
        "  </worldbody>",
        "</mujoco>",
        "```",
        "",
        "Rules that make a creature portable (all verified by `tools/check_creatures.py`):",
        "",
        "- The host sets `compiler angle=\"radian\" autolimits=\"true\"`. An included file's `<compiler>` is",
        "  **ignored**, so a creature authored in degrees would silently corrupt inside a radian host.",
        "- The host provides `<geom name=\"floor\">`; creatures strip their own floor, lights and textures.",
        "- MuJoCo's `<include>` resolves mesh paths against the *including* file's directory and drops",
        "  `meshdir`, which is why every mesh is referenced as `meshes/<basename>` with no meshdir.",
        "- Unity cannot read `<include>` at all; give it `creature.xml` directly (it is already flat).",
        "- URLab drops the included `<option>` block; reapply `contract.json` -> `physics.option` at bind time.",
        "",
        "## The universal agent interface",
        "",
        "Every creature here answers the same 131-float observation and takes the same 28-float action",
        "(`contract.observation` / `contract.action`). `tools/role_obs.py` is the reference builder and",
        "writes `golden_role_obs.npz` beside each contract: 100 random states with the inputs and the",
        "expected vector. `tools/role_env.py` steps any creature through that interface.",
        "",
        "Each runtime reimplements the builder and proves itself against the golden — the C++ twin, the",
        "web twin, a Unity plugin. Two independent implementations agreeing to 1e-5 is evidence; one",
        "library called from two places is not.",
        "",
        "```powershell",
        "python tools/role_obs.py --verify     # every creature reproduces its golden",
        "python tools/role_env.py --smoke      # step all six through one interface",
        "```",
    ]
    (L.CREATURES / "INDEX.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("only", nargs="*", help="creature ids (default: all)")
    ap.add_argument("--check", action="store_true")
    a = ap.parse_args()

    ids = a.only or sorted(REGISTRY)
    contracts = {}
    stale = False
    for cid in ids:
        if cid not in REGISTRY:
            raise SystemExit(f"unknown creature {cid}; known: {sorted(REGISTRY)}")
        before = {}
        for f in ("creature.xml", "scene.xml", "role_map.json", "contract.json"):
            p = L.CREATURES / cid / f
            before[f] = p.read_text(encoding="utf-8") if p.exists() else None
        c = build_one(cid, REGISTRY[cid], L.CREATURES)
        contracts[cid] = c
        for f in before:
            p = L.CREATURES / cid / f
            after = p.read_text(encoding="utf-8")
            if before[f] is not None and _same(f, before[f], after):
                continue
            stale = True
            if not a.check:
                print(f"  wrote  {cid}/{f}")
        if not stale or not a.check:
            print(f"{cid:20s} joints={len(c['morphology']['joints']):2d} "
                  f"roles={sum(c['roles']['mask']):2d}/{L.N_ROLES} "
                  f"n_obs={c['observation']['n_obs']} n_action={c['action']['n_action']}")

    if a.check and stale:
        print("STALE")
        return 1
    if not a.check:
        # index covers every creature on disk, not just the ones rebuilt
        allc = {p.parent.name: json.loads(p.read_text(encoding="utf-8"))
                for p in sorted(L.CREATURES.glob("*/contract.json"))}
        write_index(allc)
        print(f"wrote creatures/INDEX.md ({len(allc)} creatures)")
    print("OK")
    return 0


def _same(fname, old, new):
    if fname == "contract.json":
        return L.strip_generated(json.loads(old)) == L.strip_generated(json.loads(new))
    return old == new


if __name__ == "__main__":
    sys.exit(main())
