#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

# shellcheck source=scripts/ci/lib/python-resolve.sh
. "${repo_root}/scripts/ci/lib/python-resolve.sh"
python_bin="${HONUA_NORMALIZATION_PYTHON:-}"
if [[ -z "${python_bin}" ]]; then
  python_bin="$(honua_resolve_python)"
fi

"${python_bin}" scripts/ci/normalization-envelope.test.py
"${python_bin}" scripts/ci/fixtures/validate-normalization-workflows.py

if command -v node >/dev/null 2>&1; then
  node --test scripts/ci/normalization-mutation.test.js
else
  echo "⚠️  Skipping normalization mutation fixtures (no Node.js)."
fi
