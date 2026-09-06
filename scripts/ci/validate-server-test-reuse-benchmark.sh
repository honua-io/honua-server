#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. "${repo_root}/scripts/ci/lib/python-resolve.sh"
python_bin="$(honua_resolve_python)"
config=".github/server-test-reuse-benchmark.json"
workflow=".github/workflows/server-test-reuse-benchmark.yml"

jq -e '
  .contract_version == 1 and
  .decision_thresholds.max_wall_clock_regression_percent == 5 and
  .decision_thresholds.require_p90_test_start_improvement == true and
  .decision_thresholds.require_rounded_runner_minutes_improvement == true and
  .decision_thresholds.require_result_parity == true and
  ([.profiles[].name] | sort) == ["five-hybrid-project", "two-mixed-project", "two-same-project"] and
  ([.profiles[].shards | length] | sort) == [2, 2, 5] and
  (.shards | length == 5) and
  ([.shards[].name] | length) == ([.shards[].name] | unique | length) and
  all(.shards[]; (.name | test("^[a-z0-9-]+$")) and (.project | endswith(".csproj")))
' "${config}" >/dev/null

"${python_bin}" scripts/ci/plan-server-test-reuse-benchmark.test.py
"${python_bin}" scripts/ci/summarize-dotnet-trx.test.py
"${python_bin}" scripts/ci/summarize-server-test-reuse-benchmark.test.py
"${python_bin}" scripts/ci/server-test-reuse-receipt.test.py

fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-reuse-validator.XXXXXX")"
cleanup() { rm -rf "${fixture}"; }
trap cleanup EXIT
"${python_bin}" scripts/ci/plan-server-test-reuse-benchmark.py \
  --config "${config}" --shards .github/ci-shards.json \
  --registry .github/server-test-artifact-projects.json --mode core \
  --output "${fixture}/core.json" >/dev/null
"${python_bin}" scripts/ci/plan-server-test-reuse-benchmark.py \
  --config "${config}" --shards .github/ci-shards.json \
  --registry .github/server-test-artifact-projects.json --mode observed-full \
  --output "${fixture}/full.json" >/dev/null
jq -e '
  (.baseline | length == 5) and (.producers | length == 1) and
  (.reused_consumers | length == 2) and .producers[0].identity == "server"
' "${fixture}/core.json" >/dev/null
expected_baseline="$(jq -r '.shards | length' .github/ci-shards.json)"
# Every shard is either one of the five producers or a reused consumer, so the
# consumer count is derived rather than pinned: a capacity split that adds a
# shard must not need a hand-edit here to stay honest.
jq -e --argjson expected_baseline "${expected_baseline}" '
  ((.baseline | length) == $expected_baseline) and (.producers | length == 5) and
  ((.reused_consumers | length) == ($expected_baseline - (.producers | length))) and
  ([.producers[].identity] | sort) == ["geoservices", "odata", "ogc-api", "ogc-classic", "server"]
' "${fixture}/full.json" >/dev/null

grep -Fq '  workflow_dispatch:' "${workflow}"
grep -Fq 'options: [core, observed-full]' "${workflow}"
grep -Fq 'Deliberately do not depend on producer' "${workflow}"
grep -Fq 'scripts/ci/wait-for-run-artifact.sh' "${workflow}"
grep -Fq 'scripts/ci/server-test-reuse-receipt.py validate' "${workflow}"
grep -Fq 'permissions:' "${workflow}"
grep -Fq '  actions: read' "${workflow}"
grep -Fq '  contents: read' "${workflow}"
if grep -Eq '^  (push|pull_request|schedule|pull_request_target):' "${workflow}"; then
  echo "::error::Reuse benchmark must remain manual and non-authoritative." >&2
  exit 1
fi
if grep -Eq '^  (actions|contents|pull-requests|statuses): write' "${workflow}"; then
  echo "::error::Reuse benchmark gained a write permission." >&2
  exit 1
fi

bash -n scripts/ci/benchmark-server-test-transfer.sh
bash -n scripts/ci/wait-for-run-artifact.sh
bash -n scripts/ci/fixtures/validate-wait-for-run-artifact.sh
bash scripts/ci/fixtures/validate-wait-for-run-artifact.sh
"${python_bin}" -m py_compile \
  scripts/ci/plan-server-test-reuse-benchmark.py \
  scripts/ci/server-test-reuse-receipt.py \
  scripts/ci/summarize-dotnet-trx.py \
  scripts/ci/summarize-server-test-reuse-benchmark.py

echo "server-test-reuse-benchmark=ok mode=shadow"
