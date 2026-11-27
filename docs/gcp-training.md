# Training on a Google Cloud GPU (Spot L4)

`scripts/gcp/` runs the MuJoCo Warp trainings of the sibling Po repos on a rented NVIDIA L4
instead of this PC. The repos keep training exactly the way they do locally — the cloud part is
only *ship the tree, run the repo's own command, stream the checkpoints out*.

    python scripts/gcp/gcp_queue.py quota      # is there a GPU to rent?
    python scripts/gcp/gcp_queue.py up         # create/start the Spot VM, bootstrap it
    python scripts/gcp/gcp_queue.py run --job bball_queue --hours 6 --wait --pull --into-repo
    python scripts/gcp/gcp_queue.py down       # stop paying for compute

Every command accepts `--dry-run`, which prints the `gcloud`/`tar`/`ssh` commands it would run
and touches nothing — no login required. That is also how the tests exercise the flow.

## 1. The pieces

| File | Role |
| --- | --- |
| `gcp_queue.py` | Local orchestrator: quota, VM lifecycle, packing, upload, launch, wait, logs, pull. |
| `cloud_jobs.json` | One entry per trainable job: repo, source tree, setup command, trainer command, artifacts, excludes. |
| `vm/bootstrap.sh` | Idempotent VM prep: waits for the NVIDIA driver, adds headless EGL/GL libraries, installs `uv`, exports `MUJOCO_GL=egl`. |
| `vm/run_job.sh` | Per-job runner on the VM: setup → trainer → rsync artifacts to GCS every 5 min → `SUMMARY.txt` → stop the VM. |
| `test_gcp_queue.py` | Offline tests (catalog sanity, tar excludes, job env, dry run). No network. |

## 2. One-time setup (a human has to do these)

```powershell
# 1. Cloud SDK (already installed here: %LOCALAPPDATA%\Google\Cloud SDK\google-cloud-sdk\bin)
winget install --id Google.CloudSDK -e

# 2. Sign in and pick the project (opens a browser)
gcloud auth login
gcloud config set project poredomedia-publishing

# 3. Make sure billing is on, then set a budget at 80% / 95% of the remaining credits
#    Console > Billing > Budgets & alerts
```

**GPU quota.** A new project has `NVIDIA_L4_GPUS = 0` in every region, and Compute Engine
refuses the VM until that is raised. `gcp_queue.py quota` prints the current limits; if it says 0,
open *IAM & Admin → Quotas & System Limits*, filter metric `NVIDIA L4 GPUs`, select the `us-east4`
row, *Edit Quota*, request **1**, reason "PyTorch and MuJoCo Warp reinforcement learning research".
Approval is usually minutes, sometimes a day. `doctor` tells you when it is in.

**What the credits do and do not cover.** Compute Engine Spot VMs with GPUs and Cloud Storage are
covered by an upgraded trial's credits. Managed model endpoints (Gemini API, Vertex AI
Model-as-a-Service) are not — this pipeline never calls them.

## 3. Repo → cloud map

Each row is a `cloud_jobs.json` entry. The command is the trainer the repo already ships; the
cloud only supplies a Linux box with a GPU and a time budget.

| Job | Repo | Trainer that runs there | Writes back |
| --- | --- | --- | --- |
| `bball_queue` | `PoBasketball` (`sim/`) | `uv run python -m streetball.queue --hours N` — the same curriculum queue as locally: ladder stages, gates, ONNX export | `sim/runs`, `docs` |
| `box_queue` | `PoBox` (`Training/`) | `uv run python tools/queue_runner.py` — works through `Training/logs/queue.json` | `Training/checkpoints`, `Training/logs`, `Training/models`, `Training/queues` |
| `slap_queue` | `PoSlapUnreal` (`training/`) | `.venv/bin/python overnight.py --resume --hours N --queue retrain_v5_queue.json` | `training/runs`, `training/models`, `training/configs` |

Environment details the pipeline relies on (and that are easy to get wrong on Linux):

- `PoBasketball/sim` pins Python 3.12 and `torch==2.14.1+cu130` from an explicit index, so the
  VM's driver must be **≥ 580**. `bootstrap.sh` prints a warning if it is older. MuJoCo Warp's
  wheels are `py3-none-any`, so nothing is compiled for the platform.
- `PoBox` puts MuJoCo Warp, torch and onnx in the `train` dependency group:
  `uv sync --group train`. The PyPI Linux torch wheel is already the CUDA build, so the manual
  `uv pip install torch` step the Windows notes mention is not needed. The folder is
  `Training` with a capital T; Linux cares.
- `PoSlapUnreal` has no `pyproject.toml` for training: `requirements.txt` plus a CUDA torch.
  `--torch-backend=auto` picks the wheel that matches the VM driver. `overnight.py` resolves
  `--queue` against `training/runs/`, so that queue file travels with the tree.

Adding a repo is one JSON entry: `source`, `cwd`, `setup`, `command`, `artifacts`, `excludes`.
`PoRobotTrain`'s level-2 trainer is deliberately absent: it is a Docker image (`scripts/level2/Dockerfile`,
mjlab + unitree_rl_mjlab), so it wants `gcloud run jobs`/Artifact Registry rather than a bare VM.

## 4. What leaves this PC

`tar` is run over the repo folder with the exclude list from `cloud_jobs.json`, so Unity's
`Library/`, `Temp/`, `Build/`, `.venv/`, `.git/`, old runs and large media never travel:

| Job | Source tree on disk | Tarball |
| --- | --- | --- |
| `bball_queue` | 13.2 GB | 25 MB |
| `box_queue` | 11.0 GB | 113 MB |
| `slap_queue` | 11.6 GB | 238 MB |

(The big drop is Unity's `Library`/`Temp`/`Build`, the 3.5 GB `.venv`s, `BLENDER` source art in
`PoBox` and the older `runs/` checkpoints. `slap_queue` keeps `training/runs/` because its
`--resume` queue rebuilds each athlete from the stages already there.)

Plain names (`Library`, `.git`, `__pycache__`) are pruned at any depth; globs (`*.mp4`,
`Training/checkpoints/v4*`) are passed through as-is. Add `--exclude <pattern>` to a run to drop
something one-off, or edit the job's `excludes`. The staging tarball lands in
`scripts/gcp/runs/<run-id>/` (git-ignored) and is deleted by the next run of the same job.

Everything a cloud run produces also lives in Cloud Storage, under
`gs://<project>-po-rl-runs/jobs/<run-id>/`: one folder per artifact directory, plus `job.env`,
`runner.log`, `progress.log` and `SUMMARY.txt`.

## 5. What happens during a run

1. `run` packs the tree, uploads it with `gcloud compute scp`, extracts it to
   `/opt/po/jobs/<run-id>/` and starts `run_job.sh` detached (`nohup`), so closing the laptop
   does not kill training.
2. On the VM: `SETUP` (`uv sync …`), then the trainer in its own session, output to `train.log`.
3. Every `SYNC_SECONDS` (default 300) a background loop runs `gcloud storage rsync` for each
   artifact directory. A Spot preemption therefore costs at most five minutes of training.
4. The time budget is `--hours`; `run_job.sh` sends `SIGINT` at the deadline, `SIGTERM` three
   minutes later, then `SIGKILL`. The budget is `hours + timeout_grace_minutes` from the job, so a
   queue that stops itself cleanly is never cut short.
5. To stop early, use `python scripts/gcp/gcp_queue.py stop --run-id <run>`: it drops a `STOP`
   marker object in the run's bucket folder, the sync loop sees it within one interval and sends
   the trainer `SIGINT`.
6. At the end `SUMMARY.txt` (exit code, duration, GPU, last 40 progress lines) is uploaded, and —
   because `SELF_STOP=1` on the last job — the VM shuts itself down. `--max-run-duration=8h` is a
   second, independent stop if the script itself dies.

## 6. Getting results back

```powershell
python scripts/gcp/gcp_queue.py pull --job bball_queue --run-id <run> --into-repo
```

`--into-repo` rsyncs each artifact directory straight into the sibling repo (`PoBasketball/sim/runs`,
…), which is additive: local runs survive, cloud runs appear beside them. Without it everything
goes to `scripts/gcp/runs/<run>/artifacts/`. Either way the run stays in the bucket, so the same
`run-id` can be pulled again on another machine.

To continue a cloud run locally, pull it and run the repo's normal command — e.g.
`cd PoBasketball/sim; uv run python -m streetball.queue --hours 2`. To continue a local run in the
cloud, add `--exclude sim/runs` to keep the tree you already trained (the job ships a fresh tree by
default) or copy the run directory in afterwards.

## 7. Money

- `g2-standard-8` (1× L4, 8 vCPU, 32 GB) on Spot is roughly **$0.20–0.35 per hour**; an 8-hour
  overnight queue is about **$2**.
- The 150 GB boot disk is about **$0.50 per day** while the instance exists, running or stopped
  (pd-balanced ≈ $0.10/GiB-month). For a long pause, delete instead of stop:
  `gcloud compute instances delete po-rl-runner --zone us-east4-a` — the next `up` recreates it
  and re-runs `bootstrap.sh` (about ten minutes, the uv/warp caches are rebuilt).
- Storage of a few GB of checkpoints is cents per month. Delete old runs with
  `gcloud storage rm -r gs://<bucket>/jobs/<run-id>`.
- `--max-run-duration` and `SELF_STOP` are the two fences that stop a forgotten VM; the billing
  budget alert is the third.

## 8. Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `quota is 0` / create fails with `QUOTA_EXCEEDED` | Request `NVIDIA L4 GPUs` = 1 (section 2). `gcp_queue.py quota` shows the row. |
| `no zone had Spot L4 capacity` | Spot L4 is often out of stock in one zone; `--zone us-east4-c` (or edit `defaults.zones`). On-demand (`--provisioning-model=STANDARD`) is the fallback, at ~3× the price. |
| `ssh` times out right after create | Normal for ~40 s while the guest agent starts; `up` retries for ten minutes. |
| `NO nvidia-smi` | The image family had no driver agent. `up` only uses Deep Learning VM families with `install-nvidia-driver=True`; check `gcloud compute images describe-from-family`. |
| torch reports `no kernel image` / `CUDA driver version is insufficient` | `torch cu130` needs driver ≥ 580; the `common-cu130`/`common-cu129` families install 580+. |
| `setup failed` in `SUMMARY.txt` | Read `runner.log` (`gcp_queue.py logs --run-id <run> --follow`). Most often a pinned version that does not exist for Linux, or a `uv` download hiccup. |
| `working directory ... missing` | `cwd` in `cloud_jobs.json` disagrees with the tree, usually case (`Training` ≠ `training`). |
| MuJoCo rendering error about EGL/GLFW | `MUJOCO_GL=egl` and the EGL libraries come from `bootstrap.sh`; a re-used VM from another project may need `up` again. |
| VM stopped itself mid-queue | The time budget ran out, or Spot was reclaimed. `SUMMARY.txt` says which; pull and resume. |

## 9. Verifying without spending anything

```powershell
python -m unittest discover -s scripts/gcp -p "test_*.py"      # 9 tests, offline
uvx ruff check scripts/gcp
python scripts/gcp/gcp_queue.py --dry-run --project demo-project run --job bball_queue
```

The tar test builds a fake Unity tree and proves `Library/`, nested `.git/`, `*.mp4` and
`Training/checkpoints/v4*` really are dropped from the tarball. `--dry-run` prints the complete
`gcloud` sequence (create, scp, ssh launch) for review before anything is billed.
