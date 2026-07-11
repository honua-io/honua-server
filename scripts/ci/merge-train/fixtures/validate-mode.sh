#!/usr/bin/env bash
# Focused, offline regression tests for merge-train trigger mode resolution.

set -euo pipefail

FIXTURE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESOLVER="$(cd "${FIXTURE_DIR}/.." && pwd)/resolve-mode.sh"
SCRATCH="$(mktemp -d)"
trap 'rm -rf "${SCRATCH}"' EXIT

assert_output() {
  local case_name="$1" event_name="$2" dispatch_apply="$3" use_llm="$4" use_autofix="$5"
  local have_keys="$6" expected_apply="$7" expected_llm="$8" expected_autofix="$9"
  local output_file="${SCRATCH}/${case_name}.out"

  EVENT_NAME="${event_name}" \
  DISPATCH_APPLY="${dispatch_apply}" \
  DISPATCH_MAX_BATCH="10" \
  DISPATCH_USE_LLM="${use_llm}" \
  DISPATCH_USE_AUTOFIX="${use_autofix}" \
  DISPATCH_AUTOFIX_MODEL="test-model" \
  HAVE_BEDROCK_KEYS="${have_keys}" \
    "${RESOLVER}" "${output_file}" >/dev/null

  [[ "$(sed -n 's/^train_apply=//p' "${output_file}")" == "${expected_apply}" ]]
  [[ "$(sed -n 's/^train_llm=//p' "${output_file}")" == "${expected_llm}" ]]
  [[ "$(sed -n 's/^train_autofix=//p' "${output_file}")" == "${expected_autofix}" ]]
  printf 'PASS: %s\n' "${case_name}"
}

# Even injected truthy dispatch values and available secrets cannot make an
# automatic schedule live.
assert_output scheduled-dry-run schedule true true true true 0 0 0
assert_output manual-dry-run workflow_dispatch false true true true 0 0 0
assert_output explicit-manual-live workflow_dispatch true true true true 1 1 1
assert_output manual-live-without-bedrock workflow_dispatch true true true false 1 0 0
