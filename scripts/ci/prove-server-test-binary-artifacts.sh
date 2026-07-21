#!/usr/bin/env bash
HONUA_TAB="$(printf '\tX')"; HONUA_TAB="${HONUA_TAB%X}"
# Package and execute representative no-build/no-restore tests for all shard projects.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json"
OUTPUT_DIR="${1:-${RUNNER_TEMP:-/tmp}/honua-server-test-artifact-proof}"
SOURCE_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
CONSUMER_ROOT="${HONUA_SERVER_TEST_ARTIFACT_CONSUMER_ROOT:-$(dirname "${REPO_ROOT}")/.honua-artifact-consumer-$$}"
EMPTY_NUGET="${HONUA_SERVER_TEST_ARTIFACT_EMPTY_NUGET:-$(dirname "${REPO_ROOT}")/.honua-artifact-nuget-$$}"
USE_EXISTING="${HONUA_SERVER_TEST_ARTIFACT_USE_EXISTING:-false}"

cleanup() {
  git -C "${REPO_ROOT}" worktree remove --force "${CONSUMER_ROOT}" >/dev/null 2>&1 || true
  rm -rf "${EMPTY_NUGET}"
}
trap cleanup EXIT
if [[ "${USE_EXISTING}" != "true" ]]; then
  rm -rf "${OUTPUT_DIR}"
fi
rm -rf "${CONSUMER_ROOT}" "${EMPTY_NUGET}"
mkdir -p "${OUTPUT_DIR}" "${EMPTY_NUGET}"

"${SCRIPT_DIR}/validate-server-test-binary-artifacts.sh"
git -C "${REPO_ROOT}" worktree add --detach "${CONSUMER_ROOT}" "${SOURCE_SHA}" >/dev/null

manifests=()
while IFS=${HONUA_TAB} read -r project suffix proof_filter; do
  echo "::group::Package ${project}"
  manifest="${OUTPUT_DIR}/server-test-binaries-${suffix}.manifest.json"
  if [[ "${USE_EXISTING}" == "true" ]]; then
    [[ -f "${manifest}" ]] || { echo "::error::Existing proof manifest is missing: ${manifest}" >&2; exit 1; }
  else
    "${SCRIPT_DIR}/package-server-test-binaries.sh" \
      --project "${project}" --output "${OUTPUT_DIR}" --source-sha "${SOURCE_SHA}"
  fi
  manifests+=("${manifest}")
  echo "::endgroup::"

  rm -rf "${CONSUMER_ROOT}/$(dirname "${project}")/bin" "${CONSUMER_ROOT}/$(dirname "${project}")/obj"
  "${SCRIPT_DIR}/restore-server-test-binaries.sh" \
    --manifest "${manifest}" --destination "${CONSUMER_ROOT}" \
    --project "${project}" --source-sha "${SOURCE_SHA}"

  results_dir="${OUTPUT_DIR}/proof-${suffix}"
  mkdir -p "${results_dir}"
  list_log="${results_dir}/discovery.log"
  NUGET_PACKAGES="${EMPTY_NUGET}" dotnet test "${CONSUMER_ROOT}/${project}" \
    --configuration Release --no-build --no-restore --list-tests > "${list_log}"
  discovered="$(sed -n '/The following Tests are available:/,$p' "${list_log}" | tail -n +2 | sed '/^[[:space:]]*$/d' | wc -l)"
  if (( discovered < 1 )); then
    echo "::error::No tests were discovered from restored artifact ${project}." >&2
    exit 1
  fi

  NUGET_PACKAGES="${EMPTY_NUGET}" dotnet test "${CONSUMER_ROOT}/${project}" \
    --configuration Release --no-build --no-restore \
    --filter "${proof_filter}" \
    --logger "trx;LogFileName=proof.trx" \
    --results-directory "${results_dir}"
  executed="$(grep -o 'executed="[0-9]*"' "${results_dir}/proof.trx" | head -1 | tr -cd '0-9')"
  if [[ -z "${executed}" || "${executed}" == "0" ]]; then
    echo "::error::Representative artifact proof executed no tests for ${project}." >&2
    exit 1
  fi
  jq --argjson discovered "${discovered}" --argjson executed "${executed}" \
    '. + {discovered_tests: $discovered, proof_executed_tests: $executed}' \
    "${manifest}" > "${manifest}.proof"
  mv "${manifest}.proof" "${manifest}"
done < <(jq -r '.projects[] | [.csproj, .artifact_suffix, .proof_filter] | @tsv' "${REGISTRY}")

jq -s '{
  contract: "honua.server-test-binaries.proof.v1",
  project_count: length,
  totals: {
    raw_bytes: (map(.raw_bytes) | add),
    unpacked_bytes: (map(.unpacked_bytes) | add),
    archive_bytes: (map(.archive_bytes) | add),
    package_milliseconds: (map(.package_milliseconds) | add),
    discovered_tests: (map(.discovered_tests) | add),
    proof_executed_tests: (map(.proof_executed_tests) | add)
  },
  projects: .
}' "${manifests[@]}" > "${OUTPUT_DIR}/server-test-binaries-proof-summary.json"

jq . "${OUTPUT_DIR}/server-test-binaries-proof-summary.json"
