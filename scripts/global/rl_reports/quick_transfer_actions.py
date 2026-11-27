# Vendored from _CODEMATT/GLOBAL/rl_training on 2026-11-27 (GLOBAL moved into this repo).
# Only the path constants were changed; build_rl_reports.py drives it.
"""
Immediate-action add-ons for the top of rl_training_overview.html (below the Top-3 urgent cards):

  A. "0-2 h training actions" - things that finish (or make a resumable, useful step) in under 2 hours.
     Every entry names the documented command and where its time estimate comes from.
  B. "TRANSFER" - the easiest ways to put an existing agent into another game without training from scratch,
     grounded in the brain/body crawl of GLOBAL/station (same socket = obs size -> action size, same body).

Edit the QUICK and TRANSFER lists below; the shared-socket line is recomputed from station/data/fleet.json.
"""
from __future__ import annotations

import html
import json
from collections import defaultdict
from pathlib import Path

import sys  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import station_common as sc  # noqa: E402

ROOT = sc.WORKSPACE
FLEET = sc.OUT / "fleet.json"


def esc(s) -> str:
    return html.escape(str(s), quote=True)


# kind: "finish" = completes inside 2 h; "slice" = a 2 h time-box of a resumable queue (state is kept, next run continues)
# Every "cmd" is one paste-ready PowerShell line, run from "dir" (relative to the workspace root) and joined with
# the matching Set-Location before it is rendered, so nothing in the page needs a placeholder or a hand edit.
QUICK = [
    {
        "repo": "PoRobotTrain", "kind": "finish", "time": "~20 min", "dir": "PoRobotTrain",
        "what": "Level 0 upper-body retrain + validate",
        "why": "A complete documented train-and-validate in about 20 minutes; use it to try any reward/gain change on the R1 safely on its stand.",
        "cmd": r".venv\Scripts\python.exe -u RobotControl\training\train_level0.py --iters 1500 --num-envs 512; if ($?) { .venv\Scripts\python.exe RobotControl\training\validate_level0.py }",
        "source": "PoRobotTrain/README.md:41 (\"about 20 min on the RTX 5070 Ti\")",
    },
    {
        "repo": "PoRaceUnreal", "kind": "finish", "time": "< 1 h", "dir": "PoRaceUnreal",
        "what": "Go2 racer gate: stand 10 s on CPU MuJoCo (eval only, no training)",
        "why": "Runs the shipped Go2 locomotion brain through eval.py's r0 stand test (10 seeds, must be 10/10) before any new training. "
               "Needs Docker Desktop running (the trainer runs in the porace-trainer container). eval.py is wired to the Go2 model, so point it at a Go2 brain.",
        "cmd": r"powershell -NoProfile -ExecutionPolicy Bypass -File training\run.ps1 -Name gate-go2 eval.py --rung r0 --params final/go2_loco_params.pkl",
        "source": "PoRaceUnreal/training/run.ps1 + eval.py:3 (r0 = stand 10 s, 10 seeds); brain: training/final/go2_loco_params.pkl",
    },
    {
        "repo": "PoSlapUnreal", "kind": "slice", "time": "2 h slice", "dir": r"PoSlapUnreal\training",
        "what": "Next 2 h of the v5 retrain queue (Mom / Tong L1 onward)",
        "why": "#3 urgent item. The queue file is the state, so a 2 h slice is real progress and the next run resumes where it stopped.",
        "cmd": r".venv\Scripts\python.exe overnight.py --resume --hours 2 --queue retrain_v5_queue.json",
        "source": "PoSlapUnreal/training/overnight.py docstring (--resume, --hours); queue in training/runs/",
    },
    {
        "repo": "PoBasketball", "kind": "slice", "time": "2 h slice", "dir": r"PoBasketball\sim",
        "what": "Streetball curriculum queue, 2 h (moving dribble L3a)",
        "why": "#1 urgent item (urgency 90). 26k steps/s ≈ 190M env-steps in 2 h; the queue picks up at the next level.",
        "cmd": r"uv run python -m streetball.queue --hours 2",
        "source": "PoBasketball/sim/streetball/queue.py:5",
    },
    {
        "repo": "PoHillKing", "kind": "slice", "time": "≤ 2 h slice", "dir": r"PoHillKing\Training",
        "what": "Hill queue, one short round (--iters 400)",
        "why": "Polishes the 8-way platform balance item; 400 iterations is the queue's own short setting.",
        "cmd": r".venv\Scripts\python.exe scripts\queue_hill.py --iters 400",
        "source": "PoHillKing/AGENTS.md:220 + queue_hill.py docstring",
    },
    {
        "repo": "PoRobotTrain", "kind": "finish", "time": "~2 min (cached)", "dir": r"..\_rl_scratch",
        "what": "PartyMimic R1 whole-body tracker: refresh the Linux image",
        "why": "PartyMimic needs Linux (torch cu128 + mjlab), so it runs in Docker. This rebuilds partymimic-r1:scratch from "
               "Downloads/_rl_scratch/Dockerfile.partymimic (fast when the layers are cached). Training then runs with: "
               "docker run --rm --gpus all -v \"$PWD/PartyMimic:/pm\" -w /pm partymimic-r1:scratch uv run python -m partymimic.train "
               "--robot g1mj --motion_dir data/motions/g1_mix — fetch the clip library into PartyMimic/data/motions first, it ships empty.",
        "cmd": r"docker build -f Dockerfile.partymimic -t partymimic-r1:scratch .",
        "source": "Downloads/_rl_scratch/Dockerfile.partymimic + PartyMimic README quickstart (MIT)",
    },
]

# ease: 1 = minutes (same game), 2 = hours (warm start / check inputs), 3 = days (adapter or teacher)
TRANSFER = [
    {
        "ease": 1, "title": "New PoOlympic athlete from an existing athlete's brain",
        "from": "PoOlympic slope_matt / slope_kim", "to": "PoOlympic (new 23-joint character)",
        "match": {"body": "yes", "physics": "yes", "senses": "yes", "file": "yes"},
        "evidence": "slope_matt, slope_kim, slope_grandma and slope_zombie all use the same 84 → 23 socket, so one controller already runs four different bodies.",
        "how": "Warm-start the new body from the nearest athlete's brain and fine-tune it in the same queue (Training/runs/queue.txt has an init column).",
    },
    {
        "ease": 1, "title": "New PoBox boxer warm-started from a similar boxer",
        "from": "PoBox match.onnx (Nick / Timmy / Trump / Zombie / Grandpa)", "to": "PoBox (new boxer)",
        "match": {"body": "yes", "physics": "yes", "senses": "yes", "file": "yes"},
        "evidence": "All five boxers' match brains share the 103 → 21 socket.",
        "how": "Pick the boxer with the closest reach and height, then warm-start it on that boxer's own sparring body: training\\train_box.py --stage footwork --xml training\\models\\v2\\grandma_spar.xml --resume training\\checkpoints\\bag_grandma\\latest.pt — swap in the xml and checkpoint of the boxer (and the new body) you picked.",
    },
    {
        "ease": 2, "title": "PoHillKing G1-family fighter → PoRaceUnreal G1 racer",
        "from": "PoHillKing getup_policy / r0–r3 (103 → 29)", "to": "PoRaceUnreal G1 (no race brain yet)",
        "match": {"body": "yes", "physics": "partly", "senses": "check", "file": "yes"},
        "evidence": "The only socket two different games share: 103 inputs → 29 joints, both at 50 Hz, both on a 29-joint G1-family body. PoRaceUnreal's 103 inputs include a 2-value gait clock and it trains in JAX/Brax, so the input order is not proven to match.",
        "how": "First compare the two observation layouts (PoHillKing koth/obs.py vs PoRaceUnreal G1Controller.cs). If they match, try the get-up brain in the race scene as-is; if not, use it as a teacher (distill) for the G1 racer.",
    },
    {
        "ease": 2, "title": "PartyMimic G1 tracker → any 29-DoF G1 game",
        "from": "PartyMimic checkpoints/g1_mlp.pt (MIT code)", "to": "PoHillKing / PoRaceUnreal / PoBasketball G1-family bodies",
        "match": {"body": "yes", "physics": "yes", "senses": "no", "file": "export"},
        "evidence": "Same robot (Unitree G1, 29 joints) on MuJoCo; but the tracker needs a reference motion as input, not game commands.",
        "how": "Use it for motion quality: track mocap clips to get natural poses, then distill into the game's own controller. Checkpoint licence follows its datasets (some non-commercial).",
    },
    {
        "ease": 3, "title": "Go2 robot dog into another Unreal game",
        "from": "PoRaceUnreal Go2 (12 joints)", "to": "PoRobotTrain scene (Go2 model already there) / any Unreal + MuJoCo arena",
        "match": {"body": "yes", "physics": "yes", "senses": "check", "file": "yes"},
        "evidence": "The 12-joint Go2 MJCF exists in both PoRaceUnreal and PoRobotTrain.",
        "how": "Bring the Go2 race brain plus its controller code (obs builder) together; the arena only needs the same MJCF and a 50 Hz control loop.",
    },
]
NOT_EASY = ("PoSumo's ML-Agents fighters are Unity PhysX bodies, so they can't swap brains with the MuJoCo games "
            "without rebuilding the body in MuJoCo. PoFootball and PoSoccer are 2D games and are left out of body "
            "transfer on purpose.")

CSS = """
<style>
.qa-wrap{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:16px;margin:18px 0 22px}
@media(max-width:980px){.qa-wrap{grid-template-columns:minmax(0,1fr)}}
.qa-col{background:var(--panel,#111926);border:1px solid var(--border,#243247);border-radius:14px;padding:14px 16px;min-width:0}
.qa-col h3{margin:0 0 4px;font-size:17px}
.qa-sub{color:var(--muted,#93a4bc);font-size:12.5px;margin:0 0 10px}
.qa-item{border:1px solid var(--border,#243247);border-radius:10px;padding:9px 11px;margin:8px 0;background:rgba(255,255,255,.02)}
.qa-top{display:flex;justify-content:space-between;gap:8px;align-items:baseline;flex-wrap:wrap}
.qa-top strong{font-size:13.5px}
.qa-badge{font-size:10.5px;font-weight:800;padding:2px 8px;border-radius:999px;white-space:nowrap}
.qa-finish{background:rgba(74,222,128,.15);color:#4ade80}.qa-slice{background:rgba(96,198,255,.15);color:#60c6ff}
.qa-e1{background:rgba(74,222,128,.15);color:#4ade80}.qa-e2{background:rgba(245,194,66,.16);color:#f5c242}.qa-e3{background:rgba(248,113,113,.15);color:#f87171}
.qa-why{font-size:12.5px;color:#cbd5e1;margin:4px 0}
.qa-cmd{font-family:Consolas,monospace;font-size:11.3px;background:#070b11;border:1px solid var(--border,#243247);border-radius:7px;padding:5px 8px;color:#cbd5e1;word-break:break-all}
.qa-src{font-size:11px;color:#64748b;margin-top:4px}
.qa-match{display:flex;gap:5px;flex-wrap:wrap;margin:5px 0}
.qa-m{font-size:10.5px;padding:1px 7px;border-radius:6px;border:1px solid var(--border,#243247)}
.qa-yes{color:#4ade80}.qa-partly,.qa-check,.qa-export{color:#f5c242}.qa-no{color:#f87171}
.qa-note{font-size:12px;color:var(--muted,#93a4bc);border-left:3px solid #f5c242;padding:6px 10px;margin-top:10px}
</style>"""


def shared_socket_line() -> str:
    try:
        fleet = json.loads(FLEET.read_text(encoding="utf-8"))
    except Exception:
        return ""
    sock = defaultdict(set)
    for n, r in fleet.get("repos", {}).items():
        for o in r.get("onnx", []):
            nm = o["name"].lower()
            if o.get("obs") and (o.get("act") or 0) > 1 and "critic" not in nm and "value" not in nm:
                sock[(o["obs"], o["act"])].add(n)
    shared = {k: v for k, v in sock.items() if len(v) > 1}
    lines = ", ".join(f"{a}→{b} ({' + '.join(sorted(v))})" for (a, b), v in shared.items()) or "none"
    return (f'<div class="qa-note">Live check from the Brain Vault: of {len(sock)} distinct policy sockets, '
            f'<strong>{len(shared)}</strong> appear in more than one game: {esc(lines)}. '
            f'Moving a brain <em>inside</em> a game is easy; across games it usually needs an input adapter or a teacher run. '
            f'<a href="../../station/pages/vault.html">Open the vault →</a></div>')


def paste_ready(it: dict) -> str:
    """One PowerShell line: change to the repo folder, then run the command as written."""
    where = (ROOT / it["dir"]).resolve() if it.get("dir") else ROOT
    return f"Set-Location '{where}'; {it['cmd']}"


def build_quick_and_transfer_section() -> str:
    q = []
    for it in QUICK:
        cls = "qa-finish" if it["kind"] == "finish" else "qa-slice"
        label = "DONE IN " + it["time"] if it["kind"] == "finish" else it["time"].upper() + " · RESUMABLE"
        q.append(f"""<div class="qa-item"><div class="qa-top"><strong>{esc(it['what'])}</strong><span class="qa-badge {cls}">{esc(label)}</span></div>
<div class="qa-src" style="margin:0">{esc(it['repo'])}</div>
<div class="qa-why">{esc(it['why'])}</div><div class="qa-cmd">{esc(paste_ready(it))}</div><div class="qa-src">Source: {esc(it['source'])}</div></div>""")
    t = []
    ease_lbl = {1: "EASY · MINUTES", 2: "MEDIUM · HOURS", 3: "HARDER · A DAY+"}
    for it in sorted(TRANSFER, key=lambda x: x["ease"]):
        m = "".join(f'<span class="qa-m qa-{esc(v)}">{esc(k)}: {esc(v)}</span>' for k, v in it["match"].items())
        t.append(f"""<div class="qa-item"><div class="qa-top"><strong>{esc(it['title'])}</strong><span class="qa-badge qa-e{it['ease']}">{ease_lbl[it['ease']]}</span></div>
<div class="qa-src" style="margin:0">{esc(it['from'])} → {esc(it['to'])}</div>
<div class="qa-match">{m}</div>
<div class="qa-why">{esc(it['evidence'])}</div><div class="qa-why"><strong>How:</strong> {esc(it['how'])}</div></div>""")
    return f"""{CSS}
<div class="qa-wrap" id="quickTransfer">
  <div class="qa-col">
    <h3>⚡ 0–2 hour training actions</h3>
    <p class="qa-sub"><span class="qa-badge qa-finish">DONE IN</span> finishes inside 2 h. <span class="qa-badge qa-slice">2 H SLICE</span> a time-boxed run of a resumable queue: it keeps its state, so the next run continues. Queue several on the <a href="../../station/pages/gpu_queue.html">GPU Queue</a> page.</p>
    {''.join(q)}
  </div>
  <div class="qa-col">
    <h3>🔁 TRANSFER: add agents without training from scratch</h3>
    <p class="qa-sub">A brain moves cleanly when four things match: <strong>body</strong> (same joints), <strong>physics</strong> (same engine), <strong>senses</strong> (same inputs in the same order) and <strong>file</strong> (ONNX the game can load). Easiest first.</p>
    {''.join(t)}
    <div class="qa-note">{esc(NOT_EASY)}</div>
    {shared_socket_line()}
  </div>
</div>"""
