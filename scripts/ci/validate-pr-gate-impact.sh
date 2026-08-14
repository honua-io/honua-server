#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. scripts/ci/lib/python-resolve.sh
python_bin="${HONUA_PR_GATE_IMPACT_PYTHON:-$(honua_resolve_python)}"
required_workflow=.github/workflows/pr-gate.yml
observer_workflow=.github/workflows/pr-gate-impact-observe.yml

"${python_bin}" scripts/ci/classify-pr-gate-impact.test.py
"${python_bin}" -m py_compile scripts/ci/classify-pr-gate-impact.py
node --test scripts/ci/trusted-pr-workflow-run.test.js

grep -Fq 'PR_GATE_IMPACT_MODE: observe' "${observer_workflow}"
grep -Fq 'workflow_run:' "${observer_workflow}"
grep -Fq 'workflows: [PR Gate]' "${observer_workflow}"
grep -Fq 'context.ref !== trustedRef' "${observer_workflow}"
grep -Fq 'SOURCE_RUN_ID: ${{ github.event.workflow_run.id || inputs.run_id }}' "${observer_workflow}"
grep -Fq "process.env.SOURCE_RUN_ID" "${observer_workflow}"
if grep -Fq "String('\${{ inputs.run_id }}')" "${observer_workflow}"; then
  echo '::error::Manual run id must not be interpolated into trusted JavaScript.' >&2
  exit 1
fi
grep -Fq "require('./policy/scripts/ci/trusted-pr-workflow-run')" "${observer_workflow}"
grep -Fq 'repositoryId: repository.id' "${observer_workflow}"
grep -Fq 'github.event.workflow_run.head_repository.full_name == github.repository' "${observer_workflow}"
"${python_bin}" - "${observer_workflow}" <<'PY'
from pathlib import Path
import sys

expected = {
    "actions": "read",
    "checks": "read",
    "contents": "read",
    "pull-requests": "read",
}
path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8")
canonical = """permissions:
  actions: read
  checks: read
  contents: read
  pull-requests: read
"""

try:
    import yaml
except ModuleNotFoundError:
    # Keep the supported no-PyYAML developer path fail closed by requiring the
    # one canonical byte representation and forbidding any second workflow/job
    # permissions key. Hosted validation normally takes the semantic path.
    if canonical not in source or source.count("permissions:") != 1:
        raise SystemExit("observer permissions are not the canonical read-only block")
else:
    def validate(candidate: str) -> None:
        try:
            document = yaml.safe_load(candidate)
        except yaml.YAMLError as error:
            raise ValueError("observer workflow YAML is invalid") from error
        if not isinstance(document, dict) or document.get("permissions") != expected:
            raise ValueError("observer workflow permissions are not the exact read-only allowlist")
        jobs = document.get("jobs")
        if not isinstance(jobs, dict) or any(
            isinstance(job, dict) and "permissions" in job for job in jobs.values()
        ):
            raise ValueError("observer jobs must not override workflow permissions")

    validate(source)
    unsafe = (
        source.replace("  actions: read", '  actions: "write" # required', 1),
        source.replace(canonical, "permissions: write-all\n", 1),
        source.replace(
            "  observe:\n    if:",
            "  observe:\n    permissions:\n      contents: write\n    if:",
            1,
        ),
    )
    for candidate in unsafe:
        try:
            validate(candidate)
        except ValueError:
            pass
        else:
            raise SystemExit("permission failure-injection fixture was accepted")
PY
grep -Fq 'github.rest.actions.listJobsForWorkflowRun' scripts/ci/trusted-pr-workflow-run.js
grep -Fq 'github.rest.checks.get' scripts/ci/trusted-pr-workflow-run.js
grep -Fq 'checkRun.pull_requests' scripts/ci/trusted-pr-workflow-run.js
grep -Fq "associated.base?.ref !== defaultBranch" scripts/ci/trusted-pr-workflow-run.js
grep -Fq 'associated.base?.repo?.id !== repositoryId' scripts/ci/trusted-pr-workflow-run.js
grep -Fq 'associated.head?.repo?.id !== repositoryId' scripts/ci/trusted-pr-workflow-run.js
grep -Fq "pullRequest.base?.sha !== associatedBase" scripts/ci/trusted-pr-workflow-run.js
grep -Fq "pullRequest.head?.repo?.full_name !== repository" scripts/ci/trusted-pr-workflow-run.js
if grep -Fq 'listPullRequestsAssociatedWithCommit' scripts/ci/trusted-pr-workflow-run.js; then
  echo '::error::Gate-time base identity must not be reconstructed from mutable commit association.' >&2
  exit 1
fi
grep -Fq 'currentPr.base.sha !== expectedBase || currentPr.head.sha !== expectedHead' "${observer_workflow}"
grep -Fq 'github.rest.repos.getContent' "${observer_workflow}"
grep -Fq 'ref: context.sha' "${observer_workflow}"
grep -Fq "trusted_execution: 'default-branch-workflow-run/v1'" "${observer_workflow}"
grep -Fq 'trusted-classify-pr-gate-impact.py' "${observer_workflow}"
grep -Fq 'policy_blob_sha' scripts/ci/classify-pr-gate-impact.py
grep -Fq 'gate_run_head_sha != head_sha' scripts/ci/classify-pr-gate-impact.py
grep -Fq 'gate_run_conclusion' scripts/ci/classify-pr-gate-impact.py
grep -Fq '"authoritative_gate": "full"' scripts/ci/classify-pr-gate-impact.py
grep -Fq 'Authoritative path: `full` (unchanged)' "${observer_workflow}"
grep -Fq 'name: pr-gate-impact-observation-${{ steps.collect.outputs.pr }}-${{ steps.collect.outputs.head }}' "${observer_workflow}"
if grep -Fq 'pr-gate-impact' "${required_workflow}"; then
  echo '::error::Candidate-controlled required workflow must not collect or publish impact evidence.' >&2
  exit 1
fi

if grep -Eq '^    paths(-ignore)?:' "${required_workflow}"; then
  echo '::error::Required PR Gate must not use a workflow-level paths filter.' >&2
  exit 1
fi

lean_gate_block="$(awk '
  /^      - name: Lean gate \(build \+ format \+ fast unit\/architecture smoke\)$/ { found=1 }
  found { print }
  found && /^        with:/ { exit }
' "${required_workflow}")"
if grep -Eq 'impact|docs-only' <<<"${lean_gate_block}"; then
  echo '::error::Observe mode must not alter the authoritative lean-gate decision.' >&2
  exit 1
fi

echo 'pr-gate-impact=ok mode=observe authoritative=full'
