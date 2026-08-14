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

grep -Fq 'PR_GATE_IMPACT_MODE: observe' "${observer_workflow}"
grep -Fq 'workflow_run:' "${observer_workflow}"
grep -Fq 'workflows: [PR Gate]' "${observer_workflow}"
grep -Fq "gateRun.path !== '.github/workflows/pr-gate.yml'" "${observer_workflow}"
grep -Fq "gateRun.event !== 'pull_request'" "${observer_workflow}"
grep -Fq "gateRun.status !== 'completed'" "${observer_workflow}"
grep -Fq 'context.ref !== trustedRef' "${observer_workflow}"
grep -Fq 'SOURCE_RUN_ID: ${{ github.event.workflow_run.id || inputs.run_id }}' "${observer_workflow}"
grep -Fq "process.env.SOURCE_RUN_ID" "${observer_workflow}"
if grep -Fq "String('\${{ inputs.run_id }}')" "${observer_workflow}"; then
  echo '::error::Manual run id must not be interpolated into trusted JavaScript.' >&2
  exit 1
fi
grep -Fq 'gateHead !== gateRun.head_sha' "${observer_workflow}"
grep -Fq 'pr.base?.sha !== expectedBase || pr.head?.sha !== expectedHead' "${observer_workflow}"
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
