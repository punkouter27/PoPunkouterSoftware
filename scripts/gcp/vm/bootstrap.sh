#!/usr/bin/env bash
# Prepare a Po RL runner VM (Spot g2-standard-8, NVIDIA L4) to run the trainers.
#
# Idempotent: safe to run on every `up`/`run`. It waits for the NVIDIA driver the Deep
# Learning VM installs from metadata (install-nvidia-driver=True), adds the headless
# EGL/GL libraries MuJoCo and Warp need, installs uv, and puts the environment variables
# the trainers read into ~/.bashrc. gcloud is already on Deep Learning VM images.
#
# Run as: gcloud compute ssh po-rl-runner --command 'bash /tmp/po-bootstrap.sh'
set -uo pipefail

log() { printf '[bootstrap %s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }

# --- GPU + driver ---------------------------------------------------------------
if ! command -v nvidia-smi >/dev/null 2>&1; then
  log "nvidia-smi not there yet; waiting for the driver agent (up to 10 min)"
  for _ in $(seq 1 60); do
    command -v nvidia-smi >/dev/null 2>&1 && break
    sleep 10
  done
fi
if ! command -v nvidia-smi >/dev/null 2>&1; then
  log "NO nvidia-smi. The VM was probably created without --metadata=install-nvidia-driver=True."
  exit 3
fi
GPU_INFO=$(nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader | head -n1)
log "GPU: $GPU_INFO"
DRIVER_MAJOR=$(nvidia-smi --query-gpu=driver_version --format=csv,noheader | head -n1 | cut -d. -f1)
if [ "${DRIVER_MAJOR:-0}" -lt 580 ] 2>/dev/null; then
  log "WARNING: driver $DRIVER_MAJOR.x is older than 580; the CUDA 13 wheels PoBasketball pins (torch cu130) need >= 580."
fi

# --- libraries ------------------------------------------------------------------
need=0
for pkg in libegl1 libgl1 libglib2.0-0 libegl1-mesa-dev libgles2-mesa-dev git curl rsync; do
  dpkg -s "$pkg" >/dev/null 2>&1 || need=1
done
if [ "$need" = 1 ]; then
  log "apt: headless EGL/GL libraries, git, curl, rsync"
  sudo apt-get update -qq
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
    libegl1 libgl1 libglib2.0-0 libegl1-mesa-dev libgles2-mesa-dev git curl rsync >/dev/null
fi

# --- uv -------------------------------------------------------------------------
export PATH="$HOME/.local/bin:$PATH"
if ! command -v uv >/dev/null 2>&1; then
  log "installing uv"
  curl -LsSf https://astral.sh/uv/install.sh | sh
  export PATH="$HOME/.local/bin:$PATH"
fi
if ! command -v uv >/dev/null 2>&1; then
  log "uv install failed"
  exit 4
fi
log "uv $(uv --version 2>/dev/null | head -n1)"

# --- environment the trainers read ----------------------------------------------
mkdir -p "$HOME/.cache/warp"
BASHRC="$HOME/.bashrc"
for line in \
  'export PATH=$HOME/.local/bin:$PATH' \
  'export MUJOCO_GL=egl' \
  'export PYOPENGL_PLATFORM=egl' \
  'export WARP_CACHE_PATH=$HOME/.cache/warp'
do
  grep -qxF "$line" "$BASHRC" 2>/dev/null || printf '%s\n' "$line" >> "$BASHRC"
done

command -v gcloud >/dev/null 2>&1 || log "WARNING: gcloud missing - checkpoint sync to GCS will fail"
log "ready: $(nvidia-smi --query-gpu=name --format=csv,noheader | head -n1), uv $(uv --version 2>/dev/null | head -n1), disk $(df -h / | awk 'NR==2{print $4}') free"
