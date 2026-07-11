#!/usr/bin/env bash
# Deterministic fixture validation for shard-local exact-head cache routing.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
HELPER="${SCRIPT_DIR}/server-test-shard-cache.sh"
SERVER_PROJECT="tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
ODATA_PROJECT="tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj"
SOURCE_SHA="0123456789abcdef0123456789abcdef01234567"

fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-shard-cache-fixture.XXXXXX")"
cleanup() { rm -rf "${fixture}"; }
trap cleanup EXIT

same_project_matrix="$(jq -nc --arg project "${SERVER_PROJECT}" '[
  {shard_name:"z-second",csproj:$project},
  {shard_name:"a-writer",csproj:$project}
]')"
mixed_matrix="$(jq -nc --arg server "${SERVER_PROJECT}" --arg odata "${ODATA_PROJECT}" '[
  {shard_name:"server",csproj:$server},
  {shard_name:"odata",csproj:$odata}
]')"

plan() {
  local output="${fixture}/$1.out"
  shift
  GITHUB_OUTPUT="${output}" "${HELPER}" plan "$@"
  cat "${output}"
}

writer="$(plan writer --shard a-writer --project "${SERVER_PROJECT}" --matrix-json "${same_project_matrix}" \
  --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301)"
second="$(plan second --shard z-second --project "${SERVER_PROJECT}" --matrix-json "${same_project_matrix}" \
  --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301)"
server="$(plan server --shard server --project "${SERVER_PROJECT}" --matrix-json "${mixed_matrix}" \
  --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301)"
odata="$(plan odata --shard odata --project "${ODATA_PROJECT}" --matrix-json "${mixed_matrix}" \
  --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301)"

grep -qx 'cache_writer=true' <<<"${writer}"
grep -qx 'cache_writer=false' <<<"${second}"
grep -qx 'cache_writer=true' <<<"${server}"
grep -qx 'cache_writer=true' <<<"${odata}"
grep -qx 'cache_writer_shard=a-writer' <<<"${writer}"
writer_key="$(sed -n 's/^cache_key=//p' <<<"${writer}")"
second_key="$(sed -n 's/^cache_key=//p' <<<"${second}")"
[[ "${writer_key}" == "${second_key}" ]]
[[ "${writer_key}" == honua-server-test-v1-Linux-${SOURCE_SHA}-10.0.301-server-* ]]
[[ "${writer_key}" != *restore-keys* ]]
odata_key="$(sed -n 's/^cache_key=//p' <<<"${odata}")"
[[ "${odata_key}" != "${writer_key}" ]]
[[ "${odata_key}" == *-odata-* ]]

fallback_matrix='[{"shard_name":"fallback","csproj":""}]'
fallback="$(plan fallback --shard fallback --project '' --matrix-json "${fallback_matrix}" \
  --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301)"
grep -qx "project=${SERVER_PROJECT}" <<<"${fallback}"
grep -qx 'cache_writer=true' <<<"${fallback}"

miss_output="${fixture}/miss.out"
GITHUB_OUTPUT="${miss_output}" "${HELPER}" restore --project "${SERVER_PROJECT}" \
  --source-sha "${SOURCE_SHA}" --payload "${fixture}/missing" --cache-hit false
grep -qx 'restored=false' "${miss_output}"
grep -qx 'reason=exact_cache_miss' "${miss_output}"

mkdir -p "${fixture}/repo/tests/dotnet/Honua.Server.Tests/bin/Release" \
  "${fixture}/repo/tests/dotnet/Honua.Server.Tests/obj"
rejected_output="${fixture}/rejected.out"
HONUA_SERVER_TEST_CACHE_REPO_ROOT="${fixture}/repo" \
HONUA_SERVER_TEST_CACHE_REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json" \
GITHUB_OUTPUT="${rejected_output}" "${HELPER}" restore --project "${SERVER_PROJECT}" \
  --source-sha "${SOURCE_SHA}" --payload "${fixture}/missing" --cache-hit true >/dev/null 2>&1
grep -qx 'restored=false' "${rejected_output}"
grep -qx 'reason=rejected_cache_evidence' "${rejected_output}"
[[ ! -e "${fixture}/repo/tests/dotnet/Honua.Server.Tests/bin/Release" ]]
[[ ! -e "${fixture}/repo/tests/dotnet/Honua.Server.Tests/obj" ]]

if GITHUB_OUTPUT="${fixture}/invalid.out" "${HELPER}" plan --shard absent --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  >/dev/null 2>&1; then
  echo "::error::Plan accepted a shard outside the selected matrix." >&2
  exit 1
fi

workflow="${REPO_ROOT}/.github/workflows/ci.yml"
proof_workflow="${REPO_ROOT}/.github/workflows/server-test-shard-cache-proof.yml"
grep -Fq 'name: Server Tests (${{ matrix.shard_name }})' "${workflow}"
grep -q 'actions/cache/restore@v5' "${workflow}"
grep -q 'actions/cache/save@v5' "${workflow}"
grep -q 'github.run_attempt > 1' "${workflow}"
grep -Fq "steps.shard-cache-materialize.outputs.restored != 'true'" "${workflow}"
grep -Fq "steps.shard-cache-plan.outputs.cache_writer == 'true' || github.run_attempt > 1" "${workflow}"
if grep -A8 'Restore exact-head shard cache' "${workflow}" | grep -q 'restore-keys:'; then
  echo "::error::Shard cache restore must not use fallback keys." >&2
  exit 1
fi
if grep -A10 '^  server-tests:' "${workflow}" | grep -qE 'producer|needs:.*build'; then
  echo "::error::Server-test shards must remain independent of shared producers/build jobs." >&2
  exit 1
fi
grep -Fq 'ci/2735-shard-local-rerun-cache' "${proof_workflow}"
grep -Fq "matrix.identity != 'a-writer' && github.run_attempt == 1" "${proof_workflow}"
if grep -qE 'pull_request:|schedule:' "${proof_workflow}"; then
  echo "::error::Hosted cache proof must remain opt-in and outside production triggers." >&2
  exit 1
fi

echo "Server-test shard cache validation passed."
