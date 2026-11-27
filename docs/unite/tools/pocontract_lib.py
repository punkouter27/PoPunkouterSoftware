"""Shared UNITE contract logic. Used by emit_contract.py, build_creatures.py and check_creatures.py.

The one canonical role order, the auto-mapper from a creature's joint names onto it, and the
contract builder. No creature-specific knowledge lives here except the naming heuristics, which
are overridable per creature in its role_map.json.
"""

import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path

TOOL_VERSION = "0.2.0"

UNITE = Path(__file__).resolve().parent.parent
CREATURES = UNITE / "creatures"

# One canonical role order for every creature in the family. Reordering this is a major
# version bump and a retrain for every policy (see GOVERNANCE.md section 1).
CANONICAL_ROLES = [
    "hip_pitch_l", "hip_roll_l", "hip_yaw_l", "knee_l", "ankle_pitch_l", "ankle_roll_l", "toe_l",
    "hip_pitch_r", "hip_roll_r", "hip_yaw_r", "knee_r", "ankle_pitch_r", "ankle_roll_r", "toe_r",
    "waist_roll", "waist_yaw",
    "shoulder_pitch_l", "shoulder_roll_l", "shoulder_yaw_l", "elbow_l", "wrist_l",
    "shoulder_pitch_r", "shoulder_roll_r", "shoulder_yaw_r", "elbow_r", "wrist_r",
    "head_pitch", "head_yaw",
]
N_ROLES = len(CANONICAL_ROLES)

OPT_SCALARS = [
    "timestep", "impratio", "tolerance", "ls_tolerance", "noslip_tolerance", "ccd_tolerance",
    "density", "viscosity", "o_margin", "integrator", "cone", "jacobian", "solver",
    "iterations", "ls_iterations", "noslip_iterations", "ccd_iterations",
    "disableflags", "enableflags", "sdf_initpoints", "sdf_iterations",
]
OPT_VECTORS = ["gravity", "wind", "magnetic", "o_solref", "o_solimp", "o_friction"]

# ---------------------------------------------------------------- naming -> role

SIDE_PREFIX = {"left": "l", "right": "r", "fl": "l", "fr": "r", "rl": "l", "rr": "r"}
SIDE_SUFFIX = {"_l": "l", "_r": "r"}

# ordered: first match wins, so put the specific patterns before the generic ones.
# Matched against a space-normalised lower-case name ("left_hip_pitch_joint" -> "left hip pitch joint"),
# because "_" is a word character and would defeat \b boundaries.
ROLE_PATTERNS = [
    (r"\bhip\s*pitch|\bhip\s*flex", "hip_pitch"),
    (r"\bhip\s*(?:roll|abd\w*)", "hip_roll"),
    (r"\bhip\s*(?:yaw|rot\w*)", "hip_yaw"),
    (r"\bknee\b", "knee"),
    (r"ankle\s*pitch", "ankle_pitch"),
    (r"ankle\s*roll", "ankle_roll"),
    (r"\btoe\b|\bball\b", "toe"),
    (r"waist\s*roll|lumbar\s*lat", "waist_roll"),
    (r"waist\s*yaw|lumbar\s*twist|torso\s*yaw", "waist_yaw"),
    (r"shoulder\s*pitch", "shoulder_pitch"),
    (r"shoulder\s*(?:roll|abd\w*)", "shoulder_roll"),
    (r"shoulder\s*(?:yaw|rot\w*)", "shoulder_yaw"),
    (r"\belbow\b", "elbow"),
    (r"\bwrist\b|\bhand\b", "wrist"),
    (r"head\s*pitch|neck\s*pitch", "head_pitch"),
    (r"head\s*yaw|neck\s*yaw", "head_yaw"),
]

# Go2-style quadruped legs fill the humanoid leg block positionally: front legs take the hip roles,
# rear legs take the knee/ankle roles. Documented as positional, never anatomical.
QUAD_LEG_PATTERNS = [
    (r"\bhip\b", "hip_pitch"),
    (r"\bthigh\b", "hip_roll"),
    (r"\bcalf\b", "hip_yaw"),
]

# Quadruped positional fill: (side, joint keyword) -> role
QUAD_POSITIONAL = {
    ("l", "hip"): "hip_pitch_l", ("l", "thigh"): "hip_roll_l", ("l", "calf"): "hip_yaw_l",
    ("r", "hip"): "hip_pitch_r", ("r", "thigh"): "hip_roll_r", ("r", "calf"): "hip_yaw_r",
    ("lr", "hip"): "knee_l", ("lr", "thigh"): "ankle_pitch_l", ("lr", "calf"): "ankle_roll_l",
    ("rr", "hip"): "knee_r", ("rr", "thigh"): "ankle_pitch_r", ("rr", "calf"): "ankle_roll_r",
}


def _side(name):
    n = name.lower()
    for prefix, side in SIDE_PREFIX.items():
        if n.startswith(prefix + "_") or n.startswith(prefix):
            # avoid matching "rl" inside a word: require the prefix to be the first token
            head = re.split(r"[_-]", n)[0]
            if head in SIDE_PREFIX:
                return side
    for suffix, side in SIDE_SUFFIX.items():
        if n.endswith(suffix):
            return side
    return None


def auto_role(joint_name, kind="humanoid"):
    """Best-effort mapping from a joint name to a canonical role. None when unsure."""
    n = re.sub(r"_joint$", "", joint_name.lower())
    flat = n.replace("_", " ")
    side = _side(n)

    if kind == "quadruped":
        head = re.split(r"[_-]", n)[0]
        group = {"fl": "l", "fr": "r", "rl": "lr", "rr": "rr"}.get(head, head)
        for kw in ("hip", "thigh", "calf"):
            if re.search(rf"\b{kw}\b", flat):
                return QUAD_POSITIONAL.get((group, kw))
        return None

    for pat, role in ROLE_PATTERNS:
        if re.search(pat, flat):
            if role in ("waist_roll", "waist_yaw", "head_pitch", "head_yaw"):
                return role
            return f"{role}_{side}" if side else role
    return None


def dedupe_roles(role_map):
    """One joint per role. The first joint (in model order) that claims a role keeps it; later
    claimants are held outside the brain instead of silently aliasing the same output."""
    seen, out = set(), {}
    for jn, role in role_map.items():
        if role is None or role in seen:
            out[jn] = None
        else:
            out[jn] = role
            seen.add(role)
    return out


# ---------------------------------------------------------------- mujoco introspection

def qpos_names(m):
    import mujoco

    out = []
    for j in range(m.njnt):
        name = mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_JOINT, j) or "@" + mujoco.mj_id2name(
            m, mujoco.mjtObj.mjOBJ_BODY, int(m.jnt_bodyid[j])
        )
        n = {mujoco.mjtJoint.mjJNT_FREE: 7, mujoco.mjtJoint.mjJNT_BALL: 4}.get(
            mujoco.mjtJoint(int(m.jnt_type[j])), 1
        )
        out += [name] if n == 1 else [f"{name}[{k}]" for k in range(n)]
    return out


def option_of(m):
    return {
        "timestep": float(m.opt.timestep),
        "integrator": int(m.opt.integrator),
        "cone": int(m.opt.cone),
        "solver": int(m.opt.solver),
        "jacobian": int(m.opt.jacobian),
        "iterations": int(m.opt.iterations),
        "ls_iterations": int(m.opt.ls_iterations),
        "impratio": float(m.opt.impratio),
        "tolerance": float(m.opt.tolerance),
        "gravity": [float(x) for x in m.opt.gravity],
        "o_solref": [float(x) for x in m.opt.o_solref],
        "o_solimp": [float(x) for x in m.opt.o_solimp],
        "o_friction": [float(x) for x in m.opt.o_friction],
    }


def model_numbers(m, root_body):
    """Bodies, joints, actuators by name for the subtree under root_body."""
    import mujoco

    T = mujoco.mjtObj
    name = lambda t, i: mujoco.mj_id2name(m, t, i)  # noqa: E731
    root = mujoco.mj_name2id(m, T.mjOBJ_BODY, root_body)

    bodies = {}
    for b in range(1, m.nbody):
        parent = int(m.body_parentid[b])
        if int(m.body_rootid[b]) != root and b != root:
            continue
        bodies[name(T.mjOBJ_BODY, b)] = {
            "mass": float(m.body_mass[b]),
            "inertia": [float(x) for x in m.body_inertia[b]],
            "pos": [float(x) for x in m.body_pos[b]],
            "quat": [float(x) for x in m.body_quat[b]],
            "parent": name(T.mjOBJ_BODY, parent) or "world",
        }
    joints = {}
    for j in range(m.njnt):
        jn = name(T.mjOBJ_JOINT, j)
        if not jn:
            continue
        if int(m.body_rootid[int(m.jnt_bodyid[j])]) != root:
            continue
        joints[jn] = {
            "type": int(m.jnt_type[j]),
            "axis": [float(x) for x in m.jnt_axis[j]],
            "range": [float(x) for x in m.jnt_range[j]],
            "parent": name(T.mjOBJ_BODY, int(m.jnt_bodyid[j])),
            "qposadr": int(m.jnt_qposadr[j]),
            "damping": [float(x) for x in m.dof_damping[int(m.jnt_dofadr[j]) : int(m.jnt_dofadr[j]) + 1]],
        }
    actuators = {}
    for a in range(m.nu):
        an = name(T.mjOBJ_ACTUATOR, a)
        if not an:
            continue
        trn = int(m.actuator_trnid[a][0])
        jn = name(T.mjOBJ_JOINT, trn) or ""
        if int(m.body_rootid[int(m.jnt_bodyid[trn])]) != root:
            continue
        actuators[an] = {
            "joint": jn,
            "gainprm": [float(x) for x in m.actuator_gainprm[a]],
            "biasprm": [float(x) for x in m.actuator_biasprm[a]],
            "ctrlrange": [float(x) for x in m.actuator_ctrlrange[a]],
            "forcerange": [float(x) for x in m.actuator_forcerange[a]],
            "gear": [float(x) for x in m.actuator_gear[a]],
        }
    return bodies, joints, actuators


def observation_slots():
    slots, at = [], 0
    for name, size, scale, frame, rule in [
        ("joint_pos_rel", N_ROLES, 1.0, "pelvis", "q - default, role order, 0 for masked roles"),
        ("joint_vel", N_ROLES, 0.1, "pelvis", "qd, role order, 0 for masked roles"),
        ("prev_action", N_ROLES, 1.0, "none", "raw policy output, role order, 0 after reset"),
        ("base_ang_vel", 3, 0.25, "pelvis", "free-joint angular velocity, used directly (already body frame)"),
        ("projected_gravity", 3, 1.0, "pelvis", "(0,0,-1) rotated into the pelvis frame"),
        ("velocity_commands", 3, 1.0, "heading", "vx, vy m/s and wz rad/s; ranges live in the policy sidecar"),
        ("gait_phase", 2, 1.0, "none", "[sin 2*pi*p, cos 2*pi*p]; both 0 while |command| < 0.1"),
        ("role_mask", N_ROLES, 1.0, "none", "1 where this creature has the role, 0 where padded"),
        ("morphology_onehot", 8, 1.0, "none", "one-hot index of this creature in the family registry"),
    ]:
        slots.append({"name": name, "start": at, "size": size, "scale": scale, "frame": frame, "rule": rule})
        at += size
    return slots, at


def canonical(obj):
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def sha(obj):
    return hashlib.sha256(canonical(obj).encode("utf-8")).hexdigest()


# ---------------------------------------------------------------- the contract

def build_contract(*, cid, xml_path, role_map, kind, mujoco_version, root_body=None,
                   decimation=None, hardware=None, notes="", source_label=None,
                   morphology_id=0, legacy=None):
    """Compile xml_path with MuJoCo and emit a v1 contract dict.

    role_map: {joint_name: role or None}. Joints absent from the map are auto-mapped.
    """
    import mujoco

    m = mujoco.MjModel.from_xml_path(str(xml_path))
    T = mujoco.mjtObj

    if root_body is None:
        roots = [mujoco.mj_id2name(m, T.mjOBJ_BODY, b) for b in range(1, m.nbody) if int(m.body_parentid[b]) == 0]
        if len(roots) != 1:
            raise SystemExit(f"{cid}: expected one free root body, found {roots}; pass root_body explicitly")
        root_body = roots[0]

    bodies, mj_joints, mj_actuators = model_numbers(m, root_body)
    ctrl_by_joint = {v["joint"]: (k, v) for k, v in mj_actuators.items()}

    # The home pose defines each joint's `default`, which is the baseline of `joint_pos_rel` and the
    # target of every masked PD-hold. The scene keyframe is the training pose; qpos0 is the fallback.
    import numpy as np

    home = np.array(m.qpos0, dtype=np.float64)
    if m.nkey > 0:
        home = np.array(m.key_qpos[0], dtype=np.float64)

    joints = []
    for jname, jd in mj_joints.items():
        if jd["type"] == int(mujoco.mjtJoint.mjJNT_FREE):
            continue
        role = role_map.get(jname, "unspecified")
        if role == "unspecified":
            role = auto_role(jname, kind)
        act_name, act = ctrl_by_joint.get(jname, (None, {}))
        kp = None
        if act:
            kp = float(-act["biasprm"][1]) if abs(act["biasprm"][1]) > 0 else None
        kd = None
        if act:
            kd = float(-act["biasprm"][2]) if abs(act["biasprm"][2]) > 0 else None
        gear = float(act["gear"][0]) if act else None
        joints.append({
            "name": jname,
            "role": role,
            "parent": jd["parent"],
            "axis": jd["axis"],
            "range": jd["range"],
            "default": float(home[jd["qposadr"]]),
            "kp": kp,
            "kd": kd,
            "effort": float(act["forcerange"][1]) if act and act.get("forcerange") else None,
            "action_scale": (0.25 * float(act["forcerange"][1]) / kp) if (act and kp and act.get("forcerange")) else None,
            "actuator": act_name,
            "gear": gear,
            "motor_slot": None,
            "hardware_sign": 1,
        })

    present = {j["role"] for j in joints if j["role"]}
    unknown = present - set(CANONICAL_ROLES)
    if unknown:
        raise SystemExit(f"{cid}: auto-mapper produced non-canonical roles {sorted(unknown)}")

    mask = [1 if r in present else 0 for r in CANONICAL_ROLES]
    slots, n_obs = observation_slots()

    if decimation is None:
        decimation = max(1, round(0.02 / float(m.opt.timestep)))
    physics = {
        "mujoco": mujoco_version,
        "timestep": float(m.opt.timestep),
        "decimation": int(decimation),
        "control_dt": float(m.opt.timestep) * int(decimation),
        "option": option_of(m),
    }

    roles = {
        "canonical_order": CANONICAL_ROLES,
        "mask": mask,
        "morphology_id": morphology_id,
        "morphology_onehot_size": 8,
        "held_outside_brain": sorted({j["name"] for j in joints if not j["role"]}),
    }
    observation = {"n_obs": n_obs, "order": "roles", "slots": slots, "normalizer": {"kind": "none", "path": None}}
    action = {
        "n_action": N_ROLES,
        "rule": "ctrl = clip(default + a * action_scale, soft_limit_lo, soft_limit_hi); masked roles are PD-held at default",
        "clip": [-5.0, 5.0],
        "soft_limit_factor": 0.9,
    }
    morphology = {
        "root_body": root_body,
        "imu_site": None,
        "qpos_names": qpos_names(m),
        "joints": joints,
        "bodies": bodies,
        "actuators": mj_actuators,
        "sites": {mujoco.mj_id2name(m, T.mjOBJ_SITE, s): {} for s in range(m.nsite) if mujoco.mj_id2name(m, T.mjOBJ_SITE, s)},
        "geoms": [],
        "frame": {"sim": "metres, right-handed, Z-up, creature faces +X at qpos=0"},
    }

    contract = {
        "contract_version": 1,
        "name": cid,
        "creature": cid,
        "kind": kind,
        "notes": notes,
        "generated": {
            "tool": "pocontract",
            "tool_version": TOOL_VERSION,
            "source": source_label or str(xml_path),
            "written_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        },
        "physics": physics,
        "morphology": morphology,
        "roles": roles,
        "observation": observation,
        "action": action,
        "hashes": {
            "morphology_sha256": sha(morphology),
            "interface_sha256": sha({"observation": observation, "action": action,
                                     "roles": CANONICAL_ROLES, "obs_rules": [s.get("rule") for s in slots]}),
            "physics_sha256": sha(physics),
        },
    }
    contract["hashes"]["interface_sha256_16"] = contract["hashes"]["interface_sha256"][:16]
    if legacy:
        contract["legacy_interfaces"] = legacy
    if hardware:
        contract["hardware"] = hardware
    return contract


def write_json(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2) + "\n", encoding="utf-8")


def strip_generated(contract):
    c = json.loads(json.dumps(contract))
    c.pop("generated", None)
    return c
