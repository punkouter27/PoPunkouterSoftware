# Training & environment standard — real Earth

`RULES.md` R35–R43 point here. The numbers live in [`../training-envelope.json`](../training-envelope.json)
(**envelope v2, real Earth**), and [`../tools/check_training.py`](../tools/check_training.py) checks them.
This page explains the reasoning. The JSON file is authoritative.

**The rule behind everything here:** every environment, game or twin, is a piece of the real Earth. A
brain is trained across the physical variation it will meet: real floors, real motors that weaken and
lag, real sensors that drift, real bumps and ramps. That is what lets the **same ONNX** run in every Po
Unreal app *and* go to the physical robot. A game that wants unreal physics (ice rinks, moon gravity)
uses app-specific brains. It never bends the envelope.

---

## 1. Three tiers of compatibility

| | conforming | portable | robot-ready |
|---|---|---|---|
| Means | right interface; loads anywhere | trained across real Earth; correct in every app | portable, and proven against the calibrated twin and the real robot |
| Proven by | sidecar hashes (R10–R14) | a valid card, tier `release`, all 10 eval corners (R39–R41) | tier `robot`: calibration, `sim_real_diff`, deploy gate, staged bring-up (R43) |
| May run | in its own app | in any app | on the physical robot, through the owner app's deploy gate |
| Load check | `--verify-sidecar` | `--require-portable` | `--require-robot-ready` + PoRobotTrain gate |

## 2. Earth (fixed)

- Gravity `[0, 0, −9.81]` m/s². Local g varies 9.78–9.83 (< 0.3%), which is well inside the mass
  randomisation.
- SI units: metres, kilograms, seconds, radians.
- Air drag is neglected (< 1% of body forces below 2 m/s).
- MuJoCo version and solver come from the contract (R17–R19). Timing is the contract's
  `control_dt` (50 Hz), which is the R1's real policy rate.

## 3. Randomisation — the real world a brain will meet (R37)

A card's range must **contain** each range below. Wider is fine.

| Parameter | Minimum range | Why (the real thing it covers) |
|---|---|---|
| floor friction | 0.3 – 1.5 | rubber feet on wet tile, up to dry concrete or carpet |
| body mass scale | 0.9 – 1.1 | manufacturing spread, cables, wear |
| base payload | −1 – +3 kg | battery and Jetson, accessories; covers the R1 twin's +1.22 kg error |
| CoM offset | ±3 cm | payload placement |
| PD `kp` / `kd` scale | 0.85 – 1.15 / 0.7 – 1.3 | firmware gains vs the sim model |
| motor strength | 0.8 – 1.0 | torque sag as the battery voltage drops |
| joint friction scale | 0.5 – 2.0 | gearbox friction, temperature, wear |
| armature scale | 0.8 – 1.2 | rotor inertia estimate |
| joint offset (encoder bias) | ±0.03 rad | zero-point calibration error |
| action latency | 0 – 25 ms | measured DDS + Jetson 10 ms (`sim_calibration.yaml`), plus jitter |
| IMU gyro bias | ±0.02 rad/s | MEMS drift |
| pushes | up to 0.8 m/s, every 4 – 10 s | bumps, people, doors |
| terrain | up to 3 cm roughness, up to 8° slope | door thresholds, cables, mats, ramps, driveways |
| observation noise σ | joint pos 0.01, joint vel 0.15, ang vel 0.2, gravity 0.05 | ≥ the measured R1 sensor noise, with margin |

**App worlds** (`world.json`, checked with `check_training.py world`) must be Earth, inside the envelope
with margin:
- friction 0.4–1.2;
- roughness ≤ 3 cm, slope ≤ 8°;
- latency ≤ 20 ms;
- `condim` 3, gravity `[0,0,−9.81]`;
- the contract's `control_dt`.

## 4. Environment (R36)

- **Observation and action** come only through the contract (131 → 28). Masked roles are PD-held at
  default.
- **Episode** length is 10–30 s. A **timeout is a truncation**, not a termination.
- **Fall** = base height < 0.55 × home, or `|projected_gravity.z|` < 0.3.
- **Reset:**
  - yaw over the full circle;
  - roll/pitch ≤ 0.1 rad;
  - joint noise σ ≤ 0.05 rad;
  - base position σ ≤ 2 cm.
- **Commands** are resampled every 4–10 s.

## 5. Reward (R38)

Ten **core terms** for locomotion skills, each logged separately as `r_<term>`. Weights are free, signs
are fixed. `torque_l2` and `joint_limit` are core because real motors overheat and real joints hit
their stops.

| term | sign | reference weight |
|---|---|---|
| `alive` | + | 0.6 |
| `track_lin_vel_xy` | + | 1.6 · exp(−err²/0.25) |
| `track_ang_vel_z` | + | 0.4 · exp(−err²/0.25) |
| `upright` | + | 0.5 · clip(\|g_z\|, 0, 1) |
| `ang_vel_xy` | − | −0.05 · ‖ω_xy‖² |
| `action_l2` | − | −0.02 · ‖a‖² |
| `action_rate` | − | −0.01 · ‖a − a_prev‖² |
| `torque_l2` | − | −1e-5 · ‖τ‖² |
| `joint_limit` | − | −1.0 per joint past its soft limit |
| `fall` | − | −1.0 on termination |

App-specific terms are named `task_*`.

## 6. Trainer, tiers and logs (R39)

- **Algorithm:** `ppo` (the default), `amp` or `sac`. The network is a plain MLP that exports the
  deterministic mean, with no normalisation in the graph (R9).
- **Tiers:**

  | tier | env steps | portable | robot |
  |---|---|---|---|
  | `smoke` | any | no | no |
  | `dev` | ≥ 10⁶ | no | no |
  | `release` | ≥ 10⁷ | can be | no |
  | `robot` | ≥ 10⁷ | can be | can be (§8) |

- **Beside every `.onnx`:** the sidecar, `<policy>.training.json` (the card, written by the trainer;
  template from `check_training.py example --creature <id> --tier <tier>`) and `train_log.csv`.
- **`train_log.csv`** columns: `iter, env_steps, wall_s, mean_return, mean_ep_len_s, fall_rate`, plus
  `r_<term>` for every term.

## 7. Evaluation in simulation (R40)

Run ≥ 20 episodes on the policy's own creature at **10 corners**:
- `nominal`;
- `friction_lo` (0.3) and `friction_hi` (1.5);
- `payload_hi` (+3 kg);
- `motor_weak` (×0.8);
- `latency_max` (25 ms);
- `push` (0.8 m/s);
- `slope` (8°);
- `rough` (3 cm);
- `sensor_noise` (the minimum σ values plus gyro bias).

**Pass at every corner:**
- ≤ 1 fall per minute;
- velocity error ≤ 0.25 m/s (for locomotion);
- peak torque ≤ 90% of the actuator limit;
- **zero** joint-limit hits.

The last two are what keep a real robot's motors and joints safe.

## 8. The robot tier — from ONNX to the physical robot (R43)

A **portable brain is not yet safe for hardware.** Simulation cannot prove the twin matches the robot.
Tier `robot` adds evidence that only the real robot can give. It applies only to a creature whose
contract has a `hardware` block (today: `unitree_r1_a5`, R21).

1. **Calibrate the twin** against real logs with `PoRobotTrain/RobotControl/tools/calibrate_sim.py`.
   - `sim_calibration.yaml` gets `last_calibrated_at`.
   - Parity: RMS ≤ 2° and max ≤ 5°.
2. **Train and evaluate** at tier `robot`, with the randomisation centred on the calibrated values.
3. **Sim-to-real trajectory gate:** `RobotControl/tools/sim_real_diff.py sim.npz real.npz --tol-deg 5`.
   - ≥ 500 ticks (10 s).
   - Max per-joint error < 5° at **every** 20 ms tick.
4. **Deploy gate:** PoRobotTrain's `RobotControl/models/<skill>/deploy.json` must pass:
   - `policy_sha256` matches;
   - `validation.passed`;
   - `joint_map_sha256` matches.
5. **Staged bring-up, in order:**
   - `gantry`: hung, feet clear;
   - `low_gain`: reduced kp, on the gantry;
   - `supported_floor`: on the floor, with a spotter or tether;
   - `free`.

   `gantry` and `low_gain` must have passed before the card can be robot-ready. A stage passed out of
   order is an error. An e-stop is in reach at every stage.

The ONNX reaches the robot **only** through PoRobotTrain's deploy path, never by copying the file. The
card records all of this under `robot`. `verify_policy.py --verify-sidecar --require-robot-ready` refuses
anything less.

## 9. Benchmark (R42)

Release and robot cards carry `steps_per_s` from the repo's `benchmark.ps1` kit.

## 10. What this does not cover

- **Cross-creature correctness.** A brain is correct on the creature it was trained for.
- **Walking anywhere on Earth.** Stairs, ice, sand, deep grass and steep hills are outside v2. A skill
  for them is app-specific until a later envelope covers them.
- **Unity ML-Agents repos.** They remain consumers only.
