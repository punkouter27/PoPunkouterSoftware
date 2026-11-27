# RL Training Simulations (MuJoCo & ML-Agents) — Global Audit Prompt

Use this prompt whenever you want to expand, re-audit, or add new RL repositories to `GLOBAL/rl_training/`:

```text
Act as an expert AI/RL Scientist, Robotics Engineer (MuJoCo), and Game AI Dev (ML-Agents).

Analyze all RL repositories under the workspace root that contains this `GLOBAL/` directory (Unreal 5.8 + URLab + MuJoCo Warp + NNE ORT, Unity 6 + org.mujoco + MuJoCo Warp + Sentis, and Unity 6 + ML-Agents PPO/MA-POCA). Update the scripts in GLOBAL/rl_training/scripts/ and regenerate:
1. GLOBAL/rl_training/rl_training_overview.html
   - Level 1: Master comparison matrix, global KPIs, speed & complexity charts
   - Plain-English & Portability section (10 non-technical views: 10x10 portability matrix, human-practice-per-hour speed chart, character passports, skill library, coaching styles, 4-point plug-and-play checklist, gym graduation ladder, pocket brains vs specialist teams, gym-to-game 3-stack pipeline, and top starter recipes)
   - Level 2: Per-repo behaviors, senses/actions, reward & penalty matrices, SVG episode flow diagrams, and real embedded GIFs/screenshots
   - Level 3: Physics/skeleton specs, hyperparameters, parity gates, and direct links to source files
2. GLOBAL/rl_training/history/rl_progress_over_time.html
   - Append a new timestamped snapshot to GLOBAL/rl_training/history/timeline.jsonl and snapshots/
   - Plot brains/behaviors learned over time, benchmark speed improvements, run-over-run deltas, and recently trained .onnx files
```

## Quick Refresh (No Prompt Needed)
Double-click `GLOBAL\rl_training\update_rl.bat` or run:
```powershell
.\GLOBAL\rl_training\update_rl.bat --open
```
