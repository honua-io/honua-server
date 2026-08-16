#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. scripts/ci/lib/python-resolve.sh
python_bin="$(honua_resolve_python)"

pr_gate=.github/workflows/pr-gate.yml
observer=.github/workflows/pr-gate-impact-observe.yml
ci=.github/workflows/ci.yml
train=.github/workflows/merge-train.yml

"${python_bin}" scripts/ci/pr-gate-build-evidence.test.py
"${python_bin}" scripts/ci/validate-server-test-archive.test.py
"${python_bin}" scripts/ci/summarize-dotnet-trx.test.py
bash scripts/ci/fixtures/validate-pr-gate-shallow-ancestry.sh
"${python_bin}" -m py_compile \
  scripts/ci/pr-gate-build-evidence.py \
  scripts/ci/validate-server-test-archive.py
bash -n \
  scripts/ci/package-pr-gate-build-evidence.sh \
  scripts/ci/lib/pr-gate-ancestry.sh \
  scripts/ci/fixtures/validate-pr-gate-shallow-ancestry.sh \
  scripts/ci/restore-server-test-binaries.sh \
  scripts/ci/merge-train/select.sh \
  scripts/ci/merge-train/smart-ci.sh \
  scripts/ci/merge-train/train.sh

# The producer is optional, bounded, and downstream of the already-required
# Release build. Disabling the one repository variable must remove all producer,
# selector, dispatch, and consumer work without changing authoritative gates.
grep -Fq "vars.HONUA_PR_GATE_BUILD_REUSE_SHADOW == 'true'" "${pr_gate}"
grep -Fq 'scripts/ci/package-pr-gate-build-evidence.sh' "${pr_gate}"
grep -Fq 'continue-on-error: true' "${pr_gate}"
grep -Fq 'retention-days: 3' "${pr_gate}"
grep -Fq 'diff --name-only "${base_sha}" "${merge_sha}"' scripts/ci/package-pr-gate-build-evidence.sh
if grep -Fq '${base_sha}...${head_sha}' scripts/ci/package-pr-gate-build-evidence.sh; then
  echo '::error::PR Gate evidence routing requires unbounded base/head history.' >&2
  exit 1
fi
grep -Fq "vars.HONUA_PR_GATE_BUILD_REUSE_SHADOW == 'true'" "${ci}"
grep -Fq "TRAIN_PR_GATE_BUILD_REUSE_SHADOW: \${{ vars.HONUA_PR_GATE_BUILD_REUSE_SHADOW == 'true' && 'true' || 'false' }}" "${train}"
grep -Fq '[[ "${TRAIN_PR_GATE_BUILD_REUSE_SHADOW:-false}" != "true" ]]' scripts/ci/merge-train/select.sh
grep -Fq '[[ "${TRAIN_PR_GATE_BUILD_REUSE_SHADOW:-false}" == "true" ]] || return 0' scripts/ci/merge-train/smart-ci.sh

# The default-branch observer is read-only and validates metadata before it
# emits the small trusted receipt. It never executes the candidate payload.
grep -Fq 'workflows: [PR Gate]' "${observer}"
grep -Fq "github.event.workflow_run.event == 'pull_request'" "${observer}"
grep -Fq 'github.event.workflow_run.head_repository.full_name == github.repository' "${observer}"
grep -Fq 'ref: ${{ github.sha }}' "${observer}"
grep -Fq 'persist-credentials: false' "${observer}"
grep -Fq 'resolveTrustedPullRequestWorkflowRun' "${observer}"
grep -Fq 'python3 policy/scripts/ci/pr-gate-build-evidence.py build' "${observer}"
grep -Fq 'pr-gate-build-evidence-receipt-' "${observer}"
# Cross-workflow artifact downloads default to the observer's own run unless
# all three source coordinates are explicit. Keep the trusted observer bound
# to the canonical PR Gate run that produced the metadata.
grep -Fq 'github-token: ${{ github.token }}' "${observer}"
grep -Fq 'repository: ${{ github.repository }}' "${observer}"
grep -Fq 'run-id: ${{ steps.collect.outputs.run_id }}' "${observer}"
if grep -Eq '^  (actions|checks|contents|pull-requests|statuses): write' "${observer}"; then
  echo '::error::PR Gate build-evidence observer gained write permission.' >&2
  exit 1
fi

# The train handoff is exact and optional. Shadow proof must validate the
# tree-bound receipt, restore safely, and execute with --no-build/--no-restore.
grep -Fq 'pr_gate_run_id:' "${ci}"
grep -Fq 'name: PR Gate Build Reuse Shadow' "${ci}"
grep -Fq 'startsWith(github.ref, '\''refs/heads/train/batch/'\'')' "${ci}"
grep -Fq "run.path === '.github/workflows/pr-gate-impact-observe.yml'" "${ci}"
grep -Fq "new Set(['workflow_run', 'workflow_dispatch'])" "${ci}"
grep -Fq 'trustedObserverEvents.has(run.event)' "${ci}"
grep -Fq "sourceRun.path !== '.github/workflows/pr-gate.yml'" "${ci}"
grep -Fq 'artifact.size_in_bytes === receipt?.artifact?.artifact_bytes' "${ci}"
grep -Fq 'artifact.digest === receipt?.artifact?.artifact_digest' "${ci}"
receipt_download_block="$(sed -n '/name: Download trusted build receipt/,/id: payload-artifact/p' "${ci}")"
grep -Fq 'github-token: ${{ github.token }}' <<<"${receipt_download_block}"
grep -Fq 'repository: ${{ github.repository }}' <<<"${receipt_download_block}"
grep -Fq 'run-id: ${{ steps.receipt-artifact.outputs.observer_run_id }}' <<<"${receipt_download_block}"
payload_download_block="$(sed -n '/name: Download exact PR Gate build payload/,/name: Setup exact .NET toolchain/p' "${ci}")"
grep -Fq 'github-token: ${{ github.token }}' <<<"${payload_download_block}"
grep -Fq 'repository: ${{ github.repository }}' <<<"${payload_download_block}"
grep -Fq 'run-id: ${{ inputs.pr_gate_run_id }}' <<<"${payload_download_block}"
grep -Fq 'python3 scripts/ci/pr-gate-build-evidence.py validate' "${ci}"
grep -Fq 'scripts/ci/restore-server-test-binaries.sh' "${ci}"
grep -Fq 'dotnet test "${project}" --no-build --no-restore --configuration Release' "${ci}"
grep -Fq 'python3 scripts/ci/summarize-dotnet-trx.py' "${ci}"
grep -Fq 'proof_executed_tests:$proof_executed_tests' "${ci}"
grep -Fq '${{ runner.temp }}/pr-gate-build-shadow/proof-*.json' "${ci}"
grep -Fq 'mode:"observe",mutation:"none",promotion_authority:"none"' "${ci}"
grep -Fq 'Authoritative server shards: `independent restore + build (unchanged)`' "${ci}"

ci_gate_block="$(sed -n '/^  ci-gate:/,/^  [[:alnum:]_-][[:alnum:]_-]*:/p' "${ci}")"
if grep -Fq 'pr-gate-build-reuse-shadow' <<<"${ci_gate_block}"; then
  echo '::error::Report-only PR Gate build reuse shadow became authoritative in CI Gate.' >&2
  exit 1
fi

echo 'pr-gate-build-evidence=ok mode=report-only exact-tree fail-closed'
