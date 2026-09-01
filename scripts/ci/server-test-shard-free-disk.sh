#!/usr/bin/env bash
# Reclaim server-test shard disk in parallel with runner setup.
#
# Usage:
#   server-test-shard-free-disk.sh          start cleanup in the background
#   server-test-shard-free-disk.sh --await  wait for cleanup before build work

set -uo pipefail

STATE_DIR="${RUNNER_TEMP:-/tmp}"
PID_FILE="${STATE_DIR}/server-test-shard-free-disk.pid"
LOG_FILE="${STATE_DIR}/server-test-shard-free-disk.log"

if [[ "${1:-}" == "--await" ]]; then
  if [[ ! -f "${PID_FILE}" ]]; then
    echo "No server-test shard disk cleanup was started."
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
    exit 0
  fi
  exit 0
fi

# Redirect before backgrounding: an inherited Actions step pipe would keep the
# start step open until cleanup exits, accidentally making this foreground.
nohup bash -c '
  sudo rm -rf /usr/local/lib/android || true
  sudo rm -rf /usr/share/swift || true
  sudo rm -rf /usr/local/.ghcup || true
  sudo rm -rf /opt/hostedtoolcache/CodeQL || true
  df -h / || true
' > "${LOG_FILE}" 2>&1 &
echo $! > "${PID_FILE}"
disown 2>/dev/null || true
echo "Server-test shard disk cleanup started in the background (log: ${LOG_FILE})."
