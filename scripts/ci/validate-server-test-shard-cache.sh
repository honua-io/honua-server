#!/usr/bin/env bash
# Deterministic fixture validation for shard-local exact-head cache routing.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# CR-safe jq: strip the CRLF the Windows jq binary emits in text mode (no-op on Linux).
source "${SCRIPT_DIR}/lib/jq-cr-safe.sh"
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

# Attempt-1 opportunistic reuse routing (#3213).
attempt1_default="$(plan attempt1-default --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse true)"
grep -qx 'restore_mode=opportunistic' <<<"${attempt1_default}"
grep -qx 'restore_enabled=true' <<<"${attempt1_default}"

attempt1_writer="$(plan attempt1-writer --shard a-writer --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse true)"
grep -qx 'restore_enabled=true' <<<"${attempt1_writer}"
grep -qx 'cache_writer=true' <<<"${attempt1_writer}"

attempt1_off="$(plan attempt1-off --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse false)"
grep -qx 'restore_mode=disabled' <<<"${attempt1_off}"
grep -qx 'restore_enabled=false' <<<"${attempt1_off}"

# A malformed switch must degrade to the build-locally path, never to a hard error.
attempt1_garbage="$(plan attempt1-garbage --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse 'TRUE')"
grep -qx 'restore_mode=disabled' <<<"${attempt1_garbage}"

# A malformed attempt counter must be read as attempt 1, not as a rerun.
attempt_garbage="$(plan attempt-garbage --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 'x9' --attempt1-reuse false)"
grep -qx 'restore_mode=disabled' <<<"${attempt_garbage}"

# Reruns keep reading regardless of the attempt-1 switch.
rerun_off="$(plan rerun-off --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 2 --attempt1-reuse false)"
grep -qx 'restore_mode=rerun' <<<"${rerun_off}"
grep -qx 'restore_enabled=true' <<<"${rerun_off}"

# Defaults (no flags) must stay on the pre-#3213 rerun-only behaviour so a caller
# that never opts in cannot be silently changed.
legacy_default="$(plan legacy-default --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301)"
grep -qx 'restore_mode=disabled' <<<"${legacy_default}"

# The attempt-1 read must not change WHO writes: exactly one writer per project.
[[ "$(sed -n 's/^cache_writer=//p' <<<"${attempt1_default}")" == "false" ]]
[[ "$(sed -n 's/^cache_key=//p' <<<"${attempt1_default}")" == "${writer_key}" ]]

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
# Major-version-agnostic on purpose: this proves the shard cache still uses the
# restore/save actions; a Dependabot major bump must not fail router validation.
grep -Eq 'actions/cache/restore@v[0-9]+' "${workflow}"
grep -Eq 'actions/cache/save@v[0-9]+' "${workflow}"
grep -q 'github.run_attempt > 1' "${workflow}"
grep -Fq "steps.shard-cache-materialize.outputs.restored != 'true'" "${workflow}"
grep -Fq "steps.shard-cache-plan.outputs.cache_writer == 'true' || github.run_attempt > 1" "${workflow}"
# Attempt-1 opportunistic reads are routed by the plan step, gated by a
# repository-variable kill switch, and never fail a shard on a cache-service
# error. Exactly one shard per project still writes on attempt 1.
grep -Fq "steps.shard-cache-plan.outputs.restore_enabled == 'true'" "${workflow}"
grep -Fq "vars.HONUA_SERVER_TEST_ATTEMPT1_REUSE == 'false'" "${workflow}"
if ! awk '/name: Restore exact-head shard cache/,/fail-on-cache-miss/' "${workflow}" | grep -q 'continue-on-error: true'; then
  echo "::error::Attempt-1 shard cache reads must be fail-open (continue-on-error)." >&2
  exit 1
fi
if grep -q 'if: github.run_attempt > 1' "${workflow}"; then
  echo "::error::Shard cache reads must be routed by the plan step, not by run_attempt directly." >&2
  exit 1
fi
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
grep -Fq 'if .=="" then 0 else tonumber end' "${proof_workflow}"
if grep -qE 'pull_request:|schedule:' "${proof_workflow}"; then
  echo "::error::Hosted cache proof must remain opt-in and outside production triggers." >&2
  exit 1
fi

echo "Server-test shard cache validation passed."
