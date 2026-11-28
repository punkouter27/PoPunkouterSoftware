# Unreal humanoid standard — how a UNITE creature is shown in UE5

This is the render-side half of UNITE. `RULES.md` R29–R34 refer to it. It applies to every Unreal app
in the family, including `PoRaceUnreal`, `PoSlapUnreal`, `PoBasketball` and the `PoRobotTrain` twin.

The contract decides what the brain sees. This file decides what the player sees. Nothing in it may feed
back into the observation.

---

## 1. The pipeline

There are three meshes and one direction of flow:

```
MuJoCo (URLab)         Sim proxy              Display mesh(es)
bodies xpos/xquat ──►  SK_<id>_Proxy  ──────►  SK_Mannequin / MetaHuman / any
50 Hz, authority       one bone per body      IK Retargeter, runtime
                       exact copy, no IK      cosmetic foot IK allowed here only
```

1. **MuJoCo is the authority.** URLab steps the creature at `contract.physics.control_dt`. Unreal reads
   body world transforms after each step.
2. **The sim proxy is exact.** It is a skeletal mesh whose bones are the MJCF bodies, with the same names
   and the same parent chain. The AnimBP copies each body's world transform onto its bone. There is no
   IK, no blending and no retargeting at this stage. It doubles as the debug view, togglable with
   `/diag` or a console variable.
3. **Display meshes retarget from the proxy.** An IK Rig on the proxy (`IKR_<id>_Sim`), an IK Rig on the
   UE5 Mannequin (`IKR_Mannequin`), and one IK Retargeter (`RTG_<id>_to_Mannequin`). The display AnimBP
   uses **Retarget Pose From Mesh**, with the proxy as its source. Any Mannequin-compatible mesh, MetaHuman
   bodies included, is then a drop-in change of mesh.

Why it is built this way:
- Copying world transforms, not joint angles, avoids every axis-order and sign mismatch between MJCF
  hinge axes and UE bone frames.
- The proxy keeps sim truth visible and testable.
- The retargeter absorbs the difference in proportions, so a mesh swap never touches the sim.

## 2. Canonical skeleton for display meshes

Display meshes use the **UE5 Mannequin** hierarchy and bone names: `root`, `pelvis`, `spine_01..05`,
`neck_01..02`, `head`, `clavicle_*`, `upperarm_*`, `lowerarm_*`, `hand_*`, `thigh_*`, `calf_*`,
`foot_*`, `ball_*`.

Asset rules:
- Z-up, facing +X, in centimetres.
- `root` sits at the origin on the floor.
- No root motion.
- LODs authored.
- Ref pose as shipped by the Mannequin. Pose alignment happens in the retarget pose (§4), never by
  editing the skeleton.

## 3. Role → Mannequin chain map (identical in every app)

The IK Retargeter chains are named after UNITE roles, so every app retargets in the same way.

| Chain | Roles | Mannequin bones |
|---|---|---|
| `Pelvis` (retarget root) | free joint | `pelvis` |
| `Spine` | `waist_roll`, `waist_yaw` | `spine_01` … `spine_05` |
| `Head` | `head_pitch`, `head_yaw` | `neck_01` … `head` |
| `LeftLeg` / `RightLeg` | `hip_*`, `knee_*`, `ankle_*` | `thigh_*` → `calf_*` → `foot_*` |
| `LeftToe` / `RightToe` | `toe_*` | `ball_*` |
| `LeftArm` / `RightArm` | `shoulder_*`, `elbow_*` | `clavicle_*` → `upperarm_*` → `lowerarm_*` |
| `LeftHand` / `RightHand` | `wrist_*` | `hand_*` |

The MJCF body that carries each role comes from `creatures/<id>/role_map.json`. A role masked in the
contract leaves its chain at the retarget pose. Leg chains use IK goals on the feet. Arm chains are FK.

## 4. Retarget pose

The retarget pose of `IKR_<id>_Sim` is the creature at `contract.action.default` (its nominal stance). The
Mannequin's retarget pose is edited to match that stance. Without this, every frame carries a constant
offset that looks like a bad policy.

## 5. Coordinates and timing

- **One conversion, in one place.** MuJoCo is right-handed, Z-up, in metres. UE is left-handed, Z-up, in
  cm. The conversion: `x_ue = 100·x`, `y_ue = −100·y`, `z_ue = 100·z`; for quaternions
  `(w, x, y, z) → (w, −x, y, −z)`. Use URLab's conversion if it provides one. Otherwise write one
  function, cover it with a round-trip unit test, and give it no second caller-side fix-up.
- **Fixed-step sim, variable-rate render.** The sim and the policy tick at `control_dt`. The render
  interpolates between the last two sim states: lerp for positions, slerp for rotations. It never
  extrapolates. That adds at most one control tick (20 ms) of display latency.
- **Tick order, each frame:** step the sim (0..n ticks) → run the policy (NNE) → write the pose buffer →
  evaluate animation. The pose copy runs on the game thread before anim evaluation, so there is no
  frame of lag.

## 6. What Unreal must not do to the creature

- **Physics Asset** (`PHYS_<id>_Query`): used only for traces, overlaps and hit reactions. `Simulate
  Physics` is off. There are no Chaos ragdolls, no Physical Animation component and no physics
  constraints on any bone.
- **Character Movement Component:** absent, or present only with movement disabled. The actor or capsule
  follows the sim pelvis. It never moves the creature.
- **Foot IK and ground conform:** allowed only on display meshes. The correction is capped at **2 cm**
  and logged. A larger correction means a mapping or retarget-pose bug, so fix the cause.
- **Blending:** no blending of animation clips into a sim-driven pose. Cut-scenes and menus may use
  clips while the sim is paused.

## 7. Inference and performance

- NNE uses the ONNX Runtime **CPU** backend. For a 131→28 MLP, the GPU dispatch overhead exceeds the
  compute.
- Input and output tensors are allocated once at bind time. The policy step and the 131 builder allocate
  nothing per tick.
- Turn Update Rate Optimisation off for hero creatures. Distant crowd creatures may use it, on display
  meshes only, never on the proxy.

## 8. Asset naming (per repo, under `/Game/Creatures/<id>/`)

| Asset | Name |
|---|---|
| sim proxy skeletal mesh | `SK_<id>_Proxy` |
| sim IK Rig | `IKR_<id>_Sim` |
| Mannequin IK Rig | `IKR_Mannequin` |
| retargeter | `RTG_<id>_to_Mannequin` |
| proxy AnimBP (pose copy) | `ABP_<id>_Proxy` |
| display AnimBP (retarget + foot IK) | `ABP_<id>_Display` |
| query-only physics asset | `PHYS_<id>_Query` |

These assets belong to the repo. They are never copied from another repo (R26/R27). The naming is shared
so that a reviewer, or a crawler, can find them in any app.

## 9. Verification

| Check | Pass |
|---|---|
| Transform round trip (MuJoCo → UE → MuJoCo) | ≤ 1e-6 m, ≤ 1e-6 rad |
| Proxy pose vs MuJoCo `xpos/xquat`, every body, 5 s rollout | ≤ 1 mm, ≤ 0.5° |
| Display foot correction from foot IK | ≤ 2 cm, logged per frame |
| No Chaos on the creature | `Simulate Physics` false on every body of `PHYS_<id>_Query` (automation test) |
| Per-tick allocations in policy and observation | 0 (Unreal Insights memory trace) |

A video of the creature moving is not evidence (R12). The proxy-vs-MuJoCo number is.
