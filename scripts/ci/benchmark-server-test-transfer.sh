#!/usr/bin/env bash
# Focused hosted-runner timing harness for server-test artifact/cache decisions.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
REPO_ROOT="${HONUA_SERVER_TEST_BENCHMARK_REPO_ROOT:-${DEFAULT_REPO_ROOT}}"
REPO_ROOT="$(cd "${REPO_ROOT}" && pwd)"
REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json"
CONFIGURATION="${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}"

mode=""
project=""
source_sha=""
payload_dir=""
metrics=""
identity=""
test_filter=""
job_start_epoch_ms="0"

usage() {
  echo "Usage: $0 <producer|baseline|consumer-artifact|consumer-cache|consumer-ready> --project <relative.csproj> --source-sha <sha> --metrics <file> --identity <name> [--payload <directory>] [--filter <dotnet-filter>] [--job-start-epoch-ms <epoch>]" >&2
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
    --filter) test_filter="${2:-}"; shift 2 ;;
    --job-start-epoch-ms) job_start_epoch_ms="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ ! "${mode}" =~ ^(producer|baseline|consumer-artifact|consumer-cache|consumer-ready)$ ]] ||
   [[ -z "${project}" || -z "${source_sha}" || -z "${metrics}" || -z "${identity}" ]]; then
  usage
  exit 2
fi
if [[ ! "${source_sha}" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "::error::Source SHA must be a full commit id." >&2
  exit 2
fi
if [[ ! "${identity}" =~ ^[a-z0-9-]+$ ]] || [[ ! "${job_start_epoch_ms}" =~ ^[0-9]+$ ]]; then
  echo "::error::Benchmark identity or job-start timestamp is invalid." >&2
  exit 2
fi
if [[ "${mode}" =~ ^(producer|consumer-artifact|consumer-cache)$ && -z "${payload_dir}" ]]; then
  echo "::error::Producer and artifact/cache consumer modes require --payload." >&2
  exit 2
fi
for command in date dotnet jq python3 sha256sum; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done

# Project paths are relative to the selected checkout, while evidence paths are
# relative to the caller's workspace. Keep both identities explicit so a
# trusted policy checkout can benchmark a separate source checkout.
mkdir -p "$(dirname "${metrics}")"
metrics="$(cd "$(dirname "${metrics}")" && pwd)/$(basename "${metrics}")"
if [[ -n "${payload_dir}" ]]; then
  mkdir -p "${payload_dir}"
  payload_dir="$(cd "${payload_dir}" && pwd)"
fi

repo_dotnet() {
  (cd "${REPO_ROOT}" && dotnet "$@")
}

artifact_suffix="$(jq -er --arg project "${project}" '.projects[] | select(.csproj == $project) | .artifact_suffix' "${REGISTRY}")" || {
  echo "::error::Project '${project}' is not registered." >&2
  exit 2
}
proof_filter="$(jq -er --arg project "${project}" '.projects[] | select(.csproj == $project) | .proof_filter' "${REGISTRY}")"
if [[ -z "${test_filter}" ]]; then
  test_filter="${proof_filter}"
fi
filter_sha256="$(printf '%s' "${test_filter}" | sha256sum | cut -d' ' -f1)"
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
test_started_epoch_ms=0
result_sha256=""
result_count=0
result_outcomes='{}'

if [[ "${mode}" == "producer" || "${mode}" == "baseline" ]]; then
  phase_ns="$(date +%s%N)"
  repo_dotnet restore "${project}"
  restore_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))

  phase_ns="$(date +%s%N)"
  repo_dotnet build "${project}" --no-restore --configuration "${CONFIGURATION}" /p:TreatWarningsAsErrors=true
  build_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))
fi

if [[ "${mode}" == "producer" ]]; then
  mkdir -p "${payload_dir}"
  phase_ns="$(date +%s%N)"
  HONUA_SERVER_TEST_ARTIFACT_REPO_ROOT="${REPO_ROOT}" \
    HONUA_SERVER_TEST_ARTIFACT_REGISTRY="${REGISTRY}" \
    "${SCRIPT_DIR}/package-server-test-binaries.sh" \
    --project "${project}" --output "${payload_dir}" --source-sha "${source_sha}"
  package_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))
  archive_bytes="$(jq -r '.archive_bytes' "${payload_dir}/server-test-binaries-${artifact_suffix}.manifest.json")"
fi

if [[ "${mode}" =~ ^consumer-(artifact|cache)$ ]]; then
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
  repo_dotnet test "${project}" --no-build --no-restore --configuration "${CONFIGURATION}" --list-tests >/dev/null
  discovery_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))

  phase_ns="$(date +%s%N)"
  test_started_epoch_ms="$(date +%s%3N)"
  result_dir="$(dirname "${metrics}")/trx-${mode}-${identity}"
  result_file="${result_dir}/${mode}-${identity}.trx"
  evidence_file="$(dirname "${metrics}")/trx-evidence-${mode}-${identity}.json"
  mkdir -p "${result_dir}"
  repo_dotnet test "${project}" --no-build --no-restore --configuration "${CONFIGURATION}" \
    --filter "${test_filter}" --logger 'console;verbosity=minimal' \
    --logger "trx;LogFileName=${mode}-${identity}.trx" --results-directory "${result_dir}"
  test_ms=$(( ($(date +%s%N) - phase_ns) / 1000000 ))
  python3 "${SCRIPT_DIR}/summarize-dotnet-trx.py" --input "${result_file}" --output "${evidence_file}"
  result_sha256="$(jq -r '.result_sha256' "${evidence_file}")"
  result_count="$(jq -r '.result_count' "${evidence_file}")"
  result_outcomes="$(jq -c '.outcomes' "${evidence_file}")"
fi

total_ms="$(elapsed_ms)"
sdk="$(repo_dotnet --version)"
jq -nS \
  --arg contract "honua.server-test-transfer-benchmark.v1" \
  --arg mode "${mode}" --arg identity "${identity}" --arg project "${project}" \
  --arg artifact_suffix "${artifact_suffix}" --arg source_sha "${source_sha,,}" --arg dotnet_sdk "${sdk}" \
  --arg filter_sha256 "${filter_sha256}" --arg result_sha256 "${result_sha256}" \
  --arg run_id "${GITHUB_RUN_ID:-local}" --arg run_attempt "${GITHUB_RUN_ATTEMPT:-1}" \
  --argjson restore_ms "${restore_ms}" --argjson build_ms "${build_ms}" \
  --argjson package_ms "${package_ms}" --argjson integrity_unpack_ms "${integrity_unpack_ms}" \
  --argjson integrity_check_ms "${integrity_check_ms}" --argjson unpack_ms "${unpack_ms}" \
  --argjson discovery_ms "${discovery_ms}" --argjson test_ms "${test_ms}" \
  --argjson total_ms "${total_ms}" --argjson archive_bytes "${archive_bytes}" \
  --argjson job_start_epoch_ms "${job_start_epoch_ms}" \
  --argjson test_started_epoch_ms "${test_started_epoch_ms}" \
  --argjson result_count "${result_count}" --argjson result_outcomes "${result_outcomes}" \
  '{contract:$contract,mode:$mode,identity:$identity,project:$project,artifact_suffix:$artifact_suffix,
    source_sha:$source_sha,dotnet_sdk:$dotnet_sdk,filter_sha256:$filter_sha256,
    result_sha256:$result_sha256,result_count:$result_count,result_outcomes:$result_outcomes,
    run_id:$run_id,run_attempt:($run_attempt|tonumber),job_start_epoch_ms:$job_start_epoch_ms,
    test_started_epoch_ms:$test_started_epoch_ms,
    restore_ms:$restore_ms,build_ms:$build_ms,package_ms:$package_ms,
    integrity_unpack_ms:$integrity_unpack_ms,integrity_check_ms:$integrity_check_ms,unpack_ms:$unpack_ms,
    discovery_ms:$discovery_ms,test_ms:$test_ms,
    total_ms:$total_ms,archive_bytes:$archive_bytes}' > "${metrics}"

cat "${metrics}"
