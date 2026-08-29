#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. scripts/ci/lib/python-resolve.sh
python_bin="$(honua_resolve_python)"
config=.github/server-test-prebuild-observe.json
observer=.github/workflows/server-test-prebuild-observe.yml
benchmark=.github/workflows/server-test-prebuild-benchmark.yml
parity=.github/workflows/server-test-prebuild-parity.yml
ledger=.github/workflows/server-test-prebuild-evidence-ledger.yml
promotion=.github/server-test-prebuild-promotion.json

jq -e '
  .contract_version == 1 and
  .automatic_enable_variable == "HONUA_SERVER_TEST_PREBUILD_SHADOW" and
  .max_projects_per_head == 2 and
  .max_selected_shards == 100 and
  .receipt_ttl_seconds == 86400 and
  (keys | sort) == ["automatic_enable_variable", "contract_version", "max_projects_per_head", "max_selected_shards", "receipt_ttl_seconds"]
' "${config}" >/dev/null

jq -e '
  .contract == "honua.server-test-prebuild-promotion-policy/v1" and
  .receipt_retention_days == 30 and
  .minimum_countable_heads == 20 and
  .minimum_cost_heads == 30 and
  .required_profiles == ["exact-head-shadow:two-shard", "exact-head-shadow:multi-shard"] and
  .minimum_countable_heads_per_profile == 10 and
  .minimum_cost_heads_per_profile == 15 and
  .minimum_runner_minute_savings_percent == 60 and
  .require_p90_test_start_improvement == true and
  .max_wall_clock_regression_percent == 5 and
  (keys | sort) == ["contract", "max_wall_clock_regression_percent", "minimum_cost_heads", "minimum_cost_heads_per_profile", "minimum_countable_heads", "minimum_countable_heads_per_profile", "minimum_runner_minute_savings_percent", "receipt_retention_days", "require_p90_test_start_improvement", "required_profiles"]
' "${promotion}" >/dev/null

"${python_bin}" scripts/ci/plan-server-test-prebuild.test.py
"${python_bin}" scripts/ci/plan-server-test-prebuild-benchmark.test.py
"${python_bin}" scripts/ci/plan-server-test-prebuild-parity.test.py
"${python_bin}" scripts/ci/server-test-prebuild-receipt.test.py
"${python_bin}" scripts/ci/summarize-server-test-prebuild-benchmark.test.py
"${python_bin}" scripts/ci/audit-server-test-prebuild-evidence.test.py
node --test scripts/ci/trusted-pr-workflow-run.test.js
"${python_bin}" -m py_compile \
  scripts/ci/plan-server-test-prebuild.py \
  scripts/ci/plan-server-test-prebuild-benchmark.py \
  scripts/ci/plan-server-test-prebuild-parity.py \
  scripts/ci/server-test-prebuild-receipt.py \
  scripts/ci/summarize-server-test-prebuild-benchmark.py \
  scripts/ci/audit-server-test-prebuild-evidence.py \
  scripts/ci/audit-server-test-prebuild-evidence.test.py

bash -n scripts/ci/benchmark-server-test-transfer.sh
bash scripts/ci/fixtures/validate-benchmark-repo-root.sh
bash -n scripts/ci/try-server-test-prebuild.sh
bash -n scripts/ci/fixtures/validate-try-server-test-prebuild.sh
bash scripts/ci/fixtures/validate-try-server-test-prebuild.sh

grep -Fq '  workflow_run:' "${observer}"
grep -Fq 'workflows: [Review Gate Attestation]' "${observer}"
grep -Fq "vars.HONUA_SERVER_TEST_PREBUILD_SHADOW == 'true'" "${observer}"
grep -Fq "github.event.workflow_run.event == 'pull_request_target'" "${observer}"
grep -Fq "workflowEvent: 'pull_request_target'" "${observer}"
grep -Fq "workflowShaRole: 'pull-request-target-associated'" "${observer}"
grep -Fq "jobName: 'Resolve pull request identity'" "${observer}"
grep -Fq 'SOURCE_RUN_ATTEMPT:' "${observer}"
grep -Fq 'checks: read' "${observer}"
grep -Fq 'persist-credentials: false' "${observer}"
grep -Fq 'server-test-prebuild-receipt.py build' "${observer}"
grep -Fq -- '--config policy/.github/ci-shards.json' "${observer}"
grep -Fq -- '--shards policy/.github/ci-shards.json' "${observer}"
grep -Fq -- '--registry policy/.github/server-test-artifact-projects.json' "${observer}"
grep -Fq 'HONUA_SERVER_TEST_ARTIFACT_REGISTRY: ${{ github.workspace }}/policy/.github/server-test-artifact-projects.json' "${observer}"
grep -Fq 'registry="${policy_root}/.github/server-test-artifact-projects.json"' scripts/ci/try-server-test-prebuild.sh
grep -Fq '  workflow_dispatch:' "${benchmark}"
grep -Fq "run.path !== expectedPath" "${benchmark}"
grep -Fq "run.event === 'workflow_run' && run.head_branch === repository.default_branch" "${benchmark}"
grep -Fq "run.event === 'workflow_dispatch' && run.head_branch === repository.default_branch" "${benchmark}"
grep -Fq 'Bind plan to trusted workflow provenance' "${observer}"
grep -Fq 'source_sha:$source_sha,base_sha:$base_sha' "${observer}"
grep -Fq 'run_head_sha:$source_run_head_sha,run_sha_role:$source_run_sha_role' "${observer}"
grep -Fq 'ref: ${{ github.sha }}' "${observer}"
grep -Fq 'Verify trusted producer provenance without polling' "${benchmark}"
grep -Fq '.observation.base_sha == $base_sha' "${benchmark}"
grep -Fq 'merge-base --is-ancestor' "${benchmark}"
grep -Fq 'Make one non-blocking exact-artifact download attempt' "${benchmark}"
grep -Fq 'try-server-test-prebuild.sh' "${benchmark}"
grep -Fq 'vars.HONUA_SERVER_TEST_PREBUILD_CONSUME' .github/workflows/ci.yml
grep -Fq -- '--prebuild-consume "${PREBUILD_CONSUME}"' .github/workflows/ci.yml
grep -Fq 'HONUA_SERVER_TEST_PREBUILD_CONSUME=\`${CONSUME_SWITCH:-unset}\`' .github/workflows/ci.yml
grep -Fq 'Fallback: \`${REASON:-consumer_disabled}\`' .github/workflows/ci.yml
if grep -Fq 'HONUA_SERVER_TEST_ATTEMPT1_REUSE' .github/workflows/ci.yml; then
  echo '::error::The retired default-on attempt-1 switch is still wired into live shards.' >&2
  exit 1
fi
grep -Fq 'rounded_runner_minutes_including_prebuild' scripts/ci/summarize-server-test-prebuild-benchmark.py
grep -Fq '  workflow_run:' "${parity}"
grep -Fq 'workflows: [PR Gate]' "${parity}"
grep -Fq 'SOURCE_RUN_ATTEMPT:' "${parity}"
grep -Fq 'checks: read' "${parity}"
grep -Fq "jobName: 'PR Gate'" "${parity}"
grep -Fq "item.context === 'Review Gate'" "${parity}"
grep -Fq 'observer-is-not-default-branch-policy' "${parity}"
grep -Fq "workflowPath: '.github/workflows/pr-gate.yml'" "${parity}"
grep -Fq 'resolveTrustedPullRequestWorkflowRun' "${parity}"
grep -Fq 'no-completed-exact-head-observer-artifact' "${parity}"
grep -Fq '.observation.base_sha == $base' "${parity}"
grep -Fq "run.event === 'workflow_dispatch' && run.head_branch === defaultBranch" "${parity}"
grep -Fq 'git -C policy merge-base --is-ancestor' "${parity}"
grep -Fq 'Checkout trusted verifier policy with producer history' "${parity}"
grep -Fq 'Make one non-blocking exact-artifact download attempt' "${parity}"
grep -Fq 'honua.server-test-prebuild-parity-observation/v1' "${parity}"
grep -Fq -- '--registry policy/.github/server-test-artifact-projects.json' "${parity}"
grep -Fq 'measurement_policy_digest:$measurement_policy_digest' "${parity}"
if [[ "$(grep -c 'HONUA_SERVER_TEST_BENCHMARK_REGISTRY: ${{ github.workspace }}/policy/.github/server-test-artifact-projects.json' "${parity}")" -ne 2 ]]; then
  echo '::error::Parity baseline and candidate benchmarks must share the trusted registry.' >&2
  exit 1
fi
grep -Fq 'retention-days: ${{ needs.plan.outputs.receipt_retention_days }}' "${parity}"
grep -Fq 'server-test-prebuild-parity-receipt-${{ needs.plan.outputs.pr }}-${{ needs.plan.outputs.head_sha }}-attempt-${{ github.run_attempt }}' "${parity}"
grep -Fq 'path: evidence/parity-observation.json' "${parity}"
grep -Fq '  schedule:' "${ledger}"
grep -Fq 'if: github.ref_name == github.event.repository.default_branch' "${ledger}"
grep -Fq 'ref: ${{ github.sha }}' "${ledger}"
grep -Fq 'actions: read' "${ledger}"
grep -Fq 'contents: read' "${ledger}"
grep -Fq 'actions/artifacts/${artifact_id}/zip' "${ledger}"
grep -Fq 'audit-server-test-prebuild-evidence.py summarize' "${ledger}"
grep -Fq 'steps.policy.outputs.measurement_policy_digest' "${ledger}"
grep -Fq 'steps.policy.outputs.receipt_created_filter' "${ledger}"
grep -Fq 'continue-on-error: true' "${ledger}"
grep -Fq "steps.ledger.outcome != 'success'" "${ledger}"
if grep -Fq 'wait-for-run-artifact.sh' "${benchmark}"; then
  echo '::error::Cross-workflow candidate must never poll or wait for a prebuild.' >&2
  exit 1
fi
if grep -Eq 'wait-for-run-artifact\.sh|github\.rest\.actions\.|gh workflow run|repository_dispatch' "${parity}"; then
  echo '::error::Parity shadow gained polling, unstable Actions helpers, or dispatch authority.' >&2
  exit 1
fi
if [[ "$(grep -c 'gh run download' "${parity}")" -ne 3 ]]; then
  echo '::error::Parity shadow must make exactly one plan, candidate, and evidence-only metrics download.' >&2
  exit 1
fi
if [[ "$(grep -c 'gh run download' "${benchmark}")" -ne 3 ]]; then
  echo '::error::Prebuild benchmark must have one plan, one candidate, and one evidence-only metrics download.' >&2
  exit 1
fi
for workflow in "${observer}" "${benchmark}" "${parity}" "${ledger}"; do
  if grep -Eq '^  (contents|actions|packages|pull-requests|statuses): write' "${workflow}"; then
    echo "::error::Shadow prebuild workflow gained a write permission: ${workflow}" >&2
    exit 1
  fi
  if grep -Eq 'statuses: write|github\.rest\.repos\.createCommitStatus|merge-train\.yml|gh[[:space:]]+pr[[:space:]]+merge' "${workflow}"; then
    echo "::error::Shadow prebuild workflow gained status or merge authority: ${workflow}" >&2
    exit 1
  fi
done
if grep -Fq '  pull_request_target:' "${observer}"; then
  echo '::error::Prebuild observer must not execute candidate code in pull_request_target.' >&2
  exit 1
fi

echo 'server-test-prebuild=ok mode=shadow-plus-var-gated-consumer single-attempt-fallback'
