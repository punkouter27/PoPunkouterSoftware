# UNITE — one contract, every creature, every engine, the real robot

UNITE is the single source of truth for **how a policy is described** across all Po
RL projects. It does not train anything, run anything, or ship in any build. It defines
the interchange format so that a brain trained once can be loaded by MuJoCo, URLab in
Unreal, Unity, the browser twin, and a physical robot without anyone re-deriving what
the 23rd float of an observation vector means.

Scope: RL agents and creatures from `PoBasketball`, `PoRaceUnreal`, `PoSlapUnreal`,
`PoRobotTrain`, `PoOlympic`, `PoFootball`, `PoSoccer`, `PoSumo`, `PoBox`, `PoHillKing`,
and anything built after them.

---

## 1. Why this folder exists

Today the same idea is implemented four times, in four dialects, in four repos:

| Repo | Contract artifact | Generator | What it pins |
|---|---|---|---|
| `PoBasketball` | `shared/obs_spec.json`, `shared/humanoid_spec.json`, `shared/golden/obs_golden.npz`, `shared/policies/*.json` | `sim/streetball/obs.py::write_spec_and_golden` | obs slots, joint order, bone map, golden vectors, `obs_spec_sha256` + `physics_sha` in every policy sidecar |
| `PoRaceUnreal` | `training/models/<creature>_fingerprint.json`, `Source/PoRaceUnreal/RaceContract.gen.h` | `training/contract.py` (`--check` in CI) | full mjModel numbers, solver options, joint/actuator names, default pose |
| `PoSlapUnreal` | `training/constants.py`, `training/rules.json` → `Source/PoSlapUnreal/SlapShared.gen.h`, `Web/shared.gen.js` | `training/gen_constants.py` (`--check`) | brain I/O sizes, timing, filters |
| `PoRobotTrain` | `RobotControl/configs/r1_joint_map.yaml`, `RobotControl/models/*/deploy.yaml` + `deploy.json` | `RobotControl/configs/joint_map.py` | joint order, motor slots, gains, limits, action scale, validation gate + `policy_sha256` |

Each one is good. Together they are four incompatible answers to one question. A policy
trained for the Go2 cannot run on Matt, a basketball brain cannot run on the R1, and
nothing in `PoFootball` can leave Unity at all.

UNITE replaces that with **one schema, one generator, many outputs**, while keeping
every repo's own files authoritative for its own creature.

---

## 2. The four layers

Almost every "it broke between engines" bug is a conflation of four things. UNITE
separates them and hashes each one.

| Layer | Question it answers | Lives in | Changes when |
|---|---|---|---|
| **Morphology** | What is this body? | `morphology` in the contract | the MJCF changes |
| **Interface** | What does the network see and emit? | `observation`, `action`, `roles` | the observation or action layout changes |
| **Timing** | At what rate, with what solver? | `physics` | the sim step or decimation changes |
| **Behaviour** | What has it learned? | the `.onnx` + its sidecar | retraining |

A policy sidecar pins three hashes — `morphology_sha256`, `interface_sha256`,
`physics_sha256` — so a runtime can refuse to load a brain that was trained against a
body or an interface it does not have. `PoRobotTrain` already does exactly this with
`validation.policy_sha256` and `metadata.joint_map_sha256`; UNITE makes it universal.

---

## 3. The role socket — how many creatures share one brain

You do not make creatures identical. You make them **interchangeable behind a socket**.

Every creature declares a `role` for each of its joints, drawn from one canonical list
(`contract-adapter-example.json`):

```
legs     hip_pitch_l hip_roll_l hip_yaw_l knee_l ankle_pitch_l ankle_roll_l toe_l   (and _r)
torso    waist_roll waist_yaw
arms     shoulder_pitch_l shoulder_roll_l shoulder_yaw_l elbow_l wrist_l           (and _r)
head     head_pitch head_yaw
```

The policy is built on **roles**, never on a creature's joint names. The adapter maps
role → that creature's joint (and, on hardware, role → `motor_slot`). Roles the creature
does not have are zero-filled and flagged in a `role_mask`, and a `morphology_onehot`
tells the policy which body it is driving.

That yields one interface, 131 observations and 28 actions wide, for every creature:

| Creature | Mapped roles | Masked | Same policy? |
|---|---|---|---|
| `unitree_r1_a5` (26 motors, **hardware**) | 26 | `toe_l`, `toe_r` | yes |
| `streetball_humanoid` (29 DoF) | 26 | `head_pitch`, `head_yaw` | yes |
| `matthew` / `grandma` / `g1` (29 DoF) | 24 | `toe_l`, `toe_r`, `head_pitch`, `head_yaw` | yes |
| `go2` (12 DoF, quadruped) | 12 | 16 | yes |

All of them live in [`creatures/`](./creatures/INDEX.md) as drop-in prefabs.

The legs are the important part: R1, Matt, the basketball humanoid and the Olympic
athletes all share `hip pitch/roll/yaw, knee, ankle pitch/roll` in that axis order.
One locomotion brain is therefore trainable on one morphology and portable to the other
three **today**, before any new work.

Worked example, with every joint named: [`contract-adapter-example.json`](./contract-adapter-example.json).

---

## 4. What "all training in Unreal" actually means

Not Chaos physics. Every repo in this family already states the same rule, and it is
correct: *MuJoCo owns the physics; Unreal never simulates the creature.*
`PoRobotTrain/AGENTS.md`, `PoSlapUnreal/AGENTS.md` and `PoBasketball` all say it, and
`PoSlapUnreal` documents that upgrading MuJoCo 3.10 → 4.0 changes match outcomes, which
is precisely why the physics engine must stay pinned and shared.

So the target is:

```
                    +----------------------------------+
                    |  trainers (one package, many envs)|
                    |  PPO / AMP / SAC, contract-driven |
                    +----------------+-----------------+
                                     |
        +----------------------------+----------------------------+
        |                            |                            |
   mujoco:// (fast, 512 envs)   urlab:// (UE host)          unity:// (legacy)
   MuJoCo / MJX / Warp          MuJoCo inside URLab          ML-Agents envs
   the training workhorse       rendering, scenarios,        bridged or retired
                                evaluation, demo, capture
                                     |
                            +--------+--------+
                            |   UE NNE 50 Hz  |  <- same ONNX as everyone else
                            +--------+--------+
                                     |
                              physical robot
                        (same contract, motor_slot map)
```

Unreal becomes the **host, renderer, scenario system and gate** — which is what
`PoRobotTrain`'s `R1Twin` and `PoBasketball`'s `StreetballGym` already are — while the
vectorised MuJoCo trainer keeps producing the sample throughput. Moving PPO *into* UE
would cost an order of magnitude in throughput and buy nothing.

---

## 5. The creature library

[`creatures/`](./creatures/INDEX.md) holds six actual prefabs, built from the MJCFs already in
your repos — not sketches. Each is `creature.xml` + `scene.xml` + `role_map.json` +
`contract.json` (+ `meshes/`, and `hardware.json` for the R1).

| id | kind | joints | roles | hardware | source |
|---|---|---|---|---|---|
| `unitree_r1_a5` | humanoid | 26 | 26/28 | **yes** | PoRobotTrain twin |
| `streetball_humanoid` | humanoid | 29 | 26/28 | no | PoBasketball |
| `matthew` | humanoid | 29 | 24/28 | no | PoRaceUnreal (Matt) |
| `grandma` | humanoid | 29 | 24/28 | no | PoRaceUnreal |
| `g1` | humanoid | 29 | 24/28 | no | PoRaceUnreal |
| `go2` | quadruped | 12 | 12/28 | no | PoRaceUnreal |

Mounting one in any scene:

```xml
<mujoco model="my game">
  <compiler angle="radian" autolimits="true"/>          <!-- required -->
  <include file="../../PoPunkouterSoftware/docs/unite/creatures/go2/creature.xml"/>
  <worldbody>
    <geom name="floor" type="plane" size="0 0 0.05"/>    <!-- required -->
  </worldbody>
</mujoco>
```

Three rules, each verified empirically on MuJoCo 4.0.0 by `tools/check_creatures.py`:

1. **No `meshdir`.** `<include>` resolves asset paths against the *including* file's directory and
   discards `meshdir`, so a creature using `meshdir="assets"` fails the moment another project
   includes it. Every mesh here is referenced as `meshes/<basename>`.
2. **One unit system, radian.** An included file's `<compiler>` is ignored — the host's wins. A
   `angle="degree"` creature inside a radian host silently multiplies every joint range by
   `pi/180`. `streetball_humanoid` is converted at build time for exactly this reason.
3. **The floor belongs to the host.** Creatures strip their own floor, lights and ground textures;
   sensors that referenced `floor` move to `scene.xml`. A creature that owns a floor cannot be
   placed in a court, a track or a hill without a name collision.

`check_creatures.py` proves all three per creature: it compiles the prefab standalone, compiles it
again from a temp directory through the mount snippet above, asserts the two joint tables are
identical, and asserts the split preserved every joint axis, range and body mass from the original
source model.

Two consumer notes: **Unity cannot read `<include>`** — give it `creature.xml` directly, it is
already flat. **URLab drops the included `<option>`** — reapply `contract.json` → `physics.option`
at bind time, exactly as `PoRaceUnreal/training/contract.py` already documents.

The R1 is the special case: its contract carries a `hardware` block (bus, 31-slot map, 50 Hz,
deploy gate) and `motor_slot` on every joint, so a sim action vector and a `LowCmd_` command vector
are the same index space. See [`creatures/unitree_r1_a5/README.md`](./creatures/unitree_r1_a5/README.md).

### The first agent in the library

`runs/ppo-r1-edu-stand` is the first policy here that loads in every runtime at once: 131/28 role
interface, a sidecar written by the export guard, and all 26 motors mapped onto their real
`unitree_hg` slots. It is a **balance** policy (command `0,0,0`) trained for 200k steps — the contract
chain is the point of the example, not locomotion quality.

```powershell
$py = 'PoRobotTrain\.venv\Scripts\python.exe'

& $py PoPunkouterSoftware\docs\unite\tools\role_ppo.py --creature unitree_r1_a5 --command-range 0,0,0 --log-std -1.0 `
      --iters 900 --steps 64 --envs 16 --lr 1e-4 --entropy 0.002 --out PoPunkouterSoftware\docs\unite\runs\ppo-r1-edu-stand
& $py PoPunkouterSoftware\docs\unite\tools\role_agent.py PoPunkouterSoftware\docs\unite\runs\ppo-r1-edu-stand --hardware
& $py PoPunkouterSoftware\docs\unite\tools\role_agent.py PoPunkouterSoftware\docs\unite\runs\ppo-r1-edu-stand --all-creatures
```

**What it is not.** This is 920k PPO steps on CPU, and it does not learn to stand: it holds the nominal stance for
under two seconds and then tips over. It is a conforming seed that proves the whole chain — interface, sidecar,
refusal, creature portability, motor-slot mapping — not a locomotion result. Two facts to carry forward: the
upright shaping term in `role_ppo.py` used to be `clip(tilt + 0.7, -1, 0)`, which is identically zero because
`tilt >= 0`, so standing had no gradient at all; with it fixed the same budget scores 1.9 instead of 1.2 and still
falls. A policy that stands needs the GPU trainer and a 10⁷-step budget, not more CPU. The measured behaviour is
in the `--hardware` report next to the 31-slot command, so nobody mistakes "loads everywhere" for "stands up".

**It already runs inside three apps.** `PoRobotTrain`, `PoBasketball` and `PoRaceUnreal` are the first
consumers, each with its own implementation of the builder and its own golden-parity test:
`RobotControl/execution/role_interface.py` (drives `PolicyRunner`, refused on hash mismatch),
`sim/streetball/role_obs.py` + `role_agent.py` (plays a portable brain on the court), and
`training/racelib/role_obs.py` (covers every body this repo races, and names the one it cannot:
the stroller has no creature). `check_cross_app.py` reports all three as `131 builder (9/9)`;
`PoSlapUnreal` still speaks its own 142-float slap layout.

---

## 6. What lives here vs. in a repo

The Po Game Standard forbids one repo generating into or syncing files with another.
UNITE respects that:

- **Here (global):** the schema, the canonical role list, the generator, the emitted
  reference contracts, the roadmap and the adoption rules.
- **In each repo:** a pinned, vendored copy of the generator and the emitted
  `contract.json` for its own creatures, plus a test that `--check` is clean. Nothing
  is copied *into* a repo by another repo.

A repo upgrades on purpose, by bumping a vendored version, exactly like it pins
ML-Agents, URLab or a MuJoCo version today.

---

## 7. Index

| File | What it is |
|---|---|
| [`RULES.md`](RULES.md) | **the universal RL training prompt — paste this at the start of a session inside one RL repo** |
| [`pocontract.schema.json`](./pocontract.schema.json) | JSON Schema for `contract.json` v1 |
| [`contract-adapter-example.json`](./contract-adapter-example.json) | one canonical role order, five creatures mapped onto it |
| [`creatures/INDEX.md`](./creatures/INDEX.md) | the six drop-in creatures: mount snippet, roles, sources |
| **docs/** | why and how, no code |
| [`docs/VISION.md`](./docs/VISION.md) | the goal, the two kinds of compatibility, and what this folder is not |
| [`docs/PORTABILITY.md`](./docs/PORTABILITY.md) | why matching gravity/friction is not enough: the six layers, and what may differ |
| [`docs/GOVERNANCE.md`](./docs/GOVERNANCE.md) | versioning, vendoring, review checklist, how to add a creature |
| [`docs/ROADMAP.md`](./docs/ROADMAP.md) | the phases, in order, with exit criteria and real file targets |
| [`docs/AGENTS-SECTION.md`](./docs/AGENTS-SECTION.md) | the cross-app section to paste into a repo's `AGENTS.md` |
| [`docs/UNREAL-HUMANOID.md`](./docs/UNREAL-HUMANOID.md) | **the Unreal render standard** (R29–R34): sim proxy → IK Retargeter → Mannequin, role→chain map, no Chaos, verification |
| [`docs/TRAINING-ENV.md`](./docs/TRAINING-ENV.md) + [`training-envelope.json`](./training-envelope.json) | **the real-Earth training standard** (R35–R43): Earth envelope, env, reward, logs, eval, tiers, robot tier; checked by `tools/check_training.py` |
| **prompts/** | one prompt per job, never mixed |
| [`prompts/PROMPT-ProfileC.md`](./prompts/PROMPT-ProfileC.md) | the **fleet-wide** Profile C replacement: scope gate (C0/C1/C2/N/A) + the fix pass for CODECONSISTANT |
| [`prompts/CODECONSISTANT-v5.md`](./prompts/CODECONSISTANT-v5.md) | the **whole** audit prompt, v4 with Profile C already swapped in — paste this one |
| [`prompts/PROMPT-UnrealApp.md`](./prompts/PROMPT-UnrealApp.md) | **the first prompt for a new Unreal RL app** (`UR_START`): one round of requirements → PRD → one approval → a working first attempt (UE + URLab + NNE, real-Earth training pipeline, smoke brain, tests, CI) → roadmap |
| *(the per-repo prompt is [`RULES.md`](RULES.md) above — rules in §4, work plan in §5)* | |
| **tools/** | generators and verifiers |
| [`tools/pocontract_lib.py`](./tools/pocontract_lib.py) | canonical roles, auto-mapper, contract builder (shared) |
| [`tools/build_creatures.py`](./tools/build_creatures.py) | builds `creatures/*` from the repos' MJCFs, `--check` for staleness |
| [`tools/check_creatures.py`](./tools/check_creatures.py) | proves portability: foreign include, split fidelity, contract invariants |
| [`tools/validate.py`](./tools/validate.py) | schema + invariant check over every creature contract |
| [`tools/verify_policy.py`](./tools/verify_policy.py) | **the export guard and sidecar writer (R10/R11)** — `--check` aborts on a non-conforming policy, `--write` writes the sidecar, `--verify-sidecar` does the runtime check, `--selftest` tests itself |
| [`tools/role_obs.py`](./tools/role_obs.py) | **the reference 131-float builder** and the per-creature `golden_role_obs.npz` every runtime must reproduce |
| [`tools/role_env.py`](./tools/role_env.py) | one virtual env for every creature: 28 actions in, 131 observations out |
| [`tools/role_ppo.py`](./tools/role_ppo.py) | a reference trainer on that interface; exports a conforming policy + sidecar |
| [`tools/role_agent.py`](./tools/role_agent.py) | **the runtime**: load a conforming policy (refuse on mismatch, R14), run it on any creature, and build the R1's 31-slot `unitree_hg` command vector |
| [`tools/check_cross_app.py`](./tools/check_cross_app.py) | **the verification**: interface identity, loadability on every creature, and whether each app can actually produce the 131 floats |
| [`tools/verify_policy.py`](./tools/verify_policy.py) | the export guard + sidecar writer (R10/R11) |

```powershell
# build every creature prefab (and creatures/INDEX.md)
& PoRobotTrain\.venv\Scripts\python.exe PoPunkouterSoftware\docs\unite\tools\build_creatures.py

# fail if a prefab on disk is stale
& PoRobotTrain\.venv\Scripts\python.exe PoPunkouterSoftware\docs\unite\tools\build_creatures.py --check

# prove every creature loads in isolation AND when included from another directory
& PoRobotTrain\.venv\Scripts\python.exe PoPunkouterSoftware\docs\unite\tools\check_creatures.py

# validate every contract against the schema
cd PoPunkouterSoftware\docs\unite; uv run --no-project --with jsonschema python tools\validate.py
```

---

## 8. Non-goals and hard constraints

- **Do not train in Chaos.** MuJoCo stays the physics engine everywhere.
- **Do not unify the creatures' geometry.** Only the *interface* is shared; bodies stay
  creature-specific.
- **Do not re-export ML-Agents ONNX as-is.** ML-Agents bakes observation normalisation
  and its own vector-observation layout into the graph, with its own input names and
  shapes. Those graphs load in Unity and mean nothing anywhere else. `PoFootball`,
  `PoSoccer` and `PoSumo` policies must be re-exported as plain MLPs with normalisation
  moved outside the graph before they can be shared — otherwise they will silently
  produce garbage on the R1 instead of failing.
- **Do not let one contract cover two physics versions.** A contract records exactly one
  MuJoCo version and one solver block. `PoRobotTrain` currently has two (4.0.0 for the
  twin/URLab, 3.5.0 for the mjlab walk sandbox); those are two contracts, not one.
- **Do not silently promote a policy.** Every repo already gates; UNITE keeps the gate
  and the SHA match as a load-time precondition, not a convention.
