"""One entry point that refreshes every JSON file the app's /global pages read.

The GLOBAL station used to generate static HTML from these crawls. The pages now live in
PoPunkouterSoftware (Blazor, behind Microsoft sign-in), so this script produces data only:

  App_Data/global/fleet.json                  crawl of every repo (collect_fleet.py)
  App_Data/global/history/station_timeline.jsonl
  App_Data/global/lessons.json                (harvest_lessons.py)
  App_Data/global/rl_facts.json               (collect_rl_facts.py)
  App_Data/global/rl/rl_timeline.jsonl          (record_rl_history.py)
  App_Data/global/unite.json                  UNITE prefab catalogue (this file)
  App_Data/global/queue.json                  training jobs + missing requirements (this file)
  App_Data/global/standards.json              docs/global-standards prose (this file)
  App_Data/global/reports/rl-*.html           GLOBAL's RL reports (build_rl_reports.py)

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


def _read_json_bom(path: Path, default):
    import json

    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return default


def _past_runs(limit: int = 10) -> list[dict]:
    """The newest overnight queue runs (run_gpu_queue.ps1 writes them to scripts/global/.local/runs)."""
    import json

    root = sc.HERE / ".local" / "runs"
    runs = sorted((p for p in root.iterdir() if p.is_dir()), reverse=True)[:limit] if root.is_dir() else []
    out = []
    for run in runs:
        # run_gpu_queue.ps1 writes these with Windows PowerShell's UTF-8 BOM.
        summary = _read_json_bom(run / "summary.json", None)
        planned = _read_json_bom(run / "queue.json", {})
        try:
            log = (run / "queue.log").read_text(encoding="utf-8-sig", errors="replace").splitlines()[-12:]
        except OSError:
            log = []
        if summary:
            jobs = [{"id": j.get("id"), "repo": j.get("repo"), "outcome": j.get("outcome"),
                     "minutes": int(j.get("minutes") or 0), "hours": float(j.get("hours") or 0),
                     "new_onnx": list(j.get("new_onnx") or [])} for j in summary.get("jobs", [])]
        else:
            jobs = [{"id": j.get("id"), "repo": j.get("repo"), "outcome": "no summary", "minutes": 0,
                     "hours": float(j.get("hours") or 0), "new_onnx": []} for j in planned.get("jobs", [])]
        out.append({"stamp": run.name, "queue": (summary or {}).get("queue") or planned.get("name"),
                    "has_summary": bool(summary), "jobs": jobs, "log": log})
    return json.loads(json.dumps(out))


def build_queue() -> Path:
    """Write queue.json: the training jobs plus which `requires` paths are missing right now."""
    import json

    source = sc.CONFIG / "train_jobs.json"
    jobs = json.loads(source.read_text(encoding="utf-8")).get("jobs", []) if source.exists() else []
    for job in jobs:
        job["missing"] = [p for p in job.get("requires", []) if not (sc.WORKSPACE / p).exists()]
    cloud = sc.load_json(sc.REPO / "scripts" / "gcp" / "cloud_jobs.json", {}).get("jobs", [])
    cloud_jobs = [{key: job.get(key) for key in ("id", "source", "cwd", "command", "hours")} for job in cloud]
    behaviors = sc.load_json(sc.CONFIG / "behaviors.json", {}).get("behaviors", [])
    for item in behaviors:
        item["urgency"] = round((item.get("importance") or 0) * (100 - (item.get("quality") or 0)) / 10)
    behaviors.sort(key=lambda item: -item["urgency"])
    out = sc.OUT / "queue.json"
    payload = {"generated": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds"), "jobs": jobs,
               "cloud_jobs": cloud_jobs, "behaviors": behaviors, "past_runs": _past_runs()}
    out.write_text(json.dumps(payload, indent=1) + "\n", encoding="utf-8")
    blocked = sum(1 for job in jobs if job["missing"])
    print(f"queue: {len(jobs)} jobs, {blocked} blocked -> {out}")
    return out


def build_standards() -> Path:
    """Copy the standards prose (docs/global-standards/**/*.md) into standards.json."""
    import json

    root = sc.REPO / "docs" / "global-standards"
    groups = {"blazor": "Blazor apps", "rl": "RL training"}
    documents = []
    for path in sorted(root.rglob("*.md")) if root.is_dir() else []:
        text = path.read_text(encoding="utf-8")
        heading = next((line[2:].strip() for line in text.splitlines() if line.startswith("# ")), path.stem)
        documents.append({
            "path": path.relative_to(sc.REPO).as_posix(),
            "title": heading,
            "group": groups.get(path.parent.name, path.parent.name),
            "markdown": text,
        })
    out = sc.OUT / "standards.json"
    generated = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
    payload = {"generated": generated, "documents": documents}
    out.write_text(json.dumps(payload, indent=1) + "\n", encoding="utf-8")
    print(f"standards: {len(documents)} documents -> {out}")
    return out


def build_benchmark() -> Path | None:
    """Copy the newest run_benchmarks.ps1 summary into benchmark_last.json (the hub's "Latest benchmark attempt")."""
    import json

    root = sc.HERE / ".local" / "benchmarks"
    runs = sorted((p for p in root.iterdir() if p.is_dir()), key=lambda p: p.name) if root.is_dir() else []
    run = next((p for p in reversed(runs) if (p / "summary.json").exists()), None)
    if run is None:
        print("benchmark: no recorded attempt yet")
        return None
    try:
        raw = json.loads((run / "summary.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        print(f"benchmark: could not read {run.name}/summary.json: {error}")
        return None
    items = raw if isinstance(raw, list) else [raw] if isinstance(raw, dict) else []
    results = []
    for item in items:
        if not isinstance(item, dict):
            continue
        detail = item.get("Detail")
        results.append({"repo": str(item.get("Repo") or "Unknown"), "result": str(item.get("Result") or "unknown"),
                        "seconds": int(item.get("Seconds") or 0),
                        "detail": detail if isinstance(detail, str) else ""})
    stamp = run.name
    try:
        stamp = dt.datetime.strptime(run.name, "%Y-%m-%d_%H%M").strftime("%Y-%m-%d %H:%M")
    except ValueError:
        pass
    out = sc.OUT / "benchmark_last.json"
    out.write_text(json.dumps({"stamp": stamp, "results": results}, indent=1) + "\n", encoding="utf-8")
    print(f"benchmark: {run.name}, {len(results)} result(s) -> {out}")
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description="Refresh the GLOBAL data snapshot the app serves.")
    steps = ["fleet", "lessons", "rl", "unite", "queue", "standards", "benchmark", "reports"]
    parser.add_argument("--only", choices=steps, help="run a single step")
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
    if only in (None, "queue"):
        build_queue()
    if only in (None, "standards"):
        build_standards()
    if only in (None, "benchmark"):
        build_benchmark()
    if only in (None, "reports"):
        _run("build_rl_reports.py")

    print("\nDone. Restart the app (or reload /global) to see the new snapshot.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
