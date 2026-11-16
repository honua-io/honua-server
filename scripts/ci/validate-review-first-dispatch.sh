#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. "${repo_root}/scripts/ci/lib/python-resolve.sh"
python_bin="$(honua_resolve_python)"

# Validate the workflow inventory projected from this checkout, in a scratch
# file: later PR Gate steps require the tracked checkout unchanged.
inventory="$(mktemp)"
trap 'rm -f "${inventory}"' EXIT
"${python_bin}" scripts/ci/generate-workflow-inventory.py --output "${inventory}"
export HONUA_WORKFLOW_INVENTORY="${inventory}"

# Prove the decode diagnostic fails, not just that it passes (#3321).
"${python_bin}" scripts/ci/fixtures/validate-review-first-utf8.py
"${python_bin}" scripts/ci/fixtures/validate-review-first-dispatch.py

node --test \
  scripts/ci/review-first-dispatch.test.js \
  scripts/ci/review-first-evidence-ledger.test.js \
  scripts/ci/review-gate-evidence.test.js \
  scripts/ci/review-gate-snapshot.test.js \
  scripts/ci/review-stale-shed.test.js \
  scripts/ci/auto-merge-ready.test.js
