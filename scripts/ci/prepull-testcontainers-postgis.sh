#!/usr/bin/env bash
# Warm the Docker image cache for the PostGIS image the lean gate's
# "Server Governance/Drift" step boots through Testcontainers, so the pull
# overlaps the fixtures/restore/build ahead of it instead of stalling the gate's
# critical path.
#
# The governance step is ~1.5 min warm and ~3-4 min cold; the delta is almost
# entirely `docker pull`. Nothing about the step itself moves (owner decision,
# #2882): EndpointRegistryDriftTests still boots the real host and its own
# ephemeral container, exactly where it does today.
#
# Usage:
#   prepull-testcontainers-postgis.sh            start the pull in the background
#   prepull-testcontainers-postgis.sh --await    block until that pull finishes
#
# BEST-EFFORT BY CONSTRUCTION. Composite-action steps cannot set
# `continue-on-error`, so this script must never fail the gate: no Docker, no
# image tag found, a pull that 429s — every path exits 0 and lets Testcontainers
# do what it already does today.
#
# The tag is read out of the fixture rather than hardcoded here, so bumping
# PostgresFixture cannot silently leave CI pre-pulling a stale image.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

FIXTURE="tests/dotnet/Honua.TestKit/PostgresFixture.cs"
STATE_DIR="${RUNNER_TEMP:-/tmp}"
PID_FILE="${STATE_DIR}/prepull-postgis.pid"
LOG_FILE="${STATE_DIR}/prepull-postgis.log"
AWAIT_TIMEOUT_SECONDS="${PREPULL_AWAIT_TIMEOUT_SECONDS:-300}"

resolve_image() {
  [[ -f "${FIXTURE}" ]] || return 1
  grep -oE 'postgis/postgis:[0-9]+(\.[0-9]+)*-[0-9]+(\.[0-9]+)*' "${FIXTURE}" | head -n 1
}

if [[ "${1:-}" == "--await" ]]; then
  if [[ ! -f "${PID_FILE}" ]]; then
    echo "No PostGIS pre-pull was started; Testcontainers will pull on demand."
    exit 0
  fi
  pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
  if [[ -z "${pid}" ]]; then
    exit 0
  fi
  waited=0
  while kill -0 "${pid}" 2>/dev/null; do
    if (( waited >= AWAIT_TIMEOUT_SECONDS )); then
      echo "::warning::PostGIS pre-pull still running after ${AWAIT_TIMEOUT_SECONDS}s; continuing anyway."
      exit 0
    fi
    sleep 5
    waited=$(( waited + 5 ))
  done
  echo "PostGIS pre-pull finished after ~${waited}s."
  [[ -f "${LOG_FILE}" ]] && tail -n 5 "${LOG_FILE}"
  exit 0
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker not available; skipping PostGIS pre-pull."
  exit 0
fi

image="$(resolve_image || true)"
if [[ -z "${image}" ]]; then
  echo "::warning::Could not resolve the PostGIS image tag from ${FIXTURE}; skipping pre-pull."
  exit 0
fi

if docker image inspect "${image}" >/dev/null 2>&1; then
  echo "PostGIS image ${image} is already present on this runner."
  exit 0
fi

# stdout/stderr MUST go to a file. A background child still holding the step's
# pipe open makes the Actions runner wait for the pipe to close, which would
# turn a background pull into a foreground one.
echo "Pre-pulling ${image} in the background (log: ${LOG_FILE})."
nohup docker pull "${image}" > "${LOG_FILE}" 2>&1 &
echo $! > "${PID_FILE}"
disown 2>/dev/null || true
exit 0
