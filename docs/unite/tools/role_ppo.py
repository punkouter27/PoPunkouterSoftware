"""PPO on the universal interface -- the first trainer that does not care which creature it drives.

    python role_ppo.py --creature go2 --iters 60 --out runs/ppo-go2
    python role_ppo.py --creature unitree_r1_a5 --iters 60 --out runs/ppo-r1
    python role_ppo.py --smoke                    # 2 iterations on go2, exits 0, proves the loop

It trains one thing only: keep the body upright and follow a velocity command. That is the smallest
task that needs the whole interface -- legs, base velocity, gravity, commands, mask -- and it is the
same task for a humanoid and a quadruped, because `RoleEnv` hands out 28 actions and 131 observations
either way.

At the end it exports a plain MLP (no normalisation in the graph, R9), then runs the export guard
(`verify_policy.py --check --write`) so the result leaves with a sidecar that pins the three hashes.
The first time this runs, `agents/registry.json` gains its first `conforming` policy.

Requires numpy + mujoco + torch. `--smoke` needs the same.
"""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pocontract_lib as L  # noqa: E402
from role_env import CreatureEnv  # noqa: E402

GUARD = Path(__file__).resolve().parent / "verify_policy.py"


# ---------------------------------------------------------------- vectorised env

class VecCreature:
    """N copies of one creature, stepped together, with a shared command sampler."""

    def __init__(self, creature, n, *, seed=0, command_range=(0.6, 0.4, 0.4)):
        self.envs = [CreatureEnv(creature, seed=seed + i) for i in range(n)]
        self.n = n
        self.rng = np.random.default_rng(seed)
        self.cmd_range = np.asarray(command_range, dtype=np.float64)
        self.creature = creature
        self.n_obs = self.envs[0].n_obs
        self.n_action = self.envs[0].n_action
        self.cmd = np.zeros((n, 3))
        self.prev_action = np.zeros((n, self.n_action))

    def _sample_cmd(self):
        return self.rng.uniform(-self.cmd_range, self.cmd_range)

    def reset(self):
        obs = []
        for i, e in enumerate(self.envs):
            self.cmd[i] = self._sample_cmd()
            e.command = self.cmd[i]
            obs.append(e.reset())
        self.prev_action[:] = 0.0
        return np.stack(obs)

    def step(self, actions):
        obs, rew, done = [], [], []
        for i, e in enumerate(self.envs):
            obs.append(e.step(actions[i]))
            lin = e.base_linvel
            ang = e.base_angvel
            tilt = float(abs(e.obs.slice(obs[-1], "projected_gravity")[2]))

            track = np.exp(-((lin[0] - self.cmd[i, 0]) ** 2 + (lin[1] - self.cmd[i, 1]) ** 2) / 0.25)
            yaw = np.exp(-((ang[2] - self.cmd[i, 2]) ** 2) / 0.25)
            # alive bonus: without it, standing still scores the same as falling over early, and PPO
            # has no gradient toward "do not end the episode". This is what makes the first 10^5 steps
            # teach balance rather than nothing.
            r = 0.6 + 1.6 * track + 0.4 * yaw
            r -= 0.05 * float(np.sum(ang[:2] ** 2))
            r -= 0.02 * float(np.sum(np.square(actions[i])))
            r -= 0.01 * float(np.sum(np.square(actions[i] - self.prev_action[i])))
            # upright bonus: full when level, 0 at 90 deg of tilt. (The term used to be clip(tilt + 0.7, -1, 0),
            # which is always 0 because tilt >= 0 -- it never paid anything and is why standing had no gradient.)
            r += 0.5 * float(np.clip(tilt, 0.0, 1.0))

            fallen = e.height < 0.55 * e.home_height or tilt < 0.3
            if fallen:
                r -= 1.0
            rew.append(float(r))
            done.append(bool(fallen))
        self.prev_action = np.asarray(actions, dtype=np.float64).copy()

        obs = np.stack(obs)
        for i, d in enumerate(done):
            if d:
                self.cmd[i] = self._sample_cmd()
                self.envs[i].command = self.cmd[i]
                obs[i] = self.envs[i].reset()
        return obs, np.asarray(rew, dtype=np.float32), np.asarray(done, dtype=np.float32)


# ---------------------------------------------------------------- network

def build_net(n_obs, n_action, hidden=128, log_std=0.0):
    import torch
    import torch.nn as nn

    class ActorCritic(nn.Module):
        def __init__(self):
            super().__init__()
            self.body = nn.Sequential(nn.Linear(n_obs, hidden), nn.Tanh(),
                                      nn.Linear(hidden, hidden), nn.Tanh())
            self.mu = nn.Linear(hidden, n_action)
            self.v = nn.Linear(hidden, 1)
            self.log_std = nn.Parameter(torch.full((n_action,), float(log_std)))
            nn.init.orthogonal_(self.mu.weight, gain=0.01)
            nn.init.zeros_(self.mu.bias)

        def forward(self, x):
            h = self.body(x)
            return self.mu(h), self.v(h).squeeze(-1)

    return ActorCritic()


# ---------------------------------------------------------------- ppo

def train(creature, *, iters, steps, envs, epochs, minibatch, gamma=0.99, lam=0.95,
          clip=0.2, lr=3e-4, entropy=0.0, seed=0, out=None, log_every=10,
          command_range=(0.6, 0.4, 0.4), log_std=0.0):
    import torch

    torch.manual_seed(seed)
    vec = VecCreature(creature, envs, seed=seed, command_range=command_range)
    net = build_net(vec.n_obs, vec.n_action, log_std=log_std)
    opt = torch.optim.Adam(net.parameters(), lr=lr)

    obs = vec.reset()
    hist = []
    t0 = time.time()
    for it in range(1, iters + 1):
        batch = {k: [] for k in ("obs", "act", "logp", "rew", "done", "val")}
        for _ in range(steps):
            o = torch.as_tensor(obs, dtype=torch.float32)
            with torch.no_grad():
                mu, v = net(o)
                std = net.log_std.exp()
                dist = torch.distributions.Normal(mu, std)
                a = dist.sample()
                logp = dist.log_prob(a).sum(-1)
            a_np = a.numpy()
            nobs, rew, done = vec.step(a_np)
            batch["obs"].append(obs)
            batch["act"].append(a_np)
            batch["logp"].append(logp.numpy())
            batch["rew"].append(rew)
            batch["done"].append(done)
            batch["val"].append(v.numpy())
            obs = nobs

        with torch.no_grad():
            _, last_v = net(torch.as_tensor(obs, dtype=torch.float32))
        OBS = torch.as_tensor(np.concatenate(batch["obs"]), dtype=torch.float32)
        A = torch.as_tensor(np.concatenate(batch["act"]), dtype=torch.float32)
        LP = torch.as_tensor(np.concatenate(batch["logp"]), dtype=torch.float32)
        R = torch.as_tensor(np.concatenate(batch["rew"]), dtype=torch.float32).reshape(steps, envs)
        D = torch.as_tensor(np.concatenate(batch["done"]), dtype=torch.float32).reshape(steps, envs)
        V = torch.as_tensor(np.concatenate(batch["val"]), dtype=torch.float32).reshape(steps, envs)

        adv = torch.zeros_like(R)
        last = last_v.detach()
        for t in reversed(range(steps)):
            nv = last if t == steps - 1 else V[t + 1]
            nonterm = 1.0 - D[t]
            delta = R[t] + gamma * nv * nonterm - V[t]
            adv[t] = delta + gamma * lam * nonterm * (adv[t + 1] if t + 1 < steps else 0.0)
        ret = adv + V
        adv = adv.reshape(-1)
        adv = (adv - adv.mean()) / (adv.std() + 1e-8)
        ret = ret.reshape(-1)

        n = steps * envs
        idx = torch.arange(n)
        for _ in range(epochs):
            perm = idx[torch.randperm(n)]
            for s in range(0, n, minibatch):
                mb = perm[s:s + minibatch]
                mu, v = net(OBS[mb])
                dist = torch.distributions.Normal(mu, net.log_std.exp())
                logp = dist.log_prob(A[mb]).sum(-1)
                ratio = (logp - LP[mb]).exp()
                l1 = ratio * adv[mb]
                l2 = torch.clamp(ratio, 1 - clip, 1 + clip) * adv[mb]
                policy_loss = -torch.min(l1, l2).mean()
                value_loss = ((v - ret[mb]) ** 2).mean()
                ent = dist.entropy().sum(-1).mean()
                loss = policy_loss + 0.5 * value_loss - entropy * ent
                opt.zero_grad()
                loss.backward()
                torch.nn.utils.clip_grad_norm_(net.parameters(), 0.5)
                opt.step()

        mean_r = float(R.mean())
        hist.append(mean_r)
        if it % log_every == 0 or it == 1:
            print(f"  iter {it:3d}/{iters}  rew {mean_r:7.3f}  ({time.time() - t0:5.1f}s)")

    return net, vec, hist


# ---------------------------------------------------------------- export

def export(net, creature, out_dir, meta):
    import torch

    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    onnx_path = out_dir / "policy.onnx"

    class Wrapper(torch.nn.Module):
        def __init__(self, n):
            super().__init__()
            self.net = n

        def forward(self, x):
            mu, _ = self.net(x)
            return torch.tanh(mu)

    net.eval()
    torch.onnx.export(
        Wrapper(net).cpu(),
        torch.zeros(1, meta["n_obs"]),
        str(onnx_path),
        input_names=["obs"], output_names=["actions"],
        dynamic_axes={"obs": {0: "batch"}, "actions": {0: "batch"}},
        opset_version=17, dynamo=False,
    )

    r = subprocess.run([sys.executable, str(GUARD), str(onnx_path), "--creature", creature,
                        "--check", "--write"], capture_output=True, text=True)
    status = "conforming" if r.returncode == 0 else "rejected"
    (out_dir / "guard.log").write_text(r.stdout + r.stderr, encoding="utf-8")
    (out_dir / "report.json").write_text(json.dumps({**meta, "guard_status": status}, indent=2) + "\n",
                                         encoding="utf-8")
    tail = [ln for ln in r.stdout.strip().splitlines() if ln.strip()][-1:] or ["(no output)"]
    print(f"  export: {onnx_path}  guard={status}  {tail[0]}")
    return onnx_path, status


# ---------------------------------------------------------------- cli

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--creature", default="go2")
    ap.add_argument("--iters", type=int, default=60)
    ap.add_argument("--steps", type=int, default=64)
    ap.add_argument("--envs", type=int, default=8)
    ap.add_argument("--epochs", type=int, default=4)
    ap.add_argument("--minibatch", type=int, default=256)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--entropy", type=float, default=0.0)
    ap.add_argument("--log-std", type=float, default=0.0, help="initial action-noise log std; -1 halves it")
    ap.add_argument("--command-range", default="0.6,0.4,0.4",
                    help="vx,vy,wz sampled uniformly in +/- this; 0,0,0 trains balance only")
    ap.add_argument("--log-every", type=int, default=10)
    ap.add_argument("--out", type=Path)
    ap.add_argument("--smoke", action="store_true", help="2 tiny iterations, then export")
    a = ap.parse_args()

    if a.smoke:
        a.iters, a.steps, a.envs = 2, 16, 2
        a.out = a.out or Path(__file__).resolve().parent / "_smoke"
        a.creature = a.creature or "go2"

    command_range = tuple(float(x) for x in a.command_range.split(","))
    if len(command_range) != 3:
        ap.error("--command-range wants three comma-separated numbers: vx,vy,wz")

    out = a.out or (L.UNITE / "runs" / f"ppo-{a.creature}-{time.strftime('%Y%m%d-%H%M%S')}")
    print(f"PPO on {a.creature}: {a.iters} iters x {a.steps} steps x {a.envs} envs "
          f"= {a.iters * a.steps * a.envs} policy steps")
    net, vec, hist = train(a.creature, iters=a.iters, steps=a.steps, envs=a.envs,
                           epochs=a.epochs, minibatch=a.minibatch, seed=a.seed, out=out,
                           log_every=a.log_every, lr=a.lr, entropy=a.entropy,
                           command_range=command_range, log_std=a.log_std)

    _, status = export(net, a.creature, out, {
        "creature": a.creature,
        "n_obs": vec.n_obs,
        "n_action": vec.n_action,
        "policy_steps": a.iters * a.steps * a.envs,
        "iters": a.iters,
        "seed": a.seed,
        "mean_reward_first": hist[0] if hist else None,
        "mean_reward_last": hist[-1] if hist else None,
        "interface_sha256_16": json.loads(
            (L.CREATURES / a.creature / "contract.json").read_text(encoding="utf-8")
        )["hashes"]["interface_sha256_16"],
    })
    print(f"done: {out}")
    return 0 if status == "conforming" else 1


if __name__ == "__main__":
    sys.exit(main())
