# Vision and scope

The one-page statement of what this folder is for. Everything else here is mechanism.

## The goal, in one sentence

Train RL agents in MuJoCo; let any creature go into any Po game; let any brain that was trained
against the shared interface go with it; and let the R1 creature's brains reach the physical robot.

## Two kinds of compatibility — do not conflate them

This is the distinction that decides what is already true and what is still work.

| | **Creature compatibility** | **Brain compatibility** |
|---|---|---|
| Means | this body loads and runs correctly in any MuJoCo app | this trained policy produces correct actions in any app |
| Requires | portable prefab + contract hashes | the policy was *trained against the role interface* |
| Status today | **done** — 6 prefabs, verified standalone and via foreign `<include>` | **in progress** — only new training produces portable brains |
| Drop a creature in | works now | — |
| Drop a *brain* in | — | needs re-export (no retrain) or retrain |

A creature is a file. A brain is a function of the interface it was trained on. Swapping files is
free; swapping brains is a migration.

## What is already true (measured, not planned)

- Six creatures load standalone **and** when included from a different directory, with identical
  joint tables (`tools/check_creatures.py`).
- All six share one interface: `interface_sha256_16 = 94377ebae772ff6c`, 131 observations,
  28 actions. One ONNX input/output spec for the whole family.
- Earth parameters are asserted, not assumed: gravity `[0,0,-9.81]`, SI units, and a
  `timestep × decimation == control_dt` check on every build.
- The R1 carries a hardware contract: 26 joints into the 31-slot `unitree_hg` bus, 50 Hz, a deploy
  gate, and its policies gated by `policy_sha256`.
- PoRobotTrain already has the sim-to-real proof: `sim_real_diff` at <5° max per-joint per 20 ms tick.

## What "same environment" should mean

Not identical worlds. A court, a track and a hill must be able to coexist — that is the point of
having several games.

> **Contracts must match. Worlds must sit inside the trained distribution.**

So the mechanism for cross-environment brains is **randomisation during training** (friction,
terrain, payload, encoder bias, pushes), not cloning one floor into every app. A brain trained on
one exact floor will not transfer even to a perfect copy of it, because the first drift is
out of distribution.

## For the R1 specifically: compatibility is not fidelity

Two separate requirements, tracked separately:

1. **Compatibility** — same interface, same role order, same motor-slot map, same 50 Hz. This makes
   a brain *loadable* on the robot. Largely done.
2. **Fidelity** — the simulated robot behaves like the real one. This makes a brain *correct* on the
   robot. Not done: mass is 30.15 kg vs 28.93 kg, contacts differ, and `sim_calibration.yaml` has
   never been filled in.

Masses and inertias belong to **links**; limits, torque and gains belong to **joints**. Sim-to-real
needs both sets, and they are separate fields in the contract for that reason.

## What this folder is, and is not

**Is:** the registry and the generator. Creatures, contracts, brains-in-adoption, and the tools that
build and verify them.

**Is not:** a monorepo. No app code, no builds, no per-game configuration, no assets that belong to a
game. Referencing `global/UNITE` from a repo is not "shared code between repos" — it is not a repo —
but every reference is pinned by hash so drift is visible.

Three subfolders only:

```
creatures/   bodies + contracts (done)
agents/      trained policies + sidecars, by adoption tier (next)
tools/       builders and verifiers (done)
```

## What would make this fail

- Training against **joint order** instead of roles → no brain is ever portable.
- **Normalisation baked into the ONNX** (Unity ML-Agents export) → loads everywhere, correct nowhere.
- Each repo **hand-typing** `n_obs`, gains or joint order → silent drift, and the hash then documents
  a lie.
- Treating the **twin as ground truth** for hardware.
- Never **calibrating**, because it needs robot time that never gets scheduled. No architecture
  substitutes for this.
- Making every game use the **same world** to force compatibility, and ending up with one game.

## The honest summary

The concept is correct and the hard mechanism already works: creatures are portable today. The
remaining work is not architectural, it is two disciplines — train against the shared interface, and
calibrate the R1 against the real robot. Everything in this folder exists to make both of those
measurable instead of aspirational.
