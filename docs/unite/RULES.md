# RULES.md — the universal RL training prompt

Paste this whole file at the start of a session **inside one RL training repo**. It does two jobs:
it aligns the session with the universal training concept, and it tells the session exactly what to
change so that every agent trained from now on is cross-compatible with every other Po app.

**Use in:** `PoRobotTrain`, `PoBasketball`, `PoRaceUnreal`, `PoSlapUnreal`, or any new MuJoCo RL app.
**Do not use in:** `PoFootball`, `PoSoccer`, `PoSumo` — Unity ML-Agents, consumer-only (§5).

Existing checkpoints are **grandfathered**. They may stay incompatible. Everything trained from now on
must conform, and the repo must make non-conformance impossible rather than merely discouraged.

---

## 0. Mission

Train in MuJoCo. Let any creature load into any Po game. Let any policy trained against the shared
interface run in any app. Let the R1's policies reach the physical robot.

---

## 1. How to behave in this session

1. Read the repo's `AGENTS.md` and `CLAUDE.md` first. Their rules win over this file except on the
   contract itself.
2. Say which rule number each change satisfies, and cite evidence (`file:line`, a command and its
   output). A claim without evidence is not a change.
3. Do not commit, push, deploy, or start a training run unless explicitly asked.
4. If a creature you need has no contract, **stop and report it**. Do not invent one, and do not
   hand-write interface constants to get unstuck.
5. Report what you grandfathered, what you did not do, and why. A partial result written down beats a
   claim.

---

## 2. Definitions

- **Contract** — `global/UNITE/creatures/<id>/contract.json`, schema `global/UNITE/pocontract.schema.json`.
- **Interface** — `contract.observation` + `contract.action` + `contract.roles`.
- **Role-ordered** — indexed by `contract.roles.canonical_order`, **never** by MuJoCo joint order.
- **Sidecar** — the JSON beside an `.onnx` that pins the hashes.
- **Lock** — `contracts.lock.json` at the repo root: the contract path plus its three hashes.
- **Legacy** — a policy trained before these rules. It stays, it is fenced, it is never promoted.

---

## 3. The concept in one page

**Two kinds of compatibility, never conflated:**

| | Creature compatibility | Brain compatibility |
|---|---|---|
| Means | the body loads and runs in any MuJoCo app | the policy produces correct actions in any app |
| Requires | portable prefab + contract | trained **against the role interface** |
| Status | done — 6 prefabs verified | per-policy; legacy ones are not |

Dropping a **creature** into another app works today. Dropping a **brain** in is a migration.

**Six layers that must match:**

| # | Layer | Must match | May differ |
|---|---|---|---|
| 1 | World | recorded contact block and solver `cone` | the environment, within the trained distribution |
| 2 | Solver | `timestep`, `integrator`, `solver`, `iterations`, `cone`, `jacobian`, `impratio` | — |
| 3 | Body | masses, inertias, joint axes/ranges/damping/armature, actuator gains | — |
| 4 | Timing | physics dt, decimation, policy rate, latency | — |
| 5 | Interface | slot order/size/scale/frame, action rule, clip, normalisation placement | — |
| 6 | Mapping | which actuator drives which joint, PD law, `motor_slot` | — |

> **Contracts must be identical. Worlds must sit inside the trained distribution.**

Identical worlds are **not** a goal; forcing every game onto one floor collapses them into one game.
The transfer mechanism is randomisation during training — friction, terrain, payload, encoder bias,
pushes — not cloning a floor.

**Compatibility ≠ fidelity.** Compatibility makes a brain *loadable*; fidelity makes it *correct*.
The R1 is compatible and not yet fidelity-verified (§9).

---

## 4. The rules — R1 to R28

### Creature

- **R1.** A creature comes from `global/UNITE/creatures/<id>/contract.json`. Never a hand-written copy.
- **R2.** A creature is a portable prefab: no `meshdir`, meshes referenced as `meshes/<name>`,
  `angle="radian"`, no floor/lights/ground textures. The host supplies `<geom name="floor">`.
  Verified behaviour, not style: `<include>` discards `meshdir`, and the host's `<compiler>` wins over
  an included file's.
- **R3.** The mount snippet is exactly `<compiler angle="radian" autolimits="true"/>` +
  `<include file="…/creature.xml"/>` + a `floor` geom.
- **R4.** New creatures go through `global/UNITE/tools/build_creatures.py` and must pass
  `check_creatures.py` — identical joint tables standalone and through a foreign `<include>`, and no
  joint axis, range or mass changed by the split.
- **R5.** Never invent a role or reorder `canonical_order`. A new role is a `roles_version` bump and a
  retrain for every policy.

### Brain

- **R6.** Observation is role-ordered: **131 floats**, canonical **28 roles**, `role_mask`,
  `morphology_onehot`; roles absent on the creature are zero-filled and PD-held at nominal.
- **R7.** Action is **28 wide**: `clip(default + a*action_scale, soft_limit_lo, hi)`.
- **R8.** Frames, scales and per-slot meaning are exactly `contract.observation.slots[*].rule`.
- **R9.** No normalisation inside the ONNX graph. Plain MLP, pinned opset. Normalisation lives in the
  runtime and is declared in `contract.observation.normalizer`. An ML-Agents export violates this by
  construction.
- **R10.** Every `.onnx` ships a sidecar: `policy_name`, `n_obs`, `n_action`, `opset`, `input`,
  `output`, `interface_sha256`, `morphology_sha256`, `physics_sha256`, `policy_sha256`, `mujoco`,
  `ort_vs_torch_max_abs_diff`, and training provenance.
- **R11.** **The export entrypoint aborts** when the layout it would produce differs from
  `contract.hashes.interface_sha256`, printing both and the first differing slot. This guard — not a
  comment — is what makes every future agent conform.
- **R12.** Parity before shipping: observations against the contract goldens within
  `contract.references.tolerance`; ONNX vs trainer actions ≤ 1e-5. A screenshot of the creature moving
  is not evidence.
- **R13.** A policy may be adopted by another app only if its sidecar `interface_sha256` matches a
  creature contract. Otherwise: re-export (no retrain), add an explicit translator, or retrain. Never
  hand-patch an observation order to make a brain load.

### Runtime

- **R14.** The loader compares sidecar against contract and lock. On mismatch it **refuses**, logs the
  three hashes, and reports "policy incompatible". It never reorders, pads or rescales to fit.
- **R15.** If the app rebuilds the MJCF in-engine (URLab, a Unity plugin, a browser port), it reapplies
  `physics.option`, free-joint damping/armature and site positions from the contract at bind time, then
  asserts the fingerprint. Known losses: the `<option>` block, free-joint defaults, site positions,
  single precision.
- **R16.** MuJoCo steps the creature; the engine renders and observes. Contact physics never moves into
  the engine.

### Physics and hardware

- **R17.** One MuJoCo version per contract. Two versions are two contracts. Never average them.
- **R18.** Record the world contact block actually used: `friction` (3-vector), `solref`, `solimp`,
  `priority`, `condim`, plus solver `cone`. "Same friction" is not a specification.
- **R19.** Earth parameters are asserted, not assumed: gravity `[0, 0, -9.81]`, SI metres/radians/kg,
  and `timestep × decimation == control_dt`.
- **R20.** Compatibility ≠ fidelity. State which one you have actually established.
- **R21.** Only `unitree_r1_a5` may carry `hardware` and `motor_slot`. Its policies additionally need
  the PoRobotTrain deploy gate and a matching `policy_sha256`. No other app may promote one.

### Legacy

- **R22.** Existing policies are grandfathered: registered as `legacy` with a note, left in place,
  never migrated and never promoted.
- **R23.** A test fails when a new `.onnx` appears without a sidecar, or with a sidecar whose interface
  hash matches no contract. This closes the leak without rewriting history.
- **R24.** The training entrypoint prints one line naming the contract and interface hash it is about to
  train against, so a log proves conformance.

### Governance

- **R25.** `global/UNITE` is a registry and generator, **not a monorepo**. No app code, builds, or
  per-game configuration lives there.
- **R26.** No shared code between repos. Referencing `global/UNITE` does not violate this — it is not a
  repo — but every reference is pinned by hash in `contracts.lock.json` so drift shows in the diff.
- **R27.** Never copy `global/UNITE` files into a repo. Never hand-edit a `contract.json`. To change a
  contract, change the source and regenerate.
- **R28.** If a repo needs a creature `global/UNITE` does not have, stop and report it. Do not invent
  one.

---

## 5. What to change in this repo

### Part 1 — Adopt the contract (no behaviour change)

1. List every creature this repo renders or trains.
2. Resolve each to `global/UNITE/creatures/<id>/contract.json`. If one does not exist, stop that
   creature and report it.
3. Add `contracts.lock.json` at the repo root: per creature `{id, path, interface_sha256,
   morphology_sha256, physics_sha256}`.
4. Add one loader module (`.py` / `.cs` / `.h`) that reads the contract. It must be the **only** source
   of `n_obs`, `n_action`, joint order, roles, gains, `action_scale`, `control_dt`, `decimation`,
   `timestep` and the solver `option`.
5. Remove or neutralise hand-copied duplicates. Where a compile-time constant is required, generate it
   from the contract and add a `--check` mode that exits 1 when stale — follow
   `PoRaceUnreal/training/contract.py` and `PoSlapUnreal/training/gen_constants.py`.
6. Test: contract loads, hashes equal the lock, generated file is current. **No render or physics
   change is acceptable here** — if existing tests move, you changed behaviour.

### Part 2 — Enforce the interface for new training only

Do not migrate old checkpoints. Make non-conforming training impossible.

1. Observation builder: 131 floats in `contract.roles.canonical_order`, zero-filled where `roles.mask`
   is 0, plus `role_mask` and `morphology_onehot`; per-slot meaning from
   `contract.observation.slots[*].rule`.
2. Action mapping: 28 wide, `clip(default + a*action_scale, soft_limit_lo, hi)`, masked roles PD-held
   at `default`.
3. Export a plain MLP, pinned opset, no normalisation in the graph.
4. Write a sidecar beside every `.onnx` (fields in R10).
5. **Hard guard:** the export entrypoint aborts when the layout hash it would produce differs from
   `contract.hashes.interface_sha256` (R11).
6. Tests: observation parity against the contract goldens; ONNX vs trainer actions ≤ 1e-5.

### Part 3 — The runtime refuses, not degrades

1. Compare sidecar against contract and lock before a policy runs. On mismatch: do not load, log the
   three hashes, surface "policy incompatible" where the app already shows status (HUD, `/diag`,
   health). Never reshape inputs to make an old brain fit.
2. If this app rebuilds the MJCF in-engine, reapply the contract's `physics.option`, free-joint
   damping/armature and site positions at bind time, then assert the fingerprint (R15).
3. Add a test that corrupts a sidecar hash and asserts the loader refuses.

### Part 4 — Physics and world

1. MuJoCo steps; the engine renders and observes.
2. One MuJoCo version per contract; do not upgrade the pin without emitting a new contract.
3. Record the contact block actually used: `friction` (3-vector), `solref`, `solimp`, `priority`,
   `condim`, and the solver `cone`.
4. Domain randomisation must be on and recorded for anything meant to transfer — friction, terrain,
   payload, encoder bias, pushes.

### Part 5 — Contain the legacy and stop the leak

1. Add `agents/registry.json`: every existing `.onnx` with `{name, path, tier, n_obs, n_action,
   interface_sha256_16 (or null), status: "legacy", note: "not cross-compatible; do not reuse"}`.
   Do not delete them; do not promote them.
2. Add a test that fails on a new `.onnx` without a sidecar, or with a sidecar matching no contract.
3. Make the training entrypoint print one line naming the contract and interface hash it is training
   against.

### Part 6 — Report

A table: rule → before → after → evidence. Then:
- **Grandfathered:** policies left alone, with their interface hashes or `null`.
- **Not done:** anything you could not do, and why.
- **Contract gaps:** creatures this repo needs that `global/UNITE` does not have.

### If this repo trains with Unity ML-Agents

Do not pretend. It is a **consumer only**: it may load contract-conforming ONNX through Sentis via the
contract-driven loader, and it must mark everything its own trainer produces as `legacy`, because an
ML-Agents graph bakes observation normalisation and its own vector layout into the network. Portable
brains for that game must come from a MuJoCo trainer. Say so in the report instead of attempting
Part 2.

---

## 6. Evidence — what counts as proof

`file:line`, a command and its output, a run URL, or a screenshot the session opened.
Status is PASS / FAIL / N/A / NOT VERIFIED. Flaky is FAIL.

| Claim | Command |
|---|---|
| Contracts current | `build_creatures.py --check` → `OK` |
| Creatures portable | `check_creatures.py` → `OK` |
| Contracts valid | `validate.py` → `OK` |
| Interface unchanged | `interface_sha256_16` is `94377ebae772ff6c` for every creature |
| Policy conforms | sidecar `interface_sha256` equals the contract's |
| Runtime refuses | the corrupt-sidecar test passes |
| Parity | parity output with max deviation and the tolerance |
| An app can consume 131 | `check_cross_app.py` → `PoRobotTrain`, `PoBasketball`, `PoRaceUnreal` `131 builder (9/9)` |

Applicability: MuJoCo-trained work is in scope. Unity ML-Agents repos are **N/A** on the interface,
parity and portability rows until re-exported — record N/A, not FAIL.

---

## 7. Do not

- Invent a role, or extend/reorder `canonical_order`.
- Hand-edit a `contract.json`.
- Copy `global/UNITE` files into a repo.
- Bake normalisation into a graph.
- "Fix" compatibility by reordering observations at load time.
- Upgrade a MuJoCo pin to make a test pass.
- Train against joint order instead of roles.
- Force apps onto identical worlds to fake compatibility.
- Claim sim-to-real fidelity without `sim_real_diff`.
- Reformat untouched files or add a second workflow.

---

## 8. Done means

`contracts.lock.json` exists and matches. The contract loader is the only source of interface values.
The export guard aborts on a non-conforming layout. The refusal test passes. A new `.onnx` without a
sidecar fails CI. Legacy policies are registered and untouched. Existing tests are green, and no
render or physics behaviour changed.

---

## 9. Known gaps — state them, do not paper over them

1. **R1 mass.** Twin is **30.150 kg**; training model and real A5 are **28.93 kg** — +1.2 kg above the
   pelvis. Fix by rebuilding from the mjlab model or setting `mass.torso_extra_mass_kg: -1.22`, then
   re-gate.
2. **Contacts.** Twin `condim 6` / foot friction 1.0 / floor 0.9 vs training `condim 3` / 0.6
   randomised 0.3–1.6. The real floor is unmeasured.
3. **Never calibrated.** `sim_calibration.yaml` has `last_calibrated_at: null`,
   `parity_rms_deg: null`. Every gain is an estimate.
4. **No randomisation in the twin**, while training randomises encoder bias, CoM and pushes. The twin
   is the easier test.
5. **Training model not vendored.** mjlab's `r1.xml` (28.93 kg) exists only inside the level-2 image.

6. **Three contracts carry no `kd`.** `matthew`, `grandma` and `g1` have `"kd": null` on every joint, because
   their MJCF actuators are `biasprm="0 -kp"` — the source models have no velocity damping. A runtime must read
   that as `0`, not as "missing". Writing `0.0` into the contracts would change `morphology_sha256` and invalidate
   every policy trained against them, so it is recorded here instead. `unitree_r1_a5` and `streetball_humanoid`
   carry real gains.

7. **PoSlapUnreal has no creature to adopt.** Its five bodies (`grandpa`, `kim`, `matt`, `mom`, `tong`) use their
   own rig — `waist_twist` / `waist_flex` / `waist_side` / `neck_turn`, 30 joints — and share no joint name with
   the six creatures, so not even its `matt` is `matthew`. R28: this is reported, not invented. Giving it a
   portable brain means building contracts for those bodies through `tools/build_creatures.py` (a new creature
   per body, each needing a `role_map.json` and a golden), which is an owner decision.
   `PoRaceUnreal`'s stroller is the same kind of gap: 4 joints, no contract, named in its test.

The only sim-to-real evidence that counts: `sim_real_diff` at **<5° max per-joint at every 20 ms tick**.

---

## 10. Current state of the family

| Creature | kind | joints | roles | hardware |
|---|---|---|---|---|
| `unitree_r1_a5` | humanoid | 26 | 26/28 | **yes** |
| `streetball_humanoid` | humanoid | 29 | 26/28 | no |
| `matthew` | humanoid | 29 | 24/28 | no |
| `grandma` | humanoid | 29 | 24/28 | no |
| `g1` | humanoid | 29 | 24/28 | no |
| `go2` | quadruped | 12 | 12/28 | no |

All six: `n_obs` 131, `n_action` 28, `interface_sha256_16` `94377ebae772ff6c`, gravity `[0,0,-9.81]`.
Creature compatibility is done and verified; brain compatibility applies to new training only.

---

## 11. Where things live

All under `global/UNITE/` unless marked otherwise:

| Path | What it is |
|---|---|
| `RULES.md` (this file) | the prompt: rules + what to change |
| [`docs/VISION.md`](docs/VISION.md) | the goal, the two compatibilities, scope boundaries |
| [`docs/PORTABILITY.md`](docs/PORTABILITY.md) | the six layers; why gravity + friction is not enough |
| [`docs/GOVERNANCE.md`](docs/GOVERNANCE.md) | versioning, vendoring, adding a creature, drift detection |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | phases, exit criteria, risks |
| [`docs/AGENTS-SECTION.md`](docs/AGENTS-SECTION.md) | the section to paste into any repo's `AGENTS.md` (and the `CLAUDE.md` pointer) |
| [`pocontract.schema.json`](pocontract.schema.json) | the contract schema |
| [`creatures/INDEX.md`](creatures/INDEX.md) | the creature library and mount snippet |
| [`creatures/unitree_r1_a5/README.md`](creatures/unitree_r1_a5/README.md) | the hardware creature and its fidelity gaps |
| [`tools/verify_policy.py`](tools/verify_policy.py) | the export guard + sidecar writer (R10/R11); `--check` aborts, `--write` writes, `--selftest` tests itself |
| [`tools/role_obs.py`](tools/role_obs.py) | **the reference 131-float builder** (R6/R8) and the per-creature goldens every other runtime must reproduce |
| [`tools/role_env.py`](tools/role_env.py) | one virtual env for every creature: 28 actions in, 131 observations out, masked roles PD-held |
| [`tools/role_agent.py`](tools/role_agent.py) | **the runtime**: sidecar refusal, any creature in MuJoCo, and the R1's 31-slot `unitree_hg` command vector (`--hardware`) |
| [`prompts/PROMPT-ProfileC.md`](prompts/PROMPT-ProfileC.md) | the *fleet-wide* CODECONSISTANT Profile C: scope gate (C0/C1/C2/N/A) + the fix pass (different job) |
| [`../station/README.md`](../station/README.md) | the 10 cross-repo tools; `vault.html` lists every `.onnx` by socket |
| [`../README.md`](../README.md) | the map of the whole `global/` area |

---

## 12. Commands

From the `_CODEMATT` root:

```powershell
$py = 'PoRobotTrain\.venv\Scripts\python.exe'

& $py global\UNITE\tools\build_creatures.py            # rebuild creatures + contracts + INDEX
& $py global\UNITE\tools\build_creatures.py --check    # fail if anything on disk is stale
& $py global\UNITE\tools\check_creatures.py            # portability + split fidelity + Earth asserts
& $py global\UNITE\tools\verify_policy.py --selftest   # test the export guard itself
& $py global\UNITE\tools\role_obs.py --selftest        # test the universal 131-float builder
& $py global\UNITE\tools\role_obs.py --verify          # every creature reproduces its golden
& $py global\UNITE\tools\role_env.py --smoke           # step every creature through one 28/131 interface
& $py global\UNITE\tools\role_ppo.py --smoke           # 2 tiny iterations, then export + guard (~5 s)
& $py global\UNITE\tools\role_agent.py --selftest      # test the runtime: bus map, sim/bus agreement, refusal
& $py global\UNITE\tools\role_agent.py global\UNITE\runs\ppo-r1-edu-stand --hardware   # twin + motor bus
& $py global\UNITE\tools\role_agent.py global\UNITE\runs\ppo-r1-edu-stand --all-creatures
& $py global\UNITE\tools\check_cross_app.py            # is a new agent actually usable in every app?
cd global\UNITE; uv run --no-project --with jsonschema python tools\validate.py

# the export guard for one policy (R10 + R11). Put this in the repo's export entrypoint.
& $py global\UNITE\tools\verify_policy.py <policy.onnx> --creature <id> --check     # exit 1 = abort
& $py global\UNITE\tools\verify_policy.py <policy.onnx> --creature <id> --write     # write sidecar
& $py global\UNITE\tools\verify_policy.py <policy.onnx> --creature <id> --verify-sidecar   # runtime check
```

This file is the per-repo prompt. For the whole-fleet audit, use `prompts/PROMPT-ProfileC.md`.
