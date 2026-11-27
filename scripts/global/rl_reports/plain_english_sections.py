# Vendored from _CODEMATT/GLOBAL/rl_training on 2026-11-27 (GLOBAL moved into this repo).
# Only the path constants were changed; build_rl_reports.py drives it.
"""
Plain-English Insights & Cross-Repo Agent Portability module
for the Global RL Training & Agent Comparison Dashboard.

Generates 10 non-technical, interactive visual stats, grids, and SVG diagrams
grounded in real crawled facts from rl_facts.json.
"""
from __future__ import annotations

import html
import json


def esc(s: str) -> str:
    return html.escape(str(s), quote=True)


REPO_IDS = [
    "PoBasketball",
    "PoBox",
    "PoFootball",
    "PoHillKing",
    "PoOlympic",
    "PoRaceUnreal",
    "PoRobotTrain",
    "PoSlapUnreal",
    "PoSoccer",
    "PoSumo",
]

# PoFootball (2D Rigidbody2D players) and PoSoccer (2D-style capsule/vehicle players) have no jointed 3D body:
# they appear only in technical comparisons (speed, brains, workflow), never in body / skill / portability views.
BODY_2D = {"PoFootball", "PoSoccer"}
BODY_REPO_IDS = [r for r in REPO_IDS if r not in BODY_2D]

SHORT_NAME = {
    "PoBasketball": "Basketball",
    "PoBox": "Box",
    "PoFootball": "Football",
    "PoHillKing": "HillKing",
    "PoOlympic": "Olympic",
    "PoRaceUnreal": "RaceUE",
    "PoRobotTrain": "RobotR1",
    "PoSlapUnreal": "SlapUE",
    "PoSoccer": "Soccer",
    "PoSumo": "Sumo",
}

# 1. Cross-repo portability rules (source -> target)
# Codes:
#   self = Same repo
#   plug = Plug & Play / Minor Map (same MuJoCo physics + same body family)
#   adapt = Adapt Senses / Joint Map (both MuJoCo, cross-engine or different joint count)
#   retrain = Keep Idea, Retrain Brain (same ML-Agents family or different creature/task)
#   rebuild = Different Physics World (MuJoCo <-> Unity PhysX/ML-Agents)
UNITY_MJ_23 = {"PoOlympic", "PoHillKing", "PoBox"}
UNREAL_MJ_HUM = {"PoSlapUnreal", "PoBasketball"}
UNREAL_MJ_OTHER = {"PoRaceUnreal", "PoRobotTrain"}
ALL_MJ = UNITY_MJ_23 | UNREAL_MJ_HUM | UNREAL_MJ_OTHER
ML_AGENTS = {"PoFootball", "PoSoccer", "PoSumo"}


def portability_cell(src: str, dst: str) -> tuple[str, str, str]:
    """Return (code, short_badge, plain_english_reason)."""
    if src == dst:
        return ("self", "—", f"{src} is already home to these agents.")

    # Same Unity MuJoCo 23-DoF humanoid family
    if src in UNITY_MJ_23 and dst in UNITY_MJ_23:
        return (
            "plug",
            "Easy",
            f"Both {src} and {dst} run Unity 6 + MuJoCo on the same 23-joint humanoid family. "
            f"Locomotion, balance, and get-up .onnx brains can be dropped in with minimal observation mapping.",
        )

    # Same Unreal MuJoCo humanoid arena family
    if src in UNREAL_MJ_HUM and dst in UNREAL_MJ_HUM:
        return (
            "plug",
            "Easy",
            f"Both {src} and {dst} run Unreal 5.8 + URLab MuJoCo + NNE ORT. "
            f"Humanoid balance/footwork brains transfer cleanly once the observation vector matches.",
        )

    # MuJoCo <-> MuJoCo (Unity <-> Unreal or different joint count like 29-DoF / R1 24-DoF / Go2)
    if src in ALL_MJ and dst in ALL_MJ:
        if src == "PoRaceUnreal" or dst == "PoRaceUnreal":
            return (
                "adapt",
                "Adapt",
                f"Both use MuJoCo + .onnx, so the physics feel matches! Matt (29 joints) or Go2 (12 joints) "
                f"from PoRaceUnreal can move to {dst if src=='PoRaceUnreal' else src} by bringing its MJCF body file and matching the sense inputs.",
            )
        if src == "PoRobotTrain" or dst == "PoRobotTrain":
            return (
                "adapt",
                "Adapt",
                f"Both use MuJoCo + .onnx. PoRobotTrain uses the Unitree R1 body (24 joints, hardware-safe head/upper-body caps). "
                f"You can port the R1 body + safety wrapper directly, or remap head/upper-body skills.",
            )
        return (
            "adapt",
            "Adapt",
            f"Unity MuJoCo ↔ Unreal MuJoCo bridge! Both simulate the exact same MuJoCo physics and run standard .onnx brains. "
            f"Bring the MJCF XML body + match the joint order and observation list—no physics rewrite needed.",
        )

    # ML-Agents <-> ML-Agents
    if src in ML_AGENTS and dst in ML_AGENTS:
        return (
            "retrain",
            "Retrain",
            f"Both {src} and {dst} use Unity ML-Agents (Sentis/Barracuda), so the training scripts & C# agent setup transfer easily, "
            f"but each game has different senses (ball/teammates vs. sumo ring) so you retrain the brain.",
        )

    # MuJoCo <-> ML-Agents
    return (
        "rebuild",
        "Rebuild",
        f"{src} and {dst} live in different physics worlds (MuJoCo joint muscles vs. Unity PhysX/ML-Agents). "
        f"You can reuse the game rules and art, but the brain must be retrained in the target physics engine.",
    )


def make_portability_matrix_html() -> str:
    headers = "".join(f'<th title="{esc(r)}">{esc(SHORT_NAME[r])}</th>' for r in BODY_REPO_IDS)
    rows_html = []
    notes_map = {}
    for src in BODY_REPO_IDS:
        tds = []
        for dst in BODY_REPO_IDS:
            code, badge, reason = portability_cell(src, dst)
            key = f"{src}->{dst}"
            notes_map[key] = {"src": src, "dst": dst, "code": code, "badge": badge, "reason": reason}
            tds.append(
                f'<td class="port-cell port-{code}" data-key="{esc(key)}" '
                f'onclick="showPortNote(\'{esc(key)}\')" title="{esc(src)} → {esc(dst)}: {esc(badge)}">'
                f'{esc(badge)}</td>'
            )
        rows_html.append(f'<tr><th>{esc(SHORT_NAME[src])}</th>{"".join(tds)}</tr>')

    notes_json = json.dumps(notes_map)
    return f"""
    <div class="pe-card full-span" id="pe-portability">
      <div class="pe-head">
        <span class="pe-num">01</span>
        <div>
          <h3>"Can I Move This Agent?" — Cross-Repo Portability Matrix</h3>
          <p class="pe-sub">Pick a <strong>Source repo (row)</strong> and a <strong>Target repo (column)</strong>. Click any square to see exactly what transfers and what needs work.</p>
        </div>
      </div>
      <div class="port-legend">
        <span><i class="dot port-plug"></i><strong>Easy (Plug &amp; Play):</strong> Same MuJoCo physics &amp; body family</span>
        <span><i class="dot port-adapt"></i><strong>Adapt (Unity ↔ Unreal MuJoCo):</strong> Same physics &amp; .onnx; bring MJCF body + map senses</span>
        <span><i class="dot port-retrain"></i><strong>Retrain (ML-Agents):</strong> Same Unity setup; retrain for new rules/senses</span>
        <span><i class="dot port-rebuild"></i><strong>Rebuild:</strong> Different physics engine (MuJoCo ↔ Unity PhysX)</span>
      </div>
      <div class="table-wrap" style="margin-bottom:12px;">
        <table class="port-table">
          <thead><tr><th>From ↓ &nbsp;/&nbsp; To →</th>{headers}</tr></thead>
          <tbody>{"".join(rows_html)}</tbody>
        </table>
      </div>
      <div class="port-inspector" id="portInspector">
        <strong>Tip:</strong> Click any cell above (for example, <code>Olympic → HillKing</code> or <code>Box → SlapUE</code>) to see plain-English transfer instructions.
      </div>
      <script>window.__PORT_NOTES = {notes_json};</script>
    </div>"""


def make_human_practice_svg(facts: dict, projects: list[dict]) -> str:
    """#2: Translate env_steps_per_s into Days of 24/7 physical practice per 1 hour of PC training."""
    rows = []
    for p in projects:
        b = (facts.get(p["id"], {}).get("benchmark") or {})
        sps = b.get("env_steps_per_s") or 0
        # 50 Hz policy step = 0.02 s simulated per step
        # In 1 real hour (3600 s): sim_seconds = sps * 0.02 * 3600 = sps * 72
        # sim_days_24_7 = (sps * 72) / 86400 = sps / 1200
        days_per_hr = sps / 1200.0 if sps > 0 else 0.0
        rows.append((p["name"], days_per_hr, sps, p["stack"]))
    rows.sort(key=lambda r: -r[1])
    max_d = max((r[1] for r in rows), default=1.0)
    bar_h, gap, top, left, right = 24, 10, 14, 126, 175
    chart_w = 680
    usable_w = chart_w - left - right
    h = top * 2 + len(rows) * (bar_h + gap)
    colors = {"unreal_mujoco": "#60c6ff", "unity_mujoco": "#f5c242", "unity_mlagents": "#c084fc"}
    svg = [f'<svg viewBox="0 0 {chart_w} {h}" class="chart-svg">']
    for i, (name, days, sps, stack) in enumerate(rows):
        y = top + i * (bar_h + gap)
        bw = max(5, int(usable_w * (days / max_d))) if days > 0 else 2
        col = colors.get(stack, "#93a4bc")
        svg.append(f'<text x="{left - 10}" y="{y + 16}" text-anchor="end" fill="#e6edf7" font-size="12.5" font-weight="600">{esc(name)}</text>')
        svg.append(f'<rect x="{left}" y="{y}" width="{usable_w}" height="{bar_h}" rx="5" fill="#182232"/>')
        svg.append(f'<rect x="{left}" y="{y}" width="{bw}" height="{bar_h}" rx="5" fill="{col}"/>')
        if days >= 1.0:
            human_months = days / 10.0  # ~8h/day practice = 3x calendar days -> /30*3 = /10
            lbl = f"{days:.1f} days/hr (~{human_months:.1f} mo human)"
        elif days > 0:
            mins = days * 24 * 60
            lbl = f"{mins:.0f} min/hr (near real-time)"
        else:
            lbl = "Not timed yet"
        svg.append(f'<text x="{left + usable_w + 10}" y="{y + 16}" fill="#cbd5e1" font-size="11.5">{esc(lbl)}</text>')
    svg.append("</svg>")
    return f"""
    <div class="pe-card" id="pe-practice">
      <div class="pe-head">
        <span class="pe-num">02</span>
        <div>
          <h3>"Human Practice" per 1 Hour of PC Training</h3>
          <p class="pe-sub">How much physical experience your agent gains in <strong>1 real hour</strong> on your laptop GPU (assuming 50 decisions/sec). A human training 8 hrs/day would need months to match 1 GPU hour.</p>
        </div>
      </div>
      {"".join(svg)}
    </div>"""


def make_character_passports_html() -> str:
    """#3: Character Passport Cards ('Who Lives Where?')."""
    cast = [
        {
            "name": "Matt / Standard Humanoid",
            "body": "23–30 joint human body",
            "homes": ["PoOlympic", "PoHillKing", "PoBox", "PoSlapUnreal", "PoBasketball", "PoRaceUnreal"],
            "skills": "Sprinting, slopes, crawling, get-up, boxing, slapping, shooting hoops, hill king-of-the-hill",
            "next": "Drop PoOlympic get-up + sprint directly into PoHillKing or PoBox",
            "accent": "cyan",
        },
        {
            "name": "Grandma",
            "body": "65 kg stooped 23-joint human",
            "homes": ["PoOlympic", "PoRaceUnreal (stub)"],
            "skills": "Stooped sprint, all-fours crawl, 15° alpine ramp, supine/prone get-up, Flamingo stance",
            "next": "Ready to guest-star in PoHillKing, PoBox, or finish PoRaceUnreal",
            "accent": "gold",
        },
        {
            "name": "Zombie",
            "body": "1.1 m hunched 23-joint body",
            "homes": ["PoOlympic"],
            "skills": "Hunched shuffle sprint, trench crawl, 15° ramp climb, balance (get-up in progress)",
            "next": "Instant comic fighter in PoHillKing or PoSlapUnreal",
            "accent": "green",
        },
        {
            "name": "Unitree Go2 Robot Dog",
            "body": "12-joint quadruped",
            "homes": ["PoRaceUnreal"],
            "skills": "Flat racing, banked turns, steeplechase hurdles, self-righting get-up",
            "next": "Can roam any Unreal MuJoCo arena (PoBasketball / PoSlapUnreal) as mascot or obstacle",
            "accent": "cyan",
        },
        {
            "name": "Unitree R1 Real Humanoid",
            "body": "24-joint real-world robot",
            "homes": ["PoRobotTrain"],
            "skills": "Hardware-safe head tracking (nod, shake, look, glance) + upper-body stand + walk sandbox",
            "next": "Sim-to-real bridge: test behaviors safely in Unreal before sending to physical Jetson",
            "accent": "purple",
        },
        {
            "name": "Jogging Stroller",
            "body": "3-wheel motorized rig",
            "homes": ["PoRaceUnreal"],
            "skills": "High-speed oval & sprint racing (100% graduation in just 16 minutes!)",
            "next": "Proof-of-concept for fast vehicle/wheeled agents in any Unreal MuJoCo scene",
            "accent": "gold",
        },
        {
            "name": "Sumo Wrestlers",
            "body": "16-part articulated Unity PhysX bipeds",
            "homes": ["PoSumo"],
            "skills": "Low sumo stance, tachi-ai clash, 1v1 ring-out pushes",
            "next": "Unity PhysX body: needs a MuJoCo rebuild to join the Unity/Unreal MuJoCo games",
            "accent": "purple",
        },
    ]
    cards = []
    for c in cast:
        homes = " ".join(f'<span class="mini-pill">{esc(h)}</span>' for h in c["homes"])
        cards.append(f"""
        <div class="passport-card accent-{c['accent']}">
          <div class="passport-top">
            <strong>{esc(c['name'])}</strong>
            <span class="passport-body">{esc(c['body'])}</span>
          </div>
          <div class="passport-homes">{homes}</div>
          <p><strong>Knows:</strong> {esc(c['skills'])}</p>
          <p class="passport-next"><strong>Next Move:</strong> {esc(c['next'])}</p>
        </div>""")
    return f"""
    <div class="pe-card full-span" id="pe-passports">
      <div class="pe-head">
        <span class="pe-num">03</span>
        <div>
          <h3>Character Passport Cards — "Who Lives Where?"</h3>
          <p class="pe-sub">Think of your agents as reusable <strong>cast members</strong> rather than isolated repos. Here is where each body already lives and where it can guest-star next.</p>
        </div>
      </div>
      <div class="passport-grid">{"".join(cards)}</div>
    </div>"""


def make_skill_library_html() -> str:
    """#4: Fleet Skill Library Matrix with 0-10 scored heat-map + click-to-compare evidence."""
    skills = [
        "Balance",
        "Walk / Sprint",
        "Get Up",
        "Slopes / Climb",
        "Crawl",
        "Punch / Slap",
        "Shove / Ring",
        "Ball / Shoot",
        "Team Play",
        "Robot Safe",
    ]
    # Real evidence-backed 0-10 scores: (score, short_reason)
    # 0 = Not trained in this repo
    matrix = {
        "PoOlympic": [
            (9.5, "Iron Pedestal & Flamingo single-leg stance pass 6 parity gates (1.09e-6 drift)."),
            (9.5, "4 distinct bodies sprint, slalom, crab-walk & hurdle across 18 playable events."),
            (7.5, "Supine/prone get-up solved on Mattbio & Grandma; Zombie 1.1m hunched get-up still open."),
            (9.5, "R4 15° Alpine Ramp graduated 48/48 finishes across all athletes."),
            (9.5, "All-fours crawl (Events 8 & 23 Trench Crawl) fully trained & shipped."),
            (0.0, "Not a combat game — no punching or slapping."),
            (0.0, "Crowd contact audited (16,184 part pairs), but no ring-out shoving."),
            (0.0, "No ball handling in current 18 events."),
            (0.0, "Individual 8-lane racing & events (no team cooperation)."),
            (0.0, "Game simulation only (not capped for real hardware)."),
        ],
        "PoHillKing": [
            (9.5, "R0/R1 stand survives 99.4% of heavy impacts on the 2.5m concrete platform."),
            (8.5, "0.14 m/s velocity error with goal-driven approach & rim avoidance."),
            (9.5, "Dedicated get-up brain recovers 99.5% of knockdowns within 5 seconds."),
            (8.0, "Handles convex mountain dome & concrete platform elevation cleanly."),
            (0.0, "No crawling behavior in King-of-the-Hill."),
            (5.5, "Basic scripted/RL jabs layered on top of shoving."),
            (8.0, "Overnight strike_pass lifted Trump to 0.680 3-way share & Kim to 0.525."),
            (0.0, "No ball mechanics."),
            (0.0, "Free-for-all 8-fighter brawls (every fighter for themselves)."),
            (0.0, "Game simulation only (no physical robot safety caps)."),
        ],
        "PoBox": [
            (8.0, "v4r_trump cut falls from 8.67→0.33/min & 69% guard; short-reach bodies still weaker under pressure."),
            (8.5, "R0–R2 boxing footwork, 360° pivot & CMU motion-prior style well trained."),
            (9.0, "28–29/30 knockdown get-ups in 1.7–2.8s + 25/28 smooth 15-step carry-on handovers."),
            (0.0, "Flat boxing ring only."),
            (0.0, "No crawling in boxing."),
            (7.0, "Strong glove-to-head/body combos on tall boxers; short-reach punching (Trump/Grandma/Soldat) still needs work."),
            (3.0, "Clinching/leaning is penalized rather than rewarded."),
            (0.0, "No ball mechanics."),
            (0.0, "1v1 bouts & 8-way FFA (no team play)."),
            (0.0, "Human-scaled force limits in v3 bodies, but sim-only."),
        ],
        "PoSlapUnreal": [
            (8.5, "L0–L1 fighters absorb 8 m/s palm strikes without flinching or falling (Matt/Grandpa/Kim strong; Mom/Tong at L0)."),
            (7.5, "S2–S3 RL walk-in to podium works; limited to straight approach rather than full open-field sprint."),
            (4.5, "G1 10-second knockdown get-up is queued in v5 (14 stages remaining)."),
            (0.0, "Flat stage & podium only."),
            (0.0, "No crawling."),
            (8.0, "Grandpa vs Kim v4 hit 0.921 & Matt L4 done; Mom & Tong L1–L4 still pending in v5 queue."),
            (0.0, "Strict foot-box podium rules (no shoving)."),
            (0.0, "No ball mechanics."),
            (0.0, "1v1 turn-based power slap."),
            (0.0, "Game physics only (cheek-dent morphs, not hardware-capped)."),
        ],
        "PoBasketball": [
            (9.0, "L0 stand & 29-DoF upright posture solid under ball-contact impulses."),
            (8.0, "L1 locomotion & L3b loose-ball scramble shipped; moving while dribbling (L3a) still untrained."),
            (4.0, "Scramble recovers loose ball, but dedicated knockdown recovery isn't a focus yet."),
            (0.0, "Flat indoor basketball court."),
            (0.0, "No crawling."),
            (0.0, "Non-contact basketball primitives."),
            (0.0, "No shoving."),
            (5.5, "Stationary dribble just hit 98.7–100% (!), but moving dribble (L3a) & set shot (L4) are still 0–10%."),
            (2.0, "Single-player motor primitives today; team play comes after L3a/L4."),
            (0.0, "Game simulation only."),
        ],
        "PoRaceUnreal": [
            (9.5, "Go2 dog, Matt 29-DoF & Stroller all pass 0.000 cm UE parity with zero unforced falls."),
            (9.5, "Go2 flat/banked/steeple, Matt 10/10 sprint rungs, Stroller 100/100 in 16 min (Grandma & G1 pending)."),
            (8.5, "Go2 self-righting get-up shipped; Matt/Stroller unstick works."),
            (9.0, "Go2 banked turns & steeplechase hurdle clearance fully graduated."),
            (0.0, "No crawling."),
            (0.0, "Racing only (bump impulse is penalized)."),
            (4.0, "Tactical passing & blocking lines on track, no ring shoving."),
            (0.0, "No ball mechanics."),
            (0.0, "Individual pack racing."),
            (0.0, "Game physics (Go2 model is real-robot shaped, not Jetson-deployed)."),
        ],
        "PoRobotTrain": [
            (9.5, "Stand-mounted upper-body & all 5 head skills rock-solid with ±10° start gate."),
            (3.0, "Level 2 omnidirectional ground walk (24 DoF) is still in Docker sandbox pending promotion."),
            (0.0, "Not trained (safety-first hardware prevents falls)."),
            (0.0, "Flat lab floor only."),
            (0.0, "No crawling."),
            (0.0, "Safety-capped real robot — no combat."),
            (0.0, "No shoving."),
            (0.0, "No ball handling (Level 3 camera-following planned)."),
            (0.0, "Single physical robot."),
            (10.0, "Gold standard: SHA-256 deploy.json gate, 0.86 Nm torque cap, 8-twin randomization & Jetson runtime."),
        ],
        "PoFootball": [
            (0.0, "2D top-down Rigidbody2D (no 3D falling/balance physics)."),
            (8.5, "Real NFL sprint speeds (6.2–9.2 m/s), route running & pursuit across 100-yard field."),
            (0.0, "2D top-down (no knockdown get-up)."),
            (0.0, "Flat 2D field."),
            (0.0, "No crawling."),
            (0.0, "No punching/slapping."),
            (7.0, "Offensive line blocking & defensive tackling/sacks."),
            (8.5, "QB dropback, target selection & 85% pass completions (8.3 net yds/play)."),
            (9.0, "22 simultaneous MA-POCA agents coordinating offense, defense & discrete play-calling."),
            (0.0, "2D game simulation only."),
        ],
        "PoSoccer": [
            (0.0, "3D rigidbody vehicle/capsule (doesn't tip over like a biped)."),
            (9.0, "Crisp thrust/steering pursuit and fast ball recovery across 1v1 & 4v4."),
            (0.0, "No falling/get-up needed."),
            (0.0, "Flat soccer pitch."),
            (0.0, "No crawling."),
            (0.0, "No combat."),
            (4.5, "Physical body-blocking for ball possession."),
            (9.0, "91.0% win rate over 2,000 matches with strong dribbling & goal shooting (6.0% stalemates)."),
            (8.0, "Works in both 1v1 and 4v4 team matches with 10 named character brains."),
            (0.0, "Game simulation only."),
        ],
        "PoSumo": [
            (8.5, "16-part articulated Unity bipeds maintain low sumo stance & resist ring-out pushes."),
            (8.0, "Dual-school walk + tachi-ai clash movement across all 16 promoted fighters."),
            (0.0, "Touching the ground loses the round immediately (no get-up phase)."),
            (0.0, "Flat circular dohyo ring."),
            (0.0, "No crawling."),
            (5.0, "Open-hand sumo strikes / counter-puncher styles."),
            (9.2, "Core specialty: 16/16 unique self-play sumo fighters pushing rivals out of the ring."),
            (0.0, "No ball mechanics."),
            (0.0, "1v1 sumo tournament bracket."),
            (0.0, "Unity PhysX game simulation only."),
        ],
    }

    def score_cls(v: float) -> str:
        if v <= 0:
            return "sk-0"
        if v >= 8.5:
            return "sk-hi"
        if v >= 6.5:
            return "sk-good"
        if v >= 4.0:
            return "sk-mid"
        return "sk-low"

    th = "".join(
        f'<th class="sk-col-hdr" onclick="compareSkillCol({idx})" title="Click to rank all repos on {esc(s)}">{esc(s)} ↕</th>'
        for idx, s in enumerate(skills)
    )
    rows = []
    skill_notes = {}
    for r in BODY_REPO_IDS:
        vals = matrix[r]
        tds = []
        for idx, (val, reason) in enumerate(vals):
            key = f"{r}|{idx}"
            skill_notes[key] = {"repo": r, "skill": skills[idx], "score": val, "reason": reason}
            disp = f"{val:g}" if val > 0 else "0"
            tds.append(
                f'<td class="sk-cell {score_cls(val)}" onclick="showSkillDetail(\'{esc(key)}\')" '
                f'title="{esc(r)} — {esc(skills[idx])}: {disp}/10 · {esc(reason)}">{disp}</td>'
            )
        rows.append(f'<tr><th>{esc(r)}</th>{"".join(tds)}</tr>')

    col_rankings = []
    for idx, sname in enumerate(skills):
        ranked = sorted(
            [(r, matrix[r][idx][0], matrix[r][idx][1]) for r in BODY_REPO_IDS if matrix[r][idx][0] > 0],
            key=lambda x: -x[1],
        )
        col_rankings.append({"skill": sname, "ranked": ranked})

    return f"""
    <div class="pe-card full-span" id="pe-skills">
      <div class="pe-head">
        <span class="pe-num">04</span>
        <div>
          <h3>Fleet Skill Library (0–10 Quality Scores) — Compare Any Skill Across Repos</h3>
          <p class="pe-sub">
            Every skill is scored <strong>0–10</strong> so you can compare repos head-to-head (e.g., <code>PoHillKing</code> Balance <strong>9.5</strong> vs. <code>PoBox</code> Balance <strong>8</strong>).
            <strong>Click any score</strong> for evidence, or <strong>click any column header</strong> (like <em>Balance</em>) to rank all repos on that skill.
          </p>
        </div>
      </div>
      <div class="port-legend" style="margin-bottom:10px;">
        <span><i class="dot" style="background:#22c55e;"></i><strong>8.5–10:</strong> Elite / Shipped</span>
        <span><i class="dot" style="background:#38bdf8;"></i><strong>6.5–8.4:</strong> Strong / Playable</span>
        <span><i class="dot" style="background:#f5c242;"></i><strong>4.0–6.4:</strong> Partial / Needs Polish</span>
        <span><i class="dot" style="background:#f87171;"></i><strong>1–3.9:</strong> Early / Gap</span>
        <span><i class="dot" style="background:#334155;"></i><strong>0:</strong> Not in this game</span>
      </div>
      <div class="table-wrap" style="margin-bottom:10px;">
        <table class="skill-table">
          <thead><tr><th>Repository</th>{th}</tr></thead>
          <tbody>{"".join(rows)}</tbody>
        </table>
      </div>
      <div class="port-inspector" id="skillInspector">
        <strong>Try it:</strong> Click <code>Balance ↕</code> above to rank all repos on Balance head-to-head, or click any <code>0–10</code> score to see why it earned that rating.
      </div>
      <script>
        window.__SKILL_NOTES = {json.dumps(skill_notes)};
        window.__SKILL_COLS = {json.dumps(col_rankings)};
      </script>
    </div>"""


def make_coaching_styles_html() -> str:
    """#5: Training Style Breakdown ('How Are They Taught?')."""
    lanes = [
        {
            "title": "Solo Practice",
            "tag": "Fastest to Learn",
            "accent": "green",
            "repos": ["PoOlympic", "PoRobotTrain"],
            "how": "One agent practices against the clock, a target pose, or a ramp—no unpredictable opponent trying to trick it.",
            "takeaway": "Great for building clean, reusable building-block skills (Sprint, Crawl, Get-Up, Head Tracking).",
        },
        {
            "title": "1-on-1 Sparring (Self-Play)",
            "tag": "Arms Race",
            "accent": "gold",
            "repos": ["PoBox", "PoSlapUnreal", "PoSumo"],
            "how": "Two fighters learn by battling older versions of themselves. Every time one finds a cheap trick, the other learns to counter it.",
            "takeaway": "Takes longer (overnight runs) because the opponent keeps getting smarter, but produces exciting reactive combat.",
        },
        {
            "title": "Pack / Free-for-All",
            "tag": "Chaos & Crowds",
            "accent": "cyan",
            "repos": ["PoHillKing", "PoRaceUnreal"],
            "how": "Multiple bodies shove, pass, or block each other in the same space. Agents must handle bumps and traffic.",
            "takeaway": "Teaches sturdy balance and collision recovery—ideal for party games and racing.",
        },
        {
            "title": "Team Sport Coaching",
            "tag": "Hardest to Master",
            "accent": "purple",
            "repos": ["PoBasketball", "PoFootball", "PoSoccer"],
            "how": "Teammates share goals (scoring, defending, passing) while coordinating around a moving ball.",
            "takeaway": "Requires multi-agent credit assignment (MA-POCA or role directors) so players pass instead of chasing the ball in a clump.",
        },
    ]
    cols = []
    for ln in lanes:
        pills = " ".join(f'<span class="mini-pill">{esc(r)}</span>' for r in ln["repos"])
        cols.append(f"""
        <div class="lane-card accent-{ln['accent']}">
          <div class="lane-top"><strong>{esc(ln['title'])}</strong><span class="lane-tag">{esc(ln['tag'])}</span></div>
          <div class="passport-homes">{pills}</div>
          <p>{esc(ln['how'])}</p>
          <p class="lane-tip"><strong>Why it matters:</strong> {esc(ln['takeaway'])}</p>
        </div>""")
    return f"""
    <div class="pe-card full-span" id="pe-coaching">
      <div class="pe-head">
        <span class="pe-num">05</span>
        <div>
          <h3>Coaching Styles — "How Are They Taught?"</h3>
          <p class="pe-sub">Why a stroller graduates in 16 minutes while boxing or 11v11 football takes overnight runs: it comes down to the coaching format.</p>
        </div>
      </div>
      <div class="lane-grid">{"".join(cols)}</div>
    </div>"""


def make_plug_checklist_html() -> str:
    """#6: The 4 Things That Must Match to Move a Brain."""
    rows_data = [
        ("PoOlympic",    "23-joint Humanoid (4 bodies)", "MuJoCo", "Target speed + stance cmd", ".onnx (Sentis)", "Easy donor for HillKing & Box"),
        ("PoHillKing",   "23-joint Humanoid",            "MuJoCo", "Hill center + nearby foes", ".onnx (Sentis)", "Shares body with Olympic & Box"),
        ("PoBox",        "23-joint Humanoid",            "MuJoCo", "Opponent hands/head + ring", ".onnx (Sentis)", "Move footwork/guard easily"),
        ("PoSlapUnreal", "24–30-joint Humanoid",         "MuJoCo", "Opponent torso/slap phase", ".onnx (NNE ORT)", "Shares Unreal MuJoCo with Basketball"),
        ("PoBasketball", "24–30-joint Humanoid",         "MuJoCo", "Ball + hoop + teammates",   ".onnx (NNE ORT)", "Locomotion transfers; shooting is sport-specific"),
        ("PoRaceUnreal", "Go2 (12) / Matt (29) / Stroller (3)", "MuJoCo", "Track spline + rivals", ".onnx (NNE ORT)", "Bring MJCF body + drop into any UE MuJoCo scene"),
        ("PoRobotTrain", "Unitree R1 (24 joints)",       "MuJoCo", "Target gaze / velocity",    ".onnx (NNE/Jetson)", "Portable across Unreal & real robot"),
        ("PoSumo",       "Unity PhysX Wrestlers",        "Unity PhysX", "Opponent + ring edge",     ".onnx (ML-Agents)", "Unity PhysX body: rebuild it in MuJoCo to move it"),
    ]
    trs = []
    for repo, body, phys, senses, fmt, note in rows_data:
        p_cls = "chk-mj" if phys == "MuJoCo" else "chk-physx"
        trs.append(
            f"<tr><td><strong>{esc(repo)}</strong></td><td>{esc(body)}</td>"
            f'<td><span class="chk-pill {p_cls}">{esc(phys)}</span></td>'
            f"<td>{esc(senses)}</td><td><code>{esc(fmt)}</code></td><td class='sub-td'>{esc(note)}</td></tr>"
        )
    return f"""
    <div class="pe-card full-span" id="pe-checklist">
      <div class="pe-head">
        <span class="pe-num">06</span>
        <div>
          <h3>Plug-and-Play Checklist — The 4 Things That Must Match to Move a Brain</h3>
          <p class="pe-sub">An <code>.onnx</code> brain is just a math recipe: <em>Senses in → Joint commands out</em>. If these 4 columns match between two repos, the brain works in both—even across Unity and Unreal!</p>
        </div>
      </div>
      <div class="table-wrap" style="margin-bottom:0;">
        <table class="inner-table">
          <thead>
            <tr>
              <th>Repo</th>
              <th>1. Same Body / Joints?</th>
              <th>2. Same Physics?</th>
              <th>3. Same Senses?</th>
              <th>4. Same Brain File?</th>
              <th>Plain-English Takeaway</th>
            </tr>
          </thead>
          <tbody>{"".join(trs)}</tbody>
        </table>
      </div>
    </div>"""


def make_graduation_ladder_html() -> str:
    """#7: Training Graduation Ladder ('How Easy Is It to Train a New Skill Here?')."""
    items = [
        ("PoRaceUnreal", 5, "16 min – 2 hrs", "Fastest Gym", "JAX/Brax runs at 26.5k steps/s; stroller graduated 100/100 in 16 min."),
        ("PoSlapUnreal", 5, "30 min – 4 hrs", "Fastest Warp Gym", "8,192 parallel worlds (32.5k steps/s) make rapid combat iteration effortless."),
        ("PoRobotTrain", 4, "20 min – 2 hrs", "Clean & Safe", "Small head/upper-body networks train fast with strict automatic safety checks."),
        ("PoOlympic",    4, "1 – 6 hrs",      "Best Skill Factory", "Mature curriculum + 6 parity gates; ideal for mass-producing locomotion skills."),
        ("PoBasketball", 4, "2 – 8 hrs",      "Fast Team Gym", "26.4k steps/s GPU Warp keeps complex ball + humanoid training practical."),
        ("PoHillKing",   4, "2 – 8 hrs",      "Crowd Sandbox", "22k steps/s; multi-agent shoving needs careful reward balance."),
        ("PoBox",        3, "4 – 12 hrs",     "Overnight Sparring", "11.1k steps/s; self-play boxing requires staged curriculum & gauntlet tests."),
        ("PoSumo",       2, "4 – 12 hrs",     "CPU Self-Play", "Unity ML-Agents self-play; slower than Warp, straightforward ring rules."),
        ("PoSoccer",     2, "8 – 24 hrs",     "CPU Team Play", "119 steps/s on CPU; 3v3 teamwork needs long MA-POCA sessions."),
        ("PoFootball",   1, "Days",           "Heavy Marathon", "22 players on CPU (~7 steps/s); best treated as a long background campaign."),
    ]
    rows = []
    for repo, score, t_first, badge, note in items:
        dots = "".join(f'<span class="lad-dot {"on" if i < score else ""}"></span>' for i in range(5))
        rows.append(f"""
        <div class="lad-row">
          <div class="lad-name"><strong>{esc(repo)}</strong><span class="lad-badge">{esc(badge)}</span></div>
          <div class="lad-meter" title="Ease & speed score: {score}/5">{dots} <span class="lad-score">{score}/5</span></div>
          <div class="lad-time"><strong>{esc(t_first)}</strong><span>typical run</span></div>
          <div class="lad-note">{esc(note)}</div>
        </div>""")
    return f"""
    <div class="pe-card" id="pe-ladder">
      <div class="pe-head">
        <span class="pe-num">07</span>
        <div>
          <h3>Training Graduation Ladder — Best "Gym" for New Skills</h3>
          <p class="pe-sub">Rated <strong>1 (Marathon)</strong> to <strong>5 (Rapid Prototyping Gym)</strong> based on your PC's real speed and how quickly a brand-new behavior starts working.</p>
        </div>
      </div>
      <div class="lad-list">{"".join(rows)}</div>
    </div>"""


def make_brain_size_svg(facts: dict, projects: list[dict]) -> str:
    """#8: Brain Size vs. Specialist Team Architecture (real crawled ONNX count & average KB)."""
    style_map = {
        "PoOlympic":    ("Specialist Team", "#f5c242"),
        "PoBox":        ("Specialist Team", "#f5c242"),
        "PoSlapUnreal": ("Specialist Team", "#60c6ff"),
        "PoHillKing":   ("Specialist Team", "#f5c242"),
        "PoBasketball": ("Role Specialists", "#60c6ff"),
        "PoSumo":       ("Staged Brains",   "#c084fc"),
        "PoRobotTrain": ("Skill Specialists", "#60c6ff"),
        "PoFootball":   ("Role Brains",     "#c084fc"),
        "PoRaceUnreal": ("Per-Racer Brains", "#60c6ff"),
        "PoSoccer":     ("All-in-One Team", "#c084fc"),
    }
    rows = []
    for p in projects:
        f = facts.get(p["id"], {})
        cnt = f.get("onnx_shipped_count", 0)
        samples = f.get("onnx_samples", [])
        kbs = [s["kb"] for s in samples if isinstance(s, dict) and s.get("kb")]
        avg_kb = sum(kbs) / len(kbs) if kbs else 0.0
        arch, col = style_map.get(p["id"], ("Mixed", "#93a4bc"))
        rows.append((p["name"], cnt, avg_kb, arch, col))
    rows.sort(key=lambda r: -r[1])
    max_cnt = max((r[1] for r in rows), default=1)
    bar_h, gap, top, left, right = 24, 10, 14, 126, 210
    chart_w = 680
    usable_w = chart_w - left - right
    h = top * 2 + len(rows) * (bar_h + gap)
    svg = [f'<svg viewBox="0 0 {chart_w} {h}" class="chart-svg">']
    for i, (name, cnt, avg_kb, arch, col) in enumerate(rows):
        y = top + i * (bar_h + gap)
        bw = max(6, int(usable_w * (cnt / max_cnt))) if cnt > 0 else 2
        svg.append(f'<text x="{left - 10}" y="{y + 16}" text-anchor="end" fill="#e6edf7" font-size="12.5" font-weight="600">{esc(name)}</text>')
        svg.append(f'<rect x="{left}" y="{y}" width="{usable_w}" height="{bar_h}" rx="5" fill="#182232"/>')
        svg.append(f'<rect x="{left}" y="{y}" width="{bw}" height="{bar_h}" rx="5" fill="{col}"/>')
        size_str = f"{avg_kb:.0f} KB avg" if avg_kb < 1024 else f"{avg_kb/1024:.1f} MB avg"
        lbl = f"{cnt} brains · {size_str} · {arch}"
        svg.append(f'<text x="{left + usable_w + 10}" y="{y + 16}" fill="#cbd5e1" font-size="11.5">{esc(lbl)}</text>')
    svg.append("</svg>")
    return f"""
    <div class="pe-card" id="pe-brains">
      <div class="pe-head">
        <span class="pe-num">08</span>
        <div>
          <h3>Pocket Brains vs. Specialist Teams (Real <code>.onnx</code> Footprint)</h3>
          <p class="pe-sub">Notice how tiny these brains are (often <strong>&lt;1 MB</strong>!)—and why repos using <strong>Specialist Teams</strong> (separate Walk, Crawl, Get-Up, Punch files) are the easiest to reuse in new games.</p>
        </div>
      </div>
      {"".join(svg)}
    </div>"""


def make_gym_to_game_html() -> str:
    """#9: 'From Gym to Game' 3-Stack Pipeline Comparison."""
    return """
    <div class="pe-card full-span" id="pe-pipeline">
      <div class="pe-head">
        <span class="pe-num">09</span>
        <div>
          <h3>"From Gym to Game" — How Your 3 Tech Stacks Connect</h3>
          <p class="pe-sub">Key insight: your <strong>Unreal + MuJoCo</strong> and <strong>Unity + MuJoCo</strong> projects actually share the <em>exact same physics brain pipeline</em>. Moving a brain between them is a bridge—not a rewrite.</p>
        </div>
      </div>
      <div class="pipe-stack">
        <div class="pipe-row pipe-unreal">
          <div class="pipe-label"><strong>Unreal 5.8 + MuJoCo</strong><span>PoBasketball · PoRaceUnreal · PoRobotTrain · PoSlapUnreal</span></div>
          <div class="pipe-steps">
            <div class="pipe-box">1. Python GPU Gym<small>MuJoCo Warp (512–8,192 worlds)</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box shared">2. Standard <code>.onnx</code><small>Shared MuJoCo Body + Brain</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box">3. Unreal NNE ORT<small>Runs brain in real time</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box done">4. Playable UE 5.8 App<small>URLab MuJoCo physics</small></div>
          </div>
        </div>
        <div class="pipe-bridge">⇅ &nbsp; <strong>SHARED MUJOCO BRIDGE:</strong> Same MJCF body XML + Same MuJoCo physics + Same <code>.onnx</code> math &nbsp; ⇅</div>
        <div class="pipe-row pipe-unity-mj">
          <div class="pipe-label"><strong>Unity 6 + MuJoCo</strong><span>PoBox · PoHillKing · PoOlympic</span></div>
          <div class="pipe-steps">
            <div class="pipe-box">1. Python GPU Gym<small>MuJoCo Warp (4,096–4,592 worlds)</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box shared">2. Standard <code>.onnx</code><small>Shared MuJoCo Body + Brain</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box">3. Unity Sentis<small>Unity.InferenceEngine</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box done">4. Playable Unity 6 App<small>org.mujoco physics</small></div>
          </div>
        </div>
        <div class="pipe-row pipe-mlagents" style="margin-top:12px;">
          <div class="pipe-label"><strong>Unity 6 + ML-Agents</strong><span>PoFootball · PoSoccer · PoSumo</span></div>
          <div class="pipe-steps">
            <div class="pipe-box">1. Unity Editor Gym<small>4 CPU worlds (PhysX)</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box">2. ML-Agents <code>.onnx</code><small>Tied to Unity PhysX rig</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box">3. Unity Sentis<small>Behavior Parameters</small></div>
            <div class="pipe-arr">→</div>
            <div class="pipe-box done">4. Playable Unity 6 App<small>Unity built-in physics</small></div>
          </div>
        </div>
      </div>
    </div>"""


def make_starter_recipes_html() -> str:
    """#10: 'You Can Do This Today' — Top 6 Cross-Repo Starter Recipes."""
    recipes = [
        {
            "title": "Drop Olympic Sprint, Crawl & Get-Up into HillKing",
            "route": "PoOlympic → PoHillKing",
            "effort": "~30 min",
            "effort_cls": "easy",
            "why": "Same Unity 6 + MuJoCo stack and same 23-joint humanoid body.",
            "steps": "Copy the .onnx brain (e.g. Grandma or Mattbio get-up/sprint), point HillKing's skill director at it when knocked down, and verify observation order.",
        },
        {
            "title": "Bring Grandma & Zombie into the Boxing Ring",
            "route": "PoOlympic → PoBox",
            "effort": "~1–2 hrs",
            "effort_cls": "easy",
            "why": "Both share the 23-joint MuJoCo humanoid contract—so Grandma/Zombie bodies drop right into PoBox's arena.",
            "steps": "Copy the MJCF character XML + mesh from PoOlympic, keep PoBox's boxing senses, and fine-tune a boxing brain on the new body proportions.",
        },
        {
            "title": "Move Box Footwork & Guard to Unreal Slap Arena",
            "route": "PoBox → PoSlapUnreal",
            "effort": "~2–3 hrs",
            "effort_cls": "med",
            "why": "Both run MuJoCo physics and .onnx brains; only the game engine wrapper differs.",
            "steps": "Match the joint ordering between Unity's 23-DoF boxer and Unreal's fighter, map opponent-distance senses, and run via Unreal NNE ORT.",
        },
        {
            "title": "Guest-Star the Go2 Robot Dog in Any Unreal Arena",
            "route": "PoRaceUnreal → PoSlapUnreal / PoBasketball",
            "effort": "~1–2 hrs",
            "effort_cls": "easy",
            "why": "Go2 is a self-contained 12-joint MJCF body + 48-float locomotion/get-up .onnx already running in Unreal 5.8 NNE ORT.",
            "steps": "Copy the Go2 MJCF asset + go2_loco.onnx + go2_getup.onnx into your target Unreal project and feed it target (vx, vy, yaw) commands.",
        },
        {
            "title": "Reuse RobotTrain's Safety-Capped Head Skills on Any Humanoid",
            "route": "PoRobotTrain → PoBasketball / PoSlapUnreal",
            "effort": "~1 hr",
            "effort_cls": "easy",
            "why": "The 5 head skills (look_at, nod_yes, shake_no, glance, hold_centre) use only 9 tiny sense inputs and 2 neck joints.",
            "steps": "Attach the tiny 9→2 .onnx network to any Unreal humanoid's neck yaw/pitch joints so characters naturally look at the ball or opponent.",
        },
    ]
    cards = []
    for r in recipes:
        cards.append(f"""
        <div class="recipe-card">
          <div class="recipe-top">
            <span class="recipe-route">{esc(r['route'])}</span>
            <span class="effort-pill effort-{r['effort_cls']}">{esc(r['effort'])}</span>
          </div>
          <h4>{esc(r['title'])}</h4>
          <p><strong>Why it works:</strong> {esc(r['why'])}</p>
          <p class="recipe-steps"><strong>How to do it:</strong> {esc(r['steps'])}</p>
        </div>""")
    return f"""
    <div class="pe-card full-span" id="pe-recipes">
      <div class="pe-head">
        <span class="pe-num">10</span>
        <div>
          <h3>"You Can Do This Today" — Top 6 Cross-Repo Starter Recipes</h3>
          <p class="pe-sub">Concrete, highest-value ways to move an existing character or brain from one of your repos into another app right now.</p>
        </div>
      </div>
      <div class="recipe-grid">{"".join(cards)}</div>
    </div>"""


def build_plain_english_section(facts: dict, projects: list[dict]) -> str:
    """Assemble all 10 plain-English views into a cohesive section."""
    return f"""
  <section id="plainEnglishSection" class="pe-section">
    <div class="pe-banner">
      <div>
        <div class="eyebrow" style="margin-bottom:6px;">Plain-English Guide · 10 Non-Technical Views</div>
        <h2 style="margin:0 0 6px;border:none;padding:0;">How Your Agents Learn &amp; Where You Can Move Them</h2>
        <p class="subtitle" style="max-width:900px;">
          Zero jargon. These 10 interactive views translate raw benchmarks and physics configs into everyday intuition:
          how fast your PC coaches each agent, which characters live where, what skills you already own, and exactly which brains can move from one app to another.
        </p>
      </div>
      <div class="pe-quick-pills">
        <a href="#pe-portability">01 Portability Matrix</a>
        <a href="#pe-practice">02 Practice Speed</a>
        <a href="#pe-passports">03 Cast Passports</a>
        <a href="#pe-skills">04 Skill Library</a>
        <a href="#pe-coaching">05 Coaching Styles</a>
        <a href="#pe-checklist">06 4-Point Checklist</a>
        <a href="#pe-ladder">07 Gym Ladder</a>
        <a href="#pe-brains">08 Brain Sizes</a>
        <a href="#pe-pipeline">09 Gym → Game</a>
        <a href="#pe-recipes">10 Starter Recipes</a>
      </div>
    </div>

    <div class="pe-grid">
      {make_portability_matrix_html()}
      {make_human_practice_svg(facts, projects)}
      {make_brain_size_svg(facts, projects)}
      {make_character_passports_html()}
      {make_skill_library_html()}
      {make_coaching_styles_html()}
      {make_plug_checklist_html()}
      {make_graduation_ladder_html()}
      {make_gym_to_game_html()}
      {make_starter_recipes_html()}
    </div>
  </section>"""


PLAIN_ENGLISH_CSS = """
  .top-nav {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 20px;
  }
  .top-nav a {
    background: var(--panel);
    border: 1px solid var(--border);
    color: var(--text);
    padding: 6px 13px;
    border-radius: 999px;
    font-size: 12.5px;
    font-weight: 600;
  }
  .top-nav a:hover { border-color: var(--gold); color: var(--gold); text-decoration: none; }
  .pe-section { margin: 28px 0 34px; }
  .pe-banner {
    background: linear-gradient(135deg, rgba(96,198,255,0.09), rgba(245,194,66,0.08));
    border: 1px solid var(--border);
    border-radius: 14px;
    padding: 20px 22px;
    margin-bottom: 18px;
  }
  .pe-quick-pills {
    display: flex;
    flex-wrap: wrap;
    gap: 7px;
    margin-top: 14px;
  }
  .pe-quick-pills a {
    background: var(--panel);
    border: 1px solid var(--border);
    color: var(--muted);
    font-size: 11.5px;
    font-weight: 600;
    padding: 4px 10px;
    border-radius: 7px;
  }
  .pe-quick-pills a:hover { color: var(--gold); border-color: var(--gold); text-decoration: none; }
  .pe-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 16px;
  }
  @media (max-width: 980px) { .pe-grid { grid-template-columns: 1fr; } }
  .pe-card {
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: 14px;
    padding: 18px 20px;
  }
  .pe-card.full-span { grid-column: 1 / -1; }
  .pe-head {
    display: flex;
    gap: 12px;
    align-items: flex-start;
    margin-bottom: 14px;
  }
  .pe-num {
    flex: 0 0 auto;
    background: rgba(245, 194, 66, 0.16);
    color: var(--gold);
    border: 1px solid rgba(245, 194, 66, 0.45);
    font-size: 12px;
    font-weight: 800;
    padding: 3px 8px;
    border-radius: 7px;
    margin-top: 2px;
  }
  .pe-head h3 { margin: 0 0 4px; font-size: 16.5px; }
  .pe-sub { margin: 0; font-size: 13px; color: var(--muted); }

  /* 01 Portability Matrix */
  .port-legend {
    display: flex;
    flex-wrap: wrap;
    gap: 14px;
    font-size: 12px;
    color: var(--muted);
    margin-bottom: 12px;
  }
  .port-legend .dot {
    display: inline-block;
    width: 11px;
    height: 11px;
    border-radius: 3px;
    margin-right: 6px;
    vertical-align: -1px;
  }
  .dot.port-plug { background: #22c55e; }
  .dot.port-adapt { background: #eab308; }
  .dot.port-retrain { background: #f97316; }
  .dot.port-rebuild { background: #ef4444; }
  table.port-table { width: 100%; border-collapse: collapse; font-size: 12px; text-align: center; }
  table.port-table th, table.port-table td {
    padding: 7px 6px;
    border: 1px solid var(--border);
  }
  table.port-table th { background: #162132; color: var(--text); font-weight: 600; white-space: nowrap; }
  table.port-table tbody th { text-align: left; padding-left: 10px; }
  td.port-cell { cursor: pointer; font-weight: 700; transition: transform 0.08s ease, filter 0.1s ease; }
  td.port-cell:hover { filter: brightness(1.25); outline: 1.5px solid #fff; }
  td.port-self { background: #121a27; color: #52637a; }
  td.port-plug { background: rgba(34, 197, 94, 0.22); color: #86efac; }
  td.port-adapt { background: rgba(234, 179, 8, 0.20); color: #fde047; }
  td.port-retrain { background: rgba(249, 115, 22, 0.20); color: #fdba74; }
  td.port-rebuild { background: rgba(239, 68, 68, 0.16); color: #fca5a5; }
  .port-inspector {
    background: var(--panel2);
    border: 1px solid var(--border);
    border-left: 4px solid var(--cyan);
    border-radius: 8px;
    padding: 11px 14px;
    font-size: 13.5px;
    color: var(--text);
  }

  /* 03 Passports & 05 Lanes */
  .passport-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(290px, 1fr));
    gap: 12px;
  }
  .passport-card, .lane-card, .recipe-card {
    background: var(--panel2);
    border: 1px solid var(--border);
    border-radius: 10px;
    padding: 13px 15px;
  }
  .passport-card.accent-cyan, .lane-card.accent-cyan { border-top: 3px solid var(--cyan); }
  .passport-card.accent-gold, .lane-card.accent-gold { border-top: 3px solid var(--gold); }
  .passport-card.accent-green, .lane-card.accent-green { border-top: 3px solid var(--green); }
  .passport-card.accent-purple, .lane-card.accent-purple { border-top: 3px solid var(--purple); }
  .passport-top, .lane-top, .recipe-top {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
  }
  .passport-body, .lane-tag {
    font-size: 11px;
    color: var(--muted);
    background: rgba(255,255,255,0.06);
    padding: 2px 7px;
    border-radius: 6px;
  }
  .passport-homes { display: flex; flex-wrap: wrap; gap: 5px; margin-bottom: 8px; }
  .mini-pill {
    font-size: 11px;
    font-weight: 600;
    color: var(--cyan);
    background: rgba(96, 198, 255, 0.13);
    padding: 2px 7px;
    border-radius: 5px;
  }
  .passport-card p, .lane-card p, .recipe-card p { margin: 0 0 6px; font-size: 12.8px; color: var(--muted); }
  .passport-card p strong, .lane-card p strong, .recipe-card p strong { color: var(--text); }
  .passport-next, .lane-tip { color: #cbd5e1 !important; }

  /* 04 Skill Matrix (0-10 scored heat-map) */
  table.skill-table { width: 100%; border-collapse: collapse; font-size: 12px; text-align: center; }
  table.skill-table th, table.skill-table td { padding: 7px 5px; border: 1px solid var(--border); }
  table.skill-table th { background: #162132; color: var(--text); font-weight: 600; font-size: 11.5px; }
  table.skill-table th.sk-col-hdr { cursor: pointer; user-select: none; }
  table.skill-table th.sk-col-hdr:hover { color: var(--gold); background: #1c2a40; }
  table.skill-table tbody th { text-align: left; padding-left: 10px; white-space: nowrap; }
  td.sk-cell { cursor: pointer; font-weight: 800; font-size: 12.5px; transition: filter 0.1s ease; }
  td.sk-cell:hover { filter: brightness(1.25); outline: 1.5px solid #fff; }
  .sk-hi { background: rgba(34, 197, 94, 0.24); color: #86efac; }
  .sk-good { background: rgba(56, 189, 248, 0.20); color: #7dd3fc; }
  .sk-mid { background: rgba(245, 194, 66, 0.20); color: #fde047; }
  .sk-low { background: rgba(248, 113, 113, 0.22); color: #fca5a5; }
  .sk-0 { background: #101723; color: #475569; font-weight: 500 !important; }

  /* 05 Lanes */
  .lane-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
    gap: 12px;
  }

  /* 06 Checklist */
  .chk-pill {
    display: inline-block;
    font-size: 11px;
    font-weight: 700;
    padding: 2px 8px;
    border-radius: 6px;
  }
  .chk-mj { background: rgba(74, 222, 128, 0.16); color: var(--green); }
  .chk-physx { background: rgba(192, 132, 252, 0.18); color: var(--purple); }
  .sub-td { color: var(--muted) !important; font-size: 12.5px; }

  /* 07 Graduation Ladder */
  .lad-list { display: flex; flex-direction: column; gap: 8px; }
  .lad-row {
    display: grid;
    grid-template-columns: 150px 105px 115px 1fr;
    gap: 10px;
    align-items: center;
    background: var(--panel2);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 12px;
    font-size: 12.5px;
  }
  @media (max-width: 720px) { .lad-row { grid-template-columns: 1fr; } }
  .lad-name strong { display: block; color: var(--text); }
  .lad-badge { font-size: 10.5px; color: var(--gold); }
  .lad-meter { display: flex; align-items: center; gap: 4px; }
  .lad-dot { width: 11px; height: 11px; border-radius: 50%; background: #263449; display: inline-block; }
  .lad-dot.on { background: var(--green); }
  .lad-score { font-size: 11px; color: var(--muted); margin-left: 4px; }
  .lad-time strong { display: block; color: var(--cyan); font-size: 12px; }
  .lad-time span { font-size: 10.5px; color: var(--muted); }
  .lad-note { color: var(--muted); }

  /* 09 Gym to Game Pipeline */
  .pipe-stack { display: flex; flex-direction: column; gap: 8px; }
  .pipe-row {
    background: var(--panel2);
    border: 1px solid var(--border);
    border-radius: 10px;
    padding: 12px 14px;
    display: grid;
    grid-template-columns: 210px 1fr;
    gap: 14px;
    align-items: center;
  }
  @media (max-width: 860px) { .pipe-row { grid-template-columns: 1fr; } }
  .pipe-label strong { display: block; font-size: 13.5px; }
  .pipe-label span { font-size: 11.5px; color: var(--muted); }
  .pipe-unreal .pipe-label strong { color: var(--cyan); }
  .pipe-unity-mj .pipe-label strong { color: var(--gold); }
  .pipe-mlagents .pipe-label strong { color: var(--purple); }
  .pipe-steps {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
  }
  .pipe-box {
    flex: 1 1 135px;
    background: #0e1624;
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 8px 10px;
    font-size: 12px;
    font-weight: 600;
    color: var(--text);
  }
  .pipe-box small { display: block; font-weight: 400; font-size: 11px; color: var(--muted); margin-top: 2px; }
  .pipe-box.shared { border-color: var(--gold); background: rgba(245, 194, 66, 0.08); }
  .pipe-box.done { border-color: var(--green); background: rgba(74, 222, 128, 0.08); }
  .pipe-arr { color: var(--cyan); font-weight: 800; font-size: 15px; }
  .pipe-bridge {
    text-align: center;
    font-size: 12px;
    color: var(--gold);
    background: rgba(245, 194, 66, 0.10);
    border: 1px dashed rgba(245, 194, 66, 0.5);
    border-radius: 8px;
    padding: 6px 12px;
  }

  /* 10 Recipes */
  .recipe-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(310px, 1fr));
    gap: 12px;
  }
  .recipe-card h4 { margin: 0 0 7px; font-size: 14.5px; color: var(--text); }
  .recipe-route { font-size: 11.5px; font-weight: 700; color: var(--cyan); }
  .effort-pill { font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 999px; }
  .effort-easy { background: rgba(74, 222, 128, 0.16); color: var(--green); }
  .effort-med { background: rgba(245, 194, 66, 0.16); color: var(--gold); }
"""

PLAIN_ENGLISH_JS = """
function showPortNote(key) {
  const info = (window.__PORT_NOTES || {})[key];
  const el = document.getElementById('portInspector');
  if (!info || !el) return;
  const colorMap = { plug: '#4ade80', adapt: '#f5c242', retrain: '#fb923c', rebuild: '#f87171', self: '#60c6ff' };
  el.style.borderLeftColor = colorMap[info.code] || '#60c6ff';
  el.innerHTML = '<strong>' + info.src + ' → ' + info.dst + '</strong> &nbsp;<span class="mini-pill">' + info.badge + '</span><br>' + info.reason;
}
function showSkillDetail(key) {
  const info = (window.__SKILL_NOTES || {})[key];
  const el = document.getElementById('skillInspector');
  if (!info || !el) return;
  const col = info.score >= 8.5 ? '#4ade80' : info.score >= 6.5 ? '#38bdf8' : info.score >= 4 ? '#f5c242' : info.score > 0 ? '#f87171' : '#64748b';
  el.style.borderLeftColor = col;
  el.innerHTML = '<strong>' + info.repo + ' — ' + info.skill + '</strong> &nbsp;<span class="mini-pill" style="color:' + col + ';">' + info.score + ' / 10</span><br>' + info.reason;
}
function compareSkillCol(colIdx) {
  const col = (window.__SKILL_COLS || [])[colIdx];
  const el = document.getElementById('skillInspector');
  if (!col || !el) return;
  el.style.borderLeftColor = '#f5c242';
  const items = col.ranked.map((r, i) => '<strong>#' + (i+1) + ' ' + r[0] + ' (' + r[1] + '/10)</strong>: ' + r[2]).join('<br>');
  el.innerHTML = '<strong>Head-to-Head Ranking — ' + col.skill + '</strong><br>' + (items || 'No repos train this skill yet.');
}
"""
