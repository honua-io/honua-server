#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. "${repo_root}/scripts/ci/lib/python-resolve.sh"
python_bin="$(honua_resolve_python)"

"${python_bin}" scripts/ci/fixtures/validate-review-first-dispatch.py
node --test \
  scripts/ci/review-first-dispatch.test.js \
  scripts/ci/review-first-evidence-ledger.test.js \
  scripts/ci/review-gate-evidence.test.js \
  scripts/ci/review-gate-snapshot.test.js
