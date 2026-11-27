"""The universal agent observation: one 131-float vector for every creature.

    python role_obs.py --selftest
    python role_obs.py --golden            # write creatures/<id>/golden_role_obs.npz for all six
    python role_obs.py --golden go2        # just one
    python role_obs.py --verify            # recompute from the stored inputs, compare to the stored obs

This is the *reference* implementation. Each runtime reimplements it -- the C++ twin, the web twin, a
Unity plugin -- and proves itself by reproducing the golden file. That is why the goldens are shared
data while the code is not: two independent implementations agreeing to 1e-5 is evidence; one library
called from two places is not.

Layout comes from `contract.observation.slots`, not from this file. Change the contract, rerun
`--golden`, and every runtime's test starts failing -- which is the point.

Requires numpy. Generating goldens also needs mujoco (for forward kinematics); verifying does not.
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402

GOLDEN_NAME = "golden_role_obs.npz"
N_SAMPLES = 100
TOLERANCE = 1e-5


# ---------------------------------------------------------------- quaternion helpers

def quat_to_mat(q):
    """(w, x, y, z) -> 3x3 rotation matrix, MuJoCo convention."""
    w, x, y, z = q
    n = w * w + x * x + y * y + z * z
    if n < 1e-12:
        return np.eye(3)
    s = 2.0 / n
    return np.array([
        [1 - s * (y * y + z * z), s * (x * y - z * w), s * (x * z + y * w)],
        [s * (x * y + z * w), 1 - s * (x * x + z * z), s * (y * z - x * w)],
        [s * (x * z - y * w), s * (y * z + x * w), 1 - s * (x * x + y * y)],
    ])


def projected_gravity(quat):
    """(0, 0, -1) expressed in the body frame: R^T g."""
    return quat_to_mat(quat).T @ np.array([0.0, 0.0, -1.0])


# ---------------------------------------------------------------- the builder

class RoleObs:
    """Builds the universal observation for one creature, from its contract."""

    def __init__(self, contract, morphology_id=None):
        self.c = contract
        roles = contract["roles"]["canonical_order"]
        self.roles = list(roles)
        self.n_roles = len(roles)
        self.mask = np.asarray(contract["roles"]["mask"], dtype=np.float32)
        self.slots = {s["name"]: (int(s["start"]), int(s["size"]), float(s["scale"]))
                      for s in contract["observation"]["slots"]}
        self.n_obs = int(contract["observation"]["n_obs"])
        self.onehot_size = int(contract["roles"].get("morphology_onehot_size", 8))
        self.morphology_id = int(contract["roles"].get("morphology_id", 0) if morphology_id is None else morphology_id)

        # role -> joint, and each joint's default q, from the contract (the hardware/sim truth)
        self.joint_of_role = {}
        self.default_of_role = np.zeros(self.n_roles, dtype=np.float64)
        for j in contract["morphology"]["joints"]:
            r = j.get("role")
            if r and r in self.roles:
                self.joint_of_role[r] = j["name"]
                self.default_of_role[self.roles.index(r)] = float(j.get("default") or 0.0)

        if len(self.slots) != 9:
            raise SystemExit(f"expected 9 observation slots, found {list(self.slots)}")

    # -- the vector -----------------------------------------------------------------------------

    def build(self, *, joint_pos, joint_vel, pelvis_quat, pelvis_angvel, prev_action,
              command=(0.0, 0.0, 0.0), gait_phase=0.0):
        """joint_pos / joint_vel: {joint_name: value}. prev_action: 28 floats, role order."""
        obs = np.zeros(self.n_obs, dtype=np.float32)

        # 1-3: per-role blocks. Masked roles stay exactly 0.
        pos = np.zeros(self.n_roles, dtype=np.float64)
        vel = np.zeros(self.n_roles, dtype=np.float64)
        act = np.zeros(self.n_roles, dtype=np.float64)
        for i, role in enumerate(self.roles):
            if self.mask[i] == 0:
                continue
            jn = self.joint_of_role.get(role)
            if jn is None:
                continue
            pos[i] = float(joint_pos.get(jn, 0.0)) - self.default_of_role[i]
            vel[i] = float(joint_vel.get(jn, 0.0))
            act[i] = float(prev_action[i]) if prev_action is not None else 0.0

        cmd = np.asarray(command, dtype=np.float64).reshape(-1)
        moved = float(np.linalg.norm(cmd)) >= 0.1
        phase = np.array([np.sin(2 * np.pi * gait_phase), np.cos(2 * np.pi * gait_phase)]) if moved else np.zeros(2)

        onehot = np.zeros(self.onehot_size, dtype=np.float32)
        if 0 <= self.morphology_id < self.onehot_size:
            onehot[self.morphology_id] = 1.0

        blocks = {
            "joint_pos_rel": pos,
            "joint_vel": vel * self.slots["joint_vel"][2],
            "prev_action": act,
            "base_ang_vel": np.asarray(pelvis_angvel, dtype=np.float64).reshape(3) * self.slots["base_ang_vel"][2],
            "projected_gravity": projected_gravity(pelvis_quat),
            "velocity_commands": cmd,
            "gait_phase": phase,
            "role_mask": self.mask,
            "morphology_onehot": onehot,
        }
        for name, values in blocks.items():
            start, size, _scale = self.slots[name]
            if len(values) != size:
                raise SystemExit(f"slot {name}: built {len(values)} values, contract says {size}")
            obs[start:start + size] = values
        return obs

    # -- the inverse, for runtimes that only have a flat observation -------------------------------

    def slice(self, obs, name):
        start, size, _ = self.slots[name]
        return np.asarray(obs)[start:start + size]


# ---------------------------------------------------------------- goldens

def _samples(creature, n=N_SAMPLES, seed=0):
    """Random states pushed through MuJoCo so the truth (quat, angular velocity) comes from the engine."""
    import mujoco

    scene = L.CREATURES / creature / "scene.xml"
    contract = json.loads((L.CREATURES / creature / "contract.json").read_text(encoding="utf-8"))
    m = mujoco.MjModel.from_xml_path(str(scene))
    d = mujoco.MjData(m)
    rng = np.random.default_rng(seed)

    root = int(mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, contract["morphology"]["root_body"]))
    T = mujoco.mjtObj
    hinge = {}
    for j in range(m.njnt):
        name = mujoco.mj_id2name(m, T.mjOBJ_JOINT, j)
        if name and int(m.jnt_type[j]) in (int(mujoco.mjtJoint.mjJNT_HINGE), int(mujoco.mjtJoint.mjJNT_SLIDE)):
            hinge[name] = (int(m.jnt_qposadr[j]), int(m.jnt_dofadr[j]))
    free_j = next((j for j in range(m.njnt) if int(m.jnt_type[j]) == int(mujoco.mjtJoint.mjJNT_FREE)), None)
    if free_j is None:
        raise SystemExit(f"{creature}: no free joint in the model; the universal observation assumes a floating base")
    free_q = int(m.jnt_qposadr[free_j])
    free_v = int(m.jnt_dofadr[free_j])

    rows = []
    for _ in range(n):
        mujoco.mj_resetData(m, d)
        # random free-joint pose near home, random joint angles inside their range
        d.qpos[free_q:free_q + 3] = rng.normal(0, 0.1, 3) + d.qpos[free_q:free_q + 3]
        q = rng.normal(size=4)
        d.qpos[free_q + 3:free_q + 7] = q / np.linalg.norm(q)
        for name, (qa, _va) in hinge.items():
            lo, hi = m.jnt_range[mujoco.mj_name2id(m, T.mjOBJ_JOINT, name)]
            d.qpos[qa] = rng.uniform(lo, hi) if hi > lo else 0.0
        d.qvel[:] = rng.normal(0, 1.0, m.nv)
        mujoco.mj_forward(m, d)

        joint_pos = {name: float(d.qpos[qa]) for name, (qa, _va) in hinge.items()}
        joint_vel = {name: float(d.qvel[va]) for name, (_qa, va) in hinge.items()}
        quat = tuple(float(x) for x in d.qpos[free_q + 3:free_q + 7])
        angvel = tuple(float(x) for x in d.qvel[free_v + 3:free_v + 6])
        prev_action = rng.uniform(-1, 1, 28)
        command = tuple(float(x) for x in rng.uniform(-1, 1, 3))
        phase = float(rng.random())

        builder = RoleObs(contract)
        obs = builder.build(joint_pos=joint_pos, joint_vel=joint_vel, pelvis_quat=quat,
                            pelvis_angvel=angvel, prev_action=prev_action,
                            command=command, gait_phase=phase)
        rows.append({
            "creature": creature,
            "joint_pos": joint_pos, "joint_vel": joint_vel,
            "pelvis_quat": quat, "pelvis_angvel": angvel,
            "prev_action": prev_action.tolist(), "command": list(command), "gait_phase": phase,
            "obs": obs,
        })
    return contract, rows


def write_golden(creature):
    contract, rows = _samples(creature)
    out = L.CREATURES / creature / GOLDEN_NAME
    np.savez_compressed(
        out,
        obs=np.stack([r["obs"] for r in rows]),
        prev_action=np.stack([np.asarray(r["prev_action"], dtype=np.float32) for r in rows]),
        command=np.stack([np.asarray(r["command"], dtype=np.float32) for r in rows]),
        gait_phase=np.asarray([r["gait_phase"] for r in rows], dtype=np.float32),
        pelvis_quat=np.stack([np.asarray(r["pelvis_quat"], dtype=np.float32) for r in rows]),
        pelvis_angvel=np.stack([np.asarray(r["pelvis_angvel"], dtype=np.float32) for r in rows]),
        joint_pos_json=np.asarray([json.dumps(r["joint_pos"]) for r in rows]),
        joint_vel_json=np.asarray([json.dumps(r["joint_vel"]) for r in rows]),
        interface_sha256_16=np.asarray([contract["hashes"]["interface_sha256_16"]]),
    )
    obs = np.stack([r["obs"] for r in rows])
    return out, obs, contract


def verify_golden(creature):
    """Recompute every sample from the stored inputs. Returns a list of problems."""
    path = L.CREATURES / creature / GOLDEN_NAME
    if not path.exists():
        return [f"{path.name} is missing; run --golden {creature}"]
    contract = json.loads((L.CREATURES / creature / "contract.json").read_text(encoding="utf-8"))
    z = np.load(path)
    problems = []
    if str(z["interface_sha256_16"][0]) != contract["hashes"]["interface_sha256_16"]:
        problems.append(f"{creature}: golden was written against interface {z['interface_sha256_16'][0]}, "
                        f"contract is {contract['hashes']['interface_sha256_16']}; regenerate")
        return problems

    builder = RoleObs(contract)
    want = z["obs"]
    got = np.stack([
        builder.build(
            joint_pos=json.loads(z["joint_pos_json"][i]),
            joint_vel=json.loads(z["joint_vel_json"][i]),
            pelvis_quat=z["pelvis_quat"][i],
            pelvis_angvel=z["pelvis_angvel"][i],
            prev_action=z["prev_action"][i],
            command=z["command"][i],
            gait_phase=float(z["gait_phase"][i]),
        )
        for i in range(len(want))
    ])
    d = float(np.max(np.abs(want - got)))
    if d > TOLERANCE:
        problems.append(f"{creature}: max deviation {d:.3g} > {TOLERANCE}")
    return problems


# ---------------------------------------------------------------- selftest

def selftest():
    contract = json.loads((L.CREATURES / "unitree_r1_a5" / "contract.json").read_text(encoding="utf-8"))
    b = RoleObs(contract)
    ok = True

    def check(label, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'OK  ' if cond else 'FAIL'} {label}{('  ' + detail) if detail else ''}")

    n = contract["observation"]["n_obs"]
    check("n_obs is 131", n == 131, f"({n})")

    upright = b.build(joint_pos={}, joint_vel={}, pelvis_quat=(1, 0, 0, 0),
                      pelvis_angvel=(0, 0, 0), prev_action=np.zeros(28))
    check("shape is (131,) float32", upright.shape == (131,) and upright.dtype == np.float32)
    check("upright gravity is (0,0,-1)", np.allclose(b.slice(upright, "projected_gravity"), [0, 0, -1], atol=1e-6))

    s, c = np.sin(np.pi / 4), np.cos(np.pi / 4)
    rolled = b.build(joint_pos={}, joint_vel={}, pelvis_quat=(c, s, 0, 0),
                     pelvis_angvel=(0, 0, 0), prev_action=np.zeros(28))
    check("90 deg roll rotates gravity to (0,-1,0)",
          np.allclose(b.slice(rolled, "projected_gravity"), [0, -1, 0], atol=1e-6),
          str(np.round(b.slice(rolled, "projected_gravity"), 6)))

    stationary = b.build(joint_pos={}, joint_vel={}, pelvis_quat=(1, 0, 0, 0), pelvis_angvel=(0, 0, 0),
                         prev_action=np.zeros(28), command=(0, 0, 0), gait_phase=0.37)
    check("gait phase is [0,0] with no command", np.array_equal(b.slice(stationary, "gait_phase"), [0, 0]))

    moving = b.build(joint_pos={}, joint_vel={}, pelvis_quat=(1, 0, 0, 0), pelvis_angvel=(0, 0, 0),
                     prev_action=np.zeros(28), command=(0.5, 0, 0), gait_phase=0.0)
    gp = b.slice(moving, "gait_phase")
    check("gait phase is [sin,cos] when commanded", np.allclose(gp, [0.0, 1.0], atol=1e-6), str(gp))

    mask = b.slice(upright, "role_mask")
    check("role_mask has 26 ones", int(mask.sum()) == 26, f"({int(mask.sum())})")
    check("morphology_onehot is a one-hot of 8", abs(float(b.slice(upright, "morphology_onehot").sum()) - 1.0) < 1e-6)

    # a masked role (toe_l, index 6) must be exactly zero in all three per-role blocks
    toe = b.roles.index("toe_l")
    check("masked role toe_l is 0 in joint_pos/vel/prev_action",
          all(b.slice(upright, s)[toe] == 0 for s in ("joint_pos_rel", "joint_vel", "prev_action")))

    # offsets are contiguous and match the contract
    at = 0
    for slot in contract["observation"]["slots"]:
        if slot["start"] != at:
            check(f"slot {slot['name']} offset", False, f"{slot['start']} != {at}")
        at += slot["size"]
    check("slots are contiguous to 131", at == 131, f"({at})")

    print("SELFTEST", "OK" if ok else "FAILED")
    return 0 if ok else 1


# ---------------------------------------------------------------- cli

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("creature", nargs="*")
    ap.add_argument("--golden", action="store_true")
    ap.add_argument("--verify", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()

    ids = a.creature or sorted(d.name for d in L.CREATURES.iterdir()
                               if d.is_dir() and (d / "contract.json").exists())
    if a.golden:
        for cid in ids:
            out, obs, contract = write_golden(cid)
            print(f"wrote {out.relative_to(L.UNITE)}  {obs.shape[0]} samples, "
                  f"{obs.shape[1]} floats, interface {contract['hashes']['interface_sha256_16']}")
        return 0
    if a.verify:
        bad = 0
        for cid in ids:
            problems = verify_golden(cid)
            print(f"{'OK  ' if not problems else 'FAIL'} {cid}")
            for p in problems:
                print(f"       {p}")
            bad += bool(problems)
        print("FAILED" if bad else "OK")
        return 1 if bad else 0

    ap.error("pass --selftest, --golden or --verify")


if __name__ == "__main__":
    sys.exit(main())
