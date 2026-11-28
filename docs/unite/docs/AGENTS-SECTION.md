# AGENTS.md section — paste into any RL repo

The cross-app agent contract, in the form it takes inside a repo. `PoRobotTrain/AGENTS.md` carries this
already; the other MuJoCo repos (`PoBasketball`, `PoRaceUnreal`, `PoSlapUnreal`, and the C1 repos once
their creatures have contracts) should carry the same section with their own creature ids and paths.

Replace `unitree_r1_a5` with the repo's creature and adjust the two repo-local paths; everything else is
verbatim. Keep it short — it is a pointer plus the four rules that are expensive to get wrong.

---

### Cross-app agent format (applies to every policy trained from now on)

This repo is one app in a family that shares agents. The rules live in `PoPunkouterSoftware/docs/unite/RULES.md`; the
authoritative numbers live in `PoPunkouterSoftware/docs/unite/creatures/<creature>/contract.json`. Four things decide
whether a brain can be used anywhere else, and all four are enforced, not documented:

- **One interface for every creature.** Role-ordered, not joint-ordered: **131 observations**
  (`joint_pos_rel` 28, `joint_vel` 28, `prev_action` 28, `base_ang_vel` 3, `projected_gravity` 3,
  `velocity_commands` 3, `gait_phase` 2, `role_mask` 28, `morphology_onehot` 8) and **28 actions**
  (`clip(default + a*action_scale, soft_lo, hi)`). Roles this creature lacks are zero-filled and
  PD-held at nominal. Take the numbers from the repo's contract loader, never from a literal.
- **The observation is shared data, the builder is not.** `PoPunkouterSoftware/docs/unite/tools/role_obs.py` writes
  `golden_role_obs.npz` (100 states per creature). Any runtime that builds the 131 floats — C++ twin,
  web twin, a Unity plugin — must reproduce that golden to 1e-5. Two implementations agreeing is
  evidence; one library imported from two places is not.
- **No normalisation inside the exported graph** (R9). Normalisation belongs in the runtime and is
  declared in `contract.observation.normalizer`. Shipped policies that bake it in are legacy and cannot
  be ported — only retrained.
- **Portable is earned, not assumed** (R35–R43, `docs/TRAINING-ENV.md`):
  - Train across the **real-Earth** envelope in `training-envelope.json` (v2): real floors, motors,
    sensors and latency.
  - Use the standard termination and core reward terms.
  - Write `<policy>.training.json` and `train_log.csv` beside every `.onnx`, and export with
    `--require-card`.
  - Only `release` brains that pass every eval corner get `portable: true`.
  - A brain from another app loads only with `--require-portable`.
  - This app's `world.json` must pass `check_training.py world`.
  - Nothing reaches a physical robot unless its card is tier `robot` and `--require-robot-ready` passes
    (calibration, `sim_real_diff` < 5°, deploy gate, staged bring-up). It always goes through
    PoRobotTrain's deploy path.
- **Unreal apps render through one pipeline** (R29–R34, `docs/UNREAL-HUMANOID.md`). MuJoCo body
  transforms go onto the sim proxy, and the IK Retargeter takes them to the UE5 Mannequin. There is no
  Chaos on the creature, the Physics Asset is query-only, and foot IK is cosmetic and capped at 2 cm.
  Omit this bullet in non-Unreal repos.
- **Legacy is fenced, not migrated.** Existing interfaces stay runnable and are listed as `legacy` in the
  repo's registry; they are never promoted. New work targets 131/28 only.

| Thing | Path |
|---|---|
| the pinned shape + hashes | `contracts.lock.json` (re-pin with a deliberate script run, never by hand) |
| the one source of interface numbers | the repo's contract loader |
| the export gate | the repo's policy guard (warns on legacy, aborts on foreign) |
| the policy inventory | `agents/registry.json` |
| the reference builder + goldens | `PoPunkouterSoftware/docs/unite/tools/role_obs.py`, `PoPunkouterSoftware/docs/unite/creatures/*/golden_role_obs.npz` |
| one env for every creature | `PoPunkouterSoftware/docs/unite/tools/role_env.py` |
| a reference trainer | `PoPunkouterSoftware/docs/unite/tools/role_ppo.py` |

Training a new policy: build observations through the contract, export a plain MLP, and let the guard run
at the end of the export. Set the strict flag only once the runtime can feed 131 observations, not before.
Never hand-write an interface constant, reorder an observation to make an old brain load, or invent a
creature contract — stop and report instead.

---

## CLAUDE.md

Every repo's `CLAUDE.md` currently reads "Read AGENTS.md.", which stays correct. If you want a pointer that
survives a skimming session, use:

```markdown
Read AGENTS.md.

Two things in it are easy to miss and expensive to get wrong:

- **Section "Cross-app agent format".** Every new policy uses the 131-observation / 28-action role-ordered
  interface, takes its numbers from the repo's contract loader, exports a plain MLP with no normalisation
  in the graph, and passes the policy guard at the end of export. Authoritative rules:
  `PoPunkouterSoftware/docs/unite/RULES.md`.
- **Legacy policies are grandfathered.** Policies listed as `legacy` in `agents/registry.json` stay runnable
  and are never migrated or promoted. Do not reorder observations so an old brain fits a new loader.

If a creature has no contract in `PoPunkouterSoftware/docs/unite/creatures/`, stop and report it. Do not invent one.
```

---

## Why this belongs in AGENTS.md, not only in UNITE

`PoPunkouterSoftware/docs/unite/RULES.md` is the rule set a session is handed. `AGENTS.md` is what the repo says about
itself, and it is the file a coding agent reads first whether or not anyone remembered to paste a prompt.
Putting the four rules here is what makes conformance hold on a day nobody pastes anything.

Keep the two in step: if RULES.md changes a number or a path, change it here. The reverse is not true —
this section may amplify the rules, never extend them.
