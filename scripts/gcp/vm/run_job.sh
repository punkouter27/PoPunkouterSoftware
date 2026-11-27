#!/usr/bin/env bash
# Run one cloud job on the Po RL runner VM and keep its artifacts in Cloud Storage.
#
#   bash $HOME/po/run_job.sh $HOME/po/jobs/<run-id>/job.env
#
# job.env (written by gcp_queue.py) carries: JOB_ID RUN_ID REPO CWD SETUP COMMAND
# ARTIFACT_DIRS BUCKET_URI HOURS TIMEOUT_SECONDS SYNC_SECONDS SELF_STOP STOP_FILE.
#
# The trainer is the repo's own command from cloud_jobs.json - nothing here changes how
# it trains. This wrapper only (a) runs the setup command, (b) rsyncs every artifact
# directory to gs://<bucket>/jobs/<run-id>/ every SYNC_SECONDS so a Spot reclaim costs
# at most that much work, (c) enforces the time budget (SIGINT, then SIGKILL), and
# (d) writes SUMMARY.txt to GCS and stops the VM.
set -uo pipefail

ENV_FILE="${1:?usage: run_job.sh <job.env>}"
# shellcheck disable=SC1090
. "$ENV_FILE"

WORKDIR="$(cd "$(dirname "$ENV_FILE")" && pwd)"
LOG="$WORKDIR/train.log"
PROGRESS="$WORKDIR/progress.log"
STOP_MARKER="$BUCKET_URI/STOP"
STARTED=$(date -u +%Y-%m-%dT%H:%M:%SZ)
START_EPOCH=$(date +%s)

export PATH="$HOME/.local/bin:$PATH"
export MUJOCO_GL=egl
export PYOPENGL_PLATFORM=egl
export WARP_CACHE_PATH="$HOME/.cache/warp"
export PYTHONUNBUFFERED=1

log() {
  printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*"
  printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*" >> "$PROGRESS"
}

sync_once() {
  local rel
  for rel in $ARTIFACT_DIRS; do
    [ -e "$WORKDIR/$rel" ] || continue
    gcloud storage rsync --recursive --quiet "$WORKDIR/$rel" "$BUCKET_URI/$rel" \
      >/dev/null 2>>"$WORKDIR/sync.err" || log "sync failed: $rel"
  done
  gcloud storage cp --quiet "$PROGRESS" "$BUCKET_URI/progress.log" >/dev/null 2>&1
  [ -f "$LOG" ] && gcloud storage cp --quiet "$LOG" "$BUCKET_URI/runner.log" >/dev/null 2>&1
  return 0
}

SYNC_LOOP_PID=""
start_sync_loop() {
  (
    while :; do
      sleep "$SYNC_SECONDS"
      sync_once
    done
  ) &
  SYNC_LOOP_PID=$!
}
stop_sync_loop() {
  if [ -n "$SYNC_LOOP_PID" ]; then
    kill "$SYNC_LOOP_PID" 2>/dev/null
    SYNC_LOOP_PID=""
  fi
}

finish() {
  local rc=$1
  local note=${2:-}
  stop_sync_loop
  sync_once
  local seconds=$(( $(date +%s) - START_EPOCH ))
  {
    echo "job        : $JOB_ID"
    echo "run        : $RUN_ID"
    echo "repo       : $REPO ($CWD)"
    echo "command    : $COMMAND"
    echo "status     : $note"
    echo "exit code  : $rc"
    echo "started    : $STARTED"
    echo "ended      : $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "seconds    : $seconds"
    echo "gpu        : $(nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader 2>/dev/null | head -n1)"
    echo "artifacts  : $ARTIFACT_DIRS"
    echo "bucket     : $BUCKET_URI"
    echo
    echo "--- last 40 progress lines ---"
    tail -n 40 "$PROGRESS" 2>/dev/null
  } > "$WORKDIR/SUMMARY.txt"
  gcloud storage cp --quiet "$WORKDIR/SUMMARY.txt" "$BUCKET_URI/SUMMARY.txt" \
    || log "could not upload SUMMARY.txt"
  log "finished: $note (exit $rc) after ${seconds}s"
  if [ "${SELF_STOP:-0}" = "1" ]; then
    log "stopping the VM in 1 minute"
    sudo shutdown -h +1 >/dev/null 2>&1
  fi
  exit "$rc"
}

trap 'stop_sync_loop' EXIT INT TERM

log "job $JOB_ID ($REPO) run $RUN_ID, budget ${HOURS}h, sync every ${SYNC_SECONDS}s"
nvidia-smi --query-gpu=name,memory.total --format=csv,noheader | head -n1 | while read -r line; do log "gpu: $line"; done
gcloud storage cp --quiet "$ENV_FILE" "$BUCKET_URI/job.env" >/dev/null 2>&1

start_sync_loop

cd "$WORKDIR/$CWD" || finish 90 "working directory $CWD missing"

if [ -n "${SETUP:-}" ]; then
  log "setup: $SETUP"
  bash -lc "$SETUP" >>"$LOG" 2>&1
  setup_rc=$?
  if [ "$setup_rc" -ne 0 ]; then
    log "setup failed ($setup_rc), last lines:"
    tail -n 15 "$LOG" | while read -r line; do log "  $line"; done
    finish "$setup_rc" "setup failed"
  fi
fi

log "train: $COMMAND"
START_EPOCH=$(date +%s)
setsid bash -lc "$COMMAND" >>"$LOG" 2>&1 < /dev/null &
TRAINER=$!
DEADLINE=$(( $(date +%s) + TIMEOUT_SECONDS ))
NOTE="finished"

while kill -0 "$TRAINER" 2>/dev/null; do
  if [ "$(date +%s)" -ge "$DEADLINE" ]; then
    log "time budget ${HOURS}h reached: SIGINT"
    kill -INT -- "-$TRAINER" 2>/dev/null
    sleep 180
    if kill -0 "$TRAINER" 2>/dev/null; then
      log "still running: SIGTERM"
      kill -TERM -- "-$TRAINER" 2>/dev/null
      sleep 30
    fi
    kill -0 "$TRAINER" 2>/dev/null && kill -KILL -- "-$TRAINER" 2>/dev/null
    NOTE="stopped at the ${HOURS}h time limit"
    break
  fi
  if [ -n "${STOP_FILE:-}" ] && gcloud storage ls "$STOP_MARKER" >/dev/null 2>&1; then
    log "STOP marker $STOP_MARKER: stopping cleanly"
    gcloud storage rm "$STOP_MARKER" >/dev/null 2>&1
    kill -INT -- "-$TRAINER" 2>/dev/null
    sleep 120
    kill -0 "$TRAINER" 2>/dev/null && kill -KILL -- "-$TRAINER" 2>/dev/null
    NOTE="stopped through the STOP marker"
    break
  fi
  sleep 20
done

wait "$TRAINER"
rc=$?
finish "$rc" "$NOTE"
