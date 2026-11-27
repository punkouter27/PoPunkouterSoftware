# unitree_r1_a5 — the hardware-backed creature

This is the only creature in the library that is also a **physical robot**. Everything else is
simulation-only; a brain trained on them never leaves MuJoCo. A brain trained on this one is
expected to eventually be copied onto the Unitree R1 A5 in the room.

## What makes it different

| | Simulation-only creatures | `unitree_r1_a5` |
|---|---|---|
| Interface | 131 / 28 role-vector | same |
| `motor_slot` per joint | `null` | real index into `LowCmd_.motor_cmd[]` |
| `hardware` block | absent | present: bus, slot map, deploy gate, 50 Hz |
| Gains / defaults | from the MJCF actuator | from `r1_joint_map.yaml` (hardware truth) |
| Gate before use | none | `PoRobotTrain`'s `deploy.json` + SHA match, mandatory |
| Owner app | each repo | **`PoRobotTrain`** |

The contract here is the portable twin. It is *not* the deployment authority — `PoRobotTrain`
keeps that. This folder exists so any other MuJoCo app (a game, a scenario lab, a benchmark) can
load the same body, the same interface and the same slot map without copying the robot's config.

## Files

| File | Role |
|---|---|
| `creature.xml` | flat, self-contained twin body: `r1_twin.xml` minus option/floor/keyframe, meshes vendored |
| `scene.xml` | the twin on a floor, with the twin's own solver block and stand-free keyframe |
| `role_map.json` | joint → canonical role, the editable input |
| `contract.json` | emitted contract, incl. `hardware` and the legacy interfaces |
| `hardware.json` | the bus contract, generated from `r1_joint_map.yaml` |
| `meshes/` | 42 STL meshes, basenames only |

## The first agent in the library

`global/UNITE/runs/ppo-r1-edu-stand` is the first conforming brain trained for this creature, and the
example of what "portable" means end to end.

```powershell
$py = 'PoRobotTrain\.venv\Scripts\python.exe'
& $py global\UNITE\tools\role_agent.py global\UNITE\runs\ppo-r1-edu-stand --hardware
& $py global\UNITE\tools\role_agent.py global\UNITE\runs\ppo-r1-edu-stand --all-creatures
```

`--hardware` loads the policy only if its sidecar still matches this contract (R14), runs one control
step in the twin, and prints the same action as a 31-slot `unitree_hg` command: `mode 1` and
`kp`/`kd` on the 26 mapped slots, `mode 0` on 14/20/21/27/28, `tau 0`, `q` inside the soft limits.
It also asserts that the sim's own ctrl targets and the bus angles agree on every mapped joint — the
one check that proves a sim action vector and a `LowCmd_` command vector are the same index space.

What it deliberately does **not** do: publish to `rt/lowcmd`. Reaching the robot still needs
`PoRobotTrain`'s deploy gate — `RobotControl/models/<skill>/deploy.json`, `validation.passed`, and a
`policy_sha256` that matches the `.onnx` (R21).

`--all-creatures` runs the same weights on every body in the library. They are *loadable* everywhere
and *correct* only here; the report labels each row `own` or `foreign` rather than calling it working.

Behaviour is the weak part, and it is stated: 920k PPO steps on CPU hold the stance for under two seconds in this
twin before tipping over. The training budget for a real walking policy is two orders of magnitude larger
(`docs/level2_walk.md`, mjlab). What this checkpoint proves is the chain, and the chain is what every later brain
reuses.

## The sim-to-real path this enables

```
train (MuJoCo, this creature)                     PoRobotTrain or any trainer using contract.json
   -> .onnx + sidecar (interface/morphology/physics sha256)
   -> pocontract verify     observation parity, ONNX/torch parity, gains match the joint map
   -> PoRobotTrain gate     RobotControl/models/<skill>/deploy.json, validation.passed, policy_sha256
   -> UE NNE (R1Twin)       load only when the sha matches           sim only, never hardware
   -> Unitree R1 A5         motor_slot -> LowCmd_.motor_cmd[], 50 Hz
```

Two facts that make this work and must not drift:

1. **One action vector.** The role-vector is 28 wide and the slot map is on the joints, so the
   sim action and the hardware command are the same index space. 26 of 28 roles are populated;
   `toe_l` / `toe_r` are masked because the R1 has no toe joint.
2. **One physics version per contract.** This contract records MuJoCo **4.0.0** (the URLab/twin
   pin). The Level 2 walk sandbox trains on mjlab with MuJoCo **3.5.0** — that is a different
   contract, recorded as a `legacy_interfaces` bridge, and its policy must be re-gated against
   this contract before it is considered deployable here.

## Verified Earth behaviour, and what is still unproven

Measured from the emitted contract and `PoRobotTrain`'s own config on 2026-11-26 by
`tools/check_creatures.py`, which now fails on any of these drifting:

| Property | Value | Status |
|---|---|---|
| Gravity | `[0, 0, -9.81]` m/s² | **verified**, asserted for every creature |
| Units | SI metres, radians, kg, right-handed Z-up | **verified** |
| Physics step | 0.005 s (200 Hz), integrator `implicitfast` (3), 10 iterations, 20 line-search | **verified** from the training `<option>` |
| Policy rate | 50 Hz (decimation 4), `timestep * decimation == control_dt` | **verified**, asserted |
| Joint limits / gains | sourced from Unitree's own `R1_C++.xml` and `R1.urdf`, per the joint-map header | **provenance recorded** |
| Motor slots | 26 mapped into the 31-slot `unitree_hg` bus, 14/20/21/27/28 unused | **verified** |
| Actuator chain | kp/kd PD, 10 ms DDS latency, 15 ms actuator low-pass, 48 V bus, back-EMF roll-off, encoder σ 0.0012 rad, IMU σ 0.003 rad, per-group armature/damping/frictionloss, unpowered friction | **modelled** in `configs/sim_calibration.yaml` |
| Robot-vs-twin trajectory gate | max per-joint error < 5° at every 20 ms tick (`sim_real_diff`) | **gate exists** |

### What is NOT yet proven, and must be before a brain reaches the robot

1. **Mass.** The twin is **30.15 kg**; the training model and the real A5 are **28.93 kg**
   (`docs/level2_walk.md`, "Known sim-to-sim differences"). That is +1.2 kg, all of it **above the
   pelvis** — the worst place, because it raises the centre of mass and the yaw inertia that a
   balance policy has to control. Fix by one of: rebuild this creature from the mjlab `r1.xml`
   (28.93 kg, no head links; it is not vendored in the repo today), or set
   `sim_calibration.yaml: mass.torso_extra_mass_kg: -1.22` and re-gate. **Do not ignore it silently.**
2. **Contacts.** Twin uses `condim 6`, foot `friction 1.0`, floor `0.9`; training uses `condim 3`,
   `friction 0.6` randomised 0.3–1.6. Your real floor has never been measured. A ball or a foot
   on a polished floor is not the same contact as on rubber.
3. **Actuator identification.** `sim_calibration.yaml` still reads `last_calibrated_at: null` and
   `parity_rms_deg: null`. The tooling exists (`robotcontrol/tools/calibrate_sim.py`) but has never
   been run against the robot, so every gain, armature and friction number today is an *estimate*,
   not a measurement.
4. **Domain randomisation.** The twin applies none (no encoder bias, no CoM shift, no pushes),
   while training randomises all three. A policy that only works in the twin is validated under
   *less* variation than it was trained on — the twin is the easier test, not the harder one.

The shortest honest path to closing this is PoRobotTrain's existing loop: run the Level 0 sweep on
the stand, then `sim_real_diff` on the same command log. Pass/fail at 5° per joint is the only
evidence that counts. Everything in this folder is a precondition for that test, not a substitute.



- `msg_type` `unitree_hg`, topics `rt/lowcmd` / `rt/lowstate`, 31 command slots
- 26 joints mapped; slots 14, 20, 21, 27, 28 unused on the A5 arm variant
- 50 Hz policy rate (5 ms physics step, decimation 4)

Regenerate everything with:

```powershell
& PoRobotTrain\.venv\Scripts\python.exe global\UNITE\tools\build_creatures.py unitree_r1_a5
& PoRobotTrain\.venv\Scripts\python.exe global\UNITE\tools\check_creatures.py
```
