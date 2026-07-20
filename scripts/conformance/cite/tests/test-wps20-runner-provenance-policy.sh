#!/bin/bash

set -euo pipefail

runner="scripts/conformance/cite/run-cite-wps20-tests.sh"
temporary_dir="$(mktemp -d)"
trap 'rm -rf "$temporary_dir"' EXIT

set +e
GITHUB_ACTIONS=true HONUA_CITE_TESTED_GIT_SHA= bash "$runner" >"$temporary_dir/ci.out" 2>"$temporary_dir/ci.err"
ci_exit_code=$?
set -e
if [[ "$ci_exit_code" -ne 2 ]]; then
    echo "Expected missing CI SHA to exit 2, got $ci_exit_code" >&2
    exit 1
fi
grep -F "HONUA_CITE_TESTED_GIT_SHA is required in GitHub Actions" "$temporary_dir/ci.err" >/dev/null

set +e
PATH=/nonexistent GITHUB_ACTIONS=false HONUA_CITE_TESTED_GIT_SHA= /bin/bash "$runner" >"$temporary_dir/local.out" 2>"$temporary_dir/local.err"
local_exit_code=$?
set -e
if [[ "$local_exit_code" -ne 2 ]]; then
    echo "Expected dependency-free local probe to exit 2, got $local_exit_code" >&2
    exit 1
fi
grep -F "Warning: HONUA_CITE_TESTED_GIT_SHA is unset; using local checkout SHA 'unknown'" "$temporary_dir/local.err" >/dev/null
