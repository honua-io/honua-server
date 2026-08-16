#!/usr/bin/env bash
# Attempt one exact cross-workflow prebuild restore, then fall back locally.

set -euo pipefail

source_root=""
policy_root=""
payload=""
repository=""
pull_request=""
source_sha=""
policy_sha=""
producer_policy_sha=""
project=""
producer_run_id=""
producer_run_attempt=""
runner_image=""
github_output=""
metrics=""
configuration="${HONUA_TEST_CONFIGURATION:-Release}"

usage() {
  echo "Usage: $0 --source-root <dir> --policy-root <dir> --payload <dir> --repository <owner/repo> --pull-request <n> --source-sha <sha> --policy-sha <current-sha> --producer-policy-sha <producer-sha> --project <relative.csproj> --producer-run-id <id> --producer-run-attempt <n> --runner-image <image> --github-output <file> --metrics <file>" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --source-root) source_root="${2:-}"; shift 2 ;;
    --policy-root) policy_root="${2:-}"; shift 2 ;;
    --payload) payload="${2:-}"; shift 2 ;;
    --repository) repository="${2:-}"; shift 2 ;;
    --pull-request) pull_request="${2:-}"; shift 2 ;;
    --source-sha) source_sha="${2:-}"; shift 2 ;;
    --policy-sha) policy_sha="${2:-}"; shift 2 ;;
    --producer-policy-sha) producer_policy_sha="${2:-}"; shift 2 ;;
    --project) project="${2:-}"; shift 2 ;;
    --producer-run-id) producer_run_id="${2:-}"; shift 2 ;;
    --producer-run-attempt) producer_run_attempt="${2:-}"; shift 2 ;;
    --runner-image) runner_image="${2:-}"; shift 2 ;;
    --github-output) github_output="${2:-}"; shift 2 ;;
    --metrics) metrics="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ -z "${source_root}" || -z "${policy_root}" || -z "${payload}" ||
      -z "${repository}" || -z "${pull_request}" || -z "${source_sha}" ||
      -z "${policy_sha}" || -z "${producer_policy_sha}" || -z "${project}" ||
      -z "${producer_run_id}" ||
      -z "${producer_run_attempt}" || -z "${runner_image}" ||
      -z "${github_output}" || -z "${metrics}" ]]; then
  usage
  exit 2
fi
if [[ ! "${source_sha}" =~ ^[0-9a-fA-F]{40}$ || ! "${policy_sha}" =~ ^[0-9a-fA-F]{40}$ ||
      ! "${producer_policy_sha}" =~ ^[0-9a-fA-F]{40}$ ||
      ! "${pull_request}" =~ ^[1-9][0-9]*$ || ! "${producer_run_id}" =~ ^[1-9][0-9]*$ ||
      ! "${producer_run_attempt}" =~ ^[1-9][0-9]*$ ]]; then
  echo "::error::Prebuild identity arguments are invalid." >&2
  exit 2
fi
for command in date dotnet jq python3; do
  command -v "${command}" >/dev/null || { echo "::error::Required command '${command}' is unavailable." >&2; exit 2; }
done

source_root="$(cd "${source_root}" && pwd)"
policy_root="$(cd "${policy_root}" && pwd)"
registry="${policy_root}/.github/server-test-artifact-projects.json"
artifact_suffix="$(jq -er --arg project "${project}" '.projects[] | select(.csproj == $project) | .artifact_suffix' "${registry}")" || {
  echo "::error::Project '${project}' is not registered for prebuild reuse." >&2
  exit 2
}
project_path="${source_root}/${project}"
project_dir="$(dirname "${project_path}")"
case "${project_dir}" in
  "${source_root}"/*) ;;
  *) echo "::error::Project path escapes the source checkout." >&2; exit 2 ;;
esac
[[ -f "${project_path}" ]] || { echo "::error::Project '${project}' is missing." >&2; exit 2; }

mkdir -p "$(dirname "${github_output}")" "$(dirname "${metrics}")"
started_ns="$(date +%s%N)"
mode="prebuild"
reason="accepted"

clean_partial_output() {
  # Targets are derived from a registry-validated project under source_root.
  rm -rf -- "${project_dir}/bin/${configuration}" "${project_dir}/obj"
}

fallback_build() {
  mode="local-fallback"
  reason="$1"
  clean_partial_output
  (
    cd "${source_root}"
    scripts/ci/dotnet-restore-retry.sh "${project}"
    dotnet build "${project}" --no-restore --configuration "${configuration}" /p:TreatWarningsAsErrors=true
  )
}

manifest="${payload}/server-test-binaries-${artifact_suffix}.manifest.json"
archive="${payload}/server-test-binaries-${artifact_suffix}.tar.gz"
receipt="${payload}/server-test-prebuild-${artifact_suffix}.receipt.json"

if [[ ! -f "${manifest}" || ! -f "${archive}" || ! -f "${receipt}" ]]; then
  fallback_build "artifact-unavailable"
else
  sdk="$(dotnet --version)"
  if ! python3 "${policy_root}/scripts/ci/server-test-prebuild-receipt.py" validate \
      --source-root "${source_root}" --policy-root "${policy_root}" \
      --repository "${repository}" --pull-request "${pull_request}" \
      --source-sha "${source_sha}" --policy-sha "${policy_sha}" \
      --producer-policy-sha "${producer_policy_sha}" \
      --project "${project}" --configuration "${configuration}" \
      --dotnet-sdk "${sdk}" --runner-os "${RUNNER_OS:-Linux}" \
      --runner-arch "${RUNNER_ARCH:-X64}" --runner-image "${runner_image}" \
      --producer-run-id "${producer_run_id}" \
      --producer-run-attempt "${producer_run_attempt}" \
      --manifest "${manifest}" --archive "${archive}" --receipt "${receipt}"; then
    fallback_build "receipt-rejected"
  else
    clean_partial_output
    if ! HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK="${sdk}" \
        "${policy_root}/scripts/ci/restore-server-test-binaries.sh" \
          --manifest "${manifest}" --destination "${source_root}" \
          --project "${project}" --source-sha "${source_sha}"; then
      fallback_build "restore-rejected"
    fi
  fi
fi

elapsed_ms=$(( ($(date +%s%N) - started_ns) / 1000000 ))
{
  echo "mode=${mode}"
  echo "reason=${reason}"
  echo "elapsed_ms=${elapsed_ms}"
} >> "${github_output}"
jq -nS \
  --arg contract "honua.server-test-prebuild-consumer/v1" \
  --arg mode "${mode}" --arg reason "${reason}" --arg project "${project}" \
  --arg source_sha "${source_sha,,}" --argjson elapsed_ms "${elapsed_ms}" \
  '{contract:$contract,mode:$mode,reason:$reason,project:$project,source_sha:$source_sha,elapsed_ms:$elapsed_ms}' \
  > "${metrics}"
echo "prebuild=${mode} reason=${reason} elapsed_ms=${elapsed_ms}"
