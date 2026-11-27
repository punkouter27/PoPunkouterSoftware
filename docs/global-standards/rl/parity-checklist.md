# Unity ↔ Unreal Agent Transfer & Parity Checklist

Use this checklist whenever moving a trained `.onnx` policy or MJCF body between a **Unity 6 (`org.mujoco` + Sentis)** project and an **Unreal 5.8 (URLab + NNE ORT)** project.

## 1. Pre-Transfer Compatibility Check
- [ ] Run the socket dry-run check:
  ```powershell
  python GLOBAL\rl_training\standards\vault_import.py "<SourceRepo>/path/to/policy.onnx" <TargetRepo> --dry-run
  ```
- [ ] Confirm both repos use **MuJoCo** physics (ML-Agents / Unity PhysX brains cannot drop into MuJoCo directly).
- [ ] Confirm the ONNX **opset is 17** (or verified supported by both Unity Sentis and Unreal NNE ORT).

## 2. Body (MJCF) Parity
- [ ] Copy the source character's MJCF `.xml` (and referenced meshes) into the target repo if the target does not already have the exact same body.
- [ ] Verify **joint count**, **actuator order**, **control ranges (`ctrlrange`)**, and **PD gains (`kp` / `kv`)** match between training XML and target engine XML.
- [ ] Verify simulation timestep & control rate match (**50 Hz** policy step: e.g. `dt = 0.002 × 10` or `dt = 0.005 × 4`).

## 3. Observation & Action Parity
- [ ] Compare the source observation builder (Python/C#/C++) against the target engine's observation builder feature-by-feature:
  - Gravity vector / root orientation convention (quaternion `w,x,y,z` vs `x,y,z,w`).
  - Angular/linear velocity frame (world frame vs. root-local frame).
  - Joint position offsets (raw `qpos` vs. `qpos - default_qpos`).
  - Phase/gait clock or command vector slots.
- [ ] Verify action post-processing matches: `target_q = default_q + action_scale * clip(action, -1, 1)` (or baked inside ONNX).

## 4. Runtime Smoke Test
- [ ] Feed a fixed zero/test observation vector into Python `onnxruntime` and into the target engine (Sentis / NNE) and confirm max output diff `< 1e-4`.
- [ ] Run a 10-second stand/idle test in the target engine scene with zero commands — verify no exploding joints, NaNs, or instant falls.
- [ ] If the observation layout differs slightly, either add a thin observation adapter in the target controller or use the imported `.onnx` as a **teacher policy** to distill/warm-start in the target repo.
