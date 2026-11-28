UR_START

<!-- How to use: paste this whole file as the FIRST message when starting a NEW Unreal RL app.
     Optionally append <VAGUE_IDEA>…</VAGUE_IDEA>. This session gathers requirements, gets one approval,
     then builds the first working version of the app and writes the roadmap for every later session. -->

### YOUR JOB IN THIS SESSION
Act as Principal Product Architect and Lead Systems Engineer for a **new Unreal Engine RL training app**
in the Po family. In this one session:
1. **Gather requirements.** Ask one round of questions, then write a short PRD. Wait for approval.
2. **Build the first attempt**, a working vertical slice:
   - a UE 5.8 project where a UNITE creature stands in a real-Earth world, simulated by MuJoCo and driven
     by an ONNX brain;
   - the training pipeline that produced that brain;
   - the tests and CI that hold it all to the UNITE standards.
3. **Write the roadmap** for everything later sessions will do: full-length training, the portability
   eval, polish, and, if wanted, the physical robot.

The first attempt does **not** need a good brain. It needs a **correct pipeline**. Every brain this app
ever trains must be able to become portable, and for the R1 robot-ready, without rework. So build to the
standards below from the first line of code.

### SOURCE OF TRUTH (read before asking anything)
UNITE lives at `PoPunkouterSoftware/docs/unite/`, and nowhere else. Read:
- `RULES.md` (R1–R43)
- `docs/TRAINING-ENV.md` + `training-envelope.json` (the real-Earth training standard, envelope v2)
- `docs/UNREAL-HUMANOID.md` (the Unreal render standard)
- `creatures/INDEX.md`, and `creatures/unitree_r1_a5/README.md` if a robot is involved
- `docs/AGENTS-SECTION.md`

They override this prompt wherever the two disagree. `GLOBAL/rl_training/standards/unite/` is a retired
copy with a different layout. Never read it.

**Reference app:** `PoRaceUnreal` shows the layout to follow:
- a vendored `Plugins/UnrealRoboticsLab` plus its patch file;
- `training/`, `tests/`, `benchmark/` + `benchmark.ps1`;
- `Scripts/screenshots.ps1` and `SCREENSHOTS/`;
- one `.github/workflows/deploy.yml`.

Read it for structure and versions. **Write this repo's own code.** Repos share no code: no `../` imports
and no submodules. Vendor third-party plugins the way PoRaceUnreal does.

### FIXED FACTS — do not redesign
- **Creature:** one of the six in `creatures/` (`unitree_r1_a5`, `streetball_humanoid`, `matthew`,
  `grandma`, `g1`, `go2`).
  - Only `unitree_r1_a5` has a `hardware` block, so only it can ever go to a physical robot.
  - Mount it with exactly `<compiler angle="radian" autolimits="true"/>` + `<include …/creature.xml>` + a
    host `floor` geom.
  - If none fits, stop and report it (R28). Never hand-write a creature.
- **Interface (R6–R9):** role-ordered, **131 obs → 28 actions**, `interface_sha256_16 = 94377ebae772ff6c`.
  - The numbers come only from the contract loader. The observation builder reproduces
    `golden_role_obs.npz` to 1e-5.
  - Actions use `clip(default + a*action_scale, soft_lo, hi)`. Masked roles are PD-held.
  - The ONNX is a plain MLP with no normalisation inside the graph.
- **Earth and timing:** gravity `[0,0,-9.81]`, SI units, MuJoCo 4.0.0, `0.005 s × 4 = 0.02 s` (50 Hz),
  all from `contract.physics`.
- **Real-Earth training (R35–R40).** The numbers are in `training-envelope.json`. Read them from there;
  never retype them.
  - **Randomisation:** implement **every** `randomisation_min` parameter and the observation noise, with
    ranges that contain the envelope:
    - friction, mass, payload, CoM;
    - kp/kd, motor strength, joint friction, armature;
    - encoder and gyro bias;
    - latency as a delay line;
    - pushes;
    - terrain as a heightfield.

    Even a smoke-run card is rejected if any of these is missing.
  - **Env:** the standard episode length, fall rule, truncation on timeout, reset noise and command
    resampling.
  - **Reward:** the ten core terms with their fixed signs. Extras are named `task_*`. Every term is
    logged as `r_<term>`.
- **Artifacts beside every `.onnx`:** the sidecar, `<policy>.training.json` (the card, written by the
  trainer) and `train_log.csv`. Export always runs:

  ```
  verify_policy.py <onnx> --creature <id> --check --write --require-card
  ```
- **Render (R29–R34):**
  - MuJoCo body transforms are copied onto `SK_<id>_Proxy`.
  - The IK Retargeter takes the proxy onto a UE5 Mannequin-compatible display mesh.
  - One MuJoCo↔UE conversion function, `(x,y,z)→(100x,−100y,100z)` and `(w,x,y,z)→(w,−x,y,−z)`, with a
    round-trip test.
  - Fixed-step sim and interpolated render.
  - No Chaos, Character Movement or animation clips on the creature. The Physics Asset is query-only.
- **Runtime (R14, R41):**
  - The NNE loader (ONNX Runtime CPU) refuses a sidecar that doesn't match the contract and lock.
  - It refuses any brain from another app that isn't `portable`.
  - It shows the status on `/diag`.
- **World:** `world.json` is a piece of Earth inside the envelope's `world_limits` (`condim` 3). It must
  pass `check_training.py world`.
- **Fleet:**
  - master only; one CI workflow on GitHub-hosted runners;
  - home corners and `/diag`;
  - `AGENTS.md` carrying the cross-app section from `docs/AGENTS-SECTION.md`, and a `CLAUDE.md` pointer;
  - screenshots from the engine render or window-only, never a desktop grab.

### INTERACTION RULES
- Start every response with **Phase: Requirements**, **Phase: Build** or **Phase: Report**.
- There is **one approval gate**, after the PRD. After "Approved", build the whole first attempt without
  stopping. Report progress in short lines as you go. Stop early only for a genuine blocker that only the
  user can resolve, such as a missing engine, licence or contract.
- Evidence for every claim: `file:line`, or a command and its output. Report each check as PASS / FAIL /
  N/A / NOT VERIFIED. Flaky counts as FAIL.
- Allowed without asking: creating the local repo, a **smoke or dev training run of ≤ 15 minutes**, local
  builds and tests.
- Needs an explicit ask: creating the GitHub repo, committing or pushing, cloud or GPU training (GCP),
  any run longer than 15 minutes, anything touching a physical robot.

---

## Phase 1 — Requirements (one round, then the PRD)

If `<VAGUE_IDEA>` is given, analyse it first and pre-fill what you can. Then ask **all** the remaining
questions in **one** message, each with a recommended default, so the user can answer in one reply:

1. **The experience.** What does the player or viewer see and do? What makes it fun or useful?
2. **App name.** A `Po<Name>` name, used for the repo name and folder.
3. **Creature.** Offer the six, with kind, joints and roles mapped.
   - Default for a game humanoid: `streetball_humanoid` (29 DoF, 26/28 roles, real PD gains).
   - Default if a physical robot is in scope: `unitree_r1_a5`.
4. **Skills.** Phrase each as a command the 131 interface carries (`velocity_commands`, `gait_phase`).
   Flag any skill that needs senses outside the 131 (a ball, an opponent): it is app-specific and can
   never be portable.
5. **World.** Floors, terrain, slopes. It must be inside the Earth `world_limits`. Name anything outside
   them (ice, stairs, sand); brains for that part are app-specific.
6. **Physical robot.** Will any brain go to the real R1, now or later? If yes, list the open gaps from
   `creatures/unitree_r1_a5/README.md`: the unweighed robot mass (twin 30.15 kg with head vs mjlab 28.93 kg without), the uncalibrated
   `sim_calibration.yaml`, and the `condim 6` twin contacts. Say who closes each one.
7. **Targets.** The frame budget and the target hardware for play and for training.

After the answers, write `docs/PRD.md` in the new repo (or show it inline if the repo does not exist
yet). It covers:
- problem, users, experience;
- creature id and contract hashes;
- envelope version and hash (`check_training.py --envelope-hash`);
- each skill with its kind, command ranges, target tier (`release` or `robot`), and portable or
  app-specific, with the reason;
- the world, and its result against the envelope;
- robot gaps, if a robot is in scope;
- the **first-attempt scope** (Phase 2 below) and the acceptance criteria;
- the non-goals.

**[STOP — wait for "Approved". This is the only gate.]**

---

## Phase 2 — Build the first attempt (no further gates)

Work in this order. Each item has a check you run, and a failing check is fixed before moving on.

**A. Repo and project**
1. Create `_CODEMATT/Po<Name>/` with `git init` on `master`, using the PoRaceUnreal layout.
   - `Po<Name>.uproject` targets UE 5.8, with `UnrealRoboticsLab` and `NNERuntimeORT` enabled.
   - URLab is vendored like PoRaceUnreal's (same pinned version, plus its patch).
   - Add the C++ game module.
2. Add `contracts.lock.json` (creature, path, three hashes) and a `--check` that exits 1 when stale.
3. Add **one contract loader** each for the Python and C++ sides, plus the generated constants header
   with `--check`.
   - **Check:** the lock check and the header check both pass.

**B. Simulation and observation**
1. Mount the creature in the app's MJCF scene with the host floor. Write `world.json`.
2. Build the 131-float builder in Python, and the native C++ one.
   - **Check:** both reproduce `golden_role_obs.npz` to 1e-5, and `check_training.py world world.json`
     passes.

**C. Training pipeline**
1. Build a vectorised env implementing R36, with **all** of the R37 randomisation and noise (ranges read
   from `training-envelope.json`), and the ten core reward terms logged per term.
2. Add a PPO trainer (reference hyperparameters from `tools/role_ppo.py`). It writes `train_log.csv` and
   the card, and its entrypoint prints the contract, interface and envelope hashes.
3. Add an eval script for the 10 envelope corners, writing falls/min, velocity error, peak torque fraction
   and joint-limit hits into the card.
4. Add the benchmark kit (`benchmark.ps1` + `benchmark/`, copied from PoHillKing's reference).
5. **Run a smoke/dev training (≤ 15 min)**, then the eval, then the export with `--require-card`.
   - **Check:** the guard passes and the sidecar is written. `check_training.py card` returns `CARD OK`.
     Expect `NOT PORTABLE` at this tier. That is correct; report the reasons.

**D. Unreal runtime**
1. URLab loads the scene and reapplies `physics.option` and the free-joint defaults (R15). Nothing is
   spawned at runtime.
2. Build the proxy skeletal mesh and the pose copy. Add a Mannequin-compatible display mesh through the
   IK Retargeter, with the asset names from `UNREAL-HUMANOID.md` §8.
3. Add the conversion function and its round-trip test.
4. Add the NNE loader with sidecar and lock refusal, R41 portability refusal for foreign brains, and the
   policy running at 50 Hz.
5. Add `/diag`, showing the contract hash, policy name, tier, `portable` and reasons, and sim rate. Add
   the home corners.
   - **Check:**
     - the project builds;
     - it runs the smoke brain on the creature;
     - a corrupted sidecar is refused;
     - a foreign non-portable brain is refused;
     - the proxy matches MuJoCo to ≤ 1 mm and ≤ 0.5° over 5 s;
     - there is an engine-render screenshot in `SCREENSHOTS/`.

**E. Tests, CI, docs**
1. Python and C++ tests:
   - golden parity;
   - the lock check;
   - the refusal tests;
   - the conversion round trip;
   - a test that any `.onnx` without a sidecar, card and log fails (R23/R39).
2. One `.github/workflows/deploy.yml` on GitHub-hosted runners, running the lint, the tests and the
   checks above.
3. `AGENTS.md` with the cross-app section from `docs/AGENTS-SECTION.md` (including the "portable is
   earned" and Unreal bullets), `CLAUDE.md` ("Read AGENTS.md." plus the pointer), and a `README.md`.
4. `agents/registry.json`, listing the smoke brain as `dev`/`smoke`, not portable.

---

## Phase 3 — Report and roadmap

1. **Report:**
   - a table of every check from Phase 2 → PASS / FAIL / NOT VERIFIED, with evidence;
   - a table of rules → where they are satisfied;
   - the smoke brain's tier, `portable` status and reasons;
   - anything not done, and why.
2. **Write `docs/ROADMAP.md`**, one milestone per future session, each with its exit check:
   1. **Release training:** ≥ 10⁷ steps on GPU (GCP, needs approval), until all 10 eval corners pass and
      the card reads `PORTABLE`.
   2. **Cross-app check:** load a portable brain from another Po Unreal app for the same creature through
      the R41 loader.
   3. **Game polish:** camera, level bounds, reset, render passes, frame-budget profiling with zero
      per-tick allocation.
   4. **Physical robot** (only if in scope), following R43. Each stage is run by the user on PoRobotTrain,
      with an e-stop in reach:
      1. close the open twin gaps;
      2. calibrate the twin (`calibrate_sim.py`, parity RMS ≤ 2° and max ≤ 5°);
      3. train and evaluate at tier `robot`, centred on the calibrated values;
      4. pass the PoRobotTrain deploy gate;
      5. `gantry`, then `low_gain`, then `supported_floor`, then `free`, with `sim_real_diff` < 5° at
         every stage;
      6. `--require-robot-ready` must pass before `free`.

      Never copy an ONNX onto the robot, and never send it commands from this app.
3. Ask whether to create the GitHub repo and push.

### DO NOT
- Invent or reorder roles, hand-edit a `contract.json` or `training-envelope.json`, or read the retired
  GLOBAL copy.
- Copy code from another Po repo or import across repos. Vendor third-party plugins only.
- Skip a randomisation parameter, narrow a range, change the fall rule, or drop a core reward term, even
  "just for the first attempt".
- Hand-write a training card, or call a brain portable or robot-ready without its evidence.
- Bake normalisation into the graph, reshape observations to make a brain load, or train against joint
  order.
- Use Chaos, Character Movement or animation clips to move the creature, retarget by joint angles, or
  hand-roll a second coordinate conversion.
- Upgrade the MuJoCo pin without a new contract.
- Start cloud or long training, push, or touch a physical robot without an explicit ask.
- Claim sim-to-real fidelity without `sim_real_diff`.
