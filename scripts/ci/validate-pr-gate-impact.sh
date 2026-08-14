#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. scripts/ci/lib/python-resolve.sh
python_bin="${HONUA_PR_GATE_IMPACT_PYTHON:-$(honua_resolve_python)}"
workflow=.github/workflows/pr-gate.yml

"${python_bin}" scripts/ci/classify-pr-gate-impact.test.py
"${python_bin}" -m py_compile scripts/ci/classify-pr-gate-impact.py

grep -Fq 'PR_GATE_IMPACT_MODE: observe' "${workflow}"
grep -Fq 'github.rest.pulls.listFiles' "${workflow}"
grep -Fq 'pr.base.sha !== expectedBase || pr.head.sha !== expectedHead' "${workflow}"
grep -Fq 'currentPr.base.sha !== expectedBase || currentPr.head.sha !== expectedHead' "${workflow}"
grep -Fq 'github.rest.repos.getContent' "${workflow}"
grep -Fq "ref: expectedBase" "${workflow}"
grep -Fq 'trusted-classify-pr-gate-impact.py' "${workflow}"
grep -Fq 'policy_blob_sha' scripts/ci/classify-pr-gate-impact.py
grep -Fq '"authoritative_gate": "full"' scripts/ci/classify-pr-gate-impact.py
grep -Fq 'Authoritative path: `full` (unchanged)' "${workflow}"
grep -Fq 'name: pr-gate-impact-observation-${{ github.event.pull_request.number }}-${{ github.event.pull_request.head.sha }}' "${workflow}"
[[ "$(grep -c 'continue-on-error: true' "${workflow}")" -ge 3 ]] \
  || { echo '::error::Observe-only impact steps must not block the authoritative full gate.' >&2; exit 1; }

if grep -Eq '^    paths(-ignore)?:' "${workflow}"; then
  echo '::error::Required PR Gate must not use a workflow-level paths filter.' >&2
  exit 1
fi

lean_gate_block="$(awk '
  /^      - name: Lean gate \(build \+ format \+ fast unit\/architecture smoke\)$/ { found=1 }
  found { print }
  found && /^        with:/ { exit }
' "${workflow}")"
if grep -Eq 'impact|docs-only' <<<"${lean_gate_block}"; then
  echo '::error::Observe mode must not alter the authoritative lean-gate decision.' >&2
  exit 1
fi

echo 'pr-gate-impact=ok mode=observe authoritative=full'
