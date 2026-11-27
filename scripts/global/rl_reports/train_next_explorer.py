# Vendored from _CODEMATT/GLOBAL/rl_training on 2026-11-27 (GLOBAL moved into this repo).
# Only the path constants were changed; build_rl_reports.py drives it.
"""
Interactive "What Should I Train Next?" Behavior Quality & Urgency Explorer
for GLOBAL/rl_training/rl_training_overview.html.

Maps real behaviors across all 10 RL repos on:
  X = Current Behavior Quality (0-100%)
  Y = Importance to the Game (1-10)
  Urgency Score = round(Importance * (100 - Quality) / 10)
"""
from __future__ import annotations

import html
import json

import sys
from pathlib import Path
_HERE = Path(__file__).resolve().parent
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))
from quick_transfer_actions import build_quick_and_transfer_section


def esc(s) -> str:
    return html.escape(str(s), quote=True)


# Real, grounded behavior audit across all 10 RL repos
# The behaviour list is App_Data/global/config/behaviors.json, shared with the GPU Queue page.
sys.path.insert(0, str(_HERE.parent))
import station_common as _sc  # noqa: E402

BEHAVIORS = [
    {k: v for k, v in item.items() if k != "urgency"}
    for item in _sc.load_json(_sc.CONFIG / "behaviors.json", {}).get("behaviors", [])
]


def classify(item: dict) -> tuple[str, str, int]:
    """Return (quadrant_code, quadrant_label, urgency_score)."""
    imp = item["importance"]
    qual = item["quality"]
    urgency = int(round(imp * (100 - qual) / 10.0))
    if imp >= 7 and qual < 55:
        return ("urgent", "Train Now", urgency)
    if qual >= 80:
        return ("shipped", "Healthy / Shipped", urgency)
    if imp >= 5 and qual >= 45:
        return ("polish", "Polish Next", urgency)
    return ("backlog", "Backlog / Later", urgency)


def build_train_next_section() -> str:
    enriched = []
    for b in BEHAVIORS:
        qcode, qlabel, urg = classify(b)
        e = dict(b)
        e["quadrant"] = qcode
        e["quadrant_label"] = qlabel
        e["urgency"] = urg
        enriched.append(e)
    enriched.sort(key=lambda x: (-x["urgency"], -x["importance"]))

    urgent_items = [x for x in enriched if x["quadrant"] == "urgent"]
    polish_items = [x for x in enriched if x["quadrant"] == "polish"]
    shipped_items = [x for x in enriched if x["quadrant"] == "shipped"]

    # Build interactive 2D Priority Scatter SVG
    # X = Current Quality (0..100), Y = Game Importance (1..10)
    w, h = 780, 420
    pl, pr, pt, pb = 64, 28, 28, 52
    pw, ph = w - pl - pr, h - pt - pb

    def sx(q: float) -> float:
        return pl + (q / 100.0) * pw

    def sy(imp: float) -> float:
        return pt + ph - ((imp - 1.0) / 9.0) * ph

    # Quadrant split lines: Quality = 55%, Importance = 6.5
    qx = sx(55)
    qy = sy(6.5)

    svg = [f'<svg viewBox="0 0 {w} {h}" class="chart-svg" id="priorityScatterSvg">']
    # Quadrant shaded backgrounds
    svg.append(f'<rect x="{pl}" y="{pt}" width="{qx-pl:.1f}" height="{qy-pt:.1f}" fill="rgba(248,113,113,0.11)"/>')
    svg.append(f'<rect x="{qx:.1f}" y="{pt}" width="{pl+pw-qx:.1f}" height="{qy-pt:.1f}" fill="rgba(74,222,128,0.08)"/>')
    svg.append(f'<rect x="{pl}" y="{qy:.1f}" width="{qx-pl:.1f}" height="{pt+ph-qy:.1f}" fill="rgba(96,198,255,0.06)"/>')
    svg.append(f'<rect x="{qx:.1f}" y="{qy:.1f}" width="{pl+pw-qx:.1f}" height="{pt+ph-qy:.1f}" fill="rgba(245,194,66,0.06)"/>')

    # Quadrant labels
    svg.append(f'<text x="{pl+10}" y="{pt+18}" fill="#f87171" font-size="11.5" font-weight="800">🔥 TRAIN NOW (High Impact · Low Quality)</text>')
    svg.append(f'<text x="{pl+pw-10}" y="{pt+18}" text-anchor="end" fill="#4ade80" font-size="11.5" font-weight="800">✅ HEALTHY CORE (Keep Locked)</text>')
    svg.append(f'<text x="{pl+10}" y="{pt+ph-10}" fill="#60c6ff" font-size="11" font-weight="700">⏳ BACKLOG / LATER</text>')
    svg.append(f'<text x="{pl+pw-10}" y="{pt+ph-10}" text-anchor="end" fill="#f5c242" font-size="11" font-weight="700">🛠️ POLISH WHEN FREE</text>')

    # Dashed split lines
    svg.append(f'<line x1="{qx:.1f}" y1="{pt}" x2="{qx:.1f}" y2="{pt+ph}" stroke="#475569" stroke-dasharray="5,4"/>')
    svg.append(f'<line x1="{pl}" y1="{qy:.1f}" x2="{pl+pw}" y2="{qy:.1f}" stroke="#475569" stroke-dasharray="5,4"/>')

    # Grid & axis ticks
    for qv in (0, 25, 50, 75, 100):
        xx = sx(qv)
        svg.append(f'<text x="{xx:.1f}" y="{h-24}" text-anchor="middle" fill="#93a4bc" font-size="11">{qv}%</text>')
    for iv in (2, 4, 6, 8, 10):
        yy = sy(iv)
        svg.append(f'<text x="{pl-10}" y="{yy+4:.1f}" text-anchor="end" fill="#93a4bc" font-size="11">{iv}/10</text>')

    svg.append(f'<text x="{pl+pw/2:.1f}" y="{h-6}" text-anchor="middle" fill="#cbd5e1" font-size="12" font-weight="600">Current Behavior Quality → (0% Missing · 100% Shipped)</text>')
    svg.append(f'<text x="16" y="{pt+ph/2:.1f}" text-anchor="middle" fill="#cbd5e1" font-size="12" font-weight="600" transform="rotate(-90 16 {pt+ph/2:.1f})">Importance to the Game ↑</text>')

    # Slight jitter offsets for overlapping points so every bubble is clickable
    used_coords: dict[tuple[int, int], int] = {}
    col_map = {"urgent": "#f87171", "polish": "#f5c242", "shipped": "#4ade80", "backlog": "#60c6ff"}
    for idx, b in enumerate(enriched):
        key = (b["quality"], b["importance"])
        n_dup = used_coords.get(key, 0)
        used_coords[key] = n_dup + 1
        dx = (n_dup % 2) * 14 - (7 if n_dup > 0 else 0)
        dy = (n_dup // 2) * 14 - (7 if n_dup > 1 else 0)
        cx = sx(b["quality"]) + dx
        cy = sy(b["importance"]) + dy
        rad = 11 if b["quadrant"] == "urgent" else (9 if b["quadrant"] == "polish" else 7.5)
        col = col_map[b["quadrant"]]
        short_lbl = b["repo"].replace("Po", "").replace("Unreal", "")
        svg.append(
            f'<g class="prio-node" data-id="{esc(b["id"])}" data-repo="{esc(b["repo"])}" data-quad="{esc(b["quadrant"])}" '
            f'data-quick="{1 if b["quick_win"] else 0}" onclick="selectPrioNode(\'{esc(b["id"])}\')" style="cursor:pointer;">'
            f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{rad}" fill="{col}" fill-opacity="0.86" stroke="#0a0f17" stroke-width="2"/>'
            f'<text x="{cx:.1f}" y="{cy - rad - 4:.1f}" text-anchor="middle" fill="#e6edf7" font-size="10.5" font-weight="700">{esc(short_lbl)}</text>'
            f'<title>{esc(b["repo"])}: {esc(b["behavior"])} | Importance {b["importance"]}/10 | Quality {b["quality"]}% | Urgency {b["urgency"]}</title>'
            f'</g>'
        )
    svg.append("</svg>")

    # Ranked Priority Queue Rows
    queue_rows = []
    for rank, b in enumerate(enriched, start=1):
        qw_badge = '<span class="qw-pill">⚡ Quick GPU Win</span>' if b["quick_win"] else ""
        queue_rows.append(f"""
        <tr class="prio-row" data-id="{esc(b['id'])}" data-repo="{esc(b['repo'])}" data-quad="{esc(b['quadrant'])}"
            data-quick="{1 if b['quick_win'] else 0}" onclick="selectPrioNode('{esc(b['id'])}')" style="cursor:pointer;">
          <td><strong>#{rank}</strong></td>
          <td><span class="urg-score urg-{b['quadrant']}">{b['urgency']}</span></td>
          <td><strong>{esc(b['repo'])}</strong><div class="sub">{esc(b['behavior'])}</div></td>
          <td><span class="q-pill q-{b['quadrant']}">{esc(b['quadrant_label'])}</span> {qw_badge}</td>
          <td><strong>{b['importance']}/10</strong></td>
          <td>
            <div class="qual-bar"><span style="width:{b['quality']}%;background:{col_map[b['quadrant']]}"></span></div>
            <span class="sub">{b['quality']}%</span>
          </td>
          <td><code>{esc(b['est_time'])}</code></td>
          <td class="sub">{esc(b['next_action'])}</td>
        </tr>""")

    # Top 3 Urgent Summary Cards
    top3_cards = []
    for i, b in enumerate(urgent_items[:3], start=1):
        top3_cards.append(f"""
        <div class="top3-card" onclick="selectPrioNode('{esc(b['id'])}')" style="cursor:pointer;">
          <div class="top3-head">
            <span class="top3-rank">#{i} MOST URGENT</span>
            <span class="urg-score urg-urgent">Urgency {b['urgency']}</span>
          </div>
          <h4>{esc(b['repo'])} — {esc(b['behavior'])}</h4>
          <p><strong>Game Impact ({b['importance']}/10):</strong> {esc(b['game_role'])}</p>
          <p><strong>Quality Today ({b['quality']}%):</strong> {esc(b['current_state'])}</p>
          <div class="top3-foot"><strong>Next Run:</strong> {esc(b['next_action'])} · <code>{esc(b['est_time'])}</code></div>
        </div>""")

    # Repo filter options
    repo_opts = "".join(f'<option value="{esc(r)}">{esc(r)}</option>' for r in sorted({b["repo"] for b in enriched}))
    data_json = json.dumps({b["id"]: b for b in enriched})

    return f"""
  <section id="trainNextSection" class="tn-section">
    <div class="tn-banner">
      <div class="eyebrow" style="color:#f87171;background:rgba(248,113,113,0.13);border-color:rgba(248,113,113,0.4);">Training Triage &amp; Decision Center · {len(urgent_items)} Urgent · {len(polish_items)} Polish · {len(shipped_items)} Healthy</div>
      <h2 style="margin:0 0 6px;border:none;padding:0;">🔥 What Should I Train Next? — Behavior Quality vs. Game Importance</h2>
      <p class="subtitle" style="max-width:920px;">
        Every major behavior across your 10 games is scored by <strong>how important it is to the game (1–10)</strong> and <strong>how good it is today (0–100%)</strong>.
        Focus your GPU hours in the <strong style="color:#f87171;">top-left red zone</strong>—and skip retraining the <strong style="color:#4ade80;">top-right green zone</strong> that already works.
      </p>
    </div>

    <div class="top3-grid">
      {"".join(top3_cards)}
    </div>

    {build_quick_and_transfer_section()}

    <div class="tn-controls">
      <div class="toolbar-group">
        <button class="tn-btn active" onclick="filterPrio('all', this)">All Behaviors ({len(enriched)})</button>
        <button class="tn-btn" onclick="filterPrio('urgent', this)">🔥 Train Now ({len(urgent_items)})</button>
        <button class="tn-btn" onclick="filterPrio('quick', this)">⚡ Fast GPU Quick Wins</button>
        <button class="tn-btn" onclick="filterPrio('polish', this)">🛠️ Polish Next ({len(polish_items)})</button>
        <button class="tn-btn" onclick="filterPrio('shipped', this)">✅ Healthy Core ({len(shipped_items)})</button>
      </div>
      <div class="toolbar-group">
        <label style="font-size:12px;color:var(--muted);">Filter Repo:</label>
        <select id="prioRepoSelect" class="tn-select" onchange="applyPrioFilters()">
          <option value="all">All 10 Repos</option>
          {repo_opts}
        </select>
      </div>
    </div>

    <div class="tn-split">
      <div class="pe-card">
        <h3 style="margin:0 0 4px;font-size:16px;">Interactive Priority Map (Click Any Bubble to Inspect)</h3>
        <p class="pe-sub" style="margin-bottom:10px;">Top-left = highest urgency to train next. Larger red dots = biggest blockers to playable fun.</p>
        {"".join(svg)}
      </div>
      <div class="pe-card tn-inspector" id="prioInspector">
        <div class="tier-label">Selected Behavior Deep-Dive</div>
        <h3 id="piTitle" style="margin:0 0 6px;">{esc(enriched[0]['repo'])} — {esc(enriched[0]['behavior'])}</h3>
        <div class="pi-metrics">
          <div><span>Urgency Score</span><strong id="piUrgency" style="color:#f87171;">{enriched[0]['urgency']} / 100</strong></div>
          <div><span>Game Importance</span><strong id="piImp">{enriched[0]['importance']} / 10</strong></div>
          <div><span>Quality Today</span><strong id="piQual">{enriched[0]['quality']}%</strong></div>
          <div><span>Est. GPU Time</span><strong id="piTime">{esc(enriched[0]['est_time'])}</strong></div>
        </div>
        <div class="pi-block"><strong>How It Relates to the Game:</strong><p id="piRole">{esc(enriched[0]['game_role'])}</p></div>
        <div class="pi-block"><strong>Why It's at This Quality Today:</strong><p id="piState">{esc(enriched[0]['current_state'])}</p></div>
        <div class="pi-block pi-action"><strong>Recommended Next Training Step:</strong><p id="piAction">{esc(enriched[0]['next_action'])}</p></div>
      </div>
    </div>

    <div class="table-wrap" style="margin-top:16px;margin-bottom:0;">
      <table class="inner-table" id="prioTable">
        <thead>
          <tr>
            <th>Rank</th><th>Urgency</th><th>Repo &amp; Behavior</th><th>Priority</th>
            <th>Game Imp.</th><th>Quality Today</th><th>Est. GPU Time</th><th>Recommended Next Action</th>
          </tr>
        </thead>
        <tbody>{"".join(queue_rows)}</tbody>
      </table>
    </div>
    <script>window.__PRIO_DATA = {data_json};</script>
  </section>"""


TRAIN_NEXT_CSS = """
  .tn-section { margin: 22px 0 34px; }
  .tn-banner {
    background: linear-gradient(135deg, rgba(248,113,113,0.12), rgba(245,194,66,0.08));
    border: 1px solid rgba(248,113,113,0.35);
    border-radius: 14px;
    padding: 20px 22px;
    margin-bottom: 16px;
  }
  .top3-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(310px, 1fr));
    gap: 14px;
    margin-bottom: 16px;
  }
  .top3-card {
    background: var(--panel);
    border: 1px solid rgba(248,113,113,0.4);
    border-top: 4px solid #f87171;
    border-radius: 12px;
    padding: 15px 17px;
    transition: transform 0.12s ease, border-color 0.12s ease;
  }
  .top3-card:hover { transform: translateY(-2px); border-color: var(--gold); }
  .top3-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; }
  .top3-rank { font-size: 11px; font-weight: 800; letter-spacing: 0.08em; color: #f87171; }
  .top3-card h4 { margin: 0 0 8px; font-size: 15px; color: var(--text); }
  .top3-card p { margin: 0 0 6px; font-size: 12.8px; color: var(--muted); }
  .top3-card p strong { color: var(--text); }
  .top3-foot { margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--border); font-size: 12.2px; color: #cbd5e1; }
  .tn-controls {
    display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 10px;
    background: var(--panel); border: 1px solid var(--border); border-radius: 10px;
    padding: 10px 14px; margin-bottom: 14px;
  }
  button.tn-btn {
    background: var(--panel2); color: var(--muted); border: 1px solid var(--border);
    border-radius: 8px; padding: 6px 11px; font-size: 12px; font-weight: 600; cursor: pointer;
  }
  button.tn-btn.active { background: rgba(248,113,113,0.16); color: #fca5a5; border-color: rgba(248,113,113,0.55); }
  .tn-select {
    background: var(--bg); color: var(--text); border: 1px solid var(--border);
    border-radius: 8px; padding: 6px 10px; font-size: 12.5px;
  }
  .tn-split {
    display: grid; grid-template-columns: 1.45fr 1fr; gap: 16px; align-items: stretch;
  }
  @media (max-width: 960px) { .tn-split { grid-template-columns: 1fr; } }
  .pi-metrics {
    display: grid; grid-template-columns: repeat(2, 1fr); gap: 8px; margin: 12px 0;
  }
  .pi-metrics > div {
    background: var(--panel2); border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
  }
  .pi-metrics span { display: block; font-size: 10.5px; color: var(--muted); text-transform: uppercase; }
  .pi-metrics strong { font-size: 14px; color: var(--text); }
  .pi-block { margin-bottom: 10px; font-size: 13px; }
  .pi-block p { margin: 3px 0 0; color: var(--muted); }
  .pi-action {
    background: rgba(96,198,255,0.09); border: 1px solid rgba(96,198,255,0.35);
    border-radius: 8px; padding: 10px 12px;
  }
  .pi-action p { color: var(--text); }
  .urg-score {
    display: inline-block; font-size: 12px; font-weight: 800; padding: 2px 8px; border-radius: 6px;
  }
  .urg-urgent { background: rgba(248,113,113,0.2); color: #fca5a5; }
  .urg-polish { background: rgba(245,194,66,0.18); color: var(--gold); }
  .urg-shipped { background: rgba(74,222,128,0.16); color: var(--green); }
  .urg-backlog { background: rgba(96,198,255,0.15); color: var(--cyan); }
  .q-pill { display: inline-block; font-size: 11px; font-weight: 700; padding: 2px 7px; border-radius: 999px; }
  .q-urgent { background: rgba(248,113,113,0.16); color: #fca5a5; }
  .q-polish { background: rgba(245,194,66,0.16); color: var(--gold); }
  .q-shipped { background: rgba(74,222,128,0.16); color: var(--green); }
  .q-backlog { background: rgba(96,198,255,0.15); color: var(--cyan); }
  .qw-pill {
    display: inline-block; font-size: 10.5px; font-weight: 700; padding: 2px 6px; border-radius: 5px;
    background: rgba(245,194,66,0.18); color: var(--gold); margin-left: 4px;
  }
  .qual-bar { width: 84px; height: 7px; background: #1e293b; border-radius: 999px; overflow: hidden; margin-bottom: 3px; }
  .qual-bar span { display: block; height: 100%; border-radius: 999px; }
  tr.prio-row.selected { background: rgba(245,194,66,0.11) !important; }
"""

TRAIN_NEXT_JS = """
let activePrioMode = 'all';
function filterPrio(mode, btn) {
  activePrioMode = mode;
  document.querySelectorAll('.tn-btn').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  applyPrioFilters();
}
function applyPrioFilters() {
  const repo = (document.getElementById('prioRepoSelect') || {}).value || 'all';
  const match = (el) => {
    const okRepo = repo === 'all' || el.dataset.repo === repo;
    const okMode = activePrioMode === 'all'
      || (activePrioMode === 'quick' ? el.dataset.quick === '1' : el.dataset.quad === activePrioMode);
    return okRepo && okMode;
  };
  document.querySelectorAll('.prio-node').forEach(n => {
    n.style.opacity = match(n) ? '1' : '0.12';
  });
  document.querySelectorAll('.prio-row').forEach(r => {
    r.style.display = match(r) ? '' : 'none';
  });
}
function selectPrioNode(id) {
  const b = (window.__PRIO_DATA || {})[id];
  if (!b) return;
  document.getElementById('piTitle').textContent = b.repo + ' — ' + b.behavior;
  document.getElementById('piUrgency').textContent = b.urgency + ' / 100 (' + b.quadrant_label + ')';
  document.getElementById('piImp').textContent = b.importance + ' / 10';
  document.getElementById('piQual').textContent = b.quality + '%';
  document.getElementById('piTime').textContent = b.est_time;
  document.getElementById('piRole').textContent = b.game_role;
  document.getElementById('piState').textContent = b.current_state;
  document.getElementById('piAction').textContent = b.next_action;
  document.querySelectorAll('.prio-row').forEach(r => r.classList.toggle('selected', r.dataset.id === id));
}
"""
