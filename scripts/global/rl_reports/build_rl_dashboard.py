# Vendored from _CODEMATT/GLOBAL/rl_training on 2026-11-27 (GLOBAL moved into this repo).
# Only the path constants were changed; build_rl_reports.py drives it.
"""Generate the RL overview from the workspace-relative GLOBAL/rl_training/rl_facts.json
and curated per-repo engineering metadata.
"""

from __future__ import annotations

import html as html_mod
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))   # scripts/global (station_common)
import station_common as sc  # noqa: E402

ROOT = sc.WORKSPACE
DASH_DIR = sc.OUT / "reports"
HIST_DIR = sc.OUT                       # rl_facts.json lives at the snapshot root
SCRIPTS_DIR = Path(__file__).resolve().parent
REL_PREFIX = "../../../"                # build_rl_reports.py rewrites these to GitHub URLs
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))
from plain_english_sections import (
    PLAIN_ENGLISH_CSS,
    PLAIN_ENGLISH_JS,
    build_plain_english_section,
)
from train_next_explorer import (
    TRAIN_NEXT_CSS,
    TRAIN_NEXT_JS,
    build_train_next_section,
)
FACTS = json.loads((HIST_DIR / "rl_facts.json").read_text(encoding="utf-8"))


def esc(s) -> str:
    return html_mod.escape(str(s if s is not None else "-"))


def pick_media(repo: str, preferred: list[str]) -> list[tuple[str, str]]:
    """Return list of (relative_path, kind) for existing media files."""
    out = []
    seen = set()
    for rel in preferred:
        p = ROOT / rel
        if p.exists() and rel not in seen:
            out.append((rel, "gif" if rel.lower().endswith(".gif") else "img"))
            seen.add(rel)
    if not out:
        for v in FACTS.get(repo, {}).get("visuals", [])[:2]:
            rel = v["rel"]
            if (ROOT / rel).exists() and rel not in seen:
                out.append((rel, "gif" if v["ext"] == ".gif" else "img"))
                seen.add(rel)
    return out[:2]


# Ground-truth structured catalog across all 10 RL repos (combined with live crawled benchmark/ONNX facts)
PROJECTS = [
    {
        "id": "PoSlapUnreal",
        "name": "PoSlapUnreal",
        "stack": "unreal_mujoco",
        "stack_label": "Unreal 5.8 + URLab + MuJoCo Warp",
        "runtime": "NNE ORT (.onnx) / Basic CPU (.bin)",
        "algorithm": "Custom PPO + Self-Play + League (5-fighter)",
        "agents_summary": "5 humanoids (Grandpa, Kim, Matt, Mom, Tong) · L0–L4, G1 get-up, S2–S3 walk-in",
        "joints_dof": 30,
        "joints_label": "30 DoF (30 actuators, 18 bodies)",
        "obs_act": "136 / 142 → 30",
        "obs_num": 142,
        "est_hours": "~45+ h",
        "est_hours_num": 45,
        "est_steps": "~3.5B+ steps",
        "status": "In Progress (v5 queue 12/26)",
        "status_cls": "warn",
        "preferred_media": [
            "PoSlapUnreal/SCREENSHOTS/review-261124/gif_01_slap_impact_120fps.gif",
            "PoSlapUnreal/SCREENSHOTS/review-261124/ann_01_overnight_matt_overlay.jpg",
        ],
        "l1_summary": "Full-body 3D power-slap fighters stand face-to-face across a podium, wind up using hip-to-hand kinetic chains, absorb impacts without falling, and compete in a 16-slot Elo bracket. Fastest Warp trainer in the fleet (8,192 parallel arenas on one GPU).",
        "l2_skills": [
            "L0–L1 Guard & Absorb (stay upright under 8 m/s strikes)",
            "L2–L3 Reference Slap & Strike/Defend Switching (Rules v2 palm-only)",
            "L4 Self-Play, Cross-Fighter Sparring & 5-Athlete League",
            "S2–S3 RL Walk-In & G1 Knockdown Get-Up (10 s count)",
        ],
        "l2_flow": ["Walk-In (S2)", "Coin Toss / Podium", "Striker (L4) vs Defender", "Palm Impact / Flinch", "Stagger / 10s Count (G1)", "KO or 3-Judge Score"],
        "l2_senses": "142 floats (Interface 5): joint angles/vels (60), root orientation & vel, opponent relative pose/hand vel, strike/defend role & turn phase, command vector (power/accuracy/brace), last actions (30).",
        "l2_actions": "30 continuous PD joint position targets at 50 Hz (8 Hz low-pass filter on v4/physics-4 bodies), mapped through NNE ORT in 0.27–0.37 ms.",
        "l2_rewards": [
            ("Positive", "Clean palm-on-cheek impulse, head snap velocity, standing upright, foot-box compliance, reference slap imitation (L2)"),
            ("Penalties", "Podium touch/foul, self-fall (-100), flinch (>8 cm pre-contact), clubbing/non-palm contact, torque², joint speed², action rate, foot slip"),
        ],
        "l2_systems": "Curriculum climbs L0 → L4 per character independently (no brain sharing across bodies). `grandpa_L4_vs_kim_v4` graduated at 0.921; overnight 2026-11-24 completed Matt L3/L4, Mom L0 (0.69), Tong L0 (0.75). 14 v5 stages remain (Mom/Tong L1–L4, G1 get-up, full league).",
        "l3_physics": "MuJoCo 3.10.0 / Warp 1.19.0 · dt = 0.005 s (200 Hz physics, 50 Hz control, decimation 4) · 18 bodies, 30 actuated joints + free root · Physics v4 bodies with cheek-dent morphs & drawn-hand clamp; Chaos collision disabled on all 124 render primitives.",
        "l3_hyperparams": "Custom PyTorch PPO (`training/ppo.py`) · 8,192 envs · Horizon 24 · MLP [512, 256, 128] · Adaptive KL · Critic warm-up 20 iters on interface widening (136→142) · GPU thermal cooldown (<75 °C) in `overnight.py`.",
        "l3_parity": "Zero-weight widening when adding command inputs; `build_body.py` A12 fingerprint gate; deterministic bit-for-bit UE 5.8 PIE parity verified even when moving Editor scenery actors; CLI: `.venv/Scripts/python.exe overnight.py --resume --hours 16 --queue retrain_v5_queue.json`.",
        "key_files": ["PoSlapUnreal/training/ppo.py", "PoSlapUnreal/training/overnight.py", "PoSlapUnreal/Source/PoSlapUnreal/SlapBind.cpp", "PoSlapUnreal/SCREENSHOTS/review-261124/index.html"],
    },
    {
        "id": "PoBasketball",
        "name": "PoBasketball",
        "stack": "unreal_mujoco",
        "stack_label": "Unreal 5.8 + URLab + MuJoCo Warp",
        "runtime": "NNE ORT (.onnx) + Browser (onnxruntime-web)",
        "algorithm": "Custom PPO + Horizon Curriculum + Anchor Loss",
        "agents_summary": "1 humanoid (29-DoF) · Balance, Locomotion, Scramble, Stationary Dribble (L2a); Moving Dribble & Shot planned",
        "joints_dof": 29,
        "joints_label": "29 DoF (29 actuators, 18 bodies)",
        "obs_act": "125 → 29",
        "obs_num": 125,
        "est_hours": "~30+ h",
        "est_hours_num": 30,
        "est_steps": "~2.5B+ steps",
        "status": "L2a Dribble Passed (100%)",
        "status_cls": "pass",
        "preferred_media": [
            "PoBasketball/SCREENSHOTS/20261121_review/gif_new_dribble.gif",
            "PoBasketball/SCREENSHOTS/20261121_review/ann_new_dribble.jpg",
        ],
        "l1_summary": "A physics-simulated 29-DoF humanoid learns basketball motor primitives from scratch—standing, walking, scrambling for a loose ball, and right-handed stationary dribbling that recently hit 98.7–100% gate success after making pelvis drift observable.",
        "l2_skills": [
            "L0 Balance / Stand & L1 Locomotion (production)",
            "L2a Stationary Dribble (`dribble_anchor10`, 98.7–100% on 1000-ep re-test)",
            "L3b Scramble (loose-ball recovery)",
            "L3a Moving Dribble & L4 Set Shot (untrained; needs composite BC teacher)",
        ],
        "l2_flow": ["L0 Stand", "L2a Stationary Dribble", "Ball Loose (>0.75 m)?", "L3b Scramble Recovery", "Hand-back to Dribble", "Tier 4/5 Walk & Shoot (WIP)"],
        "l2_senses": "125 floats (`shared/obs_spec.json`): gravity, pelvis lin/ang vel, 29 joint pos/vel, ball relative pos/vel, hand/foot contacts, height, countdown, and task block (apex target 0.8 m, anchor vector dir_x/dir_y/dist_m).",
        "l2_actions": "29 continuous PD targets at 50 Hz; shared interface (`32f5b79d`) across all skill families so the Unreal director switches policies with zero pose snapping.",
        "l2_rewards": [
            ("Positive", "Clean ball bounces within 0.3 m anchor, apex height tracking (0.8 m), palm touch timing, continuous dribble streak, upright posture"),
            ("Penalties", "Pelvis drift (L2), ball lost >1.5 m, fall, torque², joint angular vel², action rate, foot slip (`shared/reward_config.json`)"),
        ],
        "l2_systems": "Breakthrough on 2026-11-21/22: stationary dribble plateaued at 71% because a memoryless MLP couldn't see pelvis drift. Injecting the start-spot vector into unused task slots (zero-init via `open_task_inputs.py` + `--freeze-norm`) jumped gate success from 71% → 98.7–100%. Next: composite locomotion+dribble BC teacher for `l3a_walk`.",
        "l3_physics": "MuJoCo 4.0.0 / Warp 4.0.0 · 18 bodies, 29 actuators + free ball (`court.xml` adds explicit `ball_palm_l`/`ball_palm_r` contact pairs) · 50 Hz policy over 2 ms/5 ms sim steps.",
        "l3_hyperparams": "Custom PPO (`streetball`) · 4,096 envs · Horizon 24 · MLP [512, 256, 128] · KL target 0.02 · 2 epochs · LR 2e-5 → 1e-7 · Anchor coef 1.0 · `--freeze-norm` (critical when resuming ONNX-rebuilt checkpoints).",
        "l3_parity": "Export parity `1.6e-6` vs PyTorch; `sync_policies_to_unreal.py` now syncs `{scene,humanoid,court}.xml` together so Unreal contact pairs never go stale. Web viewer runs the same `.onnx` in-browser via `onnxruntime-web`. CLI: `uv run python -m streetball.train --level l2a_time`.",
        "key_files": ["PoBasketball/docs/20261122/log.md", "PoBasketball/docs/20261122/dribble_anchor10.md", "PoBasketball/shared/reward_config.json", "PoBasketball/SCREENSHOTS/20261121_review/index.html"],
    },
    {
        "id": "PoHillKing",
        "name": "PoHillKing",
        "stack": "unity_mujoco",
        "stack_label": "Unity 6 + org.mujoco + MuJoCo Warp",
        "runtime": "Unity InferenceEngine 2.6 (.onnx)",
        "algorithm": "rsl_rl PPO + Self-Play + AMP Motion Prior",
        "agents_summary": "3 humanoids (Kim, Trump, Grandpa; Brawler scaffold) + G1 · R0–R5 ladder, Get-Up, FFA, Strike Pass",
        "joints_dof": 29,
        "joints_label": "29 DoF (29 actuators, 30 bodies)",
        "obs_act": "103 / 112 / 121 → 29",
        "obs_num": 112,
        "est_hours": "~35+ h",
        "est_hours_num": 35,
        "est_steps": "~2.0B+ steps",
        "status": "Platform Candidates Ready",
        "status_cls": "info",
        "preferred_media": [
            "PoHillKing/SCREENSHOTS/ui-261124-po-standard-audit/after-game-1366.png",
            "PoHillKing/SCREENSHOTS/ui-261124-po-standard-audit/after-diag-1366.png",
        ],
        "l1_summary": "8-fighter King-of-the-Hill brawls atop a concrete mountain platform surrounded by a rising sea. Fighters shove, jab, recover from falls with dedicated get-up brains, and try to knock rivals off the 2.8 m rim while staying upright.",
        "l2_skills": [
            "R0 Stand & R1 Walk/Turn (99.4% impact survival, 0.14 m/s error)",
            "R2 Plateau Hold & R3 Approach (goal-driven geometric command law)",
            "R4–R5 Duelist, 4-Body Hill4 & League Fighters + Get-Up (99.5% up in 5 s)",
            "Concrete Platform `strike_pass` (2026-11-24): `trump_p261124_a` (0.680 3-way!) & `kim_p261124_a` (0.525)",
        ],
        "l2_flow": ["Spawn on 2.5 m Concrete Platform", "Fight (Fighter ONNX + Shoves)", "Fallen? → Get-Up ONNX", "Crosses 2.8 m Rim → Sea / KO", "Last Standing (1.5 s Confirm)", "Elo Update & Next Round"],
        "l2_senses": "103 locomotion obs (gravity, pelvis vel, 29 joint pos/vel, last actions, 3-dim command) + 9 combat inputs (112 total: opponent rel pos/vel, rim distance/dir) + optional multi-opponent slots (121).",
        "l2_actions": "29 continuous PD position targets (`ctrl = default + 0.5 * action`) at 50 Hz over 2 ms physics; optional `fighter_action.json` smoothing (0.6) + critic `fighter_value.onnx` for live win-odds bar.",
        "l2_rewards": [
            ("Positive", "Outward impulse on opponent (`strike_push` on hand/arm/foot contact), opponent radius > rim, terminal win (+20/+30), upright & alive, optional CMU/100STYLE AMP style reward"),
            ("Penalties", "Self-ejection / fall (-20), draw (-20), jitter/action rate, torque², joint speed², foot slip, arm contact (disabled on Trump)"),
        ],
        "l2_systems": "Arena widened on 2026-11-23 (`PLATEAU_R 1.5→2.5 m`, `OUT_RADIUS 1.7→2.8 m`) with scripted shoves/jabs layered in game mode. Overnight 2026-11-24 `strike_pass` produced `trump_p261124_a` (3-way share 0.217→0.680, duel vs Grandpa 0.037→0.411, 2.8× smoother) and `kim_p261124_a` (0.525). Next: round-robin balance & Unity parity gates.",
        "l3_physics": "MuJoCo 3.15.0 / Warp 3.15.0 · dt = 0.002 s (500 Hz physics, 50 Hz control, decimation 10) · 29 DoF Unitree G1 / human rigs · Convex dome + concrete platform mesh (4-point foot multi-contact); PhysX completely bypassed.",
        "l3_hyperparams": "`rsl_rl 5.5.1` PPO · 1,024–4,096 envs · Horizon 24 · MLP [512, 256, 128] · Critic warm-up 15 iters on warm-start · Optional LSGAN AMP discriminator (`koth/amp.py`, 104-dim 2-frame features, 19.8k CMU pairs/body).",
        "l3_parity": "Solved 3 Unity `org.mujoco` plugin traps (binary STL 'solid' header, default-class `biasprm` truncation, `MjActuator.OnSyncState` zeroing ctrl). `ModelDumpCheck` 0 mismatches; statistical duel parity verified (`53%` Unity vs `53.5%` CPU MuJoCo). CLI: `scripts/strike_pass.py --tag p --cache platform_cache.json --or-share`.",
        "key_files": ["PoHillKing/docs/rl_optimization_log.md", "PoHillKing/Training/scripts/strike_pass.py", "PoHillKing/Training/koth/amp.py", "PoHillKing/Assets/_Project/Core/Editor/GameScene.cs"],
    },
    {
        "id": "PoBox",
        "name": "PoBox",
        "stack": "unity_mujoco",
        "stack_label": "Unity 6 + org.mujoco + MuJoCo Warp",
        "runtime": "Unity InferenceEngine (.onnx)",
        "algorithm": "Custom PPO + 9-Opponent Gauntlet + Handover Banks",
        "agents_summary": "10 boxers (Matt, Zombie, Nick, Trump, Grandma, Grandpa, Kim, Sokol, Timmy, Soldat) + Ref & Card Walker",
        "joints_dof": 21,
        "joints_label": "21 DoF (21 actuators, 12 bodies)",
        "obs_act": "103 → 21 (+ value)",
        "obs_num": 103,
        "est_hours": "~40+ h",
        "est_hours_num": 40,
        "est_steps": "~2.2B+ steps",
        "status": "v4r Carry-On Graded",
        "status_cls": "info",
        "preferred_media": [
            "PoBox/SCREENSHOTS/2026-11-25-new-brains/gif_timmy_trump.gif",
            "PoBox/SCREENSHOTS/2026-11-25-new-brains/gif_knockdown_getup_zombie.gif",
        ],
        "l1_summary": "Physics-driven 3D boxing with 10 distinct fighter bodies (plus RL referee and ring-card walker) fighting 1v1 bouts and 8-way Free-For-All melees. Boxers learn footwork, punching, head guard, daze survival, and knockdown get-up in MuJoCo.",
        "l2_skills": [
            "R0–R2 Footwork (stand, walk, 360° turn, CMU style reward)",
            "R3 Knockdown Get-Up (`getup.onnx`, 28–29/30 up in 1.7–2.8 s)",
            "R4–R7 Match Boxing (`match.onnx` — attack, guard, chin/daze, carrying on after get-up)",
            "Overnight `v4r_*` handover runs: `v4r_trump` (0.33 falls/min vs 8.67 shipped, 69% guard, 25/28 carry-on!)",
        ],
        "l2_flow": ["Ring Bell (v3 Bodies)", "Match Policy (103→21)", "Clean Head/Body Hit (Daze)", "Knockdown → Get-Up Policy", "15-Step Blend Handover", "KO / 10-Count / Decision"],
        "l2_senses": "103 floats: 100-dim proprioception + opponent torso/head/gloves relative state + ring ropes + 3-dim state/command tail (reserve, own daze, opponent daze with `--obs-state`).",
        "l2_actions": "21 continuous PD joint targets at 50 Hz (5 ms physics step) + critic `value` output; per-joint human-scaled action ranges in `models/v3`.",
        "l2_rewards": [
            ("Positive", "Closing glove-to-head/body hits (>1 m/s, head 1.5×), knockdown bonus, blocking head shots, upright stance & facing, combo variety, handover recovery (`--recover-w`)"),
            ("Penalties", "Taking hits, fall (-12 to -20), repeat-punch decay, self-contact, wasted power/torque², joint speed², jerky actions, foot slip, leaning/clinching"),
        ],
        "l2_systems": "Graded by a strict 7-line C MuJoCo exam (`tools/exam_v2.py`): footing, attack, under-pressure, guard, chin, getting up, carrying on. Overnight `v4r_*` handover training lifted Trump's footing from 8.67→0.33 falls/min and guard to 69%. Open: `under-pressure` & punching recipes for short-reach/low-strength bodies (Trump, Grandma, Soldat) + Phase F crowd melee.",
        "l3_physics": "MuJoCo 3.15.0 / Warp 3.15.0 · dt = 0.005 s (200 Hz physics, 50 Hz control) · 12 bodies, 21 actuated joints per boxer (`models/v3` human force limits & skull-inset heads) · Runs 60 FPS on Pixel 9 Pro Android (0.64–1.05 ms physics).",
        "l3_hyperparams": "Custom PyTorch PPO (`Training/ppo.py`) · 3,072–4,592 rings · Horizon 24 · MLP [512, 256, 128] · Value Huber 10 · Max std 0.35 · 50% handover-bank spawns + 15-step blend · `train_resilient.py` auto-restarts on GPU thermal faults (180 s cooldown).",
        "l3_parity": "Pinned MuJoCo 3.15.0 across Python, Windows DLL, Android arm64 `.so`, and WebGL WASM; `mj_kinematics` called before obs to match trainer. `MjParityTests` 3/3 & `MjGateTests` 24/24 pass. CLI: `uv run python tools/queue_runner.py` & `tools/exam_v2.py --only trump`.",
        "key_files": ["PoBox/docs/rl_optimization_log.md", "PoBox/docs/tasks.md", "PoBox/Training/train_gauntlet.py", "PoBox/Training/tools/exam_v2.py"],
    },
    {
        "id": "PoOlympic",
        "name": "PoOlympic",
        "stack": "unity_mujoco",
        "stack_label": "Unity 6 + org.mujoco + mjlab / MuJoCo Warp",
        "runtime": "Unity InferenceEngine (.onnx)",
        "algorithm": "rsl_rl PPO + Symmetry/Mirror Loss + Multi-Task Curriculum",
        "agents_summary": "4 humanoid bodies (Matt, Zombie 1.1 m, Grandma 1.6 m, Kim) · 18 playable Olympic events, 137 ONNX brains",
        "joints_dof": 23,
        "joints_label": "23 DoF (23 actuators per athlete)",
        "obs_act": "84 / v4 Stance → 23 (+ critic)",
        "obs_num": 84,
        "est_hours": "~30+ h",
        "est_hours_num": 30,
        "est_steps": "~1.8B+ steps",
        "status": "18 / 30 Events Playable",
        "status_cls": "pass",
        "preferred_media": [
            "PoOlympic/SCREENSHOTS/events_roster_after_assets.png",
            "PoOlympic/parity/ui/fx_menu_before_after.png",
        ],
        "l1_summary": "An 8-lane full-contact physics Olympics inside a 400 m indoor stadium. Four physically distinct athlete bodies (including a 1.1 m hunched Zombie and a 65 kg Grandma) compete across 18 playable events—from Iron Pedestal balance and Flamingo single-leg stance to 30 m All-Fours Crawl and 15° Alpine Ramp climbs.",
        "l2_skills": [
            "R0 Balance (Iron Pedestal) & R2 Omnidirectional Sprint/Turn/Slalom/Crab/Brake/Steeplechase",
            "Contract v4 Stance Skills (`rs_*`): Torso Archer, Deep Squat, Javelin Reach, Flamingo, Cadence March",
            "All-Fours Crawl (`crawl_*.onnx`, Events 8 & 23 Trench Crawl) & R4 15° Alpine Ramp (`slope_*.onnx`, 48/48 finish)",
            "R3 Supine/Prone Get-Up solved on Mattbio & Grandma (Zombie get-up & Events 15–18/20–21/24–30 still open)",
        ],
        "l2_flow": ["Stadium Hub Event Pick", "8-Lane Roster Compose (Crowd Contact)", "Countdown Hold", "Event Rules + Steering + Policy ONNX", "DQ / Fall / Finish Judge", "Podium, Records & Plackett-Luce Odds"],
        "l2_senses": "84 floats base locomotion/crawl (Froude-scaled by body leg length λ) + Contract v4 extended stance commands (pelvis height, single-leg flag, torso/hand targets, cadence phase clock).",
        "l2_actions": "23 continuous PD targets (`ctrl` + `action_raw`) + separate `.critic.onnx` value networks for live confidence & win-odds calibration.",
        "l2_rewards": [
            ("Positive", "Velocity/yaw tracking (stride-averaged yaw for Zombie/Grandma), gait air-time, stance target accuracy, Froude-scaled personality style rewards (Zombie hunch/shuffle, Grandma stoop)"),
            ("Penalties", "Falls, lateral acceleration cap (3 m/s²), yaw wobble, symmetry mirror loss, torque², joint speed², action rate, foot slip"),
        ],
        "l2_systems": "Largest brain library in the workspace (137 ONNX files across 4 bodies and multiple event families) with full 8-athlete crowd collisions (`16,184/16,184` part pairs audited). Recent fixes solved warm-start normalizer drift (`--norm-count 1e12`) and empty Adam state (`critic_warmup_runner`).",
        "l3_physics": "MuJoCo 3.11.0 / mjlab 1.6.0 / Warp 1.17.0 · dt = 0.005 s · 23 actuators/athlete (up to 1,472 actuators in 16-lane roster scenes!) · Full self-collision on Zombie/Grandma/Mattbio; verified 57.6 FPS on Pixel 9 Pro ARM with `1.09e-6` 5 s trajectory parity.",
        "l3_hyperparams": "`rsl_rl 5.4.2` PPO + `SymmetricRunner` · 4,096 envs · Horizon 24 · MLP [512, 256, 128] · Entropy 0.001 · Adaptive LR (floor 1e-5) · Froude-scaled speed/torque/gust ranges per body.",
        "l3_parity": "6-tier parity gates (G0 model fingerprint 0 mismatches, G2–G4 <1e-6, G5 5 s closed-loop drift `1.1e-6`, G6 mixed 8-lane meet 8/8 PASS). CLI: `Training/staged_queue.txt` & `eval_cpu.py --rung S`.",
        "key_files": ["PoOlympic/docs/tasks.md", "PoOlympic/docs/rl_optimization_log.md", "PoOlympic/docs/CONTRACT_V4_STANCE_PROPOSAL.md", "PoOlympic/Training/staged_queue.txt"],
    },
    {
        "id": "PoRaceUnreal",
        "name": "PoRaceUnreal",
        "stack": "unreal_mujoco",
        "stack_label": "Unreal 5.8 + URLab + Brax/JAX + MuJoCo Warp",
        "runtime": "NNE ORT (.onnx) + Browser Web Policy Viewer",
        "algorithm": "Brax PPO (JAX + MuJoCo Playground / MJX)",
        "agents_summary": "3 raceable bodies (Go2 quadruped, Matt 29-DoF humanoid, 3-wheel Stroller) + Grandma/G1 stubs",
        "joints_dof": 29,
        "joints_label": "3 / 12 / 29 DoF (Stroller / Go2 / Matt)",
        "obs_act": "48→12 (Go2) · 103→29 (Matt)",
        "obs_num": 103,
        "est_hours": "~12+ h",
        "est_hours_num": 12,
        "est_steps": "~500M+ steps",
        "status": "Slices 0–5 + Stroller Done",
        "status_cls": "pass",
        "preferred_media": [
            "PoRaceUnreal/SCREENSHOTS/261124-v4-audit.png",
            "PoRaceUnreal/SCREENSHOTS/261124-v4-audit/after_root_1366x768.png",
        ],
        "l1_summary": "Cross-species physics racing where a Unitree Go2 robot dog, a 29-DoF humanoid (Matt), and a motorized three-wheeled jogging stroller race head-to-head on flat ovals and sprints using identical tactical passing/blocking rules.",
        "l2_skills": [
            "Go2 Quadruped: Flat (`go2_loco`), Banked (`go2_loco_bank`), Steeplechase (`go2_loco_steeple`), Get-Up (`go2_getup`)",
            "Matt Humanoid: Flat sprint (`matt_loco`, 10/10 rungs on MuJoCo 4.0.0)",
            "Jogging Stroller: Rear-wheel drive + front steering (`stroller_roll`, 100/100 graduation in 16 min!)",
            "Pending: Grandma (`g14` verification on MuJoCo 4.0) & G1 humanoid (no race brain yet)",
        ],
        "l2_flow": ["Grid Spawn & Countdown", "Track Progress & Tactics (Pass/Block)", "Policy ONNX (NNE ORT)", "Fallen? → Get-Up / Unstick / Reset", "Checkpoints & Laps", "Finish Window & Contact Audit"],
        "l2_senses": "Go2: 48 floats (IMU, vel, gyro, 12 joint pos/vel, last actions, velocity/yaw cmd). Matt: 103 floats (pelvis IMU/linvel/gyro, torso up, 29 joints, gait clock 1.375 Hz, cmd). Stroller: chassis vel/tilt + wheel states.",
        "l2_actions": "Go2: 12 continuous leg joint targets; Matt: 29 continuous humanoid joint targets; Stroller: 3 continuous drive/steer wheel commands.",
        "l2_rewards": [
            ("Positive", "Forward cruise velocity tracking (1.5 m/s), yaw tracking, upright/heading stability, smooth wheel drive (stroller), hurdle clearance (steeple)"),
            ("Penalties", "Tipping/falling, off-track, excessive bump impulse, torque², joint angular vel², action rate, foot slip (`DA_RaceRewardConfig`)"),
        ],
        "l2_systems": "Only repo in the fleet using **JAX / Brax PPO** (`playground 0.2.0` + `mujoco_warp 3.15.0`) inside Docker/Linux-style workflow, achieving a blisteringly fast `38.6 ms/step` (`26,506 sps` at 1,024 envs). All 3 active racers reproduce Python reference races down to `0.000 cm` in Unreal 5.8.",
        "l3_physics": "MuJoCo 4.0.0 (Unreal URLab) / 3.15.0 (training) · dt = 0.004 s · Per-racer `impratio` (100 for Go2, 1.0 for Matt & Stroller so wheels don't bind) · Strict runtime no-Chaos assert + `FContactAudit`.",
        "l3_hyperparams": "Brax `0.14.2` PPO (JAX `0.11.2`) · 1,024 envs · Horizon 20 · MLP [512, 256, 128] · Stroller converged in 14.7M steps (16 min on RTX 5070 Ti).",
        "l3_parity": "140 physics arrays readback = 0 mismatches; 5 s closed-loop error `0.000 cm`; seeds 1–5 identical to Python reference races; includes browser Web Policy Viewer (`onnxruntime-web`). CLI: `Training/train.py --env stroller` & `scripts/verify_all.ps1`.",
        "key_files": ["PoRaceUnreal/docs/tasks/todo.md", "PoRaceUnreal/docs/20261110/stroller.md", "PoRaceUnreal/Training/train.py", "PoRaceUnreal/Training/characters/go2.json"],
    },
    {
        "id": "PoRobotTrain",
        "name": "PoRobotTrain",
        "stack": "unreal_mujoco",
        "stack_label": "Unreal 5.8 + MuJoCo CPU/CUDA + Unitree R1 Jetson",
        "runtime": "NNE ORT (.onnx) + Jetson Python 3.8 Runtime",
        "algorithm": "Custom PyTorch PPO + Safety-Envelope Validation Gates",
        "agents_summary": "Unitree R1 Humanoid · Level 0 Upper-Body (stand), 5 Head Skills (Hold/Look/Nod/Shake/Glance), Level 2 Walk Sandbox",
        "joints_dof": 24,
        "joints_label": "2 / 24 DoF (Head Skills / Level 2 Walk)",
        "obs_act": "9→2 (Head) · 83→24 (Walk)",
        "obs_num": 83,
        "est_hours": "~5+ h",
        "est_hours_num": 5,
        "est_steps": "~200M+ steps",
        "status": "L0 + 5 Head Skills Validated",
        "status_cls": "pass",
        "preferred_media": [
            "PoRobotTrain/SCREENSHOTS/v4_after_unreal_overlay_policy_1600x900.png",
            "PoRobotTrain/SCREENSHOTS/v4_audit_after_root_1366x768.png",
        ],
        "l1_summary": "Real-hardware sim-to-real robotics pipeline targeting a physical Unitree R1 humanoid via SDK2/Jetson. Starts safely on a stand (head + upper-body skills with strict torque/angle safety envelopes) and extends to an omnidirectional ground walking sandbox.",
        "l2_skills": [
            "Level 0 Stand-Mounted Upper-Body Policy (`r1_level0_upperbody/policy.onnx`)",
            "5 Validated Head Skills: `hold_centre`, `look_at`, `nod_yes`, `shake_no`, `glance_around` (9 obs → 2 actions)",
            "Level 2 Omnidirectional Walking Sandbox (83 obs → 24 joints, `scripts/level2/` Docker training pending promotion)",
            "Level 3 Camera-Driven Following (planned)",
        ],
        "l2_flow": ["Safety Check (±10° Start Gate)", "50 Hz Policy ONNX", "JointSafetyChannel (Torque/Stale/Timeout)", "200 Hz Sim / 500 Hz Real Hardware", "Unreal Puppet Twin + Blazor Lab", "1 s Hold-Centre Release"],
        "l2_senses": "Head skills (9 floats): `q - default` (2), `dq` (2), `last_action` (2), command (3: `[yaw, pitch, 0]` or `[sin, cos, size]`). Level 2 walk (83 floats): floating-base IMU/vel + 24 joint states + velocity commands.",
        "l2_actions": "Continuous PD joint targets (`default + scale * clip(a, ±5)`, clamped to 90% joint limits) at 50 Hz; hardware safety cap `kp*|target - q| ≤ effort` (0.86 Nm on head yaw).",
        "l2_rewards": [
            ("Positive", "Reference trajectory tracking (1–5° angular error bars), smooth sinusoidal phase tracking, velocity tracking (Level 2)"),
            ("Penalties", "Torque², joint angular velocity², action rate, foot slip (Level 2 only), tracking timeout (>8° for 0.4 s)"),
        ],
        "l2_systems": "Uniquely safety-critical compared to the game repos: every policy must pass SHA-256 `deploy.json` verification, randomized-robot testing (8 randomized physics twins), and the Jetson runtime realistic twin (control delay, low-pass, back-EMF, sensor noise) before unlocking.",
        "l3_physics": "MuJoCo 4.0.1 (23 CPU threads + CUDA PPO) · dt = 0.002 s (500 Hz real / 200 Hz sim / 50 Hz policy) · Calibrated leg friction/armature/damping (`sim_calibration.yaml`); Unreal acts purely as a visual URLab puppet driven every 20 ms.",
        "l3_hyperparams": "Custom PyTorch PPO (`RobotControl/training/ppo.py`) · 512 envs · Horizon 24 · MLP [256, 128, 64] · 25.4 ms/step (`20,158 sps`) · Level 0 trains in ~20 min (1,500 iters).",
        "l3_parity": "Single numpy-only source (`head_skills.py`) shared between training and Jetson Python 3.8 runtime; ONNX vs PyTorch < `1e-4`; Unreal NNE `R1SkillBrain.cpp` enforces `deploy.json` SHA-256 gate. CLI: `.venv/Scripts/python.exe -u RobotControl/training/train_level0.py --iters 1500 --num-envs 512`.",
        "key_files": ["PoRobotTrain/docs/head_skills.md", "PoRobotTrain/docs/level2_walk.md", "PoRobotTrain/RobotControl/training/train_level0.py", "PoRobotTrain/RobotControl/configs/reward_config.json"],
    },
    {
        "id": "PoFootball",
        "name": "PoFootball",
        "stack": "unity_mlagents",
        "stack_label": "Unity 6 + ML-Agents 1.1.0 (MA-POCA + PPO)",
        "runtime": "Unity InferenceEngine (.onnx)",
        "algorithm": "MA-POCA (Offense/Defense) + PPO (Quarterback / Callers)",
        "agents_summary": "22 simultaneous 2D Rigidbody2D players (10 roles) · Offense, Defense, Quarterback & Play-Call brains",
        "joints_dof": 0,
        "joints_label": "0 joints (2D Rigidbody2D, 2–4 cont + 1–2 disc)",
        "obs_act": "134 (84+50) → 2c+1d / 4c+2d",
        "obs_num": 134,
        "est_hours": "~25+ h",
        "est_hours_num": 25,
        "est_steps": "~35M+ steps",
        "status": "Shipped (Rev 18, Base33)",
        "status_cls": "pass",
        "preferred_media": [
            "PoFootball/SCREENSHOTS/new_features/gif_01_dropback_separation.gif",
            "PoFootball/SCREENSHOTS/new_features/02_pursuit_surround.png",
        ],
        "l1_summary": "Full 11-on-11 top-down American football where 22 neural agents coordinate plays, run routes, throw passes, block, and pursue ball carriers on a real 100-yard field using multi-agent centralized critics (MA-POCA).",
        "l2_skills": [
            "Offense Team (`Offense.onnx`, MA-POCA): route running, blocking, ball carrying",
            "Defense Team (`Defense.onnx`, MA-POCA): coverage, blitzing, pursuit, tackling",
            "Quarterback (`Quarterback.onnx`, PPO): dropback, pocket movement, target selection & pass throw",
            "Play-Calling (`OffenseCall.onnx`, `DefenseCall.onnx`): discrete formation/play selection (12 obs → 9 masked calls)",
        ],
        "l2_flow": ["Play-Call Brain (12→9)", "Snap Ball (22 Agents Active)", "QB Dropback + Routes + Coverage", "Pass / Run / Scramble", "Tackle / TD / Incomplete / Stall", "Down & Yardage Update"],
        "l2_senses": "134 total per player (Revision 18): 84-float pre-normalized `[-1, 1]` vector (role one-hot, pos/vel/facing, fatigue, ball state, down/distance, play-call) + 50-float `RayPerceptionSensor2D` (Offense/Defense tags).",
        "l2_actions": "Offense/Defense: 2 continuous (drive, steer) + 1 discrete (2-mask). Quarterback: 4 continuous + 2 discrete (10-mask pass target/throw). Play callers: 1 discrete (9-mask).",
        "l2_rewards": [
            ("Positive", "Yards gained, completions, first downs, touchdowns, defensive stops, sacks, tackles, role-aligned spacing (`Reward_{Progress,Call,Role,Terminal}.cs`)"),
            ("Penalties", "Turnovers, penalties/flags, play stall (`STALL_DEFENSE_CHARGE = 0.25` added in Base33 so defense doesn't stall to the clock), per-term Po penalty ledger"),
        ],
        "l2_systems": "Uses centralized-critic **MA-POCA** so 11 teammates share credit for team yardage/stops rather than fighting over individual rewards. Base32 reached 85% passes and 8.3 net yds/play; Base33 fixed late-training clock stalling via `StallCharge`.",
        "l3_physics": "Unity PhysX 2D (`Rigidbody2D`, 100-yard field, `YARD = 0.9144 m`, real NFL top speeds 6.2–9.2 m/s) · `Time.fixedDeltaTime = 0.02` (50 Hz) · 4 parallel headless env processes over gRPC.",
        "l3_hyperparams": "ML-Agents 1.1.0 (Comms API 1.5.0, Torch 2.5.1+cu121 CPU) · `poca`/`ppo` · Batch 1,024 · Buffer 20,480 · LR 3e-4 (constant) · Beta 5e-3 · Clip ε 0.2 · λ 0.95 · γ 0.99 · 3 epochs · MLP [256, 256], `normalize: false`.",
        "l3_parity": "Strict C# `Systems_ContractTests` asserts `normalize: false` and observation bounds `[-1, 1]`; `Editor_SimSpeed` trap (8× EditorPref) documented & fixed. CLI: `.venv/Scripts/python.exe Tools/run_unattended.py --run football_base33 --config Training/FootballBase33.yaml`.",
        "key_files": ["PoFootball/rl_optimization_log.md", "PoFootball/Training/FootballBase33.yaml", "PoFootball/Assets/_Project/Agents/Agent/Agent_FootballPlayer.cs"],
    },
    {
        "id": "PoSoccer",
        "name": "PoSoccer",
        "stack": "unity_mlagents",
        "stack_label": "Unity 6 + ML-Agents 1.2.0 (PPO)",
        "runtime": "Unity InferenceEngine (.onnx)",
        "algorithm": "PPO (Bot-Curriculum + Self-Play League)",
        "agents_summary": "10 unique named player profiles (Alan, Baily, Brendan, Decoy, Kim, Matt, Nick, etc.) · 1v1 & 4v4 + Elo",
        "joints_dof": 0,
        "joints_label": "0 joints (3D vehicle/rigidbody, 4 cont actions)",
        "obs_act": "173 (39+39+27+68) → 4",
        "obs_num": 173,
        "est_hours": "~20+ h",
        "est_hours_num": 20,
        "est_steps": "~100M+ steps",
        "status": "Passed Bar (91.0% Win)",
        "status_cls": "pass",
        "preferred_media": [
            "PoSoccer/SCREENSHOTS/20261117/gameplay_1v1_ai.gif",
            "PoSoccer/SCREENSHOTS/20261117/gameplay_4v4_1p.gif",
        ],
        "l1_summary": "Fast-paced 1v1 and 4v4 physics soccer where 10 individually trained AI player profiles dribble, turn, shoot, and defend against both scripted bots and each other, hitting a 91.0% win rate with only 6.0% stalemates across 2,000 evaluation matches.",
        "l2_skills": [
            "10 Individual Character Brains (`ALAN`, `BAILY`, `BRENDAN`, `DECOY`, `KIM`, `MATT`, `NICK`, etc.)",
            "Ball Pursuit, Dribbling Control, Goal Shooting & Defensive Positioning (1v1 & 4v4)",
            "All bouts (demo + exhibition) update live tournament Elo + 5-emotion procedural voice matrix",
        ],
        "l2_flow": ["Kickoff Spawn", "4-Sensor Perception (173 floats)", "PPO Policy (4 Cont Actions)", "Ball Contact / Shot / Save", "Goal or Timeout Stalemate", "Elo Update & Goal Replay"],
        "l2_senses": "173 floats across 4 sensor streams (`39 + 39 + 27 + 68`): multi-directional raycasts, own velocity/heading, ball relative pos/vel, goal vectors, opponent/teammate state.",
        "l2_actions": "4 continuous actions (forward/reverse thrust, steering/turn, boost/kick controls).",
        "l2_rewards": [
            ("Positive", "Scoring goals, moving ball toward opponent goal, ball possession/touches, winning matches"),
            ("Penalties", "Conceding goals, stalemate/time cost; newer C9 penalty params (`action_rate_sq`, `energy_penalty`, `torque_sq`, `spin_sq`, `foot_slide`) configured in YAMLs (shipped brains predate them)"),
        ],
        "l2_systems": "Rigorous ablation (`p33team → p37bot`) proved 1v1 bot-curriculum PPO beat league-only training, lifting win rate from 37.1% → 91.0% (1,819/2,000 wins, 6.0% stalemates) while cutting mean episode length from 5,212 → 2,900 steps.",
        "l3_physics": "Unity 3D PhysX · 4 parallel headless Unity envs (`step_ms = 33.7 ms`, `119 sps`) · Goal width curriculum (`goal_width: 6.0`, `bot_strength: 1.0`).",
        "l3_hyperparams": "ML-Agents `1.2.0.dev0` PPO · Batch 2,048 · Buffer 20,480 · Horizon 1,024 · LR 3e-4 (linear decay) · Beta 8e-3 · Clip ε 0.2 · λ 0.95 · γ 0.99 · 3 epochs · MLP [256, 256], `normalize: true` · 10M steps/stage.",
        "l3_parity": "Each character trains from scratch under its own profile (`AGENTS.md`: never copy another profile's brain). Shipped `Reward_*.asset` keeps new penalty weights at 0 until retrained. CLI: `mlagents-learn Training/TRAIN_ALAN_p51bot.yaml --run-id=soccer_p51_alan`.",
        "key_files": ["PoSoccer/docs/training-history.md", "PoSoccer/Training/TRAIN_ALAN_p51bot.yaml", "PoSoccer/AGENTS.md"],
    },
    {
        "id": "PoSumo",
        "name": "PoSumo",
        "stack": "unity_mlagents",
        "stack_label": "Unity 6 + ML-Agents (PPO + Self-Play)",
        "runtime": "Unity InferenceEngine (.onnx)",
        "algorithm": "PPO + Self-Play (Window 10, 50% Latest Self)",
        "agents_summary": "16 unique bipedal sumo fighters (Alan, Baily, Brendan, Decoy, Grandma, Grandpa, Kim, Matt, Nick, etc.)",
        "joints_dof": 14,
        "joints_label": "13–14 powered joints (16 body parts)",
        "obs_act": "53 → 14",
        "obs_num": 53,
        "est_hours": "~15+ h",
        "est_hours_num": 15,
        "est_steps": "~80M+ steps",
        "status": "16 / 16 Roster Promoted",
        "status_cls": "pass",
        "preferred_media": [
            "PoSumo/docs/ui_reports/2026-11-10_bracket16/after_bout.jpg",
            "PoSumo/SCREENSHOTS/20261124_v4-audit/v4-audit.png",
        ],
        "l1_summary": "16 individually trained bipedal sumo wrestlers square off in a circular dohyo ring, pushing, countering, and balancing with 14 continuous joint actions inside a full 16-fighter Elo tournament bracket with zero heuristic bots.",
        "l2_skills": [
            "16 Distinct RL Sumo Fighters promoted in Unique01 (no BYEs, no heuristic bots)",
            "Dual-school shaping (`Reward_WalkObjective` + `Reward_SumoObjective`) with distinct fighter styles (e.g., Counter-Puncher)",
            "Stamina system + 16-slot Tournament Elo + 5-emotion voice matrix (60/400 recorded + procedural synth)",
        ],
        "l2_flow": ["Shikiri Crouch Countdown", "Tachi-Ai Clash (53→14 PPO)", "Ring Pushing & Balance", "Stamina Drain / Edge Pressure", "Ring-Out or Ground Touch", "Winner & Tournament Elo"],
        "l2_senses": "53 floats (`Agent_Biped.cs`): joint states across the 16-part biped rig, torso orientation, opponent relative vector, ring-edge distance, stamina, and extended observations.",
        "l2_actions": "14 continuous joint actuator targets driving the 16-part articulated Unity `Agent_BipedBody` rig.",
        "l2_rewards": [
            ("Positive", "Opponent push-out toward dohyo edge, upright stability, facing opponent, round win / Elo progression from 1200"),
            ("Penalties", "Self ring-out, falling, stamina exhaustion; 4 Po smoothness penalty weights added (kept at 0 on shipped sheets until retrained)"),
        ],
        "l2_systems": "Every fighter has its own `.onnx` brain, `_Character.asset` sheet, and `MANIFEST.md` changelog. Unique01 promoted all 12 former placeholders after fixing the shikiri-crouch thigh-overlap pose, giving a full 16-fighter RL-only bracket.",
        "l3_physics": "Unity 3D PhysX Articulation/Joints (`PART_DEFS`: 16 parts, 16 joints, 13–14 powered) · `SCN_TRAIN_MATT` headless env (`--num-envs 2`) · HTTP live telemetry on `:8787/metrics`.",
        "l3_hyperparams": "ML-Agents PPO + Self-Play (`window: 10`, `play_against_latest_model_ratio: 0.5`) · Batch 1,024 · Buffer 10,240 · Horizon 1,000 · LR 1.2e-4 (constant) · Beta 6e-3 · Clip ε 0.2 · λ 0.95 · γ 0.996 · 4 epochs · MLP [512, 512, 512], `normalize: true`.",
        "l3_parity": "In-place ONNX deployment preserves `.meta` GUIDs (`PoSumo -> Deploy <Name> Brain`); automated harness `PoSumo.MatchTestHarness.Run(10)` verifies unattended 10-match brackets. CLI: `Training/Run-Unique01.ps1`.",
        "key_files": ["PoSumo/docs/onnx_summary.md", "PoSumo/Training/configs/AlanUnique01.yaml", "PoSumo/Training/Run-Unique01.ps1"],
    },
]


def make_flow_svg(steps: list[str]) -> str:
    """Render a crisp inline SVG horizontal episode/director flow diagram."""
    box_w, box_h, gap, pad_x = 152, 46, 26, 12
    total_w = pad_x * 2 + len(steps) * box_w + (len(steps) - 1) * gap
    total_h = 74
    parts = [
        f'<svg viewBox="0 0 {total_w} {total_h}" class="flow-svg" role="img" aria-label="Episode flow diagram">',
        '<defs><marker id="arr" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">'
        '<path d="M0 1 L10 5 L0 9 z" fill="#60c6ff"/></marker></defs>',
    ]
    for i, label in enumerate(steps):
        x = pad_x + i * (box_w + gap)
        y = 14
        fill = "#1a283d" if i < len(steps) - 1 else "#17332a"
        stroke = "#60c6ff" if i < len(steps) - 1 else "#4ade80"
        parts.append(f'<rect x="{x}" y="{y}" width="{box_w}" height="{box_h}" rx="8" fill="{fill}" stroke="{stroke}" stroke-width="1.5"/>')
        # Wrap text cleanly across 2 lines if > 20 chars
        words = label.split()
        line1, line2 = "", ""
        for w in words:
            if len(line1) + len(w) + 1 <= 20:
                line1 = (line1 + " " + w).strip()
            else:
                line2 = (line2 + " " + w).strip()
        if line2:
            parts.append(f'<text x="{x + box_w/2}" y="{y + 20}" text-anchor="middle" fill="#e6edf7" font-size="11.5" font-weight="600">{esc(line1)}</text>')
            parts.append(f'<text x="{x + box_w/2}" y="{y + 35}" text-anchor="middle" fill="#b7c7de" font-size="11">{esc(line2)}</text>')
        else:
            parts.append(f'<text x="{x + box_w/2}" y="{y + 27}" text-anchor="middle" fill="#e6edf7" font-size="11.5" font-weight="600">{esc(line1)}</text>')
        if i < len(steps) - 1:
            ax1 = x + box_w + 2
            ax2 = x + box_w + gap - 4
            parts.append(f'<line x1="{ax1}" y1="{y + box_h/2}" x2="{ax2}" y2="{y + box_h/2}" stroke="#60c6ff" stroke-width="2" marker-end="url(#arr)"/>')
    parts.append("</svg>")
    return "".join(parts)


def make_speed_chart_svg() -> str:
    """Horizontal bar chart comparing real benchmarked env-steps/s across repos."""
    rows = []
    for p in PROJECTS:
        b = (FACTS.get(p["id"], {}).get("benchmark") or {})
        sps = b.get("env_steps_per_s") or 0
        rows.append((p["name"], sps, p["stack"], b.get("num_envs") or "-"))
    rows.sort(key=lambda r: -r[1])
    max_sps = max((r[1] for r in rows), default=1)
    bar_h, gap, top, left, right = 24, 10, 14, 130, 140
    chart_w = 680
    usable_w = chart_w - left - right
    h = top * 2 + len(rows) * (bar_h + gap)
    colors = {"unreal_mujoco": "#60c6ff", "unity_mujoco": "#f5c242", "unity_mlagents": "#c084fc"}
    svg = [f'<svg viewBox="0 0 {chart_w} {h}" class="chart-svg">']
    for i, (name, sps, stack, envs) in enumerate(rows):
        y = top + i * (bar_h + gap)
        bw = max(4, int(usable_w * (sps / max_sps))) if sps > 0 else 2
        col = colors.get(stack, "#93a4bc")
        svg.append(f'<text x="{left - 10}" y="{y + 16}" text-anchor="end" fill="#e6edf7" font-size="12.5" font-weight="600">{esc(name)}</text>')
        svg.append(f'<rect x="{left}" y="{y}" width="{usable_w}" height="{bar_h}" rx="5" fill="#182232"/>')
        svg.append(f'<rect x="{left}" y="{y}" width="{bw}" height="{bar_h}" rx="5" fill="{col}"/>')
        lbl = f"{sps:,} sps ({envs} envs)" if sps > 0 else "not benchmarked"
        svg.append(f'<text x="{left + usable_w + 10}" y="{y + 16}" fill="#cbd5e1" font-size="12">{esc(lbl)}</text>')
    svg.append("</svg>")
    return "".join(svg)


def make_complexity_chart_svg() -> str:
    """Horizontal grouped bars comparing Observation width vs Actuated DoF/Actions."""
    bar_h, gap, top, left, right = 22, 12, 16, 130, 125
    chart_w = 680
    usable_w = chart_w - left - right
    # Body comparison: only repos with a jointed 3D body (PoFootball / PoSoccer are 2D players)
    body_projects = [p for p in PROJECTS if p["id"] not in ("PoFootball", "PoSoccer")]
    h = top * 2 + len(body_projects) * (bar_h + gap)
    max_obs = max(p["obs_num"] for p in body_projects)
    svg = [f'<svg viewBox="0 0 {chart_w} {h}" class="chart-svg">']
    for i, p in enumerate(body_projects):
        y = top + i * (bar_h + gap)
        obs_w = max(6, int(usable_w * (p["obs_num"] / max_obs)))
        dof_w = max(4, int(usable_w * (p["joints_dof"] / max_obs))) if p["joints_dof"] > 0 else 0
        svg.append(f'<text x="{left - 10}" y="{y + 15}" text-anchor="end" fill="#e6edf7" font-size="12.5" font-weight="600">{esc(p["name"])}</text>')
        svg.append(f'<rect x="{left}" y="{y}" width="{usable_w}" height="{bar_h}" rx="5" fill="#182232"/>')
        svg.append(f'<rect x="{left}" y="{y}" width="{obs_w}" height="{bar_h}" rx="5" fill="rgba(96,198,255,0.42)"/>')
        if dof_w:
            svg.append(f'<rect x="{left}" y="{y + 4}" width="{dof_w}" height="{bar_h - 8}" rx="4" fill="#4ade80"/>')
        lbl = f"{p['obs_num']} obs · {p['joints_dof']} DoF" if p["joints_dof"] else f"{p['obs_num']} obs · 2D/3D RB"
        svg.append(f'<text x="{left + usable_w + 10}" y="{y + 15}" fill="#cbd5e1" font-size="12">{esc(lbl)}</text>')
    svg.append("</svg>")
    return "".join(svg)


def make_machine_matrix_html() -> str:
    """Repo × computer matrix of best benchmarked env-steps/s from benchmark/history.json runs."""
    machines: dict[str, dict] = {}
    for p in PROJECTS:
        b = FACTS.get(p["id"], {}).get("benchmark") or {}
        for m in b.get("by_machine") or []:
            e = machines.setdefault(m["hostname"], {"gpu": None, "cpu": None})
            e["gpu"] = e["gpu"] or m.get("gpu")
            e["cpu"] = e["cpu"] or m.get("cpu")
    hosts = sorted(machines, key=str.lower)
    if not hosts:
        return ""
    head_cells = "".join(
        f'<th>{esc(h)}<div class="mach-sub">{esc(machines[h]["gpu"] or "-")}</div></th>' for h in hosts
    )
    body = []
    for p in PROJECTS:
        b = FACTS.get(p["id"], {}).get("benchmark") or {}
        by = {m["hostname"]: m for m in b.get("by_machine") or []}
        best = max((m["best_env_steps_per_s"] for m in by.values()), default=0)
        cells = []
        for h in hosts:
            m = by.get(h)
            if not m:
                cells.append('<td><span class="mach-sub">not run here</span></td>')
                continue
            if not m.get("best_env_steps_per_s"):
                cells.append(f'<td><span class="mach-sub">run failed ({m.get("failed_runs", 0)}×)</span></td>')
                continue
            strong = ' style="color:var(--gold);font-weight:700;"' if m["best_env_steps_per_s"] == best else ""
            envs = m.get("best_num_envs")
            env_txt = f"{envs:,} envs" if isinstance(envs, int) else "envs -"
            when = (m.get("best_when") or "")[:10]
            cells.append(
                f'<td><span{strong}>{m["best_env_steps_per_s"]:,} sps</span>'
                f'<div class="mach-sub">{esc(env_txt)} · {esc(when)} · {m["runs"]} run(s)</div></td>'
            )
        body.append(
            f'<tr><td><a href="#repo-{esc(p["id"])}">{esc(p["name"])}</a>'
            f'<div class="mach-sub">{esc(b.get("machine_gpu") or "no benchmark yet")}</div></td>'
            + "".join(cells) + "</tr>"
        )
    return f"""
  <section id="machineBench" style="margin:18px 0 6px;padding:16px 18px;background:var(--panel);border:1px solid var(--border);border-radius:12px;">
    <h3 style="margin:0 0 4px;font-size:15px;">🖥️ Benchmark Speed by Computer</h3>
    <p class="chart-sub">Best <code>env-steps/s</code> per repository per machine hostname, read from each repo's <code>benchmark/history.json</code>
      (<code>runs[].machine.hostname</code>). Each laptop that runs its repo's benchmark and commits <code>benchmark/history.json</code> fills its own column.
      Compare only cells with the same env count and training setup — a faster GPU is not the only difference.</p>
    <div class="table-wrap">
      <table class="master mach-table">
        <thead><tr><th>Project</th>{head_cells}</tr></thead>
        <tbody>{"".join(body)}</tbody>
      </table>
    </div>
  </section>
"""


def build_html() -> str:
    total_onnx = sum(FACTS.get(p["id"], {}).get("onnx_shipped_count", 0) for p in PROJECTS)
    total_hours = sum(p["est_hours_num"] for p in PROJECTS)
    peak_sps = max((FACTS.get(p["id"], {}).get("benchmark") or {}).get("env_steps_per_s") or 0 for p in PROJECTS)
    count_unreal = sum(1 for p in PROJECTS if p["stack"] == "unreal_mujoco")
    count_unity_mj = sum(1 for p in PROJECTS if p["stack"] == "unity_mujoco")
    count_mlagents = sum(1 for p in PROJECTS if p["stack"] == "unity_mlagents")

    # Master Level 1 Table Rows
    table_rows = []
    cards_html = []

    for p in PROJECTS:
        f = FACTS.get(p["id"], {})
        b = f.get("benchmark") or {}
        onnx_cnt = f.get("onnx_shipped_count", 0)
        mjcf_cnt = f.get("mjcf_count", 0)
        sps = b.get("env_steps_per_s") or 0
        envs = b.get("num_envs") or "-"
        step_ms = f"{b['step_time_ms']:.1f} ms" if b.get("step_time_ms") else "-"
        net_str = "×".join(str(x) for x in b["net"]) if b.get("net") else ("256×256" if p["id"] == "PoFootball" else "512×512×512" if p["id"] == "PoSumo" else "-")
        media = pick_media(p["id"], p["preferred_media"])
        thumb_rel = media[0][0] if media else ""

        table_rows.append(f"""
        <tr data-stack="{p['stack']}" data-search="{esc((p['name'] + ' ' + p['stack_label'] + ' ' + p['algorithm'] + ' ' + p['agents_summary'] + ' ' + p['status']).lower())}">
          <td><a href="#repo-{p['id']}" class="repo-link"><strong>{esc(p['name'])}</strong></a><div class="sub">{onnx_cnt} .onnx · {mjcf_cnt} MJCF</div></td>
          <td><span class="stack-pill {p['stack']}">{esc(p['stack_label'])}</span></td>
          <td>{esc(p['algorithm'])}</td>
          <td data-sort="{p['joints_dof']}">{esc(p['joints_label'])}</td>
          <td data-sort="{p['obs_num']}"><code>{esc(p['obs_act'])}</code><div class="sub">MLP {esc(net_str)}</div></td>
          <td data-sort="{sps}"><strong>{f'{sps:,} sps' if sps else '-'}</strong><div class="sub">{envs} envs · {step_ms}</div></td>
          <td data-sort="{p['est_hours_num']}"><strong>{esc(p['est_hours'])}</strong><div class="sub">{esc(p['est_steps'])}</div></td>
          <td><span class="status-pill {p['status_cls']}">{esc(p['status'])}</span></td>
        </tr>""")

        # Media HTML for Level 2
        media_items = []
        for rel, kind in media:
            badge = "LIVE GIF" if kind == "gif" else "CAPTURE"
            media_items.append(
                f'<figure class="media-box"><span class="media-tag">{badge}</span>'
                f'<img src="{REL_PREFIX}{esc(rel)}" alt="{esc(p["name"])} {esc(Path(rel).name)}" loading="lazy">'
                f'<figcaption><code>{esc(rel)}</code></figcaption></figure>'
            )
        media_grid = f'<div class="media-grid">{"".join(media_items)}</div>' if media_items else ""

        # Versions from benchmark
        vers = b.get("versions") or {}
        vers_str = ", ".join(f"{k} {v}" for k, v in vers.items()) if vers else "See repo pyproject.toml / venv"
        bench_detail = (
            f"Trainer: {b.get('trainer')} · {envs} envs · Horizon {b.get('horizon') or '-'} · "
            f"Step {step_ms} (Sim {b.get('sim_ms', '-')} ms / Learn {b.get('learn_ms', '-')} ms) · {sps:,} env-steps/s on {b.get('machine_gpu', 'RTX 5070 Ti')}"
            if b.get("step_time_ms")
            else "No benchmark/history.json entry recorded yet."
        )

        skills_li = "".join(f"<li>{esc(s)}</li>" for s in p["l2_skills"])
        rew_rows = "".join(
            f'<tr><td><span class="rew-tag {k.lower()}">{esc(k)}</span></td><td>{esc(v)}</td></tr>'
            for k, v in p["l2_rewards"]
        )
        key_links = " · ".join(f'<a href="{REL_PREFIX}{esc(kf)}" class="file-link"><code>{esc(Path(kf).name)}</code></a>' for kf in p["key_files"])

        cards_html.append(f"""
        <article class="repo-card" id="repo-{p['id']}" data-stack="{p['stack']}" data-search="{esc((p['name'] + ' ' + p['stack_label'] + ' ' + p['algorithm'] + ' ' + p['agents_summary'] + ' ' + p['l1_summary']).lower())}">
          <div class="repo-head">
            <div>
              <div class="repo-title-row">
                <h3>{esc(p['name'])}</h3>
                <span class="stack-pill {p['stack']}">{esc(p['stack_label'])}</span>
                <span class="status-pill {p['status_cls']}">{esc(p['status'])}</span>
              </div>
              <div class="repo-meta">
                <span><strong>{onnx_cnt}</strong> shipped <code>.onnx</code></span> ·
                <span><strong>{esc(p['joints_label'])}</strong></span> ·
                <span>I/O <code>{esc(p['obs_act'])}</code></span> ·
                <span>Runtime: <code>{esc(p['runtime'])}</code></span>
              </div>
            </div>
            <div class="level-tabs" role="tablist">
              <button class="lvl-btn active" data-lvl="1" onclick="setCardLevel(this, 1)">Level 1 · Overview</button>
              <button class="lvl-btn" data-lvl="2" onclick="setCardLevel(this, 2)">Level 2 · Behaviors &amp; Rewards</button>
              <button class="lvl-btn" data-lvl="3" onclick="setCardLevel(this, 3)">Level 3 · Physics &amp; Specs</button>
            </div>
          </div>

          <!-- LEVEL 1 -->
          <div class="lvl-pane active" data-pane="1">
            <div class="l1-grid">
              <div class="l1-text">
                <div class="tier-label">Level 1 · Intuitive / Executive Summary</div>
                <p class="lead">{esc(p['l1_summary'])}</p>
                <div class="mini-stats">
                  <div><span>Algorithm</span><strong>{esc(p['algorithm'])}</strong></div>
                  <div><span>Agents &amp; Skills</span><strong>{esc(p['agents_summary'])}</strong></div>
                  <div><span>Training Scale</span><strong>{esc(p['est_hours'])} · {esc(p['est_steps'])}</strong></div>
                  <div><span>Benchmark Speed</span><strong>{f'{sps:,} sps' if sps else 'N/A'} ({step_ms})</strong></div>
                </div>
              </div>
              <div class="l1-thumb">
                {f'<img src="{REL_PREFIX}{esc(thumb_rel)}" alt="{esc(p["name"])} preview" loading="lazy"><div class="thumb-cap"><code>{esc(Path(thumb_rel).name)}</code></div>' if thumb_rel else ''}
              </div>
            </div>
          </div>

          <!-- LEVEL 2 -->
          <div class="lvl-pane" data-pane="2">
            <div class="tier-label">Level 2 · Game Dev / Systems View (Behaviors, Senses, Rewards &amp; Live Media)</div>
            {media_grid}
            <div class="two-col">
              <div class="subpanel">
                <h4>Trained Skills &amp; Behaviors</h4>
                <ul class="clean-list">{skills_li}</ul>
              </div>
              <div class="subpanel">
                <h4>Senses → Brain → Actions</h4>
                <p><strong>Observations:</strong> {esc(p['l2_senses'])}</p>
                <p><strong>Actions:</strong> {esc(p['l2_actions'])}</p>
              </div>
            </div>
            <div class="subpanel">
              <h4>Episode &amp; Director Flow</h4>
              {make_flow_svg(p['l2_flow'])}
            </div>
            <div class="subpanel">
              <h4>Reward &amp; Penalty Matrix</h4>
              <table class="inner-table"><tbody>{rew_rows}</tbody></table>
            </div>
            <div class="subpanel">
              <h4>Systems Analysis &amp; What Still Needs Training</h4>
              <p>{esc(p['l2_systems'])}</p>
            </div>
          </div>

          <!-- LEVEL 3 -->
          <div class="lvl-pane" data-pane="3">
            <div class="tier-label">Level 3 · RL &amp; Robotics Engineering Deep Dive</div>
            <table class="inner-table spec-table">
              <tbody>
                <tr><th>Physics &amp; Body Contract</th><td>{esc(p['l3_physics'])}</td></tr>
                <tr><th>Algorithm &amp; Hyperparameters</th><td>{esc(p['l3_hyperparams'])}</td></tr>
                <tr><th>Benchmark Telemetry</th><td>{esc(bench_detail)}</td></tr>
                <tr><th>Software Stack</th><td><code>{esc(vers_str)}</code></td></tr>
                <tr><th>Parity Traps &amp; CLI</th><td>{esc(p['l3_parity'])}</td></tr>
                <tr><th>Key Source Files</th><td>{key_links}</td></tr>
              </tbody>
            </table>
          </div>
        </article>""")

    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Global RL Training &amp; Agent Comparison Dashboard — _CODEMATT</title>
<style>
  :root {{
    --bg: #0a0f17;
    --panel: #111926;
    --panel2: #172233;
    --border: #243247;
    --text: #e6edf7;
    --muted: #93a4bc;
    --gold: #f5c242;
    --cyan: #60c6ff;
    --green: #4ade80;
    --purple: #c084fc;
    --red: #f87171;
  }}
  * {{ box-sizing: border-box; }}
  body {{
    margin: 0;
    font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
    background: var(--bg);
    color: var(--text);
    line-height: 1.55;
  }}
  a {{ color: var(--cyan); text-decoration: none; }}
  a:hover {{ text-decoration: underline; }}
  .wrap {{ max-width: 1360px; margin: 0 auto; padding: 26px 22px 72px; }}
  header {{
    border-bottom: 1px solid var(--border);
    padding-bottom: 18px;
    margin-bottom: 24px;
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: flex-end;
    gap: 16px;
  }}
  .eyebrow {{
    display: inline-block;
    font-size: 11.5px;
    font-weight: 700;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--gold);
    background: rgba(245, 194, 66, 0.12);
    border: 1px solid rgba(245, 194, 66, 0.35);
    padding: 3px 10px;
    border-radius: 999px;
    margin-bottom: 8px;
  }}
  h1 {{ margin: 0 0 6px; font-size: 29px; }}
  .subtitle {{ margin: 0; color: var(--muted); font-size: 14.5px; max-width: 820px; }}
  h2 {{
    margin: 34px 0 14px;
    font-size: 20px;
    border-left: 4px solid var(--gold);
    padding-left: 12px;
  }}
  .kpis {{
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(195px, 1fr));
    gap: 14px;
    margin-bottom: 24px;
  }}
  .kpi {{
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 15px 17px;
  }}
  .kpi .val {{ font-size: 25px; font-weight: 700; color: var(--gold); }}
  .kpi.cyan .val {{ color: var(--cyan); }}
  .kpi.green .val {{ color: var(--green); }}
  .kpi.purple .val {{ color: var(--purple); }}
  .kpi .lbl {{ font-size: 12.5px; color: var(--muted); margin-top: 3px; }}
  .charts-grid {{
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(460px, 1fr));
    gap: 16px;
    margin-bottom: 26px;
  }}
  .chart-card {{
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 16px 18px;
  }}
  .chart-card h3 {{ margin: 0 0 6px; font-size: 15px; }}
  .chart-card .chart-sub {{ margin: 0 0 10px; font-size: 12.5px; color: var(--muted); }}
  .mach-table {{ margin-top: 4px; }}
  .mach-table td {{ white-space: nowrap; }}
  .mach-sub {{ font-size: 10.5px; color: var(--muted); font-weight: 400; }}
  .chart-svg {{ width: 100%; height: auto; display: block; }}
  .toolbar {{
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    align-items: center;
    justify-content: space-between;
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 12px 16px;
    margin-bottom: 16px;
    position: sticky;
    top: 8px;
    z-index: 20;
    box-shadow: 0 8px 24px rgba(0,0,0,0.45);
  }}
  .toolbar-group {{ display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }}
  input[type="search"] {{
    background: var(--bg);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 7px 12px;
    font-size: 13.5px;
    min-width: 240px;
  }}
  button.flt-btn, button.lvl-btn, button.global-lvl {{
    background: var(--panel2);
    color: var(--muted);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 6px 12px;
    font-size: 12.5px;
    font-weight: 600;
    cursor: pointer;
    transition: all 0.12s ease;
  }}
  button.flt-btn.active, button.lvl-btn.active, button.global-lvl.active {{
    background: rgba(245, 194, 66, 0.16);
    color: var(--gold);
    border-color: rgba(245, 194, 66, 0.5);
  }}
  button:hover {{ color: var(--text); border-color: var(--cyan); }}
  .table-wrap {{
    overflow-x: auto;
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: 12px;
    margin-bottom: 28px;
  }}
  table.master {{
    width: 100%;
    border-collapse: collapse;
    font-size: 13.5px;
  }}
  table.master th, table.master td {{
    padding: 11px 13px;
    text-align: left;
    border-bottom: 1px solid var(--border);
    vertical-align: top;
  }}
  table.master th {{
    background: #162132;
    color: var(--text);
    font-weight: 600;
    cursor: pointer;
    user-select: none;
    white-space: nowrap;
  }}
  table.master th:hover {{ color: var(--gold); }}
  table.master tbody tr:hover {{ background: rgba(96, 198, 255, 0.05); }}
  .sub {{ font-size: 11.5px; color: var(--muted); margin-top: 2px; }}
  .stack-pill {{
    display: inline-block;
    font-size: 11.5px;
    font-weight: 700;
    padding: 3px 8px;
    border-radius: 6px;
    white-space: nowrap;
  }}
  .stack-pill.unreal_mujoco {{ background: rgba(96, 198, 255, 0.15); color: var(--cyan); }}
  .stack-pill.unity_mujoco {{ background: rgba(245, 194, 66, 0.15); color: var(--gold); }}
  .stack-pill.unity_mlagents {{ background: rgba(192, 132, 252, 0.16); color: var(--purple); }}
  .status-pill {{
    display: inline-block;
    font-size: 11.5px;
    font-weight: 700;
    padding: 3px 8px;
    border-radius: 999px;
    white-space: nowrap;
  }}
  .status-pill.pass {{ background: rgba(74, 222, 128, 0.16); color: var(--green); }}
  .status-pill.info {{ background: rgba(96, 198, 255, 0.16); color: var(--cyan); }}
  .status-pill.warn {{ background: rgba(245, 194, 66, 0.16); color: var(--gold); }}
  .repo-card {{
    background: var(--panel);
    border: 1px solid var(--border);
    border-radius: 14px;
    margin-bottom: 20px;
    overflow: hidden;
  }}
  .repo-head {{
    background: #151f2e;
    border-bottom: 1px solid var(--border);
    padding: 15px 20px;
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
  }}
  .repo-title-row {{ display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }}
  .repo-title-row h3 {{ margin: 0; font-size: 19px; }}
  .repo-meta {{ font-size: 12.5px; color: var(--muted); margin-top: 4px; }}
  .level-tabs {{ display: flex; flex-wrap: wrap; gap: 6px; }}
  .lvl-pane {{ display: none; padding: 18px 20px; }}
  .lvl-pane.active {{ display: block; }}
  .tier-label {{
    font-size: 11.5px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--cyan);
    margin-bottom: 10px;
  }}
  .l1-grid {{
    display: grid;
    grid-template-columns: 1.45fr 1fr;
    gap: 20px;
    align-items: start;
  }}
  @media (max-width: 860px) {{ .l1-grid {{ grid-template-columns: 1fr; }} }}
  .lead {{ margin: 0 0 14px; font-size: 15px; color: var(--text); }}
  .mini-stats {{
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
    gap: 10px;
  }}
  .mini-stats > div {{
    background: var(--panel2);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 10px 12px;
  }}
  .mini-stats span {{ display: block; font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.05em; }}
  .mini-stats strong {{ font-size: 13px; color: var(--text); }}
  .l1-thumb {{
    background: #070b11;
    border: 1px solid var(--border);
    border-radius: 10px;
    overflow: hidden;
    text-align: center;
  }}
  .l1-thumb img {{ width: 100%; max-height: 290px; object-fit: contain; display: block; background: #070b11; }}
  .thumb-cap {{ padding: 6px 10px; font-size: 11.5px; color: var(--muted); border-top: 1px solid var(--border); }}
  .media-grid {{
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
    gap: 14px;
    margin-bottom: 16px;
  }}
  .media-box {{
    margin: 0;
    position: relative;
    background: #070b11;
    border: 1px solid var(--border);
    border-radius: 10px;
    overflow: hidden;
  }}
  .media-box img {{ width: 100%; max-height: 360px; object-fit: contain; display: block; }}
  .media-box figcaption {{ padding: 7px 10px; font-size: 11.5px; color: var(--muted); border-top: 1px solid var(--border); }}
  .media-tag {{
    position: absolute;
    top: 8px;
    left: 8px;
    background: rgba(10, 15, 23, 0.85);
    color: var(--gold);
    border: 1px solid rgba(245, 194, 66, 0.45);
    font-size: 10.5px;
    font-weight: 700;
    padding: 2px 7px;
    border-radius: 6px;
  }}
  .two-col {{
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
    gap: 14px;
    margin-bottom: 14px;
  }}
  .subpanel {{
    background: var(--panel2);
    border: 1px solid var(--border);
    border-radius: 10px;
    padding: 13px 16px;
    margin-bottom: 12px;
  }}
  .subpanel h4 {{ margin: 0 0 8px; font-size: 14px; color: var(--gold); }}
  .subpanel p {{ margin: 0 0 6px; font-size: 13.5px; color: var(--muted); }}
  .subpanel p strong {{ color: var(--text); }}
  .clean-list {{ margin: 0; padding-left: 18px; font-size: 13.5px; color: var(--text); }}
  .clean-list li {{ margin-bottom: 4px; }}
  .flow-svg {{ width: 100%; height: auto; display: block; }}
  table.inner-table {{ width: 100%; border-collapse: collapse; font-size: 13.5px; }}
  table.inner-table th, table.inner-table td {{
    padding: 9px 12px;
    border-bottom: 1px solid var(--border);
    text-align: left;
    vertical-align: top;
  }}
  table.inner-table th {{ width: 210px; color: var(--gold); font-weight: 600; }}
  table.inner-table td {{ color: var(--text); }}
  .rew-tag {{
    display: inline-block;
    font-size: 11px;
    font-weight: 700;
    padding: 2px 8px;
    border-radius: 6px;
    text-transform: uppercase;
  }}
  .rew-tag.positive {{ background: rgba(74, 222, 128, 0.16); color: var(--green); }}
  .rew-tag.penalties {{ background: rgba(248, 113, 113, 0.16); color: var(--red); }}
  code {{
    font-family: Consolas, "Cascadia Mono", monospace;
    font-size: 12px;
    background: rgba(255,255,255,0.06);
    padding: 1.5px 5px;
    border-radius: 4px;
    color: #dbeafe;
  }}
  footer {{
    margin-top: 38px;
    padding-top: 16px;
    border-top: 1px solid var(--border);
    color: var(--muted);
    font-size: 12.5px;
  }}
{PLAIN_ENGLISH_CSS}
{TRAIN_NEXT_CSS}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <div>
      <div class="eyebrow">Global RL Portfolio Dashboard · 10 Repositories · 2026-11-25</div>
      <h1>RL Training &amp; Agent Behavior Comparison</h1>
      <p class="subtitle">
        Live-crawled overview of every reinforcement learning project in <code>_CODEMATT</code>—spanning
        <strong>Unreal 5.8 + MuJoCo Warp</strong>, <strong>Unity 6 + org.mujoco</strong>, and <strong>Unity ML-Agents</strong>.
        Switch any project (or the whole page) across <strong>Level 1 (Overview)</strong>, <strong>Level 2 (Behaviors, Rewards &amp; Media)</strong>,
        and <strong>Level 3 (Physics, Hyperparameters &amp; Parity Specs)</strong>.
      </p>
    </div>
    <div class="toolbar-group">
      <span style="font-size:12px;color:var(--muted);">Global Depth:</span>
      <button class="global-lvl active" onclick="setAllLevels(1, this)">All Level 1</button>
      <button class="global-lvl" onclick="setAllLevels(2, this)">All Level 2</button>
      <button class="global-lvl" onclick="setAllLevels(3, this)">All Level 3</button>
    </div>
  </header>

  <nav class="top-nav" aria-label="Quick section links">
    <a href="../../index.html">← Global Hub</a>
    <a href="rl_progress_over_time.html">📈 Progress Over Time</a>
    <a href="../standards/README.md">🧩 Unity ↔ Unreal Standards</a>
    <a href="#trainNextSection" style="border-color:rgba(248,113,113,0.6);color:#fca5a5;">🔥 What Should I Train Next?</a>
    <a href="#machineBench">🖥️ Benchmark by Computer</a>
    <a href="#plainEnglishSection">★ Plain-English &amp; Portability (10 Views)</a>
    <a href="#pe-portability">Can I Move This Agent?</a>
    <a href="#pe-passports">Character Passports</a>
    <a href="#pe-recipes">Starter Recipes</a>
    <a href="#masterMatrix">Level 1 Master Matrix</a>
    <a href="#repoCards">Per-Repo Deep Dive (L1–L3)</a>
  </nav>

  <section class="kpis">
    <div class="kpi"><div class="val">{len(PROJECTS)}</div><div class="lbl">Active RL Repositories ({count_unreal} Unreal · {count_unity_mj} Unity+MJ · {count_mlagents} ML-Agents)</div></div>
    <div class="kpi cyan"><div class="val">{total_onnx}</div><div class="lbl">Shipped <code>.onnx</code> Brains Audited Directly via ONNX Runtime</div></div>
    <div class="kpi green"><div class="val">{peak_sps:,} sps</div><div class="lbl">Peak Training Speed (<code>PoSlapUnreal</code>, 8,192 Warp envs)</div></div>
    <div class="kpi"><div class="val">~{total_hours}+ h</div><div class="lbl">Estimated Cumulative Training Time (~12.7B+ Simulated Steps)</div></div>
    <div class="kpi purple"><div class="val">2 – 30 DoF</div><div class="lbl">Actuated Joint Range (Head Skills → 30-DoF Humanoids)</div></div>
  </section>

  {build_train_next_section()}

  <section class="charts-grid">
    <div class="chart-card">
      <h3>Training Throughput Comparison (env-steps/s)</h3>
      <p class="chart-sub">From each repo's <code>benchmark/history.json</code> (RTX 5070 Ti). GPU MuJoCo Warp runs ~200×–4,000× faster than CPU ML-Agents.</p>
      {make_speed_chart_svg()}
    </div>
    <div class="chart-card">
      <h3>Agent Complexity (Observation Width vs. Actuated DoF)</h3>
      <p class="chart-sub">Blue bar = observation vector width · Green inner bar = actuated continuous joints (DoF).</p>
      {make_complexity_chart_svg()}
    </div>
  </section>

  {make_machine_matrix_html()}

  {build_plain_english_section(FACTS, PROJECTS)}

  <div class="toolbar" id="masterMatrix">
    <div class="toolbar-group">
      <input id="searchBox" type="search" placeholder="Filter projects, algorithms, skills, agents..." oninput="applyFilters()">
      <button class="flt-btn active" data-stack="all" onclick="setStack('all', this)">All (10)</button>
      <button class="flt-btn" data-stack="unreal_mujoco" onclick="setStack('unreal_mujoco', this)">Unreal + MuJoCo ({count_unreal})</button>
      <button class="flt-btn" data-stack="unity_mujoco" onclick="setStack('unity_mujoco', this)">Unity + MuJoCo ({count_unity_mj})</button>
      <button class="flt-btn" data-stack="unity_mlagents" onclick="setStack('unity_mlagents', this)">Unity ML-Agents ({count_mlagents})</button>
    </div>
    <div class="toolbar-group" style="font-size:12px;color:var(--muted);">
      Click table headers to sort · Click repo name to jump to deep-dive
    </div>
  </div>

  <h2>Level 1 — Master Comparison Matrix</h2>
  <div class="table-wrap">
    <table class="master" id="masterTable">
      <thead>
        <tr>
          <th onclick="sortTable(0)">Project ↕</th>
          <th onclick="sortTable(1)">Engine &amp; Physics ↕</th>
          <th onclick="sortTable(2)">RL Algorithm ↕</th>
          <th onclick="sortTable(3, true)">Joints / DoF ↕</th>
          <th onclick="sortTable(4, true)">Obs → Act ↕</th>
          <th onclick="sortTable(5, true)">Benchmark Speed ↕</th>
          <th onclick="sortTable(6, true)">Est. Hours / Steps ↕</th>
          <th onclick="sortTable(7)">Gate Status ↕</th>
        </tr>
      </thead>
      <tbody>
        {"".join(table_rows)}
      </tbody>
    </table>
  </div>

  <h2>Per-Repository Breakdown (Levels 1 → 3)</h2>
  <section id="repoCards">
    {"".join(cards_html)}
  </section>

  <footer>
    Auto-generated from live repository crawl (<code>GLOBAL/rl_training/scripts/collect_rl_facts.py</code> → <code>GLOBAL/rl_training/rl_facts.json</code>).
    Run <code>GLOBAL/rl_training/update_rl.bat</code> anytime after future training runs to refresh all dashboards and record a new time-series snapshot.
  </footer>
</div>

<script>
let activeStack = 'all';
function setStack(stack, btn) {{
  activeStack = stack;
  document.querySelectorAll('.flt-btn').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  applyFilters();
}}
function applyFilters() {{
  const q = (document.getElementById('searchBox').value || '').trim().toLowerCase();
  document.querySelectorAll('#masterTable tbody tr').forEach(tr => {{
    const okStack = activeStack === 'all' || tr.dataset.stack === activeStack;
    const okQuery = !q || (tr.dataset.search || '').includes(q);
    tr.style.display = (okStack && okQuery) ? '' : 'none';
  }});
  document.querySelectorAll('#repoCards .repo-card').forEach(card => {{
    const okStack = activeStack === 'all' || card.dataset.stack === activeStack;
    const okQuery = !q || (card.dataset.search || '').includes(q);
    card.style.display = (okStack && okQuery) ? '' : 'none';
  }});
}}
function setCardLevel(btn, lvl) {{
  const card = btn.closest('.repo-card');
  card.querySelectorAll('.lvl-btn').forEach(b => b.classList.toggle('active', b.dataset.lvl === String(lvl)));
  card.querySelectorAll('.lvl-pane').forEach(p => p.classList.toggle('active', p.dataset.pane === String(lvl)));
}}
function setAllLevels(lvl, btn) {{
  document.querySelectorAll('.global-lvl').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  document.querySelectorAll('.repo-card').forEach(card => {{
    card.querySelectorAll('.lvl-btn').forEach(b => b.classList.toggle('active', b.dataset.lvl === String(lvl)));
    card.querySelectorAll('.lvl-pane').forEach(p => p.classList.toggle('active', p.dataset.pane === String(lvl)));
  }});
}}
let sortDir = {{}};
function sortTable(colIdx, numeric = false) {{
  const tbody = document.querySelector('#masterTable tbody');
  const rows = Array.from(tbody.querySelectorAll('tr'));
  const asc = !sortDir[colIdx];
  sortDir[colIdx] = asc;
  rows.sort((a, b) => {{
    const ca = a.children[colIdx], cb = b.children[colIdx];
    const va = numeric ? parseFloat(ca.dataset.sort || '0') : ca.innerText.trim().toLowerCase();
    const vb = numeric ? parseFloat(cb.dataset.sort || '0') : cb.innerText.trim().toLowerCase();
    if (va < vb) return asc ? -1 : 1;
    if (va > vb) return asc ? 1 : -1;
    return 0;
  }});
  rows.forEach(r => tbody.appendChild(r));
}}
{PLAIN_ENGLISH_JS}
{TRAIN_NEXT_JS}
</script>
</body>
</html>
"""


def main() -> None:
    DASH_DIR.mkdir(parents=True, exist_ok=True)
    out = DASH_DIR / "rl_training_overview.html"
    html = build_html()
    out.write_text(html, encoding="utf-8")
    # Verify every referenced relative image/gif exists on disk
    import re
    srcs = re.findall(r'src="([^"]+)"', html)
    missing = [s for s in srcs if not (DASH_DIR / s).resolve().exists()]
    print(f"Wrote {out} ({out.stat().st_size // 1024} KB) | embedded media={len(srcs)} | missing={len(missing)}")
    if missing:
        print("MISSING:", missing)


if __name__ == "__main__":
    main()

