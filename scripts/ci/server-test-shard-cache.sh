#!/usr/bin/env bash
# Plan and safely materialize shard-local exact-head server-test caches.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
REPO_ROOT="${HONUA_SERVER_TEST_CACHE_REPO_ROOT:-${DEFAULT_REPO_ROOT}}"
REGISTRY="${HONUA_SERVER_TEST_CACHE_REGISTRY:-${REPO_ROOT}/.github/server-test-artifact-projects.json}"
DEFAULT_PROJECT="tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
CONTRACT_VERSION="1"
OUTPUT_FILE="${GITHUB_OUTPUT:-/dev/stdout}"

emit() {
  printf '%s=%s\n' "$1" "$2" >> "${OUTPUT_FILE}"
}

effective_project() {
  if [[ -n "$1" ]]; then
    printf '%s\n' "$1"
  else
    printf '%s\n' "${DEFAULT_PROJECT}"
  fi
}

usage() {
  cat >&2 <<'EOF'
Usage:
  server-test-shard-cache.sh plan --shard NAME --project CSPROJ --matrix-json JSON --source-sha SHA --runner-os OS --sdk VERSION
  server-test-shard-cache.sh restore --project CSPROJ --source-sha SHA --payload DIR --cache-hit true|false
EOF
}

mode="${1:-}"
[[ -n "${mode}" ]] || { usage; exit 2; }
shift

shard=""
project=""
matrix_json=""
source_sha=""
runner_os=""
sdk=""
payload=""
cache_hit="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --shard) shard="${2:-}"; shift 2 ;;
    --project) project="${2:-}"; shift 2 ;;
    --matrix-json) matrix_json="${2:-}"; shift 2 ;;
    --source-sha) source_sha="${2:-}"; shift 2 ;;
    --runner-os) runner_os="${2:-}"; shift 2 ;;
    --sdk) sdk="${2:-}"; shift 2 ;;
    --payload) payload="${2:-}"; shift 2 ;;
    --cache-hit) cache_hit="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

for command in jq sha256sum; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done

if [[ ! "${source_sha}" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "::error::A full source commit SHA is required." >&2
  exit 2
fi
project="$(effective_project "${project}")"
suffix="$(jq -er --arg project "${project}" '.projects[] | select(.csproj == $project) | .artifact_suffix' "${REGISTRY}")" || {
  echo "::error::Project '${project}' is not registered for server-test artifacts." >&2
  exit 2
}

case "${mode}" in
  plan)
    if [[ -z "${shard}" || -z "${matrix_json}" || -z "${runner_os}" || -z "${sdk}" ]]; then
      usage
      exit 2
    fi
    if [[ ! "${runner_os}" =~ ^[A-Za-z0-9._-]+$ ]] || [[ ! "${sdk}" =~ ^[A-Za-z0-9._+-]+$ ]]; then
      echo "::error::Runner/toolchain cache-key inputs are invalid." >&2
      exit 2
    fi
    jq -e 'type == "array" and length > 0 and all(.[]; (.shard_name | type == "string" and length > 0))' \
      <<<"${matrix_json}" >/dev/null || { echo "::error::Selected shard matrix is invalid." >&2; exit 2; }
    jq -e --arg shard "${shard}" 'any(.[]; .shard_name == $shard)' <<<"${matrix_json}" >/dev/null || {
      echo "::error::Current shard '${shard}' is absent from the selected matrix." >&2
      exit 2
    }
    writer="$(jq -r --arg project "${project}" --arg fallback "${DEFAULT_PROJECT}" '
      [ .[]
        | select((if ((.csproj // "") == "") then $fallback else .csproj end) == $project)
        | .shard_name ]
      | sort | first
    ' <<<"${matrix_json}")"
    [[ -n "${writer}" && "${writer}" != "null" ]] || {
      echo "::error::Selected matrix has no writer for '${project}'." >&2
      exit 2
    }
    registry_hash="$(sha256sum "${REGISTRY}" | cut -d' ' -f1)"
    cache_key="honua-server-test-v${CONTRACT_VERSION}-${runner_os}-${source_sha,,}-${sdk}-${suffix}-${registry_hash}"
    payload_dir="${RUNNER_TEMP:-/tmp}/honua-server-test-cache/${suffix}"

    emit project "${project}"
    emit project_suffix "${suffix}"
    emit cache_key "${cache_key}"
    emit payload_dir "${payload_dir}"
    emit cache_writer "$([[ "${shard}" == "${writer}" ]] && echo true || echo false)"
    emit cache_writer_shard "${writer}"
    ;;

  restore)
    if [[ -z "${payload}" || ! "${cache_hit}" =~ ^(true|false)$ ]]; then
      usage
      exit 2
    fi
    if [[ "${cache_hit}" != "true" ]]; then
      emit restored false
      emit reason exact_cache_miss
      emit integrity_check_ms 0
      emit unpack_ms 0
      exit 0
    fi

    manifest="${payload}/server-test-binaries-${suffix}.manifest.json"
    timing_file="${payload}/restore-timing.json"
    restore_log="${payload}/restore.log"
    set +e
    HONUA_SERVER_TEST_ARTIFACT_TIMING_FILE="${timing_file}" \
      "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
        --manifest "${manifest}" --destination "${REPO_ROOT}" \
        --project "${project}" --source-sha "${source_sha}" >"${restore_log}" 2>&1
    restore_status=$?
    set -e
    if (( restore_status != 0 )); then
      cat "${restore_log}" >&2 || true
      project_dir="${REPO_ROOT}/$(dirname "${project}")"
      rm -rf "${project_dir}/bin/${HONUA_SERVER_TEST_ARTIFACT_CONFIGURATION:-Release}" "${project_dir}/obj"
      emit restored false
      emit reason rejected_cache_evidence
      emit integrity_check_ms 0
      emit unpack_ms 0
      echo "::warning::Exact shard cache evidence was rejected; falling back to restore/build."
      exit 0
    fi

    cat "${restore_log}"
    emit restored true
    emit reason exact_cache_hit
    emit integrity_check_ms "$(jq -r '.integrity_check_ms' "${timing_file}")"
    emit unpack_ms "$(jq -r '.unpack_ms' "${timing_file}")"
    ;;

  *) usage; exit 2 ;;
esac
