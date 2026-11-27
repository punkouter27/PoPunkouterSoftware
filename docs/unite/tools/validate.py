"""Validate every emitted contract and the adapter example against the UNITE schema.

    uv run --no-project --with jsonschema python tools/validate.py

Exit 1 on the first structural problem. Also checks the invariants the schema cannot
express: slot offsets are contiguous, mask length matches the role list, n_obs/n_action
agree with the slot table, and hashes are present and well-formed.
"""

import json
import sys
from pathlib import Path

import jsonschema

UNITE = Path(__file__).resolve().parent.parent
SCHEMA = json.loads((UNITE / "pocontract.schema.json").read_text(encoding="utf-8"))
CONTRACTS = sorted((UNITE / "creatures").glob("*/contract.json"))
ADAPTER = UNITE / "contract-adapter-example.json"


def check_invariants(path, c):
    errs = []
    roles = c["roles"]["canonical_order"]
    mask = c["roles"]["mask"]
    if len(mask) != len(roles):
        errs.append(f"mask has {len(mask)} entries for {len(roles)} roles")

    at = 0
    for slot in c["observation"]["slots"]:
        if slot["start"] != at:
            errs.append(f"slot {slot['name']} starts at {slot['start']}, expected {at}")
        at += slot["size"]
    if at != c["observation"]["n_obs"]:
        errs.append(f"slots cover {at} floats but n_obs is {c['observation']['n_obs']}")
    if c["action"]["n_action"] != len(roles):
        errs.append(f"n_action is {c['action']['n_action']} but the role vector is {len(roles)}")

    mapped = {j["role"] for j in c["morphology"]["joints"] if j.get("role")}
    unknown = mapped - set(roles)
    if unknown:
        errs.append(f"joints declare roles not in canonical_order: {sorted(unknown)}")
    expected_mask = [1 if r in mapped else 0 for r in roles]
    if mask != expected_mask:
        errs.append("mask disagrees with the joints present on the creature")

    for k, v in c["hashes"].items():
        if k.endswith("_16"):
            continue
        if len(v) != 64:
            errs.append(f"{k} is not 64 hex chars")

    # role_map.json is the editable input; the contract must already reflect it
    rm = path.parent / "role_map.json"
    if rm.exists():
        declared = json.loads(rm.read_text(encoding="utf-8"))["roles"]
        for j in c["morphology"]["joints"]:
            want = declared.get(j["name"], j.get("role"))
            if j.get("role") != want:
                errs.append(f"role_map says {j['name']}->{want}, contract says {j.get('role')}")

    if "hardware" in c:
        hw = c["hardware"]
        gaps = [j["name"] for j in c["morphology"]["joints"] if j.get("motor_slot") is None]
        if gaps:
            errs.append(f"hardware creature has joints without a motor slot: {gaps}")
        if hw.get("owner_app") != "PoRobotTrain":
            errs.append("hardware creature should name PoRobotTrain as the owner app")

    print(f"  {path.parent.name}: n_obs={c['observation']['n_obs']} n_action={c['action']['n_action']} "
          f"roles={sum(mask)}/{len(roles)} joints={len(c['morphology']['joints'])} "
          f"hw={'yes' if 'hardware' in c else 'no'} iface={c['hashes'].get('interface_sha256_16', '?')} "
          f"morph={c['hashes']['morphology_sha256'][:8]}")
    return errs


def main():
    failed = False
    for path in CONTRACTS:
        c = json.loads(path.read_text(encoding="utf-8"))
        print(f"validating {path.relative_to(UNITE)}")
        try:
            jsonschema.validate(c, SCHEMA)
        except jsonschema.ValidationError as e:
            print(f"  SCHEMA: {e.message} at {list(e.absolute_path)}")
            failed = True
        for e in check_invariants(path, c):
            print(f"  INVARIANT: {e}")
            failed = True

    if ADAPTER.exists():
        a = json.loads(ADAPTER.read_text(encoding="utf-8"))
        print("validating contract-adapter-example.json")
        n = len(a["canonical_roles"])
        for name, cr in a["creatures"].items():
            if cr["n_obs"] != 3 * n + 47:
                print(f"  INVARIANT: {name} n_obs {cr['n_obs']} != 3*{n}+47")
                failed = True
            if cr["n_action"] != n:
                print(f"  INVARIANT: {name} n_action {cr['n_action']} != {n}")
                failed = True
            known = set(a["canonical_roles"])
            bad = {k for k, v in cr["role_map"].items() if k not in known}
            if bad:
                print(f"  INVARIANT: {name} maps unknown roles {sorted(bad)}")
                failed = True
            if cr["mapped_roles"] + len(cr["masked_roles"]) != n:
                print(f"  INVARIANT: {name} mapped+masked != {n}")
                failed = True
            mapped_actual = sum(1 for v in cr["role_map"].values() if v)
            if mapped_actual != cr["mapped_roles"]:
                print(f"  INVARIANT: {name} role_map has {mapped_actual} non-null entries but mapped_roles says {cr['mapped_roles']}")
                failed = True
        print(f"  {len(a['creatures'])} creatures, {n} roles")

    print("FAILED" if failed else "OK")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
