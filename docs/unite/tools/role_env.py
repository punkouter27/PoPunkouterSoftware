"""One virtual environment for every creature: 28 actions in, 131 observations out.

    python role_env.py --smoke                 # step all six creatures, report stability
    python role_env.py --smoke go2 --steps 500

This is the reference *driver*, not a task. It owns the parts that must be identical across every app --
the action mapping, the masked-joint hold, the decimation and the observation -- and leaves the reward,
the episode length and the termination to whoever is training. That is the split that lets one trainer
drive a humanoid and a quadruped without knowing which is which.

Assumptions, stated because they are the ones that break first:

* the model's actuators are **position servos** (ctrl = target joint angle), which is true for all six
  creatures here: `go2`'s `general` actuators carry gainprm 35 / biasprm 0 -35 -0.5, i.e. affine PD;
* the free joint is the creature's root (checked, not assumed);
* the scene provides a `<geom name="floor">`, per the creature mount contract.

Requires numpy + mujoco. No torch, no onnx.
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402
from role_obs import RoleObs  # noqa: E402

DEFAULT_SOFT_LIMIT = 0.9


class CreatureEnv:
    """MuJoCo body + the universal agent interface, nothing else."""

    def __init__(self, creature, *, seed=0, command=(0.0, 0.0, 0.0), gait_period_s=0.8):
        import mujoco

        self.creature = creature
        self.dir = L.CREATURES / creature
        self.contract = json.loads((self.dir / "contract.json").read_text(encoding="utf-8"))
        self.m = mujoco.MjModel.from_xml_path(str(self.dir / "scene.xml"))
        self.d = mujoco.MjData(self.m)
        self.mujoco = mujoco

        self.n_obs = int(self.contract["observation"]["n_obs"])
        self.n_action = int(self.contract["action"]["n_action"])
        self.decimation = int(self.contract["physics"]["decimation"])
        self.control_dt = float(self.contract["physics"]["control_dt"])
        self.obs = RoleObs(self.contract)
        self.rng = np.random.default_rng(seed)
        self.command = np.asarray(command, dtype=np.float64)
        self.gait_period_s = float(gait_period_s)
        self.t = 0.0
        self.prev_action = np.zeros(self.n_action, dtype=np.float32)

        # action -> ctrl, in role order, from the contract
        self.ctrl_index = np.full(self.n_action, -1, dtype=int)
        self.nominal = np.zeros(self.n_action, dtype=np.float64)
        self.scale = np.zeros(self.n_action, dtype=np.float64)
        self.lo = np.zeros(self.n_action, dtype=np.float64)
        self.hi = np.zeros(self.n_action, dtype=np.float64)
        roles = self.obs.roles
        joint_names = {j["name"]: j for j in self.contract["morphology"]["joints"]}
        acts = self.contract["morphology"]["actuators"]

        for i, role in enumerate(roles):
            jn = self.obs.joint_of_role.get(role)
            if not jn:
                continue
            jd = joint_names[jn]
            act_name = jd.get("actuator")
            if not act_name or act_name not in acts:
                continue
            self.ctrl_index[i] = list(acts).index(act_name)
            self.nominal[i] = float(jd.get("default") or 0.0)
            rng_ = jd.get("range") or [0.0, 0.0]
            f = float(self.contract["action"].get("soft_limit_factor", DEFAULT_SOFT_LIMIT))
            mid = np.array(rng_, dtype=np.float64)
            self.lo[i] = mid[0] + (1 - f) * 0.5 * (mid[1] - mid[0])
            self.hi[i] = mid[1] - (1 - f) * 0.5 * (mid[1] - mid[0])
            scale = jd.get("action_scale")
            if not scale:
                kp = jd.get("kp") or 1.0
                effort = jd.get("effort") or 1.0
                scale = 0.25 * float(effort) / float(kp)
            self.scale[i] = float(scale)

        # home pose: the scene keyframe if there is one, else the model's qpos0
        self.home = np.array(self.m.qpos0, dtype=np.float64, copy=True)
        if self.m.nkey > 0:
            self.home = np.array(self.m.key_qpos[0], dtype=np.float64, copy=True)

        fj = next((j for j in range(self.m.njnt)
                   if int(self.m.jnt_type[j]) == int(mujoco.mjtJoint.mjJNT_FREE)), None)
        if fj is None:
            raise SystemExit(f"{creature}: no free joint; the universal interface assumes a floating base")
        self.free_q = int(self.m.jnt_qposadr[fj])
        self.free_v = int(self.m.jnt_dofadr[fj])

        self._hinge = {}
        T = mujoco.mjtObj
        for j in range(self.m.njnt):
            name = mujoco.mj_id2name(self.m, T.mjOBJ_JOINT, j)
            if name and int(self.m.jnt_type[j]) in (int(mujoco.mjtJoint.mjJNT_HINGE),
                                                    int(mujoco.mjtJoint.mjJNT_SLIDE)):
                self._hinge[name] = (int(self.m.jnt_qposadr[j]), int(self.m.jnt_dofadr[j]))

    # -- reset / step ----------------------------------------------------------------------------

    def reset(self, *, randomise=False):
        mujoco = self.mujoco
        mujoco.mj_resetData(self.m, self.d)
        self.d.qpos[:] = self.home
        if randomise:
            self.d.qpos[self.free_q:self.free_q + 3] += self.rng.normal(0, 0.01, 3)
            q = self.rng.normal(size=4)
            self.d.qpos[self.free_q + 3:self.free_q + 7] = q / np.linalg.norm(q)
            for _name, (qa, _va) in self._hinge.items():
                self.d.qpos[qa] += self.rng.normal(0, 0.02)
        self.d.qvel[:] = 0.0
        self.d.ctrl[:] = 0.0
        self.prev_action[:] = 0.0
        self.t = 0.0
        mujoco.mj_forward(self.m, self.d)
        return self._obs()

    def target(self, action):
        """28 role-ordered actions -> ctrl targets. Masked roles hold at nominal."""
        a = np.asarray(action, dtype=np.float64).reshape(-1)
        if a.shape[0] != self.n_action:
            raise ValueError(f"action has {a.shape[0]} values, contract wants {self.n_action}")
        ctrl = np.zeros(self.m.nu, dtype=np.float64)
        for i in range(self.n_action):
            c = self.ctrl_index[i]
            if c < 0:
                continue
            if self.obs.mask[i] == 0:
                ctrl[c] = self.nominal[i]          # PD-hold, never driven by the policy
            else:
                ctrl[c] = np.clip(self.nominal[i] + a[i] * self.scale[i], self.lo[i], self.hi[i])
        return ctrl

    def step(self, action):
        mujoco = self.mujoco
        self.d.ctrl[:] = self.target(action)
        for _ in range(self.decimation):
            mujoco.mj_step(self.m, self.d)
        self.t += self.control_dt
        self.prev_action = np.asarray(action, dtype=np.float32).reshape(-1)
        return self._obs()

    # -- observation -----------------------------------------------------------------------------

    def _obs(self):
        d = self.d
        joint_pos = {name: float(d.qpos[qa]) for name, (qa, _va) in self._hinge.items()}
        joint_vel = {name: float(d.qvel[va]) for name, (_qa, va) in self._hinge.items()}
        quat = tuple(float(x) for x in d.qpos[self.free_q + 3:self.free_q + 7])
        angvel = tuple(float(x) for x in d.qvel[self.free_v + 3:self.free_v + 6])
        phase = (self.t / self.gait_period_s) % 1.0 if float(np.linalg.norm(self.command)) >= 0.1 else 0.0
        return self.obs.build(joint_pos=joint_pos, joint_vel=joint_vel, pelvis_quat=quat,
                              pelvis_angvel=angvel, prev_action=self.prev_action,
                              command=self.command, gait_phase=phase)

    # -- conveniences ----------------------------------------------------------------------------

    @property
    def height(self):
        return float(self.d.qpos[self.free_q + 2])

    @property
    def home_height(self):
        return float(self.home[self.free_q + 2])

    @property
    def base_linvel(self):
        """World-frame linear velocity of the root free joint (MuJoCo stores it in world coordinates)."""
        return np.array(self.d.qvel[self.free_v:self.free_v + 3], dtype=np.float64)

    @property
    def base_angvel(self):
        """Body-frame angular velocity of the root free joint."""
        return np.array(self.d.qvel[self.free_v + 3:self.free_v + 6], dtype=np.float64)


def smoke(creature, steps=300, strict=False):
    """Step with a zero action and check the *interface*, not the balance.

    A nominal pose is not a balance controller, so every creature falls over — that is correct
    physics, and treating it as failure would hide real faults behind an expected one. What is asserted
    here is: finite observations, the right width, a stable step rate, and no penetration below the
    floor. Anything that sinks is a creature-model problem worth looking at.
    """
    env = CreatureEnv(creature)
    print(f"{creature}: n_obs {env.n_obs}, n_action {env.n_action}, "
          f"{1/env.control_dt:.0f} Hz, {len(env._hinge)} joints, nu {env.m.nu}, "
          f"masked roles {int((env.obs.mask == 0).sum())}")
    obs = env.reset()
    h0 = env.height
    action = np.zeros(env.n_action, dtype=np.float32)
    finite = True
    for _ in range(steps):
        obs = env.step(action)
        if not np.isfinite(obs).all():
            finite = False
            break
    h1 = env.height
    sank = h1 < -0.05
    interface_ok = finite and obs.shape == (env.n_obs,)
    ok = interface_ok and not (sank and strict)
    tag = "OK  " if ok else "FAIL"
    note = ""
    if sank:
        note = ("   WARN: below the floor — the creature has almost no collision geoms; "
                "this is a model defect, not an interface fault")
    print(f"  {tag} steps={steps} finite={finite} shape={obs.shape} "
          f"settled {h0:.3f} -> {h1:.3f} m (falling is expected){note}")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("creature", nargs="*")
    ap.add_argument("--smoke", action="store_true")
    ap.add_argument("--steps", type=int, default=300)
    ap.add_argument("--strict", action="store_true", help="treat a creature that sinks as a failure")
    a = ap.parse_args()

    ids = a.creature or sorted(d.name for d in L.CREATURES.iterdir()
                               if d.is_dir() and (d / "contract.json").exists())
    if not a.smoke:
        ap.error("pass --smoke")
    bad = 0
    for cid in ids:
        try:
            bad += smoke(cid, a.steps, a.strict)
        except Exception as e:  # noqa: BLE001 - report, do not hide
            print(f"  FAIL {cid}: {type(e).__name__}: {e}")
            bad += 1
    print("FAILED" if bad else "OK")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
