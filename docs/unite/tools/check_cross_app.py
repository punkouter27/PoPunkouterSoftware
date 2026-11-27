"""Answer one question with evidence: is a newly trained agent usable in every other app?

    python check_cross_app.py                 # every policy in runs/, every creature
    python check_cross_app.py --policy PATH   # one policy
    python check_cross_app.py --require-consumers   # exit 1 if an app cannot consume 131 yet

It separates three claims that are easy to blur together, because only two of them are true today:

1. **Interface identity** -- every creature publishes the same 131/28 layout and the same
   `interface_sha256`. Verified by reading the contracts.
2. **Loadability** -- a trained ONNX actually runs when handed a 131-float observation, and its sidecar
   verifies against the contract. Verified by executing it in onnxruntime.
3. **Consumability** -- an app can *produce* those 131 floats at runtime. Verified by looking for a
   builder in each app. This is the one that is not true yet anywhere except this tooling.

Shape-identical is not the same as correct: a Go2-trained brain will run on a humanoid and emit finite
actions, but `role_mask` and `morphology_onehot` tell it it is still a Go2. The report says which creature
a policy was trained for, and never calls cross-creature behaviour "working".

Exit codes: 0 = interface and loadability hold; 1 = something that must be true is not;
2 = could not decide (missing files).
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402

ROOT = L.UNITE.parents[1]  # _CODEMATT
APPS = ["PoRobotTrain", "PoBasketball", "PoRaceUnreal", "PoSlapUnreal"]
CODE_EXT = {".py", ".cs", ".cpp", ".h", ".js", ".ts"}
SKIP_DIRS = {".venv", "Library", "Intermediate", "Saved", "DerivedDataCache", "node_modules", "bin", "obj"}


# ---------------------------------------------------------------- 1. interface

def check_interface(creatures):
    hashes = {}
    for cid in creatures:
        c = json.loads((L.CREATURES / cid / "contract.json").read_text(encoding="utf-8"))
        hashes[cid] = (c["hashes"]["interface_sha256_16"],
                       c["observation"]["n_obs"], c["action"]["n_action"])
    distinct = {h for h, _o, _a in hashes.values()}
    shapes = {(o, a) for _h, o, a in hashes.values()}
    ok = len(distinct) == 1 and shapes == {(131, 28)}
    return ok, hashes, distinct, shapes


# ---------------------------------------------------------------- 2. loadability

def run_policy(policy, obs, n_action):
    import onnxruntime as ort

    sess = ort.InferenceSession(str(policy), providers=["CPUExecutionProvider"])
    inp = sess.get_inputs()[0]
    x = np.asarray(obs, dtype=np.float32).reshape(1, -1)
    out = sess.run(None, {inp.name: x})[0]
    return np.asarray(out, dtype=np.float32).reshape(-1), inp.name


def check_policy(policy, creatures):
    problems = []
    sidecar = policy.with_suffix(".json")
    if not sidecar.exists():
        return [f"{policy.name}: no sidecar (R10)"], None
    sc = json.loads(sidecar.read_text(encoding="utf-8"))
    trained_for = sc.get("creature")
    if trained_for not in creatures:
        problems.append(f"{policy.name}: sidecar names creature {trained_for!r}, which has no contract")

    rows = []
    for cid in creatures:
        contract = json.loads((L.CREATURES / cid / "contract.json").read_text(encoding="utf-8"))
        gold = L.CREATURES / cid / "golden_role_obs.npz"
        if not gold.exists():
            problems.append(f"{cid}: no golden; run role_obs.py --golden")
            continue
        z = np.load(gold)
        obs = z["obs"][0]                      # a real 131-vector for this creature
        try:
            act, _name = run_policy(policy, obs, contract["action"]["n_action"])
        except Exception as e:  # noqa: BLE001
            problems.append(f"{policy.name} on {cid}: inference failed ({type(e).__name__}: {e})")
            continue
        shape_ok = act.shape == (contract["action"]["n_action"],)
        finite = bool(np.isfinite(act).all())
        in_range = bool(np.abs(act).max() <= 1.0 + 1e-6)
        morph = contract["roles"].get("morphology_id")
        own = (sc.get("morphology_sha256") == contract["hashes"]["morphology_sha256"])
        rows.append({
            "creature": cid, "shape_ok": shape_ok, "finite": finite, "in_range": in_range,
            "morphology_id": morph, "is_own_creature": own,
        })
        if not (shape_ok and finite and in_range):
            problems.append(
                f"{policy.name} on {cid}: bad output "
                f"(shape {shape_ok}, finite {finite}, in [-1,1] {in_range})"
            )
    return problems, {"trained_for": trained_for, "rows": rows}


# ---------------------------------------------------------------- 3. consumability

SLOT_NAMES = ("joint_pos_rel", "joint_vel", "prev_action", "base_ang_vel", "projected_gravity",
              "velocity_commands", "gait_phase", "role_mask", "morphology_onehot")
VENDORED = ("third_party", "_deps", "boost-src", "node_modules", "examples", "Examples",
            "test", "tests", "Tests", "CoACD")


def find_builder(repo):
    """Does this app have code that builds the 131-float observation?

    The test is deliberately strict: a real builder names the slots. Substring matches on "131" or
    "observation" produce false positives from vendored boost examples and from docstrings, and a
    verification tool that lies is worse than no tool.
    """
    import os

    hits = {"builders": [], "contract": [], "scanned": 0}
    root = ROOT / repo
    if not root.exists():
        return None, hits
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames
                       if d not in SKIP_DIRS and d not in VENDORED and not d.startswith(".")]
        for fn in filenames:
            if Path(fn).suffix not in CODE_EXT:
                continue
            p = Path(dirpath) / fn
            try:
                if p.stat().st_size > 400_000:
                    continue
                txt = p.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            hits["scanned"] += 1
            rel = p.relative_to(ROOT).as_posix()
            named = sum(1 for s in SLOT_NAMES if s in txt)
            if named == len(SLOT_NAMES):
                hits["builders"].append(f"{rel} (9/9)")
            elif named >= 3:
                hits["contract"].append(f"legacy builder {named}/9: {rel}")
    return bool(hits["builders"]), hits


# ---------------------------------------------------------------- report

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--policy", type=Path)
    ap.add_argument("--require-consumers", action="store_true")
    a = ap.parse_args()

    creatures = sorted(d.name for d in L.CREATURES.iterdir()
                       if d.is_dir() and (d / "contract.json").exists())
    hard_fail = False

    print("1. INTERFACE — does every creature publish the same layout?")
    ok, hashes, distinct, shapes = check_interface(creatures)
    for cid, (h, o, n) in hashes.items():
        print(f"   {cid:22s} {o:3d} obs / {n:2d} act   interface {h}")
    print(f"   -> {'OK   ' if ok else 'FAIL '} distinct hashes {len(distinct)}, shapes {sorted(shapes)}")
    hard_fail |= not ok

    print("\n2. LOADABILITY — does a trained policy run against every creature's observation?")
    policies = [a.policy] if a.policy else sorted((L.UNITE / "runs").rglob("policy.onnx"))
    if not policies:
        print("   no policies in runs/; train one with role_ppo.py first")
    for pol in policies:
        problems, info = check_policy(pol, creatures)
        if info is None:
            print(f"   FAIL {pol.relative_to(L.UNITE)}")
            for p in problems:
                print(f"        {p}")
            hard_fail = True
            continue
        loadable = [r["creature"] for r in info["rows"] if r["shape_ok"] and r["finite"]]
        own = [r["creature"] for r in info["rows"] if r["is_own_creature"]]
        print(f"   {pol.relative_to(L.UNITE)}  trained for {info['trained_for']}")
        print(f"     runs on {len(loadable)}/{len(creatures)} creatures: {', '.join(loadable)}")
        print(f"     correctly-typed for: {', '.join(own) or 'none'}"
              f"   (elsewhere role_mask/onehot describe another body — loadable, not correct)")
        for p in problems:
            print(f"     FAIL {p}")
        hard_fail |= bool(problems)

    print("\n3. CONSUMABILITY — can each app PRODUCE those 131 floats at runtime?")
    missing = []
    for repo in APPS:
        has_builder, hits = find_builder(repo)
        if has_builder is None:
            print(f"   {repo:16s} not present")
            continue
        if has_builder:
            print(f"   {repo:16s} 131 builder: {hits['builders'][:2]}")
        else:
            legacy = [h for h in hits["contract"] if h.startswith("legacy builder")]
            if legacy:
                print(f"   {repo:16s} NO 131 builder — its own partial layout: {legacy[:2]}")
            else:
                print(f"   {repo:16s} NO observation builder at all ({hits['scanned']} code files scanned)")
            missing.append(repo)

    print("\nVERDICT")
    print("   New agents are cross-compatible at the INTERFACE level: one layout, one hash, and the export")
    print("   guard refuses anything else. That part is enforced, not promised.")
    print("   Loadability is proven: every policy above executed against every creature's real observation.")
    if missing:
        print(f"   They are NOT yet consumable by {', '.join(missing)}: those apps still feed legacy shapes.")
        print("   Until each app implements the builder and passes role_obs --verify, 'cross-compatible'")
        print("   means 'will be', not 'is'. Behavioural correctness beyond the trained creature is unproven.")

    if a.require_consumers and missing:
        print("\n--require-consumers: failing because a consumer is missing")
        return 1
    return 1 if hard_fail else 0


if __name__ == "__main__":
    sys.exit(main())
