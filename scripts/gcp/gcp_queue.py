#!/usr/bin/env python3
"""Run the Po MuJoCo Warp trainings on a Google Cloud Spot L4 VM.

The Po games train locally on the Windows GPU through each repo's own queue runner
(`PoBasketball/sim/streetball/queue.py`, `PoBox/Training/tools/queue_runner.py`,
`PoSlapUnreal/training/overnight.py`). This is the same idea on a rented GPU: one Spot
g2-standard-8 (1x NVIDIA L4, 24 GB) is created once, a repo's source tree is shipped to
it, the repo's own trainer command runs there unchanged, and every artifact directory is
rsynced to Cloud Storage while it trains. A Spot reclaim, the max-run-duration stop or a
killed ssh session therefore costs at most SYNC_SECONDS of work.

    python scripts/gcp/gcp_queue.py doctor
    python scripts/gcp/gcp_queue.py quota
    python scripts/gcp/gcp_queue.py up
    python scripts/gcp/gcp_queue.py run --job bball_queue --hours 6 --wait
    python scripts/gcp/gcp_queue.py pull --job bball_queue --run-id <run> --into-repo
    python scripts/gcp/gcp_queue.py stop --run-id <run>
    python scripts/gcp/gcp_queue.py down

Every command takes --dry-run, which prints the gcloud/tar/ssh argv it would use and
touches nothing (no project or login is needed for it).

What "link" means (see cloud_jobs.json): each job names the repo, the source tree to
ship, the working directory, the setup command and the trainer command the repo already
uses locally. Nothing in the sibling repos is modified by shipping them; the only
repo-side requirement is the one the trainer already has locally (uv sync / a .venv).
The runbook is docs/gcp-training.md.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import shlex
import shutil
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[2]  # the folder holding the sibling Po repos
JOBS_FILE = HERE / "cloud_jobs.json"
VM_DIR = HERE / "vm"
RUNS_DIR = HERE / "runs"

DRY_RUN = False
_GCLOUD: str | None = None
_QUIET = 0


class Failed(RuntimeError):
    """A gcloud/tar/ssh step the user has to see; main() turns it into exit code 2."""


# --------------------------------------------------------------------------- process


def gcloud_exe() -> str:
    global _GCLOUD
    if _GCLOUD:
        return _GCLOUD
    for name in ("gcloud", "gcloud.cmd", "gcloud.exe"):
        found = shutil.which(name)
        if found:
            _GCLOUD = found
            return found
    local = pathlib.Path(os.environ.get("LOCALAPPDATA", "")) / "Google/Cloud SDK/google-cloud-sdk/bin/gcloud.cmd"
    _GCLOUD = str(local)
    return _GCLOUD


def emit(argv) -> None:
    if _QUIET:
        return
    print("  $ " + " ".join(shlex.quote(str(a)) for a in argv), flush=True)


def call(argv, check=True, capture=True, input_text=None):
    argv = [str(a) for a in argv]
    if DRY_RUN:
        emit(argv)
        return ""
    proc = subprocess.run(
        argv,
        capture_output=capture,
        text=True,
        encoding="utf-8",
        errors="replace",
        input=input_text,
    )
    if check and proc.returncode != 0:
        detail = ((proc.stderr or "") + (proc.stdout or "")).strip() if capture else ""
        raise Failed("command failed (" + str(proc.returncode) + "): " + " ".join(argv) + "\n" + detail)
    return (proc.stdout or "") if capture else ""


def gcloud(*args, **kwargs):
    return call([gcloud_exe(), *args], **kwargs)


def gcloud_value(*args, default=""):
    """A quiet probe: no dry-run echo, no exception."""
    global _QUIET
    _QUIET += 1
    try:
        return gcloud(*args).strip() or default
    except Failed:
        return default
    finally:
        _QUIET -= 1


def fmt_hours(hours) -> str:
    number = float(hours)
    return str(int(number)) if number == int(number) else str(number)


def sh_quote(value: str) -> str:
    return "'" + str(value).replace("'", "'\\''") + "'"


# --------------------------------------------------------------------------- config


class Settings:
    def __init__(self, args, cfg):
        defaults = cfg["defaults"]
        self.cfg = cfg
        self.project = (
            args.project or os.environ.get("PO_GCP_PROJECT") or gcloud_value("config", "get-value", "project")
        )
        if not self.project or self.project == "(unset)":
            self.project = "<project>" if DRY_RUN else ""
        if not self.project:
            raise Failed("no project: run `gcloud config set project <id>`, or pass --project / set PO_GCP_PROJECT")
        self.instance = args.instance or os.environ.get("PO_GCP_INSTANCE") or defaults["instance"]
        zone = args.zone or os.environ.get("PO_GCP_ZONE") or ""
        self.zone = zone
        self.zones = [zone] if zone else list(defaults["zones"])
        self.machine_type = args.machine_type or os.environ.get("PO_GCP_MACHINE") or defaults["machine_type"]
        self.bucket_name = (
            args.bucket or os.environ.get("PO_GCP_BUCKET") or f"{self.project}-{defaults['bucket_suffix']}"
        )
        self.bucket = "gs://" + self.bucket_name
        self.region = defaults["bucket_location"]
        self.sync_seconds = int(getattr(args, "sync_seconds", None) or defaults["sync_seconds"])
        self.max_run_hours = defaults.get("max_run_duration_hours", 8)
        self.image_project = defaults["image_project"]
        self.boot_disk_size = defaults["boot_disk_size"]
        self.boot_disk_type = defaults["boot_disk_type"]

    def job(self, job_id):
        for job in self.cfg["jobs"]:
            if job["id"] == job_id:
                return job
        known = ", ".join(j["id"] for j in self.cfg["jobs"])
        raise Failed(f"unknown job {job_id!r} (known: {known})")

    def gcs_job_uri(self, run_id: str) -> str:
        return f"{self.bucket}/jobs/{run_id}"


def load_jobs():
    return json.loads(JOBS_FILE.read_text(encoding="utf-8"))


# --------------------------------------------------------------------------- excludes


def exclude_patterns(cfg, job) -> list[str]:
    """tar --exclude patterns for one job.

    A glob (`*.mp4`, `checkpoints/v4*`) is passed as written, which bsdtar applies at any
    depth because `*` also matches `/`. A plain name (Library, .git, __pycache__) is given
    both as `<source>/<name>` and as `*/<name>`, so the same folder is pruned wherever it
    sits inside the project. Verified against Windows bsdtar (see test_gcp_queue.py).
    """
    source = job["source"].strip("/")
    patterns, seen = [], set()
    for raw in [*cfg.get("common_excludes", []), *job.get("excludes", [])]:
        entry = str(raw).strip().strip("/")
        if not entry:
            continue
        if any(char in entry for char in "*?["):
            candidates = [entry, f"{source}/{entry}"]
        else:
            candidates = [f"{source}/{entry}", f"*/{entry}"]
        for candidate in candidates:
            if candidate not in seen:
                seen.add(candidate)
                patterns.append(candidate)
    return patterns


def tar_argv(job, tarball, excludes):
    argv = ["tar", "-czf", str(tarball)]
    argv += [f"--exclude={pattern}" for pattern in excludes]
    argv += ["-C", str(ROOT), job["source"]]
    return argv


def pack(s, job, run_id, extra_excludes=()):
    work = RUNS_DIR / run_id
    if not DRY_RUN:
        work.mkdir(parents=True, exist_ok=True)
    tarball = work / f"{job['id']}.tar.gz"
    if tarball.exists() and not DRY_RUN:
        tarball.unlink()
    print(f"[pack] {job['source']} -> {tarball}", flush=True)
    call(tar_argv(job, tarball, exclude_patterns(s.cfg, job) + list(extra_excludes)), capture=False)
    if not DRY_RUN:
        print(f"[pack] {tarball.stat().st_size / 1e6:.1f} MB", flush=True)
    return tarball


# --------------------------------------------------------------------------- job env


def job_env(s, job, hours, run_id, self_stop) -> dict[str, str]:
    grace = int(job.get("timeout_grace_minutes", 20))
    return {
        "JOB_ID": job["id"],
        "RUN_ID": run_id,
        "REPO": job["repo"],
        "CWD": job["cwd"],
        "SETUP": job["setup"],
        "COMMAND": job["command"].replace("{hours}", fmt_hours(hours)),
        "ARTIFACT_DIRS": " ".join(job.get("artifacts", [])),
        "BUCKET_URI": s.gcs_job_uri(run_id),
        "HOURS": fmt_hours(hours),
        "TIMEOUT_SECONDS": str(int(float(hours) * 3600) + grace * 60),
        "SYNC_SECONDS": str(s.sync_seconds),
        "SELF_STOP": "1" if self_stop else "0",
        "STOP_FILE": job.get("stop_file", ""),
    }


def env_file_text(env) -> str:
    return "".join(f"{key}={sh_quote(value)}\n" for key, value in env.items())


# --------------------------------------------------------------------------- gcloud bits


def instance_status(s):
    if s.zone:
        out = gcloud(
            "compute",
            "instances",
            "describe",
            s.instance,
            "--zone",
            s.zone,
            "--project",
            s.project,
            "--format=value(status)",
            check=False,
        )
        if out.strip():
            return s.zone, out.strip()
    out = gcloud(
        "compute",
        "instances",
        "list",
        f"--filter=name={s.instance}",
        "--project",
        s.project,
        "--format=value(zone.basename(),status)",
        check=False,
    )
    rows = [line.split() for line in out.splitlines() if line.strip()]
    if not rows or len(rows[0]) != 2:
        return None, "MISSING"
    s.zone = rows[0][0]
    return rows[0][0], rows[0][1]


def resolve_image_family(s):
    for family in s.cfg["defaults"]["image_families"]:
        if DRY_RUN:
            return family
        out = gcloud(
            "compute",
            "images",
            "describe-from-family",
            family,
            "--project",
            s.image_project,
            "--format=value(name)",
            check=False,
        )
        if out.strip():
            print(f"[vm] image family {family} ({out.strip()})", flush=True)
            return family
    raise Failed(f"none of {s.cfg['defaults']['image_families']} exist in {s.image_project}")


def create_args(s, zone, family):
    return [
        "compute",
        "instances",
        "create",
        s.instance,
        "--project",
        s.project,
        "--zone",
        zone,
        "--machine-type",
        s.machine_type,
        "--provisioning-model=SPOT",
        "--instance-termination-action=STOP",
        f"--max-run-duration={s.max_run_hours}h",
        "--maintenance-policy=TERMINATE",
        "--image-family",
        family,
        "--image-project",
        s.image_project,
        "--metadata=install-nvidia-driver=True",
        "--boot-disk-size",
        s.boot_disk_size,
        "--boot-disk-type",
        s.boot_disk_type,
        "--scopes=cloud-platform",
        "--labels=app=po-rl",
        "--quiet",
    ]


def ssh(s, command, check=True, capture=True):
    return gcloud(
        "compute",
        "ssh",
        s.instance,
        "--zone",
        s.zone or s.zones[0],
        "--project",
        s.project,
        "--quiet",
        "--strict-host-key-checking=no",
        "--command",
        command,
        check=check,
        capture=capture,
    )


def wait_ssh(s, attempts=60, delay=10):
    if DRY_RUN:
        print(f"[vm] (dry run) would wait for ssh on {s.instance}", flush=True)
        return
    print(f"[vm] waiting for ssh on {s.instance} ({s.zone})", flush=True)
    for _ in range(attempts):
        if "po-ready" in ssh(s, "echo po-ready", check=False):
            print("[vm] ssh ready", flush=True)
            return
        time.sleep(delay)
    raise Failed(f"ssh to {s.instance} did not come up")


def ensure_vm(s, wait=True):
    zone, status = instance_status(s)
    if status in ("RUNNING", "PROVISIONING", "STAGING"):
        print(f"[vm] {s.instance} is {status} in {zone}", flush=True)
        if status != "RUNNING" and wait:
            wait_ssh(s)
        return
    if status in ("TERMINATED", "STOPPED", "SUSPENDED"):
        print(f"[vm] starting {s.instance} in {zone}", flush=True)
        gcloud("compute", "instances", "start", s.instance, "--zone", s.zone, "--project", s.project)
        if wait:
            wait_ssh(s)
        return

    family = resolve_image_family(s)
    errors = []
    for zone in s.zones:
        s.zone = zone
        print(f"[vm] creating {s.instance} ({s.machine_type}, Spot L4) in {zone}", flush=True)
        try:
            gcloud(*create_args(s, zone, family), capture=False)
        except Failed as exc:
            errors.append(f"{zone}: {str(exc).splitlines()[-1]}")
            print(f"[vm] could not create in {zone}, trying the next zone", flush=True)
            continue
        if wait:
            wait_ssh(s)
        return
    raise Failed(
        "no zone had Spot L4 capacity ("
        + "; ".join(errors)
        + "). Try another region with --zone, or request quota with `quota`."
    )


def ensure_bucket(s):
    if not DRY_RUN and gcloud("storage", "buckets", "describe", s.bucket, check=False).strip():
        return
    print(f"[gcs] creating {s.bucket} in {s.region}", flush=True)
    gcloud(
        "storage",
        "buckets",
        "create",
        s.bucket,
        "--project",
        s.project,
        "--location",
        s.region,
        "--uniform-bucket-level-access",
        "--public-access-prevention",
        check=False,
    )
    number = gcloud_value("projects", "describe", s.project, "--format=value(projectNumber)")
    if number:
        member = f"serviceAccount:{number}-compute@developer.gserviceaccount.com"
        gcloud(
            "storage",
            "buckets",
            "add-iam-policy-binding",
            s.bucket,
            f"--member={member}",
            "--role=roles/storage.objectAdmin",
            check=False,
            capture=False,
        )


def prepare_vm(s):
    for name, remote in (("bootstrap.sh", "/tmp/po-bootstrap.sh"), ("run_job.sh", "/tmp/po-run-job.sh")):
        gcloud(
            "compute",
            "scp",
            str(VM_DIR / name),
            f"{s.instance}:{remote}",
            "--zone",
            s.zone,
            "--project",
            s.project,
            "--quiet",
        )
    print("[vm] bootstrap (driver check, EGL, uv)", flush=True)
    ssh(s, "bash /tmp/po-bootstrap.sh", capture=False)


# --------------------------------------------------------------------------- commands


def cmd_doctor(s, args):
    print(f"gcloud  : {gcloud_exe()}")
    if not DRY_RUN:
        version = gcloud_value("--version").splitlines()
        print(f"version : {version[0] if version else 'not runnable'}")
    accounts = [a for a in gcloud_value("auth", "list", "--format=value(account)").splitlines() if a]
    print(f"account : {', '.join(accounts) if accounts else 'NONE - run: gcloud auth login'}")
    print(f"project : {s.project}")
    print(f"instance: {s.instance}")
    print(f"bucket  : {s.bucket}")
    zone, status = instance_status(s)
    print(f"vm      : {status} in {zone or '-'}")
    try:
        cmd_quota(s, args)
    except Failed as exc:
        print(f"quota   : unknown ({exc})")
    print("\njobs:")
    for job in s.cfg["jobs"]:
        mark = "ok     " if (ROOT / job["source"]).is_dir() else "MISSING"
        print(f"  {job['id']:<12} {job['repo']:<14} {mark} ({job['cwd']})")
    print(f"\nlocal tools: python {sys.version.split()[0]}, tar {shutil.which('tar') or 'MISSING'}")
    return 0


def cmd_quota(s, args):
    regions = getattr(args, "region", None) or [s.region]
    rows = []
    for region in regions:
        out = gcloud(
            "compute",
            "regions",
            "describe",
            region,
            "--project",
            s.project,
            "--format=json(quotas)",
            check=False,
        )
        if not out.strip():
            print(f"{region}: could not read quotas")
            continue
        try:
            quotas = json.loads(out).get("quotas", [])
        except ValueError:
            print(f"{region}: unreadable quota json")
            continue
        wanted = ("NVIDIA_L4_GPUS", "GPUS_ALL_REGIONS", "PREEMPTIBLE_NVIDIA_L4_GPUS", "CPUS")
        for metric in wanted:
            for quota in quotas:
                if quota.get("metric") == metric:
                    rows.append((region, metric, quota.get("limit", 0), quota.get("usage", 0)))
    if not rows:
        print("no L4 rows; IAM & Admin > Quotas & System Limits, filter metric `NVIDIA L4 GPUs`")
        return 1
    print(f"{'region':<12} {'metric':<30} {'limit':>8} {'usage':>8}")
    for region, metric, limit, usage in rows:
        print(f"{region:<12} {metric:<30} {limit:>8} {usage:>8}")
    blocked = [region for region, metric, limit, _ in rows if metric == "NVIDIA_L4_GPUS" and float(limit) < 1]
    if blocked:
        print(
            f"\n{', '.join(blocked)}: NVIDIA L4 GPUs limit is 0 - select the row in IAM & Admin > Quotas & System"
            " Limits, Edit Quota, request 1, reason 'PyTorch and MuJoCo Warp reinforcement learning research'."
        )
        return 1
    return 0


def cmd_up(s, args):
    ensure_bucket(s)
    ensure_vm(s)
    prepare_vm(s)
    print("\nVM ready. Next: run --job <id> --wait, or down when finished.")
    return 0


def cmd_down(s, args):
    zone, status = instance_status(s)
    if status == "MISSING":
        print(f"no instance {s.instance}")
        return 0
    gcloud("compute", "instances", "stop", s.instance, "--zone", s.zone, "--project", s.project, "--quiet")
    print(f"stopped {s.instance} in {s.zone} (boot disk kept)")
    return 0


def cmd_status(s, args):
    zone, status = instance_status(s)
    print(f"vm: {status}  {zone or '-'}")
    out = gcloud("storage", "ls", "--recursive", f"{s.bucket}/jobs/**/SUMMARY.txt", check=False)
    jobs = [line.strip() for line in out.splitlines() if line.strip()]
    if not jobs:
        print(f"no finished cloud jobs in {s.bucket}")
        return 0
    print("finished jobs:")
    for uri in jobs:
        print("  " + uri.replace(f"{s.bucket}/jobs/", "").replace("/SUMMARY.txt", ""))
    return 0


def launch(s, job, hours, run_id, self_stop, extra_excludes=()):
    tarball = pack(s, job, run_id, extra_excludes)
    env_path = tarball.parent / "job.env"
    if not DRY_RUN:
        env_path.write_text(env_file_text(job_env(s, job, hours, run_id, self_stop)), encoding="utf-8")
    print(f"[env ] {env_path}", flush=True)

    remote_dir = f"$HOME/po/jobs/{run_id}"
    for local in (tarball, env_path):
        gcloud(
            "compute",
            "scp",
            str(local),
            f"{s.instance}:/tmp/{run_id}.{local.name}",
            "--zone",
            s.zone,
            "--project",
            s.project,
            "--quiet",
        )

    command = (
        f"mkdir -p {remote_dir} && cd {remote_dir}"
        f" && tar -xzf /tmp/{run_id}.{tarball.name}"
        f" && cp /tmp/{run_id}.{env_path.name} ./job.env"
        " && nohup bash /tmp/po-run-job.sh job.env > runner.log 2>&1 < /dev/null & echo started"
    )
    ssh(s, command, capture=False)
    print(f"[run ] {run_id} -> {s.gcs_job_uri(run_id)}", flush=True)
    return run_id


def wait_run(s, job, run_id, poll=60):
    summary = f"{s.gcs_job_uri(run_id)}/SUMMARY.txt"
    if DRY_RUN:
        print(f"[wait] (dry run) would poll {summary}", flush=True)
        return 0
    print(f"[wait] {run_id} (poll {poll}s); Ctrl+C leaves it running", flush=True)
    while True:
        if gcloud("storage", "ls", summary, check=False).strip():
            print(gcloud("storage", "cat", summary, check=False))
            return 0
        tail = gcloud("storage", "cat", f"{s.gcs_job_uri(run_id)}/progress.log", check=False).splitlines()
        if tail:
            print(f"[wait] {tail[-1][:160]}", flush=True)
        time.sleep(poll)


def pull_run(s, job, run_id, dest=None, into_repo=False):
    prefix = s.gcs_job_uri(run_id)
    if into_repo:
        for rel in job.get("artifacts", []):
            target = ROOT / job["source"] / rel
            target.mkdir(parents=True, exist_ok=True)
            gcloud("storage", "rsync", "--recursive", "--quiet", f"{prefix}/{rel}", str(target))
            print(f"[pull] {rel} -> {target}")
        return 0
    target = pathlib.Path(dest) if dest else (RUNS_DIR / run_id / "artifacts")
    target.mkdir(parents=True, exist_ok=True)
    gcloud("storage", "rsync", "--recursive", "--quiet", prefix, str(target))
    print(f"[pull] {prefix} -> {target}")
    return 0


def cmd_pull(s, args):
    if not args.run_id:
        raise Failed("pull needs --run-id")
    job = s.job(args.job) if args.job else {"source": "", "artifacts": []}
    return pull_run(s, job, args.run_id, args.dest, args.into_repo)


def cmd_stop(s, args):
    marker = RUNS_DIR / "_markers" / f"{args.run_id}.STOP"
    if not DRY_RUN:
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text("stop\n", encoding="utf-8")
    gcloud("storage", "cp", "--quiet", str(marker), f"{s.gcs_job_uri(args.run_id)}/STOP")
    print(f"asked {args.run_id} to stop; the runner notices within {s.sync_seconds}s")
    return 0


def cmd_logs(s, args):
    instance_status(s)
    path = f"$HOME/po/jobs/{args.run_id}/runner.log"
    if args.follow:
        ssh(s, f"tail -n 60 -f {path}", capture=False)
        return 0
    print(ssh(s, f"tail -n {args.lines} {path}", check=False))
    summary = gcloud("storage", "cat", f"{s.gcs_job_uri(args.run_id)}/SUMMARY.txt", check=False)
    if summary.strip():
        print(summary)
    return 0


def cmd_run(s, args):
    jobs = [s.job(job_id) for job_id in args.job]
    if len(jobs) > 1 and not args.wait:
        raise Failed("several --job values run one after another; add --wait")

    ensure_bucket(s)
    ensure_vm(s)
    prepare_vm(s)

    for index, job in enumerate(jobs):
        hours = args.hours or job.get("hours", 6)
        run_id = args.run_id if (args.run_id and len(jobs) == 1) else f"{job['id']}_{time.strftime('%Y%m%d_%H%M%S')}"
        launch(s, job, hours, run_id, self_stop=(index == len(jobs) - 1), extra_excludes=args.exclude or ())
        if args.wait:
            wait_run(s, job, run_id, poll=args.poll)
            if args.pull:
                pull_run(s, job, run_id, args.dest, args.into_repo)
    if not args.wait:
        print("\nStarted. Follow with:\n  gcp_queue.py logs --run-id <run>\n  gcp_queue.py pull --run-id <run>")
    return 0


# --------------------------------------------------------------------------- main


def build_parser():
    parser = argparse.ArgumentParser(
        prog="gcp_queue.py",
        description="Run the Po MuJoCo Warp trainings on a Google Cloud Spot L4 VM.",
        epilog="Runbook: docs/gcp-training.md. Job catalog: scripts/gcp/cloud_jobs.json.",
    )
    parser.add_argument("--project", help="GCP project (default: gcloud config core/project)")
    parser.add_argument("--zone", help="zone (default: try us-east4-a/b/c)")
    parser.add_argument("--instance", help="VM name (default: defaults.instance)")
    parser.add_argument("--machine-type", help="default g2-standard-8 = 1x NVIDIA L4")
    parser.add_argument("--bucket", help="GCS bucket name (default: <project>-po-rl-runs)")
    parser.add_argument("--dry-run", action="store_true", help="print the commands, touch nothing")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("doctor", help="what is installed, logged in and provisioned")
    quota = sub.add_parser("quota", help="L4 GPU quota per region")
    quota.add_argument("--region", action="append", help="repeatable; default the bucket region")
    sub.add_parser("up", help="create or start the runner VM")
    sub.add_parser("down", help="stop the runner VM")
    sub.add_parser("status", help="VM state and finished cloud jobs")

    run = sub.add_parser("run", help="ship a repo and start a job")
    run.add_argument("--job", action="append", required=True, help="job id from cloud_jobs.json")
    run.add_argument("--hours", type=float, help="time budget (default: the job's hours)")
    run.add_argument("--run-id", help="name of this run (default: <job>_<stamp>)")
    run.add_argument("--wait", action="store_true", help="wait for SUMMARY.txt")
    run.add_argument("--poll", type=int, default=60, help="seconds between checks while waiting")
    run.add_argument("--pull", action="store_true", help="with --wait, fetch artifacts when it ends")
    run.add_argument("--into-repo", action="store_true", help="pull artifacts into the repo tree")
    run.add_argument("--dest", help="pull destination (default scripts/gcp/runs/<run>/artifacts)")
    run.add_argument("--sync-seconds", type=int, help="checkpoint sync interval on the VM")
    run.add_argument("--exclude", action="append", help="extra tar exclude, repeatable")

    pull = sub.add_parser("pull", help="copy a job's artifacts from GCS")
    pull.add_argument("--run-id", required=True)
    pull.add_argument("--job", help="job id (needed for --into-repo artifact paths)")
    pull.add_argument("--dest")
    pull.add_argument("--into-repo", action="store_true")

    logs = sub.add_parser("logs", help="tail a running job")
    logs.add_argument("--run-id", required=True)
    logs.add_argument("--lines", type=int, default=80)
    logs.add_argument("--follow", action="store_true")

    stop = sub.add_parser("stop", help="ask a running job to stop cleanly")
    stop.add_argument("--run-id", required=True)
    return parser


def main(argv=None):
    global DRY_RUN
    args = build_parser().parse_args(argv)
    DRY_RUN = bool(args.dry_run)
    handlers = {
        "doctor": cmd_doctor,
        "quota": cmd_quota,
        "up": cmd_up,
        "down": cmd_down,
        "status": cmd_status,
        "run": cmd_run,
        "pull": cmd_pull,
        "logs": cmd_logs,
        "stop": cmd_stop,
    }
    try:
        settings = Settings(args, load_jobs())
        return handlers[args.command](settings, args)
    except Failed as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("\ninterrupted")
        return 130


if __name__ == "__main__":
    sys.exit(main())
