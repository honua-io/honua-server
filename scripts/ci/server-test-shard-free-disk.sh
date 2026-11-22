#!/usr/bin/env bash
# Reclaim server-test shard disk in parallel with runner setup when free space
# is below the headroom threshold.
#
# Usage:
#   server-test-shard-free-disk.sh          start cleanup in the background (or skip if enough free space)
#   server-test-shard-free-disk.sh --await  wait for cleanup before build work

set -uo pipefail

STATE_DIR="${RUNNER_TEMP:-/tmp}"
PID_FILE="${STATE_DIR}/server-test-shard-free-disk.pid"
LOG_FILE="${STATE_DIR}/server-test-shard-free-disk.log"
DISK_PATH="${HONUA_FREE_DISK_PATH:-/}"
MIN_FREE_GB="${HONUA_MIN_FREE_DISK_GB:-40}"

if [[ ! "${MIN_FREE_GB}" =~ ^[1-9][0-9]*$ ]]; then
  MIN_FREE_GB=40
fi

if [[ "${1:-}" == "--await" ]]; then
  if [[ ! -f "${PID_FILE}" ]]; then
    if [[ -f "${LOG_FILE}" ]]; then
      tail -n 20 "${LOG_FILE}"
    else
      echo "No server-test shard disk cleanup was started."
    fi
    exit 0
  fi

  pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
  if [[ -n "${pid}" ]]; then
    while kill -0 "${pid}" 2>/dev/null; do
      sleep 1
    done
    # The cleanup commands are best-effort, matching the former foreground
    # step. The log remains useful when a shard later exhausts disk.
    [[ -f "${LOG_FILE}" ]] && tail -n 20 "${LOG_FILE}"
  fi
  rm -f "${PID_FILE}"
  exit 0
fi

avail_kib="$(df -Pk "${DISK_PATH}" 2>/dev/null | awk 'NR==2 {print $4}')"
if [[ "${avail_kib}" =~ ^[0-9]+$ ]]; then
  avail_gb=$(( avail_kib / 1048576 ))
  if (( avail_gb >= MIN_FREE_GB )); then
    rm -f "${PID_FILE}"
    {
      echo "Skipping server-test shard disk cleanup: ${avail_gb} GiB free on ${DISK_PATH} (>= ${MIN_FREE_GB} GiB threshold)."
      df -h "${DISK_PATH}" || true
    } > "${LOG_FILE}" 2>&1
    echo "Skipping server-test shard disk cleanup: ${avail_gb} GiB free on ${DISK_PATH} (>= ${MIN_FREE_GB} GiB threshold)."
    exit 0
  fi
fi

# Redirect before backgrounding: an inherited Actions step pipe would keep the
# start step open until cleanup exits, accidentally making this foreground.
nohup bash -c '
  sudo rm -rf /usr/local/lib/android || true
  sudo rm -rf /usr/share/swift || true
  sudo rm -rf /usr/local/.ghcup || true
  sudo rm -rf /opt/hostedtoolcache/CodeQL || true
  df -h "$1" || true
' _ "${DISK_PATH}" > "${LOG_FILE}" 2>&1 &
echo $! > "${PID_FILE}"
disown 2>/dev/null || true
echo "Server-test shard disk cleanup started in the background (log: ${LOG_FILE})."
