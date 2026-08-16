#!/usr/bin/env bash
# Poll for an exact artifact uploaded by another job in the current workflow run.

set -euo pipefail

run_id=""
artifact=""
destination=""
timeout_seconds="900"
poll_seconds="5"
output_file="${GITHUB_OUTPUT:-/dev/stdout}"

usage() {
  echo "Usage: $0 --run-id ID --artifact NAME --destination DIR [--timeout-seconds N] [--poll-seconds N]" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --run-id) run_id="${2:-}"; shift 2 ;;
    --artifact) artifact="${2:-}"; shift 2 ;;
    --destination) destination="${2:-}"; shift 2 ;;
    --timeout-seconds) timeout_seconds="${2:-}"; shift 2 ;;
    --poll-seconds) poll_seconds="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ ! "${run_id}" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "${artifact}" =~ ^server-test-[0-9a-f]{40}-[a-z0-9-]+$ ]] ||
   [[ ! "${timeout_seconds}" =~ ^[1-9][0-9]*$ ]] ||
   [[ ! "${poll_seconds}" =~ ^[1-9][0-9]*$ ]] ||
   (( timeout_seconds > 1800 || poll_seconds > 60 )); then
  usage
  exit 2
fi
if [[ ! "${GITHUB_REPOSITORY:-}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || [[ -z "${GH_TOKEN:-}" ]]; then
  echo "::error::GITHUB_REPOSITORY and GH_TOKEN are required." >&2
  exit 2
fi
for command in date gh realpath; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done

runner_temp="$(realpath -m "${RUNNER_TEMP:-/tmp}")"
destination="$(realpath -m "${destination}")"
case "${destination}/" in
  "${runner_temp}/"*) ;;
  *) echo "::error::Artifact destination must stay inside RUNNER_TEMP." >&2; exit 2 ;;
esac

mkdir -p "${destination}"
epoch_ms() {
  local epoch_ns
  epoch_ns="$(date +%s%N)"
  printf '%s\n' "$(( epoch_ns / 1000000 ))"
}
started_ms="$(epoch_ms)"
deadline_ms=$(( started_ms + timeout_seconds * 1000 ))
attempt=0
last_error="${destination}/download-error.log"

while true; do
  attempt=$(( attempt + 1 ))
  find "${destination}" -mindepth 1 -maxdepth 1 ! -name download-error.log -exec rm -rf -- {} +
  if gh run download "${run_id}" --repo "${GITHUB_REPOSITORY}" \
      --name "${artifact}" --dir "${destination}" 2>"${last_error}"; then
    finished_ms="$(epoch_ms)"
    rm -f "${last_error}"
    printf 'wait_download_ms=%s\n' "$(( finished_ms - started_ms ))" >> "${output_file}"
    printf 'poll_attempts=%s\n' "${attempt}" >> "${output_file}"
    echo "Downloaded ${artifact} after ${attempt} poll(s)."
    exit 0
  fi
  now_ms="$(epoch_ms)"
  if (( now_ms >= deadline_ms )); then
    cat "${last_error}" >&2 || true
    echo "::error::Timed out waiting for exact same-run artifact ${artifact}." >&2
    exit 1
  fi
  echo "Artifact ${artifact} is not available yet (poll ${attempt}, elapsed $(( now_ms - started_ms ))ms)."
  sleep "${poll_seconds}"
done
