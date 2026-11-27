# UNITE roadmap

Order matters. Each phase produces something that runs and is verified before the next
starts. Do not begin an engine migration while its contract is still ambiguous — you
would retrain twice.

Legend: **[P]** = python, **[C++]** = Unreal/URLab, **[C#]** = Unity, **[doc]**, **[CI]**.

---

## Phase 0 — Freeze the dialect (2–4 days)

Goal: one file that describes a creature, an interface and a timing, and nothing else.

1. Add `global/UNITE/pocontract.schema.json` (done) as the only schema. No second schema.
1b. `global/UNITE/creatures/` (done) holds six mounted prefabs; `tools/check_creatures.py`
   proves each one loads in isolation and through `<include>` from a foreign directory.
2. Run the existing generators and capture what each emits today, so nothing is lost:
   - `PoBasketball/sim/streetball/obs.py::write_spec_and_golden` → `obs_spec.json`
   - `PoRaceUnreal/training/contract.py --check` → `go2_fingerprint.json`
   - `PoSlapUnreal/training/gen_constants.py --check` → `SlapShared.gen.h`
   - `PoRobotTrain/RobotControl/configs/joint_map.py` → `r1_joint_map.yaml`
3. Record each in the contract's `legacy_interfaces` block. **Nothing is deleted.**
4. Decide the one open question: is the canonical role list 28 wide (this proposal) or
   per-kind (humanoid 28 / quadruped 12)? Ship 28 with masks; it is the only variant that
   lets one file describe a Quadruped and a Humanoid at once.

**Exit criteria**
- Every `creatures/*/contract.json` validates against the schema (`tools/validate.py`).
- `tools/check_creatures.py` passes for all six: identical joint tables standalone vs. foreign include,
  split fidelity against the original source model.
- The same generator emits `go2` and `humanoid_bball` with `n_obs == 131` for both.
- A diff against the four legacy artifacts shows every field accounted for.

---

## Phase 1 — One generator, vendored everywhere (1 week)

Goal: `pocontract`, a dependency-free-ish Python tool that writes every downstream form.

**[P] Outputs of `pocontract emit <source>`**

| Output | Consumed by | Modeled on |
|---|---|---|
| `contract.json` | every runtime | `obs_spec.json` + `<creature>_fingerprint.json` |
| `Contract.gen.h` | UE modules | `RaceContract.gen.h`, `SlapShared.gen.h` |
| `contract.py` | trainers | `training/constants.py` |
| `Contract.g.cs` | Unity | `PoOlympic/Assets/_Project/Core` |
| `contract.gen.js` | browser twin | `PoSlapUnreal/Web/shared.gen.js` |
| `golden_obs.npz` / `.bin` | every runtime's parity test | `shared/golden/obs_golden.*` |

**[P] Rules**
- `pocontract emit` writes; `pocontract --check` exits 1 when a file on disk differs.
  `PoRaceUnreal/training/contract.py` and `PoSlapUnreal/training/gen_constants.py`
  already implement this contract-test pattern — keep it verbatim.
- Float comparison with 1e-12 relative tolerance across Windows/Linux libm
  (`PoRaceUnreal/training/contract.py::_json_equal` already does this).
- Hashes: `interface_sha256` covers slot names, sizes, scales, frames and `rule` prose
  (a text-only edit must change the hash — `PoBasketball` learned this on 2026-11-20).

**[CI] Per repo**
- Vendor at `third_party/pocontract/<version>/` + `third_party/pocontract/VERSION`.
- One test: `pocontract --check` is clean, and the vendored version is on an allowlist.

**Exit criteria**
- `poemittwice` regeneration is byte-identical in `PoRobotTrain`, `PoBasketball` and
  `PoRaceUnreal`.
- Each repo's existing contract test still passes, now driving the new generator.

---

## Phase 2 — The role socket, proven by transfer (1–2 weeks)

Goal: one locomotion brain, four bodies, measured transfer.

1. **[P]** `roles.py`: canonical order, `mask`, `morphology_onehot`, and
   `roles_to_joints(creature)` / `joints_to_roles(creature)`.
2. **[P]** Extend the observation builder so ordering is by role, not by joint index.
   Start from `PoBasketball/sim/streetball/obs.py::compute_obs` (it already separates
   `joint_order` from slot layout) and `PoRobotTrain/RobotControl/walk/policy.py`.
3. **[P]** Pad/mask + `held_outside_brain` PD-hold. `deploy.yaml`'s `passive_joints` /
   `passive_kd` / `held_joints` is already the mechanism; generalize it.
4. **[P]** Retarget the nominal pose across morphologies — `PoBasketball/sim/tools/retarget_loco_mujoco.py`
   is the seed.
5. Train one velocity-tracking brain on R1 legs (12 roles), then evaluate the **same
   weights** on the basketball humanoid and Matt in MuJoCo, reporting per-creature
   fall rate and tracking error.

**Exit criteria**
- One `.onnx` loads in three MuJoCo scenes without re-export.
- The gate records a per-creature pass/fail table, not a single number.
- Documented honest result: transfer works, transfers partially, or needs an adapter
  MLP. A partial result written down beats a claim.

---

## Phase 3 — `pogym`: URLab as a training environment (1–2 weeks)

Goal: the trainer does not care which engine stepped the world.

1. **[P]** `pogym` — a Gymnasium-style `Env` whose `reset`/`step` speak
   `contract.json` (obs slots, action rule, clip) and whose transport is pluggable:
   - `mujoco://` — direct `mujoco` step, N parallel envs (today's path, unchanged)
   - `urlab://` — MuJoCo inside Unreal through `UnrealRoboticsLab`
   - `unity://` — legacy ML-Agents bridge, marked deprecated
2. **[C++]** Transport. `PoRobotTrain` already has the pieces:
   `RobotControl/interfaces/urlab_interface.py`, `UnrealProject/Saved/URLabShm`
   (shared memory) and the direct-mode step RPC. Promote the shared-memory path to the
   supported one; it is the only one that survives render-rate coupling — the documented
   `step5_runtime.md` finding that "policy is paced by the game thread" is the reason.
3. **[C++]** Never let UE drive physics. Keep the existing handoff: the in-engine NNE
   policy releases actuators while a Python client steps URLab.
4. **[P]** `potrain` env registry: `mujoco://go2`, `urlab://go2`, `mujoco://r1`,
   `urlab://r1`. Same PPO, same config keys, one `--env` flag.

**Exit criteria**
- The same policy, run for 500 steps in `mujoco://` and `urlab://`, matches within the
  documented tolerance, with the numbers pasted into the contract's `references`.
- The trainer's iteration time on `urlab://` is measured and documented, so the
  throughput trade-off is explicit.

---

## Phase 4 — One gate, sim to real (1 week)

Goal: no policy reaches the robot, UE or a showcase without the same proof.

1. **[P]** `pocontract verify <policy.onnx>` → `validation.json` with the checks every
   repo already does somewhere:
   - ONNX vs trainer parity (`ort_vs_torch_max_abs_diff`, `PoBasketball`)
   - observation parity on goldens, ≤ `references.tolerance`
   - `policy_sha256` + three contract hashes recorded
   - gains/defaults/action scale match the joint map (`PoRobotTrain`'s gate checks this)
2. **[C++]** Load-time precondition: UE refuses a policy whose sidecar does not match the
   contract it was built against. `PoRobotTrain` already does this for head skills;
   `PoBasketball`'s `ObsBuilder` already checks `n_obs` at init. Make it a hard failure
   everywhere, including `StreetballGym` and `PoRaceUnreal`.
3. **[P]** Real-telemetry replay: feed recorded `PoRobotTrain` telemetry through the
   policy and diff actions against what the robot did. `telemetry_log.py` has the data;
   this is the only test that catches an encoder-sign or unit error before hardware.
4. **[P]** Actuator model in the contract (`motor_model.json`, `sim_calibration.yaml`)
   so sim and hardware share one torque/current curve.

**Exit criteria**
- `verify` is the only path to a deployable policy, in every repo.
- A deliberately corrupted sidecar is rejected by UE, Unity and the service.

---

## Phase 5 — Engine consolidation (ongoing)

Do this **last**, contract by contract, not repo by repo.

| From | To | Trigger |
|---|---|---|
| `PoFootball`, `PoSoccer`, `PoSumo` (Unity ML-Agents) | MuJoCo + contract + UE NNE | next time the env needs a change |
| `PoOlympic` (Unity + mjlab/Sentis) | same | after its parity folder is expressed as goldens |
| `PoBox`, `PoHillKing` (MuJoCo in C#) | contract consumer, engine TBD | when their physics pin is next bumped |

ML-Agents graphs are not portable as-is (§7 of the README). A Unity game moves when its
brain is re-exported as a plain MLP with normalisation outside the graph — that is a
retrain, so schedule it deliberately.

---

## Sequencing rationale

- **Contract before engine.** Engine work without a frozen contract is a rewrite that
  must be redone when the contract settles.
- **Roles before transfer.** Transferring a joint-indexed policy is a remap script that
  will be thrown away.
- **`pogym` after transfer.** Otherwise you validate `pogym` on a policy that already
  cannot move between bodies, and learn nothing.
- **Gate before hardware.** The gate is cheap; a bad policy on a 60 Nm knee is not.

## Risks, stated

| Risk | Mitigation |
|---|---|
| Over-unification: forcing a Go2 into humanoid slots wastes network capacity | masks + morphology one-hot; measure a quadruped-trained brain both ways before committing |
| `urlab://` transport becomes the bottleneck and is unfixable upstream | keep `mujoco://` as the default training path; URLab is for validation and demo, not sample generation |
| Vendored generator drift across 10+ repos | version allowlist + `--check` in every repo's one workflow |
| Contract churn retrains everything | freeze `interface_sha256` per project; a change is a new `legacy_interfaces` entry, not an edit in place |
| Physics-version mismatch (MuJoCo 4.0.0 vs 3.5.0 inside `PoRobotTrain`) | one contract per physics pin; never average them |
