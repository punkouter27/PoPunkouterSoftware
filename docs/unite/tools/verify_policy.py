"""Write and verify a policy sidecar against a UNITE creature contract.

This is the implementation of RULES.md R10 (sidecar) and R11 (the export guard). Every repo calls
this instead of writing its own check, so "conforms" means one thing everywhere.

    # the export guard: abort if the network does not match the contract (exit 1)
    python verify_policy.py path/to/policy.onnx --creature unitree_r1_a5 --check

    # write or refresh the sidecar beside the onnx after a passing check
    python verify_policy.py path/to/policy.onnx --creature unitree_r1_a5 --write

    # re-verify an existing sidecar (what a runtime does at load time)
    python verify_policy.py path/to/policy.onnx --creature unitree_r1_a5 --verify-sidecar

    # build a synthetic conforming policy, to test the tool itself
    python verify_policy.py --selftest

Exit codes: 0 = OK, 1 = the policy does not conform, 2 = the tool cannot decide (missing file, etc).

Needs `onnx` and `hashlib` only. `onnxruntime` is not required.
"""

import argparse
import hashlib
import json
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402

SIDECAR_SUFFIX = ".json"


# ---------------------------------------------------------------- inspection

def _dims(value_info):
    """Concrete dims with a dynamic batch folded to 1."""
    t = value_info.type.tensor_type
    if not t.HasField("shape"):
        return None
    dims = []
    for d in t.shape.dim:
        if d.HasField("dim_value") and d.dim_value > 0:
            dims.append(int(d.dim_value))
        else:
            dims.append(1)  # dynamic / symbolic
    return dims


def inspect(onnx_path):
    import onnx

    model = onnx.load(str(onnx_path))
    g = model.graph
    init = {i.name for i in g.initializer}

    ins = [_ for _ in g.input if _.name not in init]
    outs = list(g.output)
    if len(ins) != 1:
        raise SystemExit(f"{onnx_path.name}: expected 1 graph input, found {[i.name for i in ins]}")
    if len(outs) != 1:
        raise SystemExit(f"{onnx_path.name}: expected 1 graph output, found {[o.name for o in outs]}")

    in_dims = _dims(ins[0])
    out_dims = _dims(outs[0])
    opset = None
    for o in model.opset_import:
        if o.domain in ("", "ai.onnx"):
            opset = int(o.version)

    return {
        "input_name": ins[0].name,
        "input_dims": in_dims,
        "output_name": outs[0].name,
        "output_dims": out_dims,
        "opset": opset,
        "nodes": [(n.op_type, list(n.input), list(n.output)) for n in g.node],
        "initializer_names": init,
        "n_nodes": len(g.node),
    }


def flat(x):
    return [d for d in x if d != 1] or [1]


def detect_baked_normalisation(info):
    """R9: normalisation must live in the runtime, not the graph.

    Heuristic: a Sub whose operands are the graph input and an initializer, feeding a Mul with an
    initializer, is (x - mean) * inv_std baked into the first layer. That is what an ML-Agents export
    looks like. Reports the chain, does not guess intent.
    """
    init = info["initializer_names"]
    source = {info["input_name"]}
    hits = []
    for op, ins, outs in info["nodes"]:
        if op == "Sub" and info["input_name"] in ins and any(i in init for i in ins):
            hits.append(("Sub", ins, outs))
            source.update(outs)
        elif op == "Mul" and any(i in source for i in ins) and any(i in init for i in ins):
            hits.append(("Mul", ins, outs))
    return hits


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------- contract checks

def contract_path(creature):
    return L.CREATURES / creature / "contract.json"


def load_contract(creature):
    p = contract_path(creature)
    if not p.exists():
        known = sorted(d.name for d in L.CREATURES.iterdir() if d.is_dir() and (d / "contract.json").exists())
        raise SystemExit(f"no contract for {creature!r} at {p}\nknown creatures: {known}\n"
                         f"R28: stop and report the gap; do not invent a contract.")
    return json.loads(p.read_text(encoding="utf-8"))


def check(policy, creature, allow_baked_norm=False):
    """Return (problems, info, contract)."""
    c = load_contract(creature)
    info = inspect(policy)
    problems = []

    want_obs = int(c["observation"]["n_obs"])
    want_act = int(c["action"]["n_action"])

    in_flat = flat(info["input_dims"] or [])
    out_flat = flat(info["output_dims"] or [])

    if len(in_flat) != 1 or in_flat[0] != want_obs:
        problems.append(
            f"input is {info['input_dims']} -> {in_flat}, contract {creature} wants [{want_obs}] "
            f"(n_obs={want_obs}); first differing slot cannot be named from shape alone"
        )
    if len(out_flat) != 1 or out_flat[0] != want_act:
        problems.append(
            f"output is {info['output_dims']} -> {out_flat}, contract {creature} wants [{want_act}] "
            f"(n_action={want_act})"
        )

    baked = detect_baked_normalisation(info)
    if baked and not allow_baked_norm:
        ops = ", ".join(f"{op}->{outs[0]}" for op, _ins, outs in baked[:3])
        problems.append(
            f"R9: normalisation looks baked into the graph ({ops}). Move it to the runtime and declare "
            f"it in contract.observation.normalizer, or pass --allow-baked-norm if this is deliberate."
        )

    return problems, info, c


def sidecar_for(policy, creature, info, contract, extra=None):
    """The sidecar R10 requires. Hashes come from the contract file itself, so they cannot drift."""
    sc = {
        "policy_name": policy.stem,
        "creature": creature,
        "n_obs": int(contract["observation"]["n_obs"]),
        "n_action": int(contract["action"]["n_action"]),
        "opset": info["opset"],
        "input": f"{info['input_name']}{info['input_dims']}",
        "output": f"{info['output_name']}{info['output_dims']}",
        "interface_sha256": contract["hashes"]["interface_sha256"],
        "interface_sha256_16": contract["hashes"]["interface_sha256_16"],
        "morphology_sha256": contract["hashes"]["morphology_sha256"],
        "physics_sha256": contract["hashes"]["physics_sha256"],
        "policy_sha256": sha256_file(policy),
        "mujoco": contract["physics"]["mujoco"],
        "contract_path": f"global/UNITE/creatures/{creature}/contract.json",
        "status": "conforming",
    }
    if extra:
        sc.update(extra)
    return sc


def verify_sidecar(policy, creature, contract, info):
    """What a runtime does at load time (R14). Returns a list of problems."""
    path = policy.with_suffix(SIDECAR_SUFFIX)
    if not path.exists():
        return [f"no sidecar beside {policy.name}; R10 requires one"]
    sc = json.loads(path.read_text(encoding="utf-8"))
    problems = []
    for k in ("interface_sha256", "morphology_sha256", "physics_sha256", "policy_sha256"):
        if k not in sc:
            problems.append(f"sidecar is missing {k}")
    for k in ("interface_sha256", "morphology_sha256", "physics_sha256"):
        if k in sc and sc[k] != contract["hashes"][k]:
            problems.append(
                f"sidecar {k} = {sc[k][:16]}... but contract has {contract['hashes'][k][:16]}... — "
                f"the policy was trained against a different contract"
            )
    actual = sha256_file(policy)
    if sc.get("policy_sha256") != actual:
        problems.append(f"policy_sha256 mismatch: sidecar {str(sc.get('policy_sha256'))[:16]}..., "
                        f"file is {actual[:16]}... — the onnx changed after the sidecar was written")
    if int(sc.get("n_obs", -1)) != int(contract["observation"]["n_obs"]):
        problems.append(f"sidecar n_obs {sc.get('n_obs')} != contract {contract['observation']['n_obs']}")
    if int(sc.get("n_action", -1)) != int(contract["action"]["n_action"]):
        problems.append(f"sidecar n_action {sc.get('n_action')} != contract {contract['action']['n_action']}")
    return problems


# ---------------------------------------------------------------- selftest

def selftest():
    """Build a conforming 131/28 policy and a non-conforming one, then exercise the paths."""
    import numpy as np
    from onnx import TensorProto, helper, numpy_helper
    import onnx

    tmp = Path(tempfile.mkdtemp(prefix="unite-selftest-"))
    want_obs = L.N_ROLES * 3 + 3 + 3 + 3 + 2 + L.N_ROLES + 8
    want_act = L.N_ROLES

    def build(n_obs, n_act, name, baked_norm):
        nodes, inits = [], []
        x = "obs"
        if baked_norm:
            inits.append(numpy_helper.from_array(np.zeros(n_obs, np.float32), "bias"))
            inits.append(numpy_helper.from_array(np.ones(n_obs, np.float32), "scale"))
            nodes.append(helper.make_node("Sub", [x, "bias"], ["centred"]))
            nodes.append(helper.make_node("Mul", ["centred", "scale"], ["scaled"]))
            src = "scaled"
        else:
            src = x
        w = numpy_helper.from_array(np.zeros((n_obs, n_act), np.float32), "W")
        inits.append(w)
        nodes.append(helper.make_node("MatMul", [src, "W"], ["logits"]))
        nodes.append(helper.make_node("Tanh", ["logits"], ["action"]))
        graph = helper.make_graph(
            nodes, name,
            [helper.make_tensor_value_info(x, TensorProto.FLOAT, [1, n_obs])],
            [helper.make_tensor_value_info("action", TensorProto.FLOAT, [1, n_act])],
            inits,
        )
        model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], producer_name="unite-selftest")
        model.ir_version = 10
        p = tmp / f"{name}.onnx"
        onnx.save(model, str(p))
        return p

    good = build(want_obs, want_act, "good", False)
    baked = build(want_obs, want_act, "baked", True)
    wrong = build(125, 29, "wrong", False)

    ok = True

    problems, info, c = check(good, "unitree_r1_a5")
    print(f"good  ({info['input_dims']} -> {info['output_dims']}): "
          f"{'PASS' if not problems else 'FAIL ' + problems[0]}")
    ok &= not problems

    problems, info, c = check(baked, "unitree_r1_a5")
    got = any("R9" in p for p in problems)
    print(f"baked ({info['input_dims']} -> {info['output_dims']}): "
          f"{'correctly flagged R9' if got else 'MISSED the baked normalisation'}")
    ok &= got

    problems, info, c = check(wrong, "unitree_r1_a5")
    got = any("n_obs" in p for p in problems) and any("n_action" in p for p in problems)
    print(f"wrong ({info['input_dims']} -> {info['output_dims']}): "
          f"{'correctly flagged shape' if got else 'MISSED the shape mismatch'}")
    ok &= got

    # sidecar round-trip
    problems, info, c = check(good, "unitree_r1_a5")
    sc = sidecar_for(good, "unitree_r1_a5", info, c)
    good.with_suffix(".json").write_text(json.dumps(sc, indent=2) + "\n", encoding="utf-8")
    vp = verify_sidecar(good, "unitree_r1_a5", c, info)
    print(f"sidecar round-trip: {'PASS' if not vp else 'FAIL ' + vp[0]}")
    ok &= not vp

    sc["interface_sha256"] = "0" * 64
    good.with_suffix(".json").write_text(json.dumps(sc, indent=2) + "\n", encoding="utf-8")
    vp = verify_sidecar(good, "unitree_r1_a5", c, info)
    print(f"corrupt sidecar:    {'correctly refused' if vp else 'MISSED the corrupt hash'}")
    ok &= bool(vp)

    print("SELFTEST", "OK" if ok else "FAILED", f"({tmp})")
    return 0 if ok else 1


# ---------------------------------------------------------------- cli

def main():
    ap = argparse.ArgumentParser(description="Write/verify a policy sidecar against a UNITE contract.")
    ap.add_argument("policy", nargs="?", help="path to the .onnx")
    ap.add_argument("--creature", help="creature id, e.g. unitree_r1_a5")
    ap.add_argument("--check", action="store_true", help="export guard: exit 1 on any mismatch")
    ap.add_argument("--write", action="store_true", help="write/refresh the sidecar after a passing check")
    ap.add_argument("--verify-sidecar", action="store_true", help="re-verify an existing sidecar")
    ap.add_argument("--allow-baked-norm", action="store_true", help="accept normalisation inside the graph")
    ap.add_argument("--selftest", action="store_true", help="test the tool itself and exit")
    a = ap.parse_args()

    if a.selftest:
        return selftest()
    if not a.policy or not a.creature:
        ap.error("--policy and --creature are required (or use --selftest)")

    policy = Path(a.policy)
    if not policy.exists():
        print(f"ERROR: {policy} not found", file=sys.stderr)
        return 2

    problems, info, contract = check(policy, a.creature, a.allow_baked_norm)

    print(f"{policy.name}: {info['input_dims']} -> {info['output_dims']}  opset {info['opset']}  "
          f"{info['n_nodes']} nodes")
    print(f"contract {a.creature}: n_obs {contract['observation']['n_obs']}, "
          f"n_action {contract['action']['n_action']}, interface "
          f"{contract['hashes']['interface_sha256_16']}")

    if problems:
        print("\nDOES NOT CONFORM")
        for p in problems:
            print(f"  - {p}")
        if a.check:
            print("\nexport guard: aborting (exit 1)")
            return 1

    if a.verify_sidecar:
        vp = verify_sidecar(policy, a.creature, contract, info)
        if vp:
            print("\nSIDECAR REFUSED")
            for p in vp:
                print(f"  - {p}")
            return 1
        print("\nsidecar OK")

    if a.write:
        if problems:
            print("\nnot writing a sidecar for a non-conforming policy")
            return 1
        sc = sidecar_for(policy, a.creature, info, contract)
        out = policy.with_suffix(SIDECAR_SUFFIX)
        out.write_text(json.dumps(sc, indent=2) + "\n", encoding="utf-8")
        print(f"\nwrote {out.name}  policy_sha256 {sc['policy_sha256'][:16]}...")

    if not problems and not a.verify_sidecar and not a.write:
        print("\nCONFORMS")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
