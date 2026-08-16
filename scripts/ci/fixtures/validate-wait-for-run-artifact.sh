#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-artifact-wait.XXXXXX")"
cleanup() { rm -rf "${fixture}"; }
trap cleanup EXIT
mkdir -p "${fixture}/bin" "${fixture}/runner"
cp "${repo_root}/scripts/ci/fixtures/stubs/fake-gh-run-download.sh" "${fixture}/bin/gh"
chmod +x "${fixture}/bin/gh"

export PATH="${fixture}/bin:${PATH}"
export RUNNER_TEMP="${fixture}/runner"
export GITHUB_REPOSITORY="honua-io/honua-server"
export GH_TOKEN="fixture"
export HONUA_FAKE_GH_COUNT="${fixture}/count"
export HONUA_FAKE_GH_SUCCEED_AT=2
export GITHUB_OUTPUT="${fixture}/output"

"${repo_root}/scripts/ci/wait-for-run-artifact.sh" \
  --run-id 123 --artifact "server-test-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-server" \
  --destination "${RUNNER_TEMP}/payload" --timeout-seconds 5 --poll-seconds 1
grep -q '^poll_attempts=2$' "${GITHUB_OUTPUT}"
test -f "${RUNNER_TEMP}/payload/server-test-binaries-server.tar.gz"

if "${repo_root}/scripts/ci/wait-for-run-artifact.sh" \
  --run-id 123 --artifact "server-test-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-server" \
  --destination "${fixture}/outside" --timeout-seconds 1 --poll-seconds 1 >/dev/null 2>&1; then
  echo "::error::Wait helper accepted a destination outside RUNNER_TEMP." >&2
  exit 1
fi

echo "same-run-artifact-wait=ok"
