"""Prove that every creature in global/UNITE/creatures is actually portable.

    python tools/check_creatures.py

For each creature:
  1. creature.xml compiles on its own.
  2. It compiles when included by a host scene living in a DIFFERENT directory, using only the
     documented mount snippet. This is the property the library exists for.
  3. The joint table (name, axis, range) from (1) and (2) is identical. Catches the two silent
     corruptions MuJoCo's <include> allows: a dropped <compiler> (units) and a dropped meshdir (assets).
  4. Split fidelity: the creature's joints, axes, ranges and body masses match the ORIGINAL source
     model, so the scene/creature split changed no physics.
  5. The contract agrees with the model: mask matches the mapped roles, slot offsets are contiguous,
     n_obs sums correctly, hashes are well formed.
"""

import json
import sys
import tempfile
from pathlib import Path

import mujoco

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402
from build_creatures import REGISTRY  # noqa: E402

MOUNT = """<mujoco model="host">
  <compiler angle="radian" autolimits="true"/>
  <include file="{rel}"/>
  <worldbody>
    <geom name="floor" type="plane" size="0 0 0.05"/>
  </worldbody>
</mujoco>
"""


def joint_table(m, root_body):
    """name -> (axis, range) for hinge/slide joints in the creature subtree."""
    T = mujoco.mjtObj
    root = mujoco.mj_name2id(m, T.mjOBJ_BODY, root_body)
    out = {}
    for j in range(m.njnt):
        name = mujoco.mj_id2name(m, T.mjOBJ_JOINT, j)
        if not name:
            continue
        if int(m.body_rootid[int(m.jnt_bodyid[j])]) != root:
            continue
        out[name] = (
            tuple(round(float(x), 9) for x in m.jnt_axis[j]),
            tuple(round(float(x), 9) for x in m.jnt_range[j]),
        )
    return out


def mass_table(m, root_body):
    T = mujoco.mjtObj
    root = mujoco.mj_name2id(m, T.mjOBJ_BODY, root_body)
    return {
        mujoco.mj_id2name(m, T.mjOBJ_BODY, b): round(float(m.body_mass[b]), 6)
        for b in range(1, m.nbody)
        if int(m.body_rootid[b]) == root or b == root
    }


def check(cid, spec):
    errs = []
    cdir = L.CREATURES / cid
    creature = cdir / "creature.xml"
    contract = json.loads((cdir / "contract.json").read_text(encoding="utf-8"))
    root_body = contract["morphology"]["root_body"]

    standalone = mujoco.MjModel.from_xml_path(str(creature))
    scene = mujoco.MjModel.from_xml_path(str(cdir / "scene.xml"))
    if scene.nu != standalone.nu:
        errs.append(f"scene has {scene.nu} actuators, creature has {standalone.nu}")

    # 2 + 3: foreign-directory include
    with tempfile.TemporaryDirectory() as tmp:
        host = Path(tmp) / "host.xml"
        rel = Path(creature).as_posix()
        host.write_text(MOUNT.format(rel=rel), encoding="utf-8")
        try:
            foreign = mujoco.MjModel.from_xml_path(str(host))
        except Exception as e:
            errs.append(f"foreign include FAILED: {e}")
            foreign = None
        if foreign is not None:
            a = joint_table(standalone, root_body)
            b = joint_table(foreign, root_body)
            if a != b:
                diff = sorted(set(a) ^ set(b)) or [k for k in a if a[k] != b.get(k)]
                errs.append(f"joint table differs when included from another directory: {diff[:6]}")
            if foreign.nmesh != standalone.nmesh:
                errs.append(f"meshes lost on include: {standalone.nmesh} -> {foreign.nmesh}")

    # 4: split fidelity against the original source
    src = Path(spec["source"])
    if src.exists():
        try:
            orig = mujoco.MjModel.from_xml_path(str(src))
            a = joint_table(standalone, root_body)
            b = joint_table(orig, root_body)
            missing = set(b) - set(a)
            if missing:
                errs.append(f"joints lost in the split: {sorted(missing)[:6]}")
            changed = [k for k in a if k in b and a[k] != b[k]]
            if changed:
                errs.append(f"joint axis/range changed in the split: {changed[:4]}")
            ma, mb = mass_table(standalone, root_body), mass_table(orig, root_body)
            mchanged = [k for k in ma if k in mb and abs(ma[k] - mb[k]) > 1e-6]
            if mchanged:
                errs.append(f"body mass changed in the split: {mchanged[:4]}")
        except Exception as e:
            errs.append(f"could not compile the source for comparison: {e}")

    # 5: contract invariants
    roles, mask = contract["roles"]["canonical_order"], contract["roles"]["mask"]
    if len(mask) != len(roles):
        errs.append("mask length != role count")
    mapped = {j["role"] for j in contract["morphology"]["joints"] if j.get("role")}
    if mapped - set(roles):
        errs.append(f"non-canonical roles: {sorted(mapped - set(roles))}")
    if mask != [1 if r in mapped else 0 for r in roles]:
        errs.append("mask disagrees with the roles present")
    at = 0
    for s in contract["observation"]["slots"]:
        if s["start"] != at:
            errs.append(f"slot {s['name']} starts at {s['start']}, expected {at}")
        at += s["size"]
    if at != contract["observation"]["n_obs"]:
        errs.append(f"slots sum to {at}, n_obs is {contract['observation']['n_obs']}")
    if contract["action"]["n_action"] != len(roles):
        errs.append("n_action != role count")
    for k in ("morphology_sha256", "interface_sha256", "physics_sha256"):
        if len(contract["hashes"][k]) != 64:
            errs.append(f"{k} malformed")

    # Earth and timing: the two things an ONNX trained for a real robot depends on most.
    g = contract["physics"]["option"]["gravity"]
    if [round(x, 6) for x in g] != [0.0, 0.0, -9.81]:
        errs.append(f"gravity is {g}, not Earth's [0, 0, -9.81]")
    ph = contract["physics"]
    if abs(ph["timestep"] * ph["decimation"] - ph["control_dt"]) > 1e-9:
        errs.append(f"timestep*decimation ({ph['timestep'] * ph['decimation']}) != control_dt ({ph['control_dt']})")
    rate = 1.0 / ph["control_dt"]
    if abs(rate - round(rate)) > 1e-6:
        errs.append(f"control rate {rate} Hz is not an integer")
    if ph["timestep"] > 0.01:
        errs.append(f"timestep {ph['timestep']} s is coarser than 100 Hz, unstable for contact")

    if "hardware" in contract:
        slots = contract["hardware"]["motor_slot"]
        for j in contract["morphology"]["joints"]:
            if j["name"] not in slots:
                errs.append(f"hardware creature: joint {j['name']} has no motor slot")

    return errs, standalone, mapped


def main():
    failed = 0
    for cid in sorted(REGISTRY):
        spec = REGISTRY[cid]
        errs, m, mapped = check(cid, spec)
        tag = "OK  " if not errs else "FAIL"
        print(f"{tag} {cid:20s} joints={m.njnt:2d} nu={m.nu:2d} mesh={m.nmesh:3d} roles={len(mapped):2d}/{L.N_ROLES}")
        for e in errs:
            print(f"       {e}")
        failed += bool(errs)
    print("FAILED" if failed else "OK")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
