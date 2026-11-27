"""
Time-Series RL Progress Tracker & Dashboard Generator.

Every time this script runs (via GLOBAL/update_all.ps1), it:
1. Reads GLOBAL/rl/rl_facts.json + scans real .onnx timestamps and benchmark/history.json across all 10 RL repos.
2. Saves a full timestamped snapshot in GLOBAL/history/snapshots/<YYYY-MM-DD_HHMMSS>.json.
3. Appends a compact run record to GLOBAL/history/timeline.jsonl (append-only history).
4. Generates GLOBAL/history/rl_progress_over_time.html with interactive SVG graphs showing
   brains/behaviors learned over time, training speed improvements, run-over-run deltas, and recent training activity.
"""
from __future__ import annotations

import datetime as dt
import html
import json
import os
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
GLOBAL_DIR = ROOT / "GLOBAL"
RL_DIR = GLOBAL_DIR / "rl_training"
HIST_DIR = RL_DIR / "history"
SNAP_DIR = HIST_DIR / "snapshots"
TIMELINE_PATH = HIST_DIR / "timeline.jsonl"

REPOS = [
    ("PoBasketball", "unreal_mujoco", 18, 2.0),
    ("PoBox",        "unity_mujoco",  30, 3.0),
    ("PoFootball",   "unity_mlagents", 20, 0.1),
    ("PoHillKing",   "unity_mujoco",  22, 2.5),
    ("PoOlympic",    "unity_mujoco",  40, 4.0),
    ("PoRaceUnreal", "unreal_mujoco", 12, 0.5),
    ("PoRobotTrain", "unreal_mujoco",  5, 0.2),
    ("PoSlapUnreal", "unreal_mujoco", 18, 1.5),
    ("PoSoccer",     "unity_mlagents", 14, 0.05),
    ("PoSumo",       "unity_mlagents", 10, 0.05),
]
REPO_STACK = {r: s for r, s, _, _ in REPOS}
REPO_EST_HOURS = {r: h for r, _, h, _ in REPOS}
REPO_EST_STEPS_B = {r: st for r, _, _, st in REPOS}

SKIP_DIRS = {
    ".git", ".venv", "venv", "Library", "Temp", "Obj", "obj", "bin",
    "Logs", "Intermediate", "DerivedDataCache", "Saved", "Binaries",
    "node_modules", "__pycache__", "ThirdParty", "External", "Packages", "Builds",
}


def esc(s) -> str:
    return html.escape(str(s), quote=True)


def scan_onnx_timeline() -> tuple[list[dict], dict[str, list[str]]]:
    """Return (all_onnx_records, per_repo_dates) across all 10 repos."""
    records = []
    per_repo_dates: dict[str, list[str]] = defaultdict(list)
    for repo, stack, _, _ in REPOS:
        rp = ROOT / repo
        if not rp.exists():
            continue
        for dp, dirs, files in os.walk(rp):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not d.startswith(".venv") and not d.startswith(".")]
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


def load_benchmark_histories() -> dict[str, list[dict]]:
    """Read full benchmark/history.json series for every repo that has one."""
    out: dict[str, list[dict]] = {}
    for repo, _, _, _ in REPOS:
        bh = ROOT / repo / "benchmark" / "history.json"
        if not bh.exists():
            continue
        try:
            raw = json.loads(bh.read_text(encoding="utf-8"))
        except Exception:
            continue
        runs = raw if isinstance(raw, list) else raw.get("runs", raw.get("history", []))
        clean = []
        for idx, item in enumerate(runs):
            if not isinstance(item, dict):
                continue
            sps = item.get("env_steps_per_s") or item.get("steps_per_sec")
            ms = item.get("step_time_ms")
            when = item.get("when") or item.get("timestamp") or f"run-{idx+1}"
            if sps or ms:
                clean.append({
                    "idx": idx + 1,
                    "when": str(when)[:16].replace("T", " "),
                    "sps": int(round(sps)) if sps else 0,
                    "step_ms": round(float(ms), 1) if ms else 0.0,
                    "envs": item.get("num_envs") or 0,
                    "ok": item.get("ok", True),
                    "note": item.get("note") or item.get("trainer") or "",
                })
        if clean:
            out[repo] = clean
    return out


def record_snapshot(facts: dict, onnx_records: list[dict]) -> tuple[dict, list[dict]]:
    """Save snapshot JSON and append to timeline.jsonl; return (current_entry, all_entries)."""
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

    # Load existing timeline
    entries: list[dict] = []
    if TIMELINE_PATH.exists():
        for line in TIMELINE_PATH.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line:
                try:
                    entries.append(json.loads(line))
                except Exception:
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


def make_cumulative_brains_svg(onnx_records: list[dict]) -> str:
    """Line chart of cumulative .onnx brains learned over time (daily dates, Fleet + 3 Stacks)."""
    dates = sorted({r["date"] for r in onnx_records})
    if not dates:
        return "<p style='color:var(--muted)'>No ONNX timestamps found yet.</p>"
    if len(dates) == 1:
        dates = [dates[0], dates[0]]

    series_defs = [
        ("Total Fleet", None, "#f5c242", 3.0),
        ("Unity + MuJoCo", "unity_mujoco", "#4ade80", 2.2),
        ("Unreal + MuJoCo", "unreal_mujoco", "#60c6ff", 2.2),
        ("Unity ML-Agents", "unity_mlagents", "#c084fc", 2.0),
    ]
    w, h = 760, 270
    pl, pr, pt, pb = 54, 24, 20, 44
    pw, ph = w - pl - pr, h - pt - pb

    max_y = max(len(onnx_records), 10)
    # Round up max_y nicely
    step_y = 50 if max_y <= 350 else 100
    top_y = ((max_y // step_y) + 1) * step_y

    svg = [f'<svg viewBox="0 0 {w} {h}" class="chart-svg">']
    # Horizontal grid
    for yv in range(0, top_y + 1, step_y):
        yy = pt + ph - (yv / top_y) * ph
        svg.append(f'<line x1="{pl}" y1="{yy:.1f}" x2="{w-pr}" y2="{yy:.1f}" stroke="#223044" stroke-dasharray="3,3"/>')
        svg.append(f'<text x="{pl-8}" y="{yy+4:.1f}" text-anchor="end" fill="#93a4bc" font-size="11">{yv}</text>')

    # X axis labels (up to 8 evenly spaced)
    n = len(dates)
    step_x = max(1, (n - 1) // 7)
    for i in range(0, n, step_x):
        xx = pl + (i / max(1, n - 1)) * pw
        svg.append(f'<text x="{xx:.1f}" y="{h-16}" text-anchor="middle" fill="#93a4bc" font-size="11">{esc(dates[i][5:])}</text>')
    if (n - 1) % step_x != 0:
        xx = pl + pw
        svg.append(f'<text x="{xx:.1f}" y="{h-16}" text-anchor="middle" fill="#93a4bc" font-size="11">{esc(dates[-1][5:])}</text>')

    # Lines
    for label, stack_filter, col, sw in series_defs:
        pts = []
        for i, d in enumerate(dates):
            cnt = sum(
                1 for r in onnx_records
                if r["date"] <= d and (stack_filter is None or r["stack"] == stack_filter)
            )
            xx = pl + (i / max(1, n - 1)) * pw
            yy = pt + ph - (cnt / top_y) * ph
            pts.append((xx, yy, cnt, d))
        poly = " ".join(f"{x:.1f},{y:.1f}" for x, y, _, _ in pts)
        svg.append(f'<polyline fill="none" stroke="{col}" stroke-width="{sw}" points="{poly}"/>')
        for x, y, cnt, d in pts:
            svg.append(
                f'<circle cx="{x:.1f}" cy="{y:.1f}" r="3.5" fill="{col}">'
                f'<title>{esc(label)} on {esc(d)}: {cnt} brains</title></circle>'
            )
    svg.append("</svg>")
    legend = "".join(
        f'<span><i class="lg-dot" style="background:{c}"></i>{esc(l)}</span>'
        for l, _, c, _ in series_defs
    )
    return f'<div class="lg-row">{legend}</div>{"".join(svg)}'


def make_pobox_speed_svg(bench_histories: dict[str, list[dict]]) -> str:
    """Line chart of real benchmark/history.json training speed progression (PoBox 17 runs)."""
    runs = bench_histories.get("PoBox", [])
    if not runs:
        return "<p style='color:var(--muted)'>No multi-run benchmark history found yet.</p>"

    w, h = 760, 270
    pl, pr, pt, pb = 62, 24, 22, 44
    pw, ph = w - pl - pr, h - pt - pb
    max_sps = max((r["sps"] for r in runs), default=1000)
    top_y = int(((max_sps // 2000) + 1) * 2000)

    svg = [f'<svg viewBox="0 0 {w} {h}" class="chart-svg">']
    for frac in (0.0, 0.25, 0.5, 0.75, 1.0):
        yv = int(top_y * frac)
        yy = pt + ph - frac * ph
        svg.append(f'<line x1="{pl}" y1="{yy:.1f}" x2="{w-pr}" y2="{yy:.1f}" stroke="#223044" stroke-dasharray="3,3"/>')
        svg.append(f'<text x="{pl-8}" y="{yy+4:.1f}" text-anchor="end" fill="#93a4bc" font-size="11">{yv:,}</text>')

    n = len(runs)
    pts = []
    for i, r in enumerate(runs):
        xx = pl + (i / max(1, n - 1)) * pw
        yy = pt + ph - (r["sps"] / top_y) * ph
        pts.append((xx, yy, r))
        if i % max(1, n // 6) == 0 or i == n - 1:
            svg.append(f'<text x="{xx:.1f}" y="{h-16}" text-anchor="middle" fill="#93a4bc" font-size="10.5">Run {r["idx"]}</text>')

    # Area fill under curve
    area_pts = f"{pl},{pt+ph} " + " ".join(f"{x:.1f},{y:.1f}" for x, y, _ in pts) + f" {pts[-1][0]:.1f},{pt+ph}"
    svg.append(f'<polygon points="{area_pts}" fill="rgba(96,198,255,0.14)"/>')
    poly = " ".join(f"{x:.1f},{y:.1f}" for x, y, _ in pts)
    svg.append(f'<polyline fill="none" stroke="#60c6ff" stroke-width="2.6" points="{poly}"/>')
    for x, y, r in pts:
        svg.append(
            f'<circle cx="{x:.1f}" cy="{y:.1f}" r="4" fill="#60c6ff" stroke="#0a0f17" stroke-width="1.2">'
            f'<title>Run #{r["idx"]} ({esc(r["when"])}): {r["sps"]:,} env-steps/s · {r["step_ms"]} ms/step ({r["envs"]} envs)</title></circle>'
        )
    svg.append("</svg>")
    return "".join(svg)


def make_run_snapshots_svg(entries: list[dict]) -> str:
    """Bar + trend chart across recorded timeline.jsonl runs (grows with every update_all.ps1 run)."""
    w, h = 760, 230
    pl, pr, pt, pb = 54, 24, 24, 46
    pw, ph = w - pl - pr, h - pt - pb
    show = entries[-20:]  # last 20 snapshots
    max_b = max((e["total_shipped_onnx"] for e in show), default=100)
    top_y = ((max_b // 50) + 1) * 50

    svg = [f'<svg viewBox="0 0 {w} {h}" class="chart-svg">']
    for frac in (0.0, 0.5, 1.0):
        yv = int(top_y * frac)
        yy = pt + ph - frac * ph
        svg.append(f'<line x1="{pl}" y1="{yy:.1f}" x2="{w-pr}" y2="{yy:.1f}" stroke="#223044" stroke-dasharray="3,3"/>')
        svg.append(f'<text x="{pl-8}" y="{yy+4:.1f}" text-anchor="end" fill="#93a4bc" font-size="11">{yv}</text>')

    n = len(show)
    slot = pw / max(1, n)
    bw = min(46, max(12, slot * 0.55))
    for i, e in enumerate(show):
        cx = pl + slot * (i + 0.5)
        bh = (e["total_shipped_onnx"] / top_y) * ph
        yy = pt + ph - bh
        svg.append(
            f'<rect x="{cx - bw/2:.1f}" y="{yy:.1f}" width="{bw:.1f}" height="{bh:.1f}" rx="5" fill="#4ade80">'
            f'<title>{esc(e["timestamp"])}: {e["total_shipped_onnx"]} shipped brains · ~{e["est_total_hours"]}h · ~{e["est_total_steps_b"]}B steps</title></rect>'
        )
        svg.append(f'<text x="{cx:.1f}" y="{yy - 6:.1f}" text-anchor="middle" fill="#e6edf7" font-size="11" font-weight="700">{e["total_shipped_onnx"]}</text>')
        lbl = e["timestamp"][5:]  # MM-DD HH:MM
        svg.append(f'<text x="{cx:.1f}" y="{h-16}" text-anchor="middle" fill="#93a4bc" font-size="10">{esc(lbl)}</text>')
    svg.append("</svg>")
    return "".join(svg)


def delta_badge(curr: float, prev: float | None, unit: str = "") -> str:
    if prev is None:
        return '<span class="delta flat">Baseline run</span>'
    diff = curr - prev
    if diff > 0:
        return f'<span class="delta up">+{diff:g}{unit} vs last run</span>'
    if diff < 0:
        return f'<span class="delta down">{diff:g}{unit} vs last run</span>'
    return '<span class="delta flat">No change vs last run</span>'


def build_history_html(snap: dict, entries: list[dict], onnx_records: list[dict], bench_histories: dict[str, list[dict]]) -> str:
    prev = entries[-2] if len(entries) >= 2 else None

    # Per-repo activity table
    repo_rows = []
    for repo, stack, _, _ in REPOS:
        info = snap["repos"][repo]
        prev_onnx = prev["per_repo_onnx"].get(repo) if prev else None
        d_html = delta_badge(info["onnx_shipped"], prev_onnx)
        repo_rows.append(f"""
        <tr>
          <td><strong>{esc(repo)}</strong></td>
          <td><span class="stack-pill {stack}">{esc(stack.replace('_',' '))}</span></td>
          <td><strong>{info['onnx_shipped']}</strong> <span class="sub">({info['onnx_total']} total)</span></td>
          <td>{d_html}</td>
          <td><strong>{info['updated_7d']}</strong> <span class="sub">in last 7d</span></td>
          <td><code>{esc(info['oldest_onnx'])}</code> → <code>{esc(info['newest_onnx'])}</code></td>
          <td><strong>{f"{info['sps']:,} sps" if info['sps'] else '-'}</strong></td>
          <td>~{info['est_hours']} h · ~{info['est_steps_b']}B</td>
        </tr>""")

    # Recently updated .onnx feed (top 25 newest)
    recent_rows = []
    for r in onnx_records[:25]:
        recent_rows.append(f"""
        <tr>
          <td><code>{esc(r['mtime_iso'])}</code></td>
          <td><strong>{esc(r['repo'])}</strong></td>
          <td><code>{esc(r['name'])}</code></td>
          <td>{r['kb']:.1f} KB</td>
          <td class="sub"><code>{esc(r['rel'])}</code></td>
        </tr>""")

    # Snapshot run log table
    log_rows = []
    for i, e in enumerate(reversed(entries)):
        idx = len(entries) - i
        prev_e = entries[idx - 2] if idx >= 2 else None
        d_onnx = delta_badge(e["total_shipped_onnx"], prev_e["total_shipped_onnx"] if prev_e else None)
        log_rows.append(f"""
        <tr>
          <td>#{idx}</td>
          <td><code>{esc(e['timestamp'])}</code></td>
          <td><strong>{e['total_shipped_onnx']}</strong> {d_onnx}</td>
          <td>{e['updated_7d_onnx']}</td>
          <td>{e['peak_sps']:,} sps</td>
          <td>~{e['est_total_hours']} h · ~{e['est_total_steps_b']}B</td>
          <td><a href="snapshots/{esc(e['snapshot_file'])}"><code>{esc(e['snapshot_file'])}</code></a></td>
        </tr>""")

    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>RL Training Progress Over Time — GLOBAL History</title>
<style>
  :root {{
    --bg: #0a0f17; --panel: #111926; --panel2: #172233; --border: #243247;
    --text: #e6edf7; --muted: #93a4bc; --gold: #f5c242; --cyan: #60c6ff;
    --green: #4ade80; --purple: #c084fc; --red: #f87171;
  }}
  * {{ box-sizing: border-box; }}
  body {{ margin: 0; font-family: "Segoe UI", system-ui, sans-serif; background: var(--bg); color: var(--text); line-height: 1.55; }}
  a {{ color: var(--cyan); text-decoration: none; }}
  a:hover {{ text-decoration: underline; }}
  .wrap {{ max-width: 1340px; margin: 0 auto; padding: 26px 22px 72px; }}
  header {{ border-bottom: 1px solid var(--border); padding-bottom: 16px; margin-bottom: 18px; }}
  .eyebrow {{ display: inline-block; font-size: 11.5px; font-weight: 700; letter-spacing: 0.1em; text-transform: uppercase; color: var(--green); background: rgba(74,222,128,0.12); border: 1px solid rgba(74,222,128,0.35); padding: 3px 10px; border-radius: 999px; margin-bottom: 8px; }}
  h1 {{ margin: 0 0 6px; font-size: 28px; }}
  h2 {{ margin: 30px 0 12px; font-size: 19px; border-left: 4px solid var(--green); padding-left: 12px; }}
  .subtitle {{ margin: 0; color: var(--muted); font-size: 14px; max-width: 860px; }}
  .top-nav {{ display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 20px; }}
  .top-nav a {{ background: var(--panel); border: 1px solid var(--border); color: var(--text); padding: 6px 13px; border-radius: 999px; font-size: 12.5px; font-weight: 600; }}
  .top-nav a:hover {{ border-color: var(--gold); color: var(--gold); text-decoration: none; }}
  .kpis {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 14px; margin-bottom: 22px; }}
  .kpi {{ background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 15px 17px; }}
  .kpi .val {{ font-size: 25px; font-weight: 700; color: var(--gold); }}
  .kpi.cyan .val {{ color: var(--cyan); }}
  .kpi.green .val {{ color: var(--green); }}
  .kpi.purple .val {{ color: var(--purple); }}
  .kpi .lbl {{ font-size: 12.5px; color: var(--muted); margin: 3px 0 6px; }}
  .delta {{ display: inline-block; font-size: 11px; font-weight: 700; padding: 2px 7px; border-radius: 6px; }}
  .delta.up {{ background: rgba(74,222,128,0.16); color: var(--green); }}
  .delta.down {{ background: rgba(248,113,113,0.16); color: var(--red); }}
  .delta.flat {{ background: rgba(147,164,188,0.14); color: var(--muted); }}
  .charts-grid {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(480px, 1fr)); gap: 16px; margin-bottom: 24px; }}
  .chart-card {{ background: var(--panel); border: 1px solid var(--border); border-radius: 12px; padding: 16px 18px; }}
  .chart-card.full {{ grid-column: 1 / -1; }}
  .chart-card h3 {{ margin: 0 0 4px; font-size: 15.5px; }}
  .chart-sub {{ margin: 0 0 10px; font-size: 12.5px; color: var(--muted); }}
  .chart-svg {{ width: 100%; height: auto; display: block; }}
  .lg-row {{ display: flex; flex-wrap: wrap; gap: 14px; margin-bottom: 8px; font-size: 12px; color: var(--muted); }}
  .lg-dot {{ display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; vertical-align: -1px; }}
  .table-wrap {{ overflow-x: auto; background: var(--panel); border: 1px solid var(--border); border-radius: 12px; margin-bottom: 24px; }}
  table {{ width: 100%; border-collapse: collapse; font-size: 13px; }}
  th, td {{ padding: 10px 12px; border-bottom: 1px solid var(--border); text-align: left; vertical-align: middle; }}
  th {{ background: #162132; color: var(--text); font-weight: 600; white-space: nowrap; }}
  tr:hover {{ background: rgba(96,198,255,0.04); }}
  .sub {{ font-size: 11.5px; color: var(--muted); }}
  .stack-pill {{ display: inline-block; font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 6px; }}
  .stack-pill.unreal_mujoco {{ background: rgba(96,198,255,0.15); color: var(--cyan); }}
  .stack-pill.unity_mujoco {{ background: rgba(245,194,66,0.15); color: var(--gold); }}
  .stack-pill.unity_mlagents {{ background: rgba(192,132,252,0.16); color: var(--purple); }}
  code {{ font-family: Consolas, monospace; font-size: 12px; background: rgba(255,255,255,0.06); padding: 1.5px 5px; border-radius: 4px; color: #dbeafe; }}
  footer {{ margin-top: 32px; padding-top: 14px; border-top: 1px solid var(--border); color: var(--muted); font-size: 12.5px; }}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <div class="eyebrow">Time-Series Training Log · Auto-Updated Every Run · Last Snapshot {esc(snap['timestamp'])}</div>
    <h1>RL Agent Progress Over Time</h1>
    <p class="subtitle">
      Tracks how your agents grow across every training session: newly learned <code>.onnx</code> skills, training speed gains,
      cumulative hours &amp; simulated steps, and run-by-run deltas. Run <code>GLOBAL/update_all.ps1</code> after any training session to add a new point to every graph.
    </p>
  </header>

  <nav class="top-nav">
    <a href="../../index.html">← Global Hub</a>
    <a href="../rl_training_overview.html">🤖 RL Comparison &amp; Portability</a>
    <a href="#graphs">📈 Progress Graphs</a>
    <a href="#repoProgress">📊 Per-Repo Activity</a>
    <a href="#recentBrains">🧠 Recently Trained Brains</a>
    <a href="#runLog">🗂️ Snapshot Log ({len(entries)} runs)</a>
  </nav>

  <section class="kpis">
    <div class="kpi green">
      <div class="val">{snap['total_shipped_onnx']}</div>
      <div class="lbl">Shipped <code>.onnx</code> Brains ({snap['total_all_onnx']} total on disk)</div>
      {delta_badge(snap['total_shipped_onnx'], prev['total_shipped_onnx'] if prev else None)}
    </div>
    <div class="kpi cyan">
      <div class="val">{snap['updated_7d_onnx']}</div>
      <div class="lbl">Brains Trained or Updated in Last 7 Days</div>
      <span class="delta up">Active training velocity</span>
    </div>
    <div class="kpi">
      <div class="val">{snap['peak_sps']:,} sps</div>
      <div class="lbl">Peak Fleet Training Speed (Avg {snap['avg_sps']:,} sps)</div>
      {delta_badge(snap['peak_sps'], prev['peak_sps'] if prev else None, ' sps')}
    </div>
    <div class="kpi purple">
      <div class="val">~{snap['est_total_hours']} h · ~{snap['est_total_steps_b']}B</div>
      <div class="lbl">Estimated Cumulative Training Time &amp; Steps</div>
      {delta_badge(snap['est_total_hours'], prev['est_total_hours'] if prev else None, ' h')}
    </div>
    <div class="kpi">
      <div class="val">{len(entries)}</div>
      <div class="lbl">Recorded Progress Snapshots in <code>timeline.jsonl</code></div>
      <span class="delta flat">Appends on every update_all.ps1</span>
    </div>
  </section>

  <section class="charts-grid" id="graphs">
    <div class="chart-card">
      <h3>1. Brains &amp; Learned Skills Growing Over Time</h3>
      <p class="chart-sub">Cumulative <code>.onnx</code> brains across your fleet by modification date (hover dots for exact counts).</p>
      {make_cumulative_brains_svg(onnx_records)}
    </div>
    <div class="chart-card">
      <h3>2. Training Speed Improvement Over Time (<code>PoBox</code> 17 Benchmark Runs)</h3>
      <p class="chart-sub">Real <code>benchmark/history.json</code> progression showing how simulator &amp; GPU tuning boosted <code>env-steps/s</code>.</p>
      {make_pobox_speed_svg(bench_histories)}
    </div>
    <div class="chart-card full">
      <h3>3. Run-by-Run Snapshot History (<code>GLOBAL/history/timeline.jsonl</code>)</h3>
      <p class="chart-sub">Each bar is a snapshot recorded by <code>GLOBAL/update_all.ps1</code>. Re-run after overnight training to watch your fleet grow over weeks and months.</p>
      {make_run_snapshots_svg(entries)}
    </div>
  </section>

  <h2 id="repoProgress">Per-Repository Activity &amp; Growth</h2>
  <div class="table-wrap">
    <table>
      <thead>
        <tr>
          <th>Repository</th><th>Stack</th><th>Shipped Brains</th><th>Since Last Run</th>
          <th>Updated (7d)</th><th>Active Brain Dates</th><th>Current Speed</th><th>Est. Cumulative</th>
        </tr>
      </thead>
      <tbody>{"".join(repo_rows)}</tbody>
    </table>
  </div>

  <h2 id="recentBrains">Recently Trained / Updated Brains (Newest 25)</h2>
  <div class="table-wrap">
    <table>
      <thead>
        <tr><th>Last Updated</th><th>Repo</th><th>Brain File</th><th>Size</th><th>Path</th></tr>
      </thead>
      <tbody>{"".join(recent_rows)}</tbody>
    </table>
  </div>

  <h2 id="runLog">Snapshot Run History (<code>GLOBAL/history/timeline.jsonl</code>)</h2>
  <div class="table-wrap">
    <table>
      <thead>
        <tr><th>Run #</th><th>Timestamp</th><th>Shipped Brains</th><th>Updated (7d)</th><th>Peak Speed</th><th>Est. Scale</th><th>Raw JSON Snapshot</th></tr>
      </thead>
      <tbody>{"".join(log_rows)}</tbody>
    </table>
  </div>

  <footer>
    Auto-generated by <code>GLOBAL/rl_training/scripts/record_and_build_history.py</code>. Data stored in <code>GLOBAL/rl_training/history/timeline.jsonl</code> and <code>GLOBAL/rl_training/history/snapshots/</code>.
  </footer>
</div>
</body>
</html>"""


def main() -> None:
    facts = json.loads((RL_DIR / "rl_facts.json").read_text(encoding="utf-8"))
    onnx_records, _ = scan_onnx_timeline()
    bench_histories = load_benchmark_histories()
    snap, entries = record_snapshot(facts, onnx_records)
    out = HIST_DIR / "rl_progress_over_time.html"
    out.write_text(build_history_html(snap, entries, onnx_records, bench_histories), encoding="utf-8")
    print(
        f"Recorded snapshot {snap['snapshot_file']} (total runs={len(entries)}) "
        f"-> Wrote {out} ({out.stat().st_size // 1024} KB)"
    )


if __name__ == "__main__":
    main()
