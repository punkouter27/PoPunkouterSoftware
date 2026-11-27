"""Time-series RL progress recorder.

Every run:
1. Reads App_Data/global/rl_facts.json and scans the real .onnx timestamps of all 10 RL repos.
2. Saves a full timestamped snapshot under App_Data/global/rl/snapshots/<stamp>.json (git-ignored).
3. Appends a compact run record to App_Data/global/rl/rl_timeline.jsonl (append-only, committed).
   The app's /global/rl page renders that history; this script only records it.
"""
from __future__ import annotations

import datetime as dt
import json
import os
from collections import defaultdict
from pathlib import Path

from station_common import OUT
from station_common import WORKSPACE as ROOT

RL_DIR = OUT                                    # rl_facts.json lives at the snapshot root
HIST_DIR = OUT / "rl"
SNAP_DIR = HIST_DIR / "snapshots"
TIMELINE_PATH = HIST_DIR / "rl_timeline.jsonl"

REPOS = [
    ("PoBasketball", "unreal_mujoco", 18, 2.0),
    ("PoBox", "unity_mujoco", 30, 3.0),
    ("PoFootball", "unity_mlagents", 20, 0.1),
    ("PoHillKing", "unity_mujoco", 22, 2.5),
    ("PoOlympic", "unity_mujoco", 40, 4.0),
    ("PoRaceUnreal", "unreal_mujoco", 12, 0.5),
    ("PoRobotTrain", "unreal_mujoco", 5, 0.2),
    ("PoSlapUnreal", "unreal_mujoco", 18, 1.5),
    ("PoSoccer", "unity_mlagents", 14, 0.05),
    ("PoSumo", "unity_mlagents", 10, 0.05),
]
REPO_EST_HOURS = {r: h for r, _, h, _ in REPOS}
REPO_EST_STEPS_B = {r: st for r, _, _, st in REPOS}

SKIP_DIRS = {
    ".git", ".venv", "venv", "Library", "Temp", "Obj", "obj", "bin",
    "Logs", "Intermediate", "DerivedDataCache", "Saved", "Binaries",
    "node_modules", "__pycache__", "ThirdParty", "External", "Packages", "Builds",
}


def scan_onnx_timeline() -> tuple[list[dict], dict[str, list[str]]]:
    """Return (all_onnx_records, per_repo_dates) across all 10 repos."""
    records: list[dict] = []
    per_repo_dates: dict[str, list[str]] = defaultdict(list)
    for repo, stack, _, _ in REPOS:
        rp = ROOT / repo
        if not rp.exists():
            continue
        for dp, dirs, files in os.walk(rp):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not d.startswith(".")]
            for fn in files:
                if not fn.lower().endswith(".onnx"):
                    continue
                p = Path(dp) / fn
                try:
                    st = p.stat()
                except OSError:
                    continue
                mtime = dt.datetime.fromtimestamp(st.st_mtime)
                date_str = mtime.strftime("%Y-%m-%d")
                per_repo_dates[repo].append(date_str)
                records.append({
                    "repo": repo,
                    "stack": stack,
                    "name": fn,
                    "rel": p.relative_to(ROOT).as_posix(),
                    "kb": round(st.st_size / 1024.0, 1),
                    "mtime_iso": mtime.strftime("%Y-%m-%d %H:%M"),
                    "date": date_str,
                    "ts": st.st_mtime,
                })
    records.sort(key=lambda r: -r["ts"])
    return records, per_repo_dates


def record_snapshot(facts: dict, onnx_records: list[dict]) -> tuple[dict, list[dict]]:
    """Save snapshot JSON and append to the timeline; return (current_entry, all_entries)."""
    HIST_DIR.mkdir(parents=True, exist_ok=True)
    SNAP_DIR.mkdir(parents=True, exist_ok=True)

    now = dt.datetime.now()
    ts_id = now.strftime("%Y-%m-%d_%H%M%S")
    ts_label = now.strftime("%Y-%m-%d %H:%M")

    per_repo_summary = {}
    total_shipped = 0
    total_all_onnx = len(onnx_records)
    peak_sps = 0
    sps_vals = []
    cutoff_7d = (now - dt.timedelta(days=7)).timestamp()
    recent_7d = sum(1 for r in onnx_records if r["ts"] >= cutoff_7d)

    for repo, stack, est_h, est_b in REPOS:
        f = facts.get(repo, {})
        b = f.get("benchmark") or {}
        shipped = f.get("onnx_shipped_count", 0)
        sps = int(b.get("env_steps_per_s") or 0)
        total_shipped += shipped
        if sps > peak_sps:
            peak_sps = sps
        if sps > 0:
            sps_vals.append(sps)
        repo_recs = [r for r in onnx_records if r["repo"] == repo]
        newest = repo_recs[0]["date"] if repo_recs else "-"
        oldest = repo_recs[-1]["date"] if repo_recs else "-"
        recent_r = sum(1 for r in repo_recs if r["ts"] >= cutoff_7d)
        per_repo_summary[repo] = {
            "stack": stack,
            "onnx_shipped": shipped,
            "onnx_total": len(repo_recs),
            "sps": sps,
            "envs": b.get("num_envs") or 0,
            "step_ms": b.get("step_time_ms") or 0,
            "est_hours": est_h,
            "est_steps_b": est_b,
            "oldest_onnx": oldest,
            "newest_onnx": newest,
            "updated_7d": recent_r,
        }

    snap_file = f"{ts_id}.json"
    full_snap = {
        "timestamp": ts_label,
        "snapshot_file": snap_file,
        "total_shipped_onnx": total_shipped,
        "total_all_onnx": total_all_onnx,
        "updated_7d_onnx": recent_7d,
        "peak_sps": peak_sps,
        "avg_sps": int(round(sum(sps_vals) / len(sps_vals))) if sps_vals else 0,
        "est_total_hours": sum(REPO_EST_HOURS.values()),
        "est_total_steps_b": round(sum(REPO_EST_STEPS_B.values()), 2),
        "repos": per_repo_summary,
    }
    (SNAP_DIR / snap_file).write_text(json.dumps(full_snap, indent=2), encoding="utf-8")

    entries: list[dict] = []
    if TIMELINE_PATH.exists():
        for line in TIMELINE_PATH.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line:
                try:
                    entries.append(json.loads(line))
                except ValueError:
                    pass

    entry = {
        "timestamp": ts_label,
        "snapshot_file": snap_file,
        "total_shipped_onnx": total_shipped,
        "total_all_onnx": total_all_onnx,
        "updated_7d_onnx": recent_7d,
        "peak_sps": peak_sps,
        "avg_sps": full_snap["avg_sps"],
        "est_total_hours": full_snap["est_total_hours"],
        "est_total_steps_b": full_snap["est_total_steps_b"],
        "per_repo_onnx": {r: per_repo_summary[r]["onnx_shipped"] for r, _, _, _ in REPOS},
        "per_repo_sps": {r: per_repo_summary[r]["sps"] for r, _, _, _ in REPOS},
    }
    with TIMELINE_PATH.open("a", encoding="utf-8") as fh:
        fh.write(json.dumps(entry) + "\n")
    entries.append(entry)
    return full_snap, entries


def main() -> None:
    facts = json.loads((RL_DIR / "rl_facts.json").read_text(encoding="utf-8"))
    onnx_records, _ = scan_onnx_timeline()
    snap, entries = record_snapshot(facts, onnx_records)
    print(
        f"Recorded snapshot {snap['snapshot_file']} (total runs={len(entries)}) "
        f"-> {TIMELINE_PATH}"
    )


if __name__ == "__main__":
    main()
