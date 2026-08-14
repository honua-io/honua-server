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
assert_reset() {
  local case_name="$1" event_name="$2" dispatch_apply="$3" dispatch_reset="$4" expected_reset="$5"
  local output_file="${SCRATCH}/${case_name}.reset.out"

  EVENT_NAME="${event_name}" \
  DISPATCH_APPLY="${dispatch_apply}" \
  DISPATCH_MAX_BATCH="10" \
  DISPATCH_RESET_STATE="${dispatch_reset}" \
  HAVE_BEDROCK_KEYS="false" \
    "${RESOLVER}" "${output_file}" >/dev/null

  [[ "$(sed -n 's/^train_reset_state=//p' "${output_file}")" == "${expected_reset}" ]]
  printf 'PASS: %s\n' "${case_name}"
}

# Even injected truthy dispatch values and available secrets cannot make an
# automatic schedule live.
assert_output scheduled-dry-run schedule true true true true 0 0 0
assert_output manual-dry-run workflow_dispatch false true true true 0 0 0
assert_output explicit-manual-live workflow_dispatch true true true true 1 1 1
assert_output manual-live-without-bedrock workflow_dispatch true true true false 1 0 0

# The recovery-only state reset is never reachable from an automatic trigger or
# a dry-run dispatch; it needs an explicit live dispatch.
assert_reset reset-default-off workflow_dispatch true '' 0
assert_reset reset-scheduled-ignored schedule true true 0
assert_reset reset-dry-run-ignored workflow_dispatch false true 0
assert_reset reset-explicit-live workflow_dispatch true true 1

# A reset-only run clears state and exits 0 without selecting or landing, so the
# live self-chain must not fire off it — otherwise the chained ordinary run can
# land a batch the operator has not looked at yet.
WORKFLOW="$(cd "${FIXTURE_DIR}/../../../.." && pwd)/.github/workflows/merge-train.yml"
[[ -f "${WORKFLOW}" ]] || { printf 'FAIL: merge-train.yml not found at %s\n' "${WORKFLOW}" >&2; exit 1; }
self_chain_if="$(awk '
  /^      - name: Self-chain next run while PRs remain$/ { found=1; next }
  found && /^ *if:/ { print; exit }
' "${WORKFLOW}")"
[[ -n "${self_chain_if}" ]] || { printf 'FAIL: self-chain step or its if: condition is missing\n' >&2; exit 1; }
grep -Fq "steps.mode.outputs.train_reset_state == '0'" <<<"${self_chain_if}" \
  || { printf 'FAIL: self-chain condition does not exclude reset-only runs: %s\n' "${self_chain_if}" >&2; exit 1; }
printf 'PASS: self-chain excludes reset-only runs\n'

# A live operator may opt into Bedrock judgment or autofix for one dispatch,
# but continuous draining must return to the deterministic defaults. Otherwise
# one opt-in run permanently turns the optional/costly path on for the queue.
self_chain_step="$(awk '
  /^      - name: Self-chain next run while PRs remain$/ { found=1 }
  found { print }
' "${WORKFLOW}")"
grep -Fq -- '-f use_llm=false' <<<"${self_chain_step}" \
  || { printf 'FAIL: self-chain does not explicitly disable optional LLM judgment\n' >&2; exit 1; }
grep -Fq -- '-f use_autofix=false' <<<"${self_chain_step}" \
  || { printf 'FAIL: self-chain does not explicitly disable optional autofix\n' >&2; exit 1; }
if grep -Eq -- '-f use_(llm|autofix)=true' <<<"${self_chain_step}"; then
  printf 'FAIL: self-chain silently enables an optional Bedrock path\n' >&2
  exit 1
fi
printf 'PASS: self-chain preserves deterministic LLM/autofix defaults\n'

# Batch size is an operator safety bound for the whole continuous drain, not a
# one-run hint. A bounded recovery/landing must not silently widen on the first
# continuation.
grep -Fq 'CHAIN_MAX_BATCH: ${{ steps.mode.outputs.max_batch }}' <<<"${self_chain_step}" \
  || { printf 'FAIL: self-chain does not inherit the resolved batch bound\n' >&2; exit 1; }
grep -Fq -- '-f max_batch="${CHAIN_MAX_BATCH}"' <<<"${self_chain_step}" \
  || { printf 'FAIL: self-chain dispatch does not use the inherited batch bound\n' >&2; exit 1; }
if grep -Eq -- '-f max_batch=[0-9]+' <<<"${self_chain_step}"; then
  printf 'FAIL: self-chain hardcodes and may widen the operator batch bound\n' >&2
  exit 1
fi
printf 'PASS: self-chain preserves the operator batch bound\n'

grep -Fq 'source scripts/ci/merge-train/state.sh' <<<"${self_chain_step}" \
  || { printf 'FAIL: self-chain cannot inspect durable land intent\n' >&2; exit 1; }
grep -Fq 'train_state_requires_live_reconciliation' <<<"${self_chain_step}" \
  || { printf 'FAIL: self-chain ignores durable land intent\n' >&2; exit 1; }

# A successful trunk CAS can briefly precede GitHub's merged-PR projection. The
# controller must chain once for all land-family phases even with an empty PR
# queue, then stop after reconciliation reaches done.
REPO_ROOT="$(cd "${FIXTURE_DIR}/../../../.." && pwd)"
# shellcheck source=scripts/ci/merge-train/lib.sh
source "${REPO_ROOT}/scripts/ci/merge-train/lib.sh"
# shellcheck source=scripts/ci/merge-train/state.sh
source "${REPO_ROOT}/scripts/ci/merge-train/state.sh"
state_sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
state_heads='[{"number":123,"head":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]'
assert_reconciliation_phase() {
  local phase="$1" expected="$2" body rc=0
  if [[ "${phase}" == "done" ]]; then
    body="$(train_state_render "" "${state_sha}" "" "done" "" 0 0 "${state_sha}")"
  else
    body="$(train_state_render "train/batch/test/1" "${state_sha}" "123" "${phase}" "" 0 0 "${state_sha}" \
      "${state_heads}" "${state_sha}")"
  fi
  export TRAIN_STATE_ISSUE_OVERRIDE=2044 TRAIN_STATE_BODY_OVERRIDE="${body}"
  train_state_requires_live_reconciliation || rc=$?
  [[ "${rc}" == "${expected}" ]] \
    || { printf 'FAIL: reconciliation phase %s returned %s, expected %s\n' "${phase}" "${rc}" "${expected}" >&2; exit 1; }
}
assert_reconciliation_phase land 0
assert_reconciliation_phase pre-land-cleanup 0
assert_reconciliation_phase post-land-finalize 0
assert_reconciliation_phase done 1
export TRAIN_STATE_BODY_OVERRIDE='```json
not-json
```'
malformed_rc=0
train_state_requires_live_reconciliation || malformed_rc=$?
[[ "${malformed_rc}" == "2" ]] \
  || { printf 'FAIL: unreadable state did not fail closed\n' >&2; exit 1; }
unset TRAIN_STATE_ISSUE_OVERRIDE TRAIN_STATE_BODY_OVERRIDE
printf 'PASS: self-chain reconciles durable land intent and fails closed on unreadable state\n'
