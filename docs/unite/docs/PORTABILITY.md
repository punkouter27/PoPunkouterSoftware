# Why "same gravity and friction" is not enough

A common instinct: if two MuJoCo apps use the same gravity, the same ground friction and the same
creature, a brain trained in one should drive the other. It does not, and this repo family already
measured why. This note records the six independent things that must match, and the three that can
legitimately differ.

---

## The three counter-examples already in these repos

**1. Same world, same creature, different solver version.**
`PoSlapUnreal/AGENTS.md`: MuJoCo is pinned to 3.10.0 in all three engines. 4.0.0 was evaluated on
2026-11-20 — gravity unchanged, body unchanged, floor unchanged — and *"changes match outcomes (the
shipped brains KO a round earlier on the same seeds; `build_body.py`'s A12 fingerprint gate fails)"*.
A solver version is not a world property, and it moved every result.

**2. Same world, same MJCF, engine rebuilds the body slightly wrong.**
`PoRaceUnreal/training/contract.py`: URLab rebuilds the model from its components. Measured
2026-11-10 on the Go2: *"the free joint picks up the default class's damping 0.5 and armature 0.01
(MuJoCo never applies defaults to a free joint), the foot sites lose their positions, and every
number passes through single precision."* Gravity and floor friction were identical. The robot
still behaved differently, because the **body's** numbers had changed.

**3. Same physics, different observation definition.**
`PoRobotTrain/docs/level2_walk.md` fixes an observation order of
`base_ang_vel(3), projected_gravity(3), velocity_commands(3), gait_phase(2), joint_pos_rel(24),
joint_vel_rel(24), last_action(24)`. Reorder any two blocks — or change an `action_scale` — with
physics byte-identical and the policy outputs garbage. Physics cannot save a mismatched interface.

---

## The six layers that must match

| # | Layer | What must be identical | Where it lives |
|---|---|---|---|
| 1 | **World** | gravity, floor/contact `friction`, `solref`, `solimp`, `priority`, `condim` | host scene (partly free) |
| 2 | **Solver** | `timestep`, `integrator`, `solver`, `iterations`, `ls_iterations`, `cone`, `jacobian`, `impratio` | `physics.option` |
| 3 | **Body** | masses, inertias, joint axes/ranges/damping/armature, actuator `gainprm`/`biasprm`/`forcerange` | `morphology` |
| 4 | **Timing** | physics dt, decimation, policy rate (50 Hz), sensor latency | `physics.decimation`, `control_dt` |
| 5 | **Interface** | obs slot order/size/scale/frame, action rule, clip, normalisation placement | `observation`, `action` |
| 6 | **Mapping** | which actuator drives which joint, PD law, `motor_slot` on hardware | `morphology.joints`, `hardware` |

Layers 2, 3, 4, 5 and 6 are **inside the contract** already. Layer 1 is the only one the host
legitimately owns — and that is the important part.

---

## The good news: you do not want identical worlds

Requiring identical worlds would forbid the games from having a court, a track and a hill at all.
What makes a brain reusable is a matching **contract** (layers 2–6), plus a world that stays inside
the distribution it was trained on.

So the real rule is:

> **Contracts must be identical. Worlds must be *within the trained distribution*, not identical.**

A locomotion brain trained with randomised terrain friction, `solref` and payload will transfer to
a futsal court and a dirt track that differ in friction — because it learned the variation. A brain
trained on one exact floor will not, even if that floor is reproduced perfectly, because the moment
anything drifts it is out of distribution.

This is why `PoBasketball` randomises and why every repo records penalties and curriculum in
`reward_config.json`: the variation is the transfer mechanism, not a nuisance.

---

## What "same physical properties" actually means in MuJoCo

`friction` is a 3-vector `[sliding, torsional, rolling]`, and contact behaviour also depends on
`solref`/`solimp`, `priority`, `condim`, and the `cone` setting. Two scenes that both say
`friction="0.6"` can resolve contact differently. Copy the whole block, never one number:

```xml
<geom name="floor" type="plane" friction="0.6 0.005 0.0001"
      solref="0.006 1" solimp="0.95 0.99 0.001" priority="1" condim="3"/>
<option timestep="0.005" integrator="implicitfast" iterations="10" ls_iterations="20" cone="elliptic"/>
```

`cone="elliptic"` vs `pyramidal` alone changes walking contact behaviour; `elliptic` is what
`PoBasketball` uses and what the shipped humanoid brains expect.

---

## The cheap test that settles it

Do not argue from properties. Measure:

1. Same creature, same policy, same seed, same `qpos0`/`ctrl0`.
2. Step both hosts 500 times with the policy in the loop.
3. Compare joint trajectories and policy outputs.
4. Pass if the max deviation is inside the contract's `references.tolerance`.

`PoRaceUnreal/training/parity_check.py` and `PoSlapUnreal/training/parity_check.py` already do this
shape of test within a repo; `check_creatures.py` does it for the body alone. Extending it to
"two hosts, one brain" is Phase 3 of the roadmap (`pogym`), and it is the only evidence that counts.

---

## Practical consequences

- Bringing a policy across apps needs **six matches**, not one. Five of them are hashes you already
  emit; the sixth (world) is a distribution question you answer by randomising during training.
- A policy whose sidecar's `interface_sha256` matches a creature's contract is *almost* portable —
  what remains is the world/distribution argument, which is a training decision, not a code change.
- Two apps with identical worlds but different contracts will still fail. That failure is silent,
  which is exactly why the hashes and the load-time refusal matter more than matching gravity.
