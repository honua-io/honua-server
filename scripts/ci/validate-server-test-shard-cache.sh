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
RUN_ID="4242424242"

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
  # GITHUB_RUN_ID is deliberately cleared so every fixture states its own run
  # identity (or proves the no-run-identity path) explicitly.
  GITHUB_RUN_ID='' GITHUB_OUTPUT="${output}" "${HELPER}" plan --run-id "${RUN_ID}" "$@"
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

# The writer is the first shard in MATRIX order, not lexicographic order:
# `z-second` is declared first in same_project_matrix and therefore wins.
grep -qx 'cache_writer=false' <<<"${writer}"
grep -qx 'cache_writer=true' <<<"${second}"
grep -qx 'cache_writer=true' <<<"${server}"
grep -qx 'cache_writer=true' <<<"${odata}"
grep -qx 'cache_writer_shard=z-second' <<<"${writer}"
grep -qx 'cache_writer_shard=z-second' <<<"${second}"
writer_key="$(sed -n 's/^cache_key=//p' <<<"${writer}")"
second_key="$(sed -n 's/^cache_key=//p' <<<"${second}")"
[[ "${writer_key}" == "${second_key}" ]]
[[ "${writer_key}" == honua-server-test-v1-Linux-${SOURCE_SHA}-10.0.301-server-run${RUN_ID}-* ]]
[[ "${writer_key}" != *restore-keys* ]]
odata_key="$(sed -n 's/^cache_key=//p' <<<"${odata}")"
[[ "${odata_key}" != "${writer_key}" ]]
[[ "${odata_key}" == *-odata-* ]]

# Attempt-1 opportunistic reuse routing (#3213).
#
# One switch rule everywhere: reuse is ON unless the raw value is exactly
# `false`. The workflow forwards the repository variable verbatim, so every
# value the workflow can produce is exercised here.
attempt1_unset="$(plan attempt1-unset --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1)"
grep -qx 'restore_mode=opportunistic' <<<"${attempt1_unset}"
grep -qx 'restore_enabled=true' <<<"${attempt1_unset}"
grep -qx 'attempt1_switch=unset' <<<"${attempt1_unset}"

attempt1_empty="$(plan attempt1-empty --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse '')"
grep -qx 'restore_mode=opportunistic' <<<"${attempt1_empty}"
grep -qx 'attempt1_switch=unset' <<<"${attempt1_empty}"

attempt1_true="$(plan attempt1-true --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse true)"
grep -qx 'restore_mode=opportunistic' <<<"${attempt1_true}"
grep -qx 'attempt1_switch=true' <<<"${attempt1_true}"

# Only the exact string `false` disables. Every other spelling stays ON, which
# is what the workflow can actually forward.
attempt1_off="$(plan attempt1-off --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse false)"
grep -qx 'restore_mode=disabled' <<<"${attempt1_off}"
grep -qx 'restore_enabled=false' <<<"${attempt1_off}"
grep -qx 'attempt1_switch=false' <<<"${attempt1_off}"

attempt1_mixedcase="$(plan attempt1-mixedcase --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse 'FALSE')"
grep -qx 'restore_mode=opportunistic' <<<"${attempt1_mixedcase}"
grep -qx 'attempt1_switch=FALSE' <<<"${attempt1_mixedcase}"

# A switch value must never be able to forge additional key=value output lines.
attempt1_injection="$(plan attempt1-injection --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1 --attempt1-reuse "$(printf 'x\nrestore_enabled=forged')")"
grep -qx 'attempt1_switch=unprintable' <<<"${attempt1_injection}"
[[ "$(grep -c '^restore_enabled=' <<<"${attempt1_injection}")" == 1 ]]
grep -qx 'restore_enabled=true' <<<"${attempt1_injection}"

# A malformed attempt counter must be read as attempt 1, not as a rerun: a
# non-writer must not inherit the rerun publishing right from a bad counter.
attempt_garbage="$(plan attempt-garbage --shard a-writer --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 'x9')"
grep -qx 'restore_mode=opportunistic' <<<"${attempt_garbage}"
grep -qx 'package_enabled=false' <<<"${attempt_garbage}"

# Reruns keep reading even when the attempt-1 switch is off.
rerun_off="$(plan rerun-off --shard writer-check --project "${SERVER_PROJECT}" \
  --matrix-json "$(jq -nc --arg p "${SERVER_PROJECT}" '[{shard_name:"lead",csproj:$p},{shard_name:"writer-check",csproj:$p}]')" \
  --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 2 --attempt1-reuse false)"
grep -qx 'restore_mode=rerun' <<<"${rerun_off}"
grep -qx 'restore_enabled=true' <<<"${rerun_off}"
# On a rerun a non-writer that rebuilt may publish for the remaining attempts.
grep -qx 'cache_writer=false' <<<"${rerun_off}"
grep -qx 'package_enabled=true' <<<"${rerun_off}"

# Package/save gating on attempt 1: writer only.
grep -qx 'package_enabled=true' <<<"${second}"
grep -qx 'package_enabled=false' <<<"${writer}"

# --- Run-scoped key (same-SHA TTL poisoning) --------------------------------
# Two runs at the SAME commit must not share a key: an aged payload would
# otherwise be downloaded and rejected by every shard of every later run of
# that head, forever, while the writer could never re-save the immutable key.
other_run="$(GITHUB_RUN_ID='' GITHUB_OUTPUT="${fixture}/other-run.out" "${HELPER}" plan \
  --run-id 9999999999 --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1; cat "${fixture}/other-run.out")"
[[ "$(sed -n 's/^cache_key=//p' <<<"${other_run}")" != "${writer_key}" ]]
# ...but every attempt of the SAME run shares it, so sibling reads and the
# #2735 failed-rerun read both still work.
same_run_rerun="$(plan same-run-rerun --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 3)"
[[ "$(sed -n 's/^cache_key=//p' <<<"${same_run_rerun}")" == "${second_key}" ]]

# No trustworthy run identity: namespace the key away from hosted entries and
# never read or publish.
no_run="$(GITHUB_RUN_ID='' GITHUB_OUTPUT="${fixture}/no-run.out" "${HELPER}" plan \
  --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301 \
  --run-attempt 1; cat "${fixture}/no-run.out")"
grep -qx 'restore_mode=disabled' <<<"${no_run}"
grep -qx 'package_enabled=false' <<<"${no_run}"
[[ "$(sed -n 's/^cache_key=//p' <<<"${no_run}")" == *-runlocal-* ]]

# GITHUB_RUN_ID is the default source of the run identity for callers that do
# not pass --run-id (the hosted proof lane).
env_run="$(GITHUB_RUN_ID="${RUN_ID}" GITHUB_OUTPUT="${fixture}/env-run.out" "${HELPER}" plan \
  --shard z-second --project "${SERVER_PROJECT}" \
  --matrix-json "${same_project_matrix}" --source-sha "${SOURCE_SHA}" --runner-os Linux --sdk 10.0.301; \
  cat "${fixture}/env-run.out")"
[[ "$(sed -n 's/^cache_key=//p' <<<"${env_run}")" == "${second_key}" ]]

# Attempt-1 reads must not change WHO writes, or WHAT key is used.
[[ "$(sed -n 's/^cache_writer=//p' <<<"${attempt1_unset}")" == "true" ]]
[[ "$(sed -n 's/^cache_key=//p' <<<"${attempt1_unset}")" == "${writer_key}" ]]

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
grep -Fq "steps.shard-cache-materialize.outputs.restored != 'true'" "${workflow}"

# Structural assertions on the shard-cache steps. Parsing beats grepping here:
# the previous line-window greps silently stopped covering the `with:` block as
# soon as comments were added above it, and a whole-file grep for an attempt
# expression would fire on unrelated jobs.
python3 - "${workflow}" <<'PYEOF'
import sys, yaml

workflow = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
steps = workflow["jobs"]["server-tests"]["steps"]
by_id = {s["id"]: s for s in steps if isinstance(s, dict) and "id" in s}

def fail(message):
    print(f"::error::{message}", file=sys.stderr)
    sys.exit(1)

for required in ("shard-cache-plan", "shard-cache-download", "shard-cache-materialize",
                 "shard-cache-package", "shard-cache-save", "shard-cache-build"):
    if required not in by_id:
        fail(f"Server-test shard is missing the '{required}' step.")

plan_run = by_id["shard-cache-plan"]["run"]
for flag in ("--run-id", "--run-attempt", "--attempt1-reuse"):
    if flag not in plan_run:
        fail(f"The shard cache plan step must pass {flag}.")
if "vars.HONUA_SERVER_TEST_ATTEMPT1_REUSE" not in yaml.dump(by_id["shard-cache-plan"]):
    fail("The attempt-1 reuse kill switch must be forwarded from the repository variable.")
if "vars.HONUA_SERVER_TEST_ATTEMPT1_REUSE ==" in yaml.dump(by_id["shard-cache-plan"]):
    fail("Forward the raw switch value; the on/off rule belongs in the plan script.")

# Reads are routed by the plan step only. No step may re-derive the decision
# from github.run_attempt, which is what put the attempt rule in two places.
read_steps = ("shard-cache-restore-start", "shard-cache-download",
              "shard-cache-restore-elapsed", "shard-cache-package")
for step_id in read_steps:
    condition = str(by_id.get(step_id, {}).get("if", ""))
    if "run_attempt" in condition:
        fail(f"Step '{step_id}' must not gate on github.run_attempt; use the plan outputs.")
for step_id in ("shard-cache-restore-start", "shard-cache-download", "shard-cache-restore-elapsed"):
    if "restore_enabled" not in str(by_id[step_id].get("if", "")):
        fail(f"Step '{step_id}' must be gated on the plan step's restore_enabled output.")
if "package_enabled" not in str(by_id["shard-cache-package"].get("if", "")):
    fail("The package step must be gated on the plan step's package_enabled output.")

download = by_id["shard-cache-download"]
if download.get("continue-on-error") is not True:
    fail("Attempt-1 shard cache reads must be fail-open (continue-on-error: true).")
with_block = download.get("with", {})
if with_block.get("fail-on-cache-miss") not in (False, "false"):
    fail("Shard cache reads must tolerate a miss.")
if "restore-keys" in with_block:
    fail("Shard cache restore must not use fallback keys.")
if "${{ github.run_id }}" not in plan_run:
    fail("The cache key must be scoped to the workflow run id.")

# The step summary must distinguish a real local-build fallback from a failed
# or cancelled build, and must not attribute the restored writer's packaging
# time to a consuming shard.
summary = next(s for s in steps if s.get("name") == "Report shard cache decision")
body = summary["run"]
for marker in ("BUILD_OUTCOME", "PACKAGE_OUTCOME", "SAVE_OUTCOME", "ATTEMPT1_SWITCH"):
    if marker not in body or marker not in yaml.dump(summary.get("env", {})):
        fail(f"The shard cache summary must report {marker}.")
print("Shard-cache workflow wiring assertions passed.")
PYEOF

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
