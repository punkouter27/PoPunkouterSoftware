"""Run one conforming policy anywhere: every creature in MuJoCo, and the R1's motor bus.

    python role_agent.py runs/ppo-r1-edu                        # evaluate on the creature it was trained for
    python role_agent.py runs/ppo-r1-edu --all-creatures        # same brain on all six bodies
    python role_agent.py runs/ppo-r1-edu --hardware             # the 31-slot unitree_hg command vector
    python role_agent.py --selftest

This is the runtime half of the contract, in one file and with no task logic:

* **load** -- read the sidecar, compare it against the creature contract, and *refuse* on mismatch
  (R14). No reordering, no padding, no rescaling to make an old brain fit.
* **run** -- drive ``CreatureEnv`` (28 actions in, 131 observations out) so the same weights can be
  evaluated on any body in the library.
* **deploy** -- for the R1 only, turn the 28 role actions into the same 31-slot ``LowCmd_`` index
  space ``PoRobotTrain`` publishes, and cross-check it against the sim's own ctrl targets. If those
  two disagree, the action vector is not portable and nothing else in this file matters.

A brain trained for one creature is *loadable* on the others (their contracts share one interface)
but not *correct* on them: ``role_mask`` and ``morphology_onehot`` still describe the body it was
trained on. The report says ``own`` / ``foreign`` per creature and never blurs the two.

Requires numpy + mujoco + onnx + onnxruntime. No torch. Deploying to the robot additionally needs
the PoRobotTrain gate (``RobotControl/models/<skill>/deploy.json``); this tool builds the command
vector, it does not publish it.
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402
import verify_policy as V  # noqa: E402
from role_env import DEFAULT_SOFT_LIMIT, CreatureEnv  # noqa: E402

FALL_FRACTION = 0.55


class Refused(RuntimeError):
    """The policy does not match the contract it would run against (R13/R14)."""


# ---------------------------------------------------------------- load


class Agent:
    """A policy plus the contract it was verified against, and nothing else."""

    def __init__(self, path, creature=None):
        self.policy = locate_policy(path)
        sidecar_path = self.policy.with_suffix(".json")
        if not sidecar_path.exists():
            raise Refused(f"{self.policy.name} has no sidecar; R10 requires one "
                          f"(write it with verify_policy.py --check --write)")
        self.sidecar = json.loads(sidecar_path.read_text(encoding="utf-8"))
        self.trained_for = self.sidecar.get("creature")
        self.creature = creature or self.trained_for
        if not self.creature:
            raise Refused("sidecar names no creature and --creature was not given")

        self.contract = V.load_contract(self.creature)
        problems, info, _ = V.check(self.policy, self.creature)
        if problems:
            raise Refused(f"{self.policy.name} does not conform to {self.creature}: {problems[0]}")

        if self.creature == self.trained_for:
            vp = V.verify_sidecar(self.policy, self.creature, self.contract, info)
            if vp:
                raise Refused(f"sidecar refused: {vp[0]}")
        else:
            # R13: a foreign body is allowed only through an identical interface.
            if self.sidecar.get("interface_sha256") != self.contract["hashes"]["interface_sha256"]:
                raise Refused(
                    f"{self.policy.name} was built against interface "
                    f"{str(self.sidecar.get('interface_sha256'))[:16]}..., but {self.creature} publishes "
                    f"{self.contract['hashes']['interface_sha256'][:16]}..."
                )

        import onnxruntime as ort

        self.session = ort.InferenceSession(str(self.policy), providers=["CPUExecutionProvider"])
        self.input_name = self.session.get_inputs()[0].name
        self.n_obs = int(self.contract["observation"]["n_obs"])
        self.n_action = int(self.contract["action"]["n_action"])
        self.info = info

    @property
    def own_creature(self):
        return self.creature == self.trained_for

    def act(self, obs):
        x = np.asarray(obs, dtype=np.float32).reshape(1, self.n_obs)
        out = self.session.run(None, {self.input_name: x})[0]
        return np.asarray(out, dtype=np.float32).reshape(-1)


def locate_policy(path):
    p = Path(path)
    if p.is_dir():
        p = p / "policy.onnx"
    if not p.exists():
        raise SystemExit(f"no policy at {path}")
    return p


# ---------------------------------------------------------------- run


def evaluate(agent, creature, *, steps=300, command=(0.4, 0.0, 0.0), seed=0):
    """Run the policy in MuJoCo and report what it actually did, not what it should have done."""
    env = CreatureEnv(creature, seed=seed, command=command)
    obs = env.reset()
    cmd = np.asarray(command, dtype=np.float64)

    h0 = env.height
    falls = 0
    speed_err, yaw_err, saturation = [], [], []
    finite = True
    for _ in range(steps):
        action = agent.act(obs)
        if not np.isfinite(action).all():
            finite = False
            break
        saturation.append(float(np.mean(np.abs(action) > 0.99)))
        obs = env.step(action)
        if not np.isfinite(obs).all():
            finite = False
            break
        speed_err.append(float(np.linalg.norm(env.base_linvel[:2] - cmd[:2])))
        yaw_err.append(abs(float(env.base_angvel[2] - cmd[2])))
        if env.height < FALL_FRACTION * env.home_height:
            falls += 1

    return {
        "creature": creature,
        "trained_for": agent.trained_for,
        "relation": "own" if creature == agent.trained_for else "foreign",
        "steps": steps,
        "finite": finite,
        "height_start": round(h0, 4),
        "height_end": round(env.height, 4),
        "steps_below_fall_height": falls,
        "mean_linvel_error": round(float(np.mean(speed_err)), 4) if speed_err else None,
        "mean_yaw_rate_error": round(float(np.mean(yaw_err)), 4) if yaw_err else None,
        "action_saturation": round(float(np.mean(saturation)), 4) if saturation else None,
        "command": list(map(float, cmd)),
    }


# ---------------------------------------------------------------- deploy


class MotorBus:
    """The R1's 31-slot ``unitree_hg`` command vector, built from the contract (R21).

    Only the R1 has a ``hardware`` block. Unmapped slots stay ``mode 0`` with zero gains, which is
    exactly the robot's unpowered state, so a slot this tool does not drive can never be energised
    by accident.
    """

    FIELDS = ("mode", "q", "dq", "kp", "kd", "tau")

    def __init__(self, contract):
        self.contract = contract
        hw = contract.get("hardware")
        if not hw:
            raise SystemExit(f"{contract['creature']} has no hardware block; R21 says only unitree_r1_a5 may")
        self.num_slots = int(hw["bus"]["num_slots"])
        self.unused_slots = sorted(int(s) for s in hw["bus"]["unused_slots"])
        self.roles = list(contract["roles"]["canonical_order"])
        self.factor = float(contract["action"].get("soft_limit_factor", DEFAULT_SOFT_LIMIT))
        self.by_role = {j["role"]: j for j in contract["morphology"]["joints"] if j.get("motor_slot") is not None}
        self.by_actuator = {j["actuator"]: int(j["motor_slot"]) for j in self.by_role.values()}

    def limits(self, joint):
        lo, hi = (float(x) for x in joint["range"])
        pad = (1.0 - self.factor) * 0.5 * (hi - lo)
        return lo + pad, hi - pad

    def targets(self, action):
        """28 role actions -> (slot, target q). Masked roles have no joint here and produce no entry."""
        a = np.asarray(action, dtype=np.float64).reshape(-1)
        if a.shape[0] != len(self.roles):
            raise ValueError(f"action has {a.shape[0]} values, contract wants {len(self.roles)}")
        out = []
        for i, role in enumerate(self.roles):
            j = self.by_role.get(role)
            if j is None:
                continue
            lo, hi = self.limits(j)
            q = float(np.clip(float(j.get("default") or 0.0) + a[i] * float(j["action_scale"]), lo, hi))
            out.append((int(j["motor_slot"]), q * float(j.get("hardware_sign", 1))))
        return out

    def build(self, action):
        cmd = {k: np.zeros(self.num_slots, dtype=np.float64) for k in self.FIELDS}
        cmd["mode"] = np.zeros(self.num_slots, dtype=np.int32)
        target = dict(self.targets(action))
        for joint in self.by_role.values():
            slot = int(joint["motor_slot"])
            if slot not in target:
                continue
            cmd["mode"][slot] = 1
            cmd["q"][slot] = target[slot]
            cmd["kp"][slot] = float(joint["kp"])
            cmd["kd"][slot] = float(joint["kd"])
            cmd["tau"][slot] = 0.0  # feed-forward torque stays zero: this is a position policy
        return {k: v.tolist() for k, v in cmd.items()}


def check_bus_matches_sim(bus, env, action):
    """The one invariant that makes sim and hardware the same index space.

    ``CreatureEnv.target`` resolves role -> actuator -> ctrl. The bus resolves role -> motor slot.
    Both must produce the same angle for every joint the R1 actually has.
    """
    ctrl = env.target(action)
    acts = list(env.contract["morphology"]["actuators"])
    mismatches = []
    bus_q = dict(bus.targets(action))
    for act_name, slot in bus.by_actuator.items():
        if act_name not in acts:
            mismatches.append(f"{act_name}: actuator missing from the twin")
            continue
        sim_q = float(ctrl[acts.index(act_name)])
        if abs(sim_q - bus_q.get(slot, float("nan"))) > 1e-9:
            mismatches.append(f"{act_name}: sim {sim_q:.6f} != slot {slot} {bus_q.get(slot, float('nan')):.6f}")
    return mismatches


def deploy_report(agent, *, steps=1, seed=0, command=(0.0, 0.0, 0.0)):
    env = CreatureEnv(agent.creature, seed=seed, command=command)
    bus = MotorBus(agent.contract)
    obs = env.reset()
    action = agent.act(obs)
    for _ in range(max(1, steps) - 1):
        obs = env.step(action)
        action = agent.act(obs)
    cmd = bus.build(action)
    mismatches = check_bus_matches_sim(bus, env, action)
    powered = [s for s, m in enumerate(cmd["mode"]) if m == 1]
    return {
        "creature": agent.creature,
        "num_slots": bus.num_slots,
        "policy_hz": float(agent.contract["hardware"]["policy_rate_hz"]),
        "powered_slots": powered,
        "unpowered_slots": bus.unused_slots,
        "roles_without_hardware": [r for r in bus.roles if r not in bus.by_role],
        "sim_vs_bus_mismatches": mismatches,
        "command": cmd,
    }


# ---------------------------------------------------------------- report


def print_eval(row):
    rel = row["relation"]
    print(f"  {row['creature']:22s} {rel:7s} h {row['height_start']:.3f} -> {row['height_end']:.3f} m  "
          f"falls {row['steps_below_fall_height']}/{row['steps']}  "
          f"linvel err {row['mean_linvel_error']}  yaw err {row['mean_yaw_rate_error']}  "
          f"sat {row['action_saturation']}  finite {row['finite']}")
    if rel == "foreign":
        print("      loadable, not correct: role_mask and morphology_onehot still describe "
              f"{row['trained_for']}, not {row['creature']}")


def print_deploy(rep):
    print(f"  {rep['creature']}: {len(rep['powered_slots'])} of {rep['num_slots']} slots powered, "
          f"{rep['policy_hz']:.0f} Hz; unpowered {rep['unpowered_slots']}")
    print(f"  roles with no motor on this body: {rep['roles_without_hardware']}")
    if rep["sim_vs_bus_mismatches"]:
        print("  SIM vs BUS MISMATCH:")
        for m in rep["sim_vs_bus_mismatches"]:
            print(f"    - {m}")
    else:
        print("  sim ctrl and bus q agree on every mapped joint (same index space)")
    q = np.asarray(rep["command"]["q"])
    print(f"  q   {np.array2string(q[rep['powered_slots']], precision=3, max_line_width=200)}")


# ---------------------------------------------------------------- selftest


def selftest():
    ok = True

    def check(name, cond):
        nonlocal ok
        print(f"  {'PASS' if cond else 'FAIL'}  {name}")
        ok &= bool(cond)

    contract = V.load_contract("unitree_r1_a5")
    bus = MotorBus(contract)
    rng = np.random.default_rng(0)
    action = rng.uniform(-1, 1, len(bus.roles))

    slots = [s for s, _q in bus.targets(action)]
    check("every mapped slot appears once", sorted(slots) == sorted(int(j["motor_slot"]) for j in bus.by_role.values()))
    check("no command touches an unpowered slot", not set(slots) & set(bus.unused_slots))
    check("masked roles carry no motor", all(contract["roles"]["mask"][bus.roles.index(r)] == 0
                                            for r in ("toe_l", "toe_r")))
    cmd = bus.build(np.zeros(len(bus.roles)))
    check("zero action -> nominal pose",
          all(abs(q - float(bus.by_role[r].get("default") or 0.0)) < 1e-12
              for r in bus.by_role
              for q in [cmd["q"][int(bus.by_role[r]["motor_slot"])]]))

    env = CreatureEnv("unitree_r1_a5")
    check("sim and bus agree", not check_bus_matches_sim(bus, env, action))

    big = dict(bus.targets(np.full(len(bus.roles), 10.0)))
    inside = all(bus.limits(j)[0] - 1e-9 <= big[int(j["motor_slot"])] <= bus.limits(j)[1] + 1e-9
                 for j in bus.by_role.values())
    check("a huge action is clipped to the soft limits", inside)

    go2 = V.load_contract("go2")
    try:
        MotorBus(go2)
        check("no hardware block on a sim-only creature", False)
    except SystemExit:
        check("no hardware block on a sim-only creature", True)

    print("SELFTEST", "OK" if ok else "FAILED")
    return 0 if ok else 1


# ---------------------------------------------------------------- cli


def main():
    ap = argparse.ArgumentParser(description="Run a conforming policy on any creature and build the R1 motor command.")
    ap.add_argument("policy", nargs="?", help="a run directory or a policy.onnx")
    ap.add_argument("--creature", help="evaluate on this creature instead of the one it was trained for")
    ap.add_argument("--all-creatures", action="store_true", help="the same brain on every creature")
    ap.add_argument("--steps", type=int, default=300)
    ap.add_argument("--command", default="0.4,0,0", help="velocity command: vx,vy,wz")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--hardware", action="store_true", help="print the R1 31-slot LowCmd vector")
    ap.add_argument("--json", type=Path, help="write the report here")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    if a.selftest:
        return selftest()
    if not a.policy:
        ap.error("a run directory or policy.onnx is required (or use --selftest)")

    command = tuple(float(x) for x in a.command.split(","))
    if len(command) != 3:
        ap.error("--command wants three comma-separated numbers: vx,vy,wz")

    try:
        if a.all_creatures:
            agent = Agent(a.policy, None)
            targets = sorted(d.name for d in L.CREATURES.iterdir()
                             if d.is_dir() and (d / "contract.json").exists())
        else:
            agent = Agent(a.policy, a.creature)
            targets = [agent.creature]
    except Refused as e:
        print(f"REFUSED: {e}", file=sys.stderr)
        return 1

    print(f"policy {agent.policy.name} trained for {agent.trained_for}, "
          f"interface {agent.sidecar.get('interface_sha256', '')[:16]}..., {agent.n_obs}/{agent.n_action}")
    rows = [evaluate(agent, cid, steps=a.steps, command=command, seed=a.seed) for cid in targets]
    for row in rows:
        print_eval(row)

    report = {"policy": str(agent.policy), "trained_for": agent.trained_for,
              "interface_sha256_16": agent.sidecar.get("interface_sha256_16"), "evaluations": rows}

    if a.hardware:
        if agent.trained_for != "unitree_r1_a5":
            print("  no hardware report: only a policy trained for unitree_r1_a5 may drive the bus (R21)")
        else:
            dep = deploy_report(agent, steps=1, seed=a.seed, command=command)
            print_deploy(dep)
            report["hardware"] = dep

    if a.json:
        a.json.parent.mkdir(parents=True, exist_ok=True)
        a.json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(f"wrote {a.json}")

    bad = any(not r["finite"] for r in rows) or any(report.get("hardware", {}).get("sim_vs_bus_mismatches", []))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
