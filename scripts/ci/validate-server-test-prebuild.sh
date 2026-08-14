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

jq -e '
  .contract_version == 1 and
  .automatic_enable_variable == "HONUA_SERVER_TEST_PREBUILD_SHADOW" and
  .max_projects_per_head == 2 and
  .max_selected_shards == 100 and
  .receipt_ttl_seconds == 86400 and
  (keys | sort) == ["automatic_enable_variable", "contract_version", "max_projects_per_head", "max_selected_shards", "receipt_ttl_seconds"]
' "${config}" >/dev/null

"${python_bin}" scripts/ci/plan-server-test-prebuild.test.py
"${python_bin}" scripts/ci/plan-server-test-prebuild-benchmark.test.py
"${python_bin}" scripts/ci/plan-server-test-prebuild-parity.test.py
"${python_bin}" scripts/ci/server-test-prebuild-receipt.test.py
"${python_bin}" scripts/ci/summarize-server-test-prebuild-benchmark.test.py
"${python_bin}" -m py_compile \
  scripts/ci/plan-server-test-prebuild.py \
  scripts/ci/plan-server-test-prebuild-benchmark.py \
  scripts/ci/plan-server-test-prebuild-parity.py \
  scripts/ci/server-test-prebuild-receipt.py \
  scripts/ci/summarize-server-test-prebuild-benchmark.py

bash -n scripts/ci/benchmark-server-test-transfer.sh
bash scripts/ci/fixtures/validate-benchmark-repo-root.sh
bash -n scripts/ci/try-server-test-prebuild.sh
bash -n scripts/ci/fixtures/validate-try-server-test-prebuild.sh
bash scripts/ci/fixtures/validate-try-server-test-prebuild.sh

grep -Fq '  pull_request_target:' "${observer}"
grep -Fq "vars.HONUA_SERVER_TEST_PREBUILD_SHADOW == 'true'" "${observer}"
grep -Fq "github.event.pull_request.head.repo.full_name == github.repository" "${observer}"
grep -Fq "github.event.pull_request.draft == false" "${observer}"
grep -Fq 'persist-credentials: false' "${observer}"
grep -Fq 'server-test-prebuild-receipt.py build' "${observer}"
grep -Fq '  workflow_dispatch:' "${benchmark}"
grep -Fq "run.path !== expectedPath" "${benchmark}"
grep -Fq "run.event === 'pull_request_target' && targetPullRequest" "${benchmark}"
grep -Fq "run.event === 'workflow_dispatch' && run.head_branch === repository.default_branch" "${benchmark}"
grep -Fq 'Bind plan to trusted workflow provenance' "${observer}"
grep -Fq 'ref: ${{ github.workflow_sha }}' "${observer}"
grep -Fq 'Verify trusted producer provenance without polling' "${benchmark}"
grep -Fq 'merge-base --is-ancestor' "${benchmark}"
grep -Fq 'Make one non-blocking exact-artifact download attempt' "${benchmark}"
grep -Fq 'try-server-test-prebuild.sh' "${benchmark}"
grep -Fq 'rounded_runner_minutes_including_prebuild' scripts/ci/summarize-server-test-prebuild-benchmark.py
grep -Fq '  workflow_run:' "${parity}"
grep -Fq 'workflows: [PR Gate]' "${parity}"
grep -Fq "item.context === 'Review Gate'" "${parity}"
grep -Fq 'no-completed-exact-head-observer-artifact' "${parity}"
grep -Fq "run.event === 'workflow_dispatch' && run.head_branch === defaultBranch" "${parity}"
grep -Fq 'git -C policy merge-base --is-ancestor' "${parity}"
grep -Fq 'Checkout trusted verifier policy with producer history' "${parity}"
grep -Fq 'Make one non-blocking exact-artifact download attempt' "${parity}"
grep -Fq 'honua.server-test-prebuild-parity-observation/v1' "${parity}"
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
for workflow in "${observer}" "${benchmark}" "${parity}"; do
  if grep -Eq '^  (contents|actions|packages|pull-requests|statuses): write' "${workflow}"; then
    echo "::error::Shadow prebuild workflow gained a write permission: ${workflow}" >&2
    exit 1
  fi
  if grep -Eq 'statuses: write|github\.rest\.repos\.createCommitStatus|merge-train\.yml|gh[[:space:]]+pr[[:space:]]+merge' "${workflow}"; then
    echo "::error::Shadow prebuild workflow gained status or merge authority: ${workflow}" >&2
    exit 1
  fi
done

echo 'server-test-prebuild=ok mode=read-only-shadow single-attempt-fallback'
