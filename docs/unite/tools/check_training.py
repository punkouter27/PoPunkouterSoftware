"""Check a training card and an app world against the UNITE training envelope (RULES.md R35-R42).

    python check_training.py card path/to/policy.training.json     # a policy's training card
    python check_training.py world path/to/world.json               # an app's world
    python check_training.py --envelope-hash                         # what a card must pin
    python check_training.py --selftest

A card has two outcomes, kept apart on purpose:

* **errors** -- the card breaks the training standard: missing fields, randomisation narrower than the
  envelope, a different termination rule, missing core reward terms, logs without per-term columns,
  hashes that do not match the contract. The export guard aborts on any of these, whatever the tier.
* **not portable** -- the card follows the standard but has not earned cross-app use yet: tier below
  `release`, too few env steps, an eval corner missing or failing, or no benchmark. Such a policy may
  run in the app that trained it, never in another (R41).

Exit codes: 0 = valid (card: and portable), 1 = errors, 3 = valid card but not portable, 2 = cannot decide.
"""

import argparse
import csv
import hashlib
import json
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402

ENVELOPE_PATH = L.UNITE / "training-envelope.json"
CARD_SUFFIX = ".training.json"


def load_envelope(path=ENVELOPE_PATH):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def envelope_sha256(env):
    canon = json.dumps(env, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(canon).hexdigest()


def card_path_for(policy):
    policy = Path(policy)
    return policy.with_name(policy.stem + CARD_SUFFIX)


def _contains(have, need):
    """have=[lo,hi] covers need=[lo,hi]."""
    try:
        return float(have[0]) <= float(need[0]) and float(have[1]) >= float(need[1])
    except (TypeError, ValueError, IndexError):
        return False


# ---------------------------------------------------------------- card

def validate_card(card, card_dir, env, contract=None):
    """Return (errors, not_portable_reasons, not_robot_ready_reasons)."""
    E, N, R = [], [], []
    E2, N2 = _validate_card(card, card_dir, env, contract, E, N)
    tier = card.get("tier")
    if not E2 and env["trainer"]["tiers"].get(tier, {}).get("robot"):
        R = _validate_robot(card, card_dir, env, contract, E2)
    elif not E2:
        R = [f"tier {tier!r} is not 'robot'; a physical robot needs tier robot (R43)"]
    return E2, N2, R + ([] if not N2 else ["not portable, so not robot-ready"])


def _validate_robot(card, card_dir, env, contract, E):
    """R43: evidence that this ONNX may go to a physical robot. Errors go to E, gaps are returned."""
    R = []
    if contract is not None and not contract.get("hardware"):
        E.append(f"tier robot needs a creature with a hardware block (R21); {card.get('creature')} has none")
        return R
    rb, spec = card.get("robot") or {}, env["robot"]
    cal = rb.get("calibration") or {}
    if not cal.get("calibrated_at"):
        R.append("twin never calibrated against the robot (robot.calibration.calibrated_at is empty)")
    for k, lim in (("parity_rms_deg", spec["calibration"]["parity_rms_deg_max"]),
                   ("parity_max_deg", spec["calibration"]["parity_max_deg_max"])):
        if cal.get(k) is None or float(cal[k]) > lim:
            R.append(f"robot.calibration.{k} = {cal.get(k)}, must be <= {lim}")
    srd = rb.get("sim_real_diff") or {}
    if srd.get("max_deg") is None or float(srd["max_deg"]) > spec["sim_real_diff"]["max_deg_max"]:
        R.append(f"robot.sim_real_diff.max_deg = {srd.get('max_deg')}, must be <= "
                 f"{spec['sim_real_diff']['max_deg_max']} at every tick")
    if int(srd.get("ticks", 0)) < spec["sim_real_diff"]["min_ticks"]:
        R.append(f"robot.sim_real_diff.ticks = {srd.get('ticks')}, need >= {spec['sim_real_diff']['min_ticks']}")
    for k in ("sim_log", "real_log"):
        if srd.get(k) and not (Path(card_dir) / srd[k]).exists() and not Path(srd[k]).exists():
            R.append(f"robot.sim_real_diff.{k} {srd[k]} not found")
    if (rb.get("deploy_gate") or {}).get("passed") is not True:
        R.append("owner app deploy gate has not passed (robot.deploy_gate.passed)")
    done = [s for s in (rb.get("bringup") or {}) if (rb["bringup"][s] or {}).get("passed")]
    for s in spec["bringup_required"]:
        if s not in done:
            R.append(f"bring-up stage {s!r} not passed")
    order = spec["bringup_order"]
    for s in done:
        if s in order and any(p not in done for p in order[:order.index(s)]):
            E.append(f"bring-up stage {s!r} passed before an earlier stage; order is {order}")
    return R


def _validate_card(card, card_dir, env, contract, E, N):

    for k in ("card_version", "envelope_version", "envelope_sha256", "creature", "interface_sha256",
              "morphology_sha256", "physics_sha256", "skill", "tier", "env", "randomisation",
              "obs_noise_std", "reward", "trainer", "logs", "eval"):
        if k not in card:
            E.append(f"missing field {k!r}")
    if E:
        return E, N

    # pins
    if card["envelope_version"] != env["envelope_version"] or card["envelope_sha256"] != envelope_sha256(env):
        E.append(f"card pins envelope v{card['envelope_version']} {str(card['envelope_sha256'])[:16]}..., "
                 f"current is v{env['envelope_version']} {envelope_sha256(env)[:16]}... (R35)")
    if contract is not None:
        for k in ("interface_sha256", "morphology_sha256", "physics_sha256"):
            if card[k] != contract["hashes"][k]:
                E.append(f"{k} {card[k][:16]}... != contract {contract['hashes'][k][:16]}... (R36)")

    # env (R36)
    e, ee = card["env"], env["env"]
    lo, hi = ee["episode_s"]
    if not (lo <= float(e.get("episode_s", -1)) <= hi):
        E.append(f"env.episode_s {e.get('episode_s')} outside [{lo}, {hi}]")
    t = e.get("termination", {})
    for k, v in ee["termination"].items():
        if t.get(k) != v:
            E.append(f"env.termination.{k} = {t.get(k)!r}, standard is {v!r}")
    r = e.get("reset", {})
    if not _contains(r.get("yaw_rad", [0, 0]), ee["reset"]["yaw_rad"]):
        E.append("env.reset.yaw_rad must cover the full circle")
    # card key -> envelope limit key
    for key, limit in (("roll_pitch_max_rad", "roll_pitch_max_rad"), ("joint_std_rad", "joint_std_rad_max"),
                       ("base_pos_std_m", "base_pos_std_m_max")):
        val = r.get(key)
        if val is None or float(val) > float(ee["reset"][limit]):
            E.append(f"env.reset.{key} = {val!r}, must be <= {ee['reset'][limit]}")
    rs, rs_lim = e.get("command_resample_s"), ee["command_resample_s"]
    if not rs or not (rs_lim[0] <= float(rs[0]) <= float(rs[1]) <= rs_lim[1]):
        E.append(f"env.command_resample_s {rs} must lie inside {rs_lim}")

    # randomisation (R37)
    dr = card["randomisation"]
    for k, need in env["randomisation_min"].items():
        if k.startswith("_"):
            continue
        if k not in dr:
            E.append(f"randomisation.{k} missing (must cover {need})")
        elif not _contains(dr[k], need):
            E.append(f"randomisation.{k} {dr[k]} does not cover the envelope {need}")
    for k, need in env["obs_noise_min_std"].items():
        if k.startswith("_"):
            continue
        if float(card["obs_noise_std"].get(k, 0.0)) < need:
            E.append(f"obs_noise_std.{k} {card['obs_noise_std'].get(k)} < {need}")

    # reward (R38)
    terms = card["reward"].get("terms", {})
    locomotion = card["skill"].get("kind", "locomotion") == "locomotion"
    core = env["reward"]["core_terms"]
    for name, sign in core.items():
        if name not in terms:
            if locomotion:
                E.append(f"reward term {name!r} missing (core term for locomotion skills)")
            continue
        w = float(terms[name])
        if (sign == "+" and w < 0) or (sign == "-" and w > 0):
            E.append(f"reward.{name} weight {w} has the wrong sign (standard: {sign})")
    for name in terms:
        if name not in core and not name.startswith(env["reward"]["extra_prefix"]):
            E.append(f"reward term {name!r} must be a core term or start with {env['reward']['extra_prefix']!r}")

    # trainer (R39)
    tr = card["trainer"]
    for k in env["trainer"]["required"]:
        if k not in tr:
            E.append(f"trainer.{k} missing")
    if tr.get("algorithm") not in env["trainer"]["algorithms"]:
        E.append(f"trainer.algorithm {tr.get('algorithm')!r} not in {env['trainer']['algorithms']}")
    net = tr.get("network", {})
    for k, v in env["trainer"]["network"].items():
        if net.get(k) != v:
            E.append(f"trainer.network.{k} = {net.get(k)!r}, standard is {v!r}")
    if contract is not None and tr.get("mujoco") != contract["physics"]["mujoco"]:
        E.append(f"trainer.mujoco {tr.get('mujoco')} != contract {contract['physics']['mujoco']} (R17)")
    tiers = env["trainer"]["tiers"]
    tier = card["tier"]
    if tier not in tiers:
        E.append(f"tier {tier!r} not in {sorted(tiers)}")
    else:
        if int(tr.get("env_steps", 0)) < tiers[tier]["min_env_steps"]:
            E.append(f"tier {tier} needs >= {tiers[tier]['min_env_steps']:,} env steps, card has {tr.get('env_steps')}")
        if not tiers[tier]["portable"]:
            N.append(f"tier {tier} is never portable; retrain at release (>= "
                     f"{tiers['release']['min_env_steps']:,} steps)")

    # logs (R39)
    lg = env["logs"]
    log_file = Path(card_dir) / card["logs"].get("file", lg["file"])
    if not log_file.exists():
        E.append(f"training log {log_file.name} not found beside the card")
    else:
        with open(log_file, newline="", encoding="utf-8") as f:
            header = next(csv.reader(f), [])
        need = lg["required_columns"] + [lg["per_term_prefix"] + t for t in terms]
        missing = [c for c in need if c not in header]
        if missing:
            E.append(f"{log_file.name} is missing columns {missing}")

    # eval (R40)
    ev, env_ev = card["eval"].get("corners", {}), env["eval"]
    for corner in env_ev["corners"]:
        res = ev.get(corner)
        if res is None:
            N.append(f"eval corner {corner!r} not run")
            continue
        if int(res.get("episodes", 0)) < env_ev["episodes_min"]:
            N.append(f"eval {corner}: {res.get('episodes')} episodes < {env_ev['episodes_min']}")
        if float(res.get("falls_per_min", 1e9)) > env_ev["pass"]["falls_per_min_max"]:
            N.append(f"eval {corner}: falls_per_min {res.get('falls_per_min')} > {env_ev['pass']['falls_per_min_max']}")
        if locomotion and float(res.get("lin_vel_err_mps", 1e9)) > env_ev["pass"]["lin_vel_err_mps_max"]:
            N.append(f"eval {corner}: lin_vel_err_mps {res.get('lin_vel_err_mps')} > "
                     f"{env_ev['pass']['lin_vel_err_mps_max']}")
        if float(res.get("peak_torque_frac", 1e9)) > env_ev["pass"]["peak_torque_frac_max"]:
            N.append(f"eval {corner}: peak_torque_frac {res.get('peak_torque_frac')} > "
                     f"{env_ev['pass']['peak_torque_frac_max']} of the actuator limit")
        if float(res.get("joint_limit_hits_per_min", 1e9)) > env_ev["pass"]["joint_limit_hits_per_min_max"]:
            N.append(f"eval {corner}: joint_limit_hits_per_min {res.get('joint_limit_hits_per_min')} > "
                     f"{env_ev['pass']['joint_limit_hits_per_min_max']}")

    # benchmark (R42)
    if tier in env["benchmark"]["required_for"] and not card.get("benchmark", {}).get(env["benchmark"]["field"]):
        N.append(f"no benchmark.{env['benchmark']['field']} from {env['benchmark']['tool']}")

    return E, N


def check_card_file(path, env=None, contract=None):
    path = Path(path)
    env = env or load_envelope()
    card = json.loads(path.read_text(encoding="utf-8"))
    if contract is None and "creature" in card:
        cp = L.CREATURES / card["creature"] / "contract.json"
        if cp.exists():
            contract = json.loads(cp.read_text(encoding="utf-8"))
    errors, notp, notr = validate_card(card, path.parent, env, contract)
    return card, errors, notp, notr


# ---------------------------------------------------------------- world

def validate_world(world, env, contracts=None):
    E = []
    wl = env["world_limits"]
    fr = world.get("floor_friction")
    fr = [fr, fr] if isinstance(fr, (int, float)) else fr
    if not fr or not (wl["floor_friction"][0] <= float(fr[0]) and float(fr[1]) <= wl["floor_friction"][1]):
        E.append(f"floor_friction {world.get('floor_friction')} outside {wl['floor_friction']}")
    for k in ("terrain_rough_max_m", "terrain_slope_max_deg", "action_latency_ms"):
        if float(world.get(k, 1e9)) > wl[k]:
            E.append(f"{k} {world.get(k)} > {wl[k]}")
    if world.get("condim") not in wl["condim"]:
        E.append(f"condim {world.get('condim')} not in {wl['condim']}")
    if [float(g) for g in world.get("gravity", [])] != wl["gravity"]:
        E.append(f"gravity {world.get('gravity')} != {wl['gravity']}")
    for cid, c in (contracts or {}).items():
        if abs(float(world.get("control_dt", -1)) - float(c["physics"]["control_dt"])) > 1e-9:
            E.append(f"control_dt {world.get('control_dt')} != {cid} contract {c['physics']['control_dt']}")
    return E


def check_world_file(path, env=None):
    env = env or load_envelope()
    world = json.loads(Path(path).read_text(encoding="utf-8"))
    contracts = {}
    for cid in world.get("creatures", []):
        cp = L.CREATURES / cid / "contract.json"
        if not cp.exists():
            return world, [f"creature {cid!r} has no contract (R28)"]
        contracts[cid] = json.loads(cp.read_text(encoding="utf-8"))
    return world, validate_world(world, env, contracts)


# ---------------------------------------------------------------- example + selftest

def example_card(env, contract, tier="release"):
    """A card that passes everything. Also the template a trainer fills in."""
    terms = dict(env["reward"]["reference_weights"])
    corners = {c: {"episodes": 20, "falls_per_min": 0.0, "lin_vel_err_mps": 0.1, "peak_torque_frac": 0.6,
                   "joint_limit_hits_per_min": 0.0} for c in env["eval"]["corners"]}
    card = {
        "card_version": 1,
        "envelope_version": env["envelope_version"],
        "envelope_sha256": envelope_sha256(env),
        "creature": contract["creature"] if "creature" in contract else contract["name"],
        "interface_sha256": contract["hashes"]["interface_sha256"],
        "morphology_sha256": contract["hashes"]["morphology_sha256"],
        "physics_sha256": contract["hashes"]["physics_sha256"],
        "skill": {"name": "walk", "kind": "locomotion",
                  "command_ranges": {"vx": [-0.6, 0.6], "vy": [-0.4, 0.4], "wz": [-0.4, 0.4]}},
        "tier": tier,
        "env": {"episode_s": 20.0,
                "termination": dict(env["env"]["termination"]),
                "reset": {"yaw_rad": [-3.141593, 3.141593], "roll_pitch_max_rad": 0.05,
                          "joint_std_rad": 0.02, "base_pos_std_m": 0.01},
                "command_resample_s": [5.0, 10.0]},
        "randomisation": {k: list(v) for k, v in env["randomisation_min"].items() if not k.startswith("_")},
        "obs_noise_std": {k: v for k, v in env["obs_noise_min_std"].items() if not k.startswith("_")},
        "reward": {"terms": terms},
        "trainer": {"algorithm": "ppo", "seed": 0, "env_steps": 10_000_000, "num_envs": 4096,
                    "hyperparameters": {"gamma": 0.99, "lam": 0.95, "clip": 0.2, "lr": 3e-4},
                    "network": {"kind": "mlp", "export": "deterministic_mean", "hidden": [256, 256, 128]},
                    "hardware": "example", "wall_s": 3600, "mujoco": contract["physics"]["mujoco"]},
        "logs": {"file": env["logs"]["file"]},
        "eval": {"corners": corners},
        "benchmark": {"steps_per_s": 50000},
    }
    if tier == "robot":
        card["robot"] = {
            "calibration": {"file": "RobotControl/configs/sim_calibration.yaml", "calibrated_at": "YYYY-MM-DD",
                            "parity_rms_deg": 1.0, "parity_max_deg": 3.0},
            "sim_real_diff": {"max_deg": 3.0, "ticks": 500, "sim_log": None, "real_log": None},
            "deploy_gate": {"app": "PoRobotTrain", "deploy_json": "RobotControl/models/<skill>/deploy.json",
                            "passed": True},
            "bringup": {"gantry": {"passed": True, "date": "YYYY-MM-DD"},
                        "low_gain": {"passed": True, "date": "YYYY-MM-DD"}},
        }
    return card


def write_example_log(dirpath, env, terms):
    cols = env["logs"]["required_columns"] + [env["logs"]["per_term_prefix"] + t for t in terms]
    with open(Path(dirpath) / env["logs"]["file"], "w", newline="", encoding="utf-8") as f:
        csv.writer(f).writerow(cols)


def selftest():
    env = load_envelope()
    contract = json.loads((L.CREATURES / "unitree_r1_a5" / "contract.json").read_text(encoding="utf-8"))
    tmp = Path(tempfile.mkdtemp(prefix="unite-training-selftest-"))
    ok = True

    def run(label, card, expect_errors, expect_portable, with_log=True, expect_robot=None):
        nonlocal ok
        d = tmp / label
        d.mkdir()
        if with_log:
            write_example_log(d, env, card["reward"]["terms"])
        e, n, r = validate_card(card, d, env, contract)
        good = bool(e) == expect_errors and (not n) == expect_portable
        if expect_robot is not None:
            good &= (not r) == expect_robot
        ok &= good
        why = (e or n or r or ["-"])[0]
        print(f"{label:22s} errors={len(e)} not_portable={len(n)} not_robot={len(r)}  "
              f"{'PASS' if good else 'FAIL'}  ({why})")

    run("release_ok", example_card(env, contract), False, True)
    run("dev_tier", example_card(env, contract, tier="dev") | {"trainer": example_card(env, contract)["trainer"]},
        False, False)
    c = example_card(env, contract)
    c["randomisation"]["floor_friction"] = [0.6, 1.0]
    run("narrow_friction", c, True, True)
    c = example_card(env, contract)
    c["reward"]["terms"].pop("upright")
    run("missing_core_term", c, True, True)
    c = example_card(env, contract)
    c["reward"]["terms"]["foot_clearance"] = 0.1
    run("unprefixed_term", c, True, True)
    c = example_card(env, contract)
    c["env"]["termination"]["fall_height_frac"] = 0.4
    run("other_termination", c, True, True)
    c = example_card(env, contract)
    c["interface_sha256"] = "0" * 64
    run("wrong_interface", c, True, True)
    c = example_card(env, contract)
    c["eval"]["corners"]["friction_lo"]["falls_per_min"] = 4.0
    run("eval_fails", c, False, False)
    run("no_log", example_card(env, contract), True, True, with_log=False)
    c = example_card(env, contract)
    c["eval"]["corners"]["motor_weak"]["peak_torque_frac"] = 0.98
    run("torque_saturates", c, False, False)

    # robot tier (R43)
    run("robot_ok", example_card(env, contract, tier="robot"), False, True, expect_robot=True)
    run("release_not_robot", example_card(env, contract), False, True, expect_robot=False)
    c = example_card(env, contract, tier="robot")
    c["robot"]["calibration"]["calibrated_at"] = None
    run("robot_uncalibrated", c, False, True, expect_robot=False)
    c = example_card(env, contract, tier="robot")
    c["robot"]["sim_real_diff"]["max_deg"] = 7.5
    run("robot_sim_real_gap", c, False, True, expect_robot=False)
    c = example_card(env, contract, tier="robot")
    c["robot"]["bringup"] = {"free": {"passed": True}}
    run("robot_skipped_bringup", c, True, True, expect_robot=False)
    go2 = json.loads((L.CREATURES / "go2" / "contract.json").read_text(encoding="utf-8"))
    d = tmp / "robot_no_hardware"
    d.mkdir()
    c = example_card(env, go2, tier="robot")
    write_example_log(d, env, c["reward"]["terms"])
    e, _n, _r = validate_card(c, d, env, go2)
    good = any("hardware block" in x for x in e)
    print(f"{'robot_no_hardware':22s} {'PASS' if good else 'FAIL'}  ({(e or ['-'])[0]})")
    ok &= good

    w_ok = {"app": "x", "floor_friction": [0.6, 1.0], "terrain_rough_max_m": 0.0, "terrain_slope_max_deg": 0.0,
            "action_latency_ms": 10, "condim": 3, "gravity": [0, 0, -9.81], "control_dt": 0.02}
    good = not validate_world(w_ok, env, {"unitree_r1_a5": contract})
    print(f"{'world_ok':22s} {'PASS' if good else 'FAIL'}")
    ok &= good
    bad = validate_world(w_ok | {"floor_friction": 1.6, "condim": 6}, env, {"unitree_r1_a5": contract})
    good = len(bad) == 2
    print(f"{'world_outside':22s} {'PASS' if good else 'FAIL'}  ({len(bad)} problems)")
    ok &= good

    print("SELFTEST", "OK" if ok else "FAILED", f"({tmp})")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("what", nargs="?", choices=["card", "world", "example"])
    ap.add_argument("path", nargs="?")
    ap.add_argument("--creature", help="for 'example': the creature to fill in")
    ap.add_argument("--tier", default="dev", help="for 'example': smoke | dev | release | robot")
    ap.add_argument("--envelope-hash", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()

    env = load_envelope()
    if a.selftest:
        return selftest()
    if a.envelope_hash:
        print(f"envelope v{env['envelope_version']} {envelope_sha256(env)}")
        return 0
    if a.what == "example":
        cid = a.creature or "unitree_r1_a5"
        contract = json.loads((L.CREATURES / cid / "contract.json").read_text(encoding="utf-8"))
        print(json.dumps(example_card(env, contract, tier=a.tier), indent=2))
        return 0
    if not a.what or not a.path:
        ap.error("give 'card <path>', 'world <path>', 'example --creature <id>', --envelope-hash or --selftest")
    if not Path(a.path).exists():
        print(f"ERROR: {a.path} not found", file=sys.stderr)
        return 2

    if a.what == "world":
        _w, errors = check_world_file(a.path, env)
        for e in errors:
            print(f"  - {e}")
        print("WORLD INSIDE ENVELOPE" if not errors else "WORLD OUTSIDE ENVELOPE")
        return 0 if not errors else 1

    card, errors, notp, notr = check_card_file(a.path, env)
    for e in errors:
        print(f"  ERROR {e}")
    for n in notp:
        print(f"  not portable: {n}")
    if card.get("tier") == "robot":
        for r in notr:
            print(f"  not robot-ready: {r}")
    if errors:
        print("CARD BREAKS THE TRAINING STANDARD")
        return 1
    print("CARD OK, PORTABLE" if not notp else "CARD OK, NOT PORTABLE (own app only)")
    if card.get("tier") == "robot":
        print("ROBOT-READY: deploy through the owner app's gate, staged" if not notr else "NOT ROBOT-READY")
    return 0 if not notp and (card.get("tier") != "robot" or not notr) else 3


if __name__ == "__main__":
    sys.exit(main())
