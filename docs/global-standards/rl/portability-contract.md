# Cross-Platform RL Training Standards (Unity ↔ Unreal)

This folder defines the **shared contract** that allows trained `.onnx` agents and MuJoCo bodies to move between **Unity 6 (`org.mujoco` + Sentis)** and **Unreal 5.8 (URLab + NNE ORT)** projects in `_CODEMATT`.

## Why agents can move across engines
Every MuJoCo-based project in the fleet simulates the **same physics engine (MuJoCo)** and executes **standard `.onnx` policy graphs**. Engine differences (Unity left-handed Y-up vs. Unreal left-handed Z-up) live purely in the renderer/bridge layer — as long as both sides follow the same **4-part contract**, a brain trained for one engine runs in the other without retraining physics from scratch.

---

## The 4-Part Portability Contract

### 1. Body & Physics (MJCF)
- **Single source of truth:** Every portable character is defined by a self-contained **MJCF `.xml`** file (joints, actuators, masses, collision geoms, PD gains).
- **Joint order is immutable:** The order of `<actuator>` entries in the MJCF defines the action vector indices `0..N-1`. Never reorder actuators between projects without remapping.
- **Control rate:** Target **50 Hz policy control** (`dt = 0.002` with 10 substeps, or `dt = 0.005` with 4 substeps).
- **Physics engine boundary:** MuJoCo brains (`PoOlympic`, `PoHillKing`, `PoBox`, `PoBasketball`, `PoSlapUnreal`, `PoRaceUnreal`, `PoRobotTrain`) share physics. Unity PhysX / ML-Agents repos (`PoFootball`, `PoSoccer`, `PoSumo`) do not share MuJoCo bodies.

### 2. Socket (`obs → action`)
A brain's **socket** is its `(observation_dim → action_dim)` shape plus the exact ordering/units of every observation feature.
- See [`sockets.json`](sockets.json) for the catalog of active sockets across the fleet.
- **Cross-engine shared socket today:** `103 → 29` (29-DoF G1-family humanoid, used by both **PoHillKing [Unity]** and **PoRaceUnreal [Unreal]**).
- **Target standard for new humanoids:** Use the **29-DoF G1-family** MJCF and standardize the base proprioception prefix (root height/orientation/vel, joint pos/vel, previous action) before appending game-specific task senses.

### 3. ONNX Export Contract (Unity Sentis ↔ Unreal NNE ORT)
- **Opset:** Export at **ONNX opset 17** (supported cleanly by both Unity Sentis and Unreal NNE ORT across all current repos).
- **Tensor names & shapes:**
  - Input: `obs` with shape `[1, obs_dim]` (or `['batch', obs_dim]`), `float32`, no NaNs.
  - Output: `actions` (or `action` / `ctrl`) with shape `[1, act_dim]`, `float32`, deterministic policy mean (strip exploration noise and optional critic heads if the target runtime expects a single output).
- **Normalization baked in:** Bake observation running-mean/std normalization and action clipping/scaling into the exported ONNX graph whenever possible, so Unity and Unreal do not have to duplicate stats files.

### 4. Parity Verification
Before declaring a brain portable across engines, run the checks in [`parity_checklist.md`](parity_checklist.md):
1. Same MJCF body & timestep.
2. Same observation vector values (within `1e-4`) on an identical pose/frame.
3. Same ONNX output tensor on a fixed dummy observation vector in Python ORT, Unity Sentis, and Unreal NNE.
4. Stand/idle smoke test in the target engine for 10 seconds without divergence.

---

## Tools in This Folder

| File | Purpose |
|---|---|
| [`sockets.json`](sockets.json) | Machine-readable catalog of fleet sockets, opsets, tensor names, and body families. |
| [`parity_checklist.md`](parity_checklist.md) | Step-by-step Unity ↔ Unreal transfer & parity verification checklist. |
| [`vault_import.py`](vault_import.py) | Safely copy an `.onnx` brain into another repo (`vault_imports/<SourceRepo>/`), check socket compatibility (`FITS` vs `ADAPTER NEEDED`), and write a `.vault.json` provenance sidecar. |
| [`quick_transfer_actions.py`](quick_transfer_actions.py) | Curated cross-game transfer recipes & 0–2 h quick training actions rendered in the RL Dashboard. |

### Safe Brain Import Command
Run from `_CODEMATT`:
```powershell
python GLOBAL\rl_training\standards\vault_import.py "<SourceRepo>/path/to/brain.onnx" <TargetRepo> --dry-run
```
Drop `--dry-run` to copy the brain into `<TargetRepo>/vault_imports/<SourceRepo>/` without overwriting existing files.
