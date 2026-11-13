#!/usr/bin/env bash
# Validate every workflow before the fast-stage build. Shell/Python checks have
# their own gates; actionlint here owns workflow syntax and expression contexts.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

if [[ "$(uname -s)/$(uname -m)" != Linux/x86_64 ]]; then
  echo 'Workflow validation requires Linux x86_64 (the PR fast-stage runner).' >&2
  exit 2
fi
version=1.7.12
checksum=8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8
tools_dir="$(mktemp -d)"
trap 'rm -rf "${tools_dir}"' EXIT
curl --fail --silent --show-error --location --retry 3 \
  "https://github.com/rhysd/actionlint/releases/download/v${version}/actionlint_${version}_linux_amd64.tar.gz" \
  --output "${tools_dir}/actionlint.tar.gz"
printf '%s  %s\n' "${checksum}" "${tools_dir}/actionlint.tar.gz" | sha256sum --check --status
tar -xzf "${tools_dir}/actionlint.tar.gz" -C "${tools_dir}" actionlint
"${tools_dir}/actionlint" -shellcheck= -pyflakes= .github/workflows/*.yml
python3 scripts/ci/fixtures/validate-workflow-lint.py "${tools_dir}/actionlint"
