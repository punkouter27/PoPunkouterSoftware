"""One entry point that refreshes every JSON file the app's /global pages read.

The GLOBAL station used to generate static HTML from these crawls. The pages now live in
PoPunkouterSoftware (Blazor, behind Microsoft sign-in), so this script produces data only:

  App_Data/global/fleet.json                  crawl of every repo (collect_fleet.py)
  App_Data/global/history/station_timeline.jsonl
  App_Data/global/lessons.json                (harvest_lessons.py)
  App_Data/global/rl_facts.json               (collect_rl_facts.py)
  App_Data/global/rl/timeline.jsonl           (record_rl_history.py)
  App_Data/global/unite.json                  UNITE prefab catalogue (this file)

Usage (from the repo root):
  python scripts/global/build_global_data.py              # everything
  python scripts/global/build_global_data.py --no-gh      # skip the GitHub CI calls
  python scripts/global/build_global_data.py --only unite # just refresh the UNITE catalogue
  python scripts/global/build_global_data.py --only rl    # facts + history only
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import subprocess
import sys
from pathlib import Path

import station_common as sc

UNITE = sc.REPO / "docs" / "unite"
CREATURES = UNITE / "creatures"


def _run(script: str, *args: str) -> None:
    print(f"\n== {script} {' '.join(args)}")
    subprocess.run([sys.executable, str(sc.HERE / script), *args], cwd=sc.HERE, check=True)


def _sha16(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()[:16]


def _creature(cdir: Path) -> dict:
    contract = sc.load_json(cdir / "contract.json", {})
    role_map = sc.load_json(cdir / "role_map.json", {})
    roles = contract.get("roles", {})
    mapped = [v for v in (role_map.get("roles") or {}).values() if v]
    morph = contract.get("morphology", {})
    physics = contract.get("physics", {})
    generation = contract.get("generated", {})
    meshes = sorted((cdir / "meshes").glob("*")) if (cdir / "meshes").is_dir() else []
    files = {p.name: p.stat().st_size for p in cdir.iterdir() if p.is_file()}
    return {
        "id": cdir.name,
        "kind": contract.get("kind") or role_map.get("kind") or "",
        "morphologyId": roles.get("morphology_id"),
        "joints": len(morph.get("joints") or []),
        "rolesTotal": len(roles.get("canonical_order") or []),
        "rolesMapped": len(mapped),
        "heldOutside": roles.get("held_outside") or [],
        "obs": (contract.get("observation") or {}).get("n_obs"),
        "action": (contract.get("action") or {}).get("n_action"),
        "timestep": physics.get("timestep"),
        "controlDt": physics.get("control_dt"),
        "hardware": bool(contract.get("hardware")),
        "source": generation.get("source") or "",
        "generatedAt": generation.get("written_at") or "",
        "notes": contract.get("notes") or "",
        "interfaceSha": (contract.get("hashes") or {}).get("interface_sha256_16") or "",
        "meshes": len(meshes),
        "files": files,
    }


def build_unite() -> Path:
    """Summarise the UNITE prefabs and the normative rules into one small JSON file."""
    creatures = []
    if CREATURES.is_dir():
        for cdir in sorted(p for p in CREATURES.iterdir() if (p / "contract.json").exists()):
            creatures.append(_creature(cdir))

    rules_path = UNITE / "RULES.md"
    rules = rules_path.read_text(encoding="utf-8") if rules_path.exists() else ""
    documents = []
    for path in sorted(UNITE.rglob("*.md")):
        rel = path.relative_to(sc.REPO).as_posix()
        documents.append({"path": rel, "name": path.name, "bytes": path.stat().st_size})

    payload = {
        "generated": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "rules": {"path": "docs/unite/RULES.md", "sha256": _sha16(rules_path) if rules_path.exists() else "",
                  "markdown": rules},
        "documents": documents,
        "creatures": creatures,
    }
    out = sc.write_json(sc.OUT / "unite.json", payload)
    print(f"  wrote {out.relative_to(sc.REPO)} ({out.stat().st_size // 1024} KB, {len(creatures)} creatures)")
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description="Refresh the GLOBAL data snapshot the app serves.")
    parser.add_argument("--only", choices=["fleet", "lessons", "rl", "unite"], help="run a single step")
    parser.add_argument("--no-gh", action="store_true", help="skip GitHub CI lookups (offline / rate limited)")
    args = parser.parse_args()

    sc.OUT.mkdir(parents=True, exist_ok=True)
    only = args.only

    if only in (None, "fleet"):
        _run("collect_fleet.py", *(["--no-gh"] if args.no_gh else []))
    if only in (None, "lessons"):
        _run("harvest_lessons.py")
    if only in (None, "rl"):
        _run("collect_rl_facts.py")
        _run("record_rl_history.py")
    if only in (None, "unite"):
        build_unite()

    print("\nDone. Restart the app (or reload /global) to see the new snapshot.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
