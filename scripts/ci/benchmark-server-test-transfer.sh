#!/usr/bin/env bash
# Focused hosted-runner timing harness for server-test artifact/cache decisions.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json"
CONFIGURATION="${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}"

mode=""
project=""
source_sha=""
payload_dir=""
metrics=""
identity=""

usage() {
  echo "Usage: $0 <producer|baseline|consumer-artifact|consumer-cache> --project <relative.csproj> --source-sha <sha> --metrics <file> --identity <name> [--payload <directory>]" >&2
}

if [[ $# -gt 0 ]]; then
  mode="$1"
  shift
fi
while [[ $# -gt 0 ]]; do
  case "$1" in
    --project) project="${2:-}"; shift 2 ;;
    --source-sha) source_sha="${2:-}"; shift 2 ;;
    --payload) payload_dir="${2:-}"; shift 2 ;;
    --metrics) metrics="${2:-}"; shift 2 ;;
    --identity) identity="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ ! "${mode}" =~ ^(producer|baseline|consumer-artifact|consumer-cache)$ ]] ||
   [[ -z "${project}" || -z "${source_sha}" || -z "${metrics}" || -z "${identity}" ]]; then
  usage
  exit 2
fi
if [[ ! "${source_sha}" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "::error::Source SHA must be a full commit id." >&2
  exit 2
fi
if [[ "${mode}" != "baseline" && -z "${payload_dir}" ]]; then
  echo "::error::Producer and consumer modes require --payload." >&2
  exit 2
fi
for command in date dotnet jq; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done

artifact_suffix="$(jq -er --arg project "${project}" '.projects[] | select(.csproj == $project) | .artifact_suffix' "${REGISTRY}")" || {
  echo "::error::Project '${project}' is not registered." >&2
  exit 2
}
proof_filter="$(jq -er --arg project "${project}" '.projects[] | select(.csproj == $project) | .proof_filter' "${REGISTRY}")"
mkdir -p "$(dirname "${metrics}")"
started_ns="$(date +%s%N)"

elapsed_ms() {
  local now_ns
  now_ns="$(date +%s%N)"
  echo $(( (now_ns - started_ns) / 1000000 ))
}

restore_ms=0
build_ms=0
package_ms=0
integrity_unpack_ms=0
integrity_check_ms=0
unpack_ms=0
discovery_ms=0
test_ms=0
archive_bytes=0

if [[ "${mode}" == "producer" || "${mode}" == "baseline" ]]; then
  phase_ns="$(date +%s%N)"
  dotnet restore "${project}"
  restore_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))

  phase_ns="$(date +%s%N)"
  dotnet build "${project}" --no-restore --configuration "${CONFIGURATION}" /p:TreatWarningsAsErrors=true
  build_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))
fi

if [[ "${mode}" == "producer" ]]; then
  mkdir -p "${payload_dir}"
  phase_ns="$(date +%s%N)"
  "${SCRIPT_DIR}/package-server-test-binaries.sh" \
    --project "${project}" --output "${payload_dir}" --source-sha "${source_sha}"
  package_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))
  archive_bytes="$(jq -r '.archive_bytes' "${payload_dir}/server-test-binaries-${artifact_suffix}.manifest.json")"
fi

if [[ "${mode}" == consumer-* ]]; then
  restore_timing="$(dirname "${metrics}")/restore-${identity}.json"
  phase_ns="$(date +%s%N)"
  HONUA_SERVER_TEST_ARTIFACT_TIMING_FILE="${restore_timing}" \
    "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
    --manifest "${payload_dir}/server-test-binaries-${artifact_suffix}.manifest.json" \
    --destination "${REPO_ROOT}" --project "${project}" --source-sha "${source_sha}"
  integrity_unpack_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))
  integrity_check_ms="$(jq -r '.integrity_check_ms' "${restore_timing}")"
  unpack_ms="$(jq -r '.unpack_ms' "${restore_timing}")"
  rm -f "${restore_timing}"
  archive_bytes="$(jq -r '.archive_bytes' "${payload_dir}/server-test-binaries-${artifact_suffix}.manifest.json")"
fi

if [[ "${mode}" == "baseline" || "${mode}" == consumer-* ]]; then
  phase_ns="$(date +%s%N)"
  dotnet test "${project}" --no-build --no-restore --configuration "${CONFIGURATION}" --list-tests >/dev/null
  discovery_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))

  phase_ns="$(date +%s%N)"
  dotnet test "${project}" --no-build --no-restore --configuration "${CONFIGURATION}" \
    --filter "${proof_filter}" --logger 'console;verbosity=minimal'
  test_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))
fi

total_ms="$(elapsed_ms)"
sdk="$(dotnet --version)"
jq -nS \
  --arg contract "honua.server-test-transfer-benchmark.v1" \
  --arg mode "${mode}" --arg identity "${identity}" --arg project "${project}" \
  --arg artifact_suffix "${artifact_suffix}" --arg source_sha "${source_sha,,}" --arg dotnet_sdk "${sdk}" \
  --arg run_id "${GITHUB_RUN_ID:-local}" --arg run_attempt "${GITHUB_RUN_ATTEMPT:-1}" \
  --argjson restore_ms "${restore_ms}" --argjson build_ms "${build_ms}" \
  --argjson package_ms "${package_ms}" --argjson integrity_unpack_ms "${integrity_unpack_ms}" \
  --argjson integrity_check_ms "${integrity_check_ms}" --argjson unpack_ms "${unpack_ms}" \
  --argjson discovery_ms "${discovery_ms}" --argjson test_ms "${test_ms}" \
  --argjson total_ms "${total_ms}" --argjson archive_bytes "${archive_bytes}" \
  '{contract:$contract,mode:$mode,identity:$identity,project:$project,artifact_suffix:$artifact_suffix,
    source_sha:$source_sha,dotnet_sdk:$dotnet_sdk,run_id:$run_id,run_attempt:($run_attempt|tonumber),
    restore_ms:$restore_ms,build_ms:$build_ms,package_ms:$package_ms,
    integrity_unpack_ms:$integrity_unpack_ms,integrity_check_ms:$integrity_check_ms,unpack_ms:$unpack_ms,
    discovery_ms:$discovery_ms,test_ms:$test_ms,
    total_ms:$total_ms,archive_bytes:$archive_bytes}' > "${metrics}"

cat "${metrics}"
