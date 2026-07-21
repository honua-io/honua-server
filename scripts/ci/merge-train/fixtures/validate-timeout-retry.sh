#!/usr/bin/env bash
# Focused offline validation for controller deadline and timeout precedence.
set -euo pipefail

TRAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export TRAIN_APPLY=0
. "${TRAIN_DIR}/lib.sh"
. "${TRAIN_DIR}/smart-ci.sh"
. "${TRAIN_DIR}/classify-timeout.sh"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
pass() { printf 'PASS: %s\n' "$1"; }
record="$(mktemp)"
trap 'rm -f "${record}" "${sequence_calls:-}"' EXIT
side_effect_fails=0
train_side_effect() {
  [[ "${side_effect_fails}" == "1" ]] && return 42
  printf '%s\n' "$*" >>"${record}"
}

# One deadline is initialized once and never reset for a retry.
now_value=10
train_now() { printf '%s\n' "${now_value}"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
train_init_controller_deadline || fail "deadline initialization failed"
[[ "${TRAIN_CONTROLLER_DEADLINE_EPOCH}" == "6610" ]] || fail "deadline does not use 6600s total budget"
now_value=100
train_init_controller_deadline
[[ "${TRAIN_CONTROLLER_DEADLINE_EPOCH}" == "6610" ]] || fail "deadline reset on second initialization"
pass "one absolute controller deadline"

# Exhaustion fails immediately and cannot grant a fresh retry budget.
gh_mode=in_progress
sequence_calls="$(mktemp)"
printf '0' >"${sequence_calls}"
gh() {
  case "${gh_mode}" in
    in_progress) printf 'in_progress\n' ;;
    attempt) printf '1\n' ;;
    sequence)
      local n; n=$(( $(cat "${sequence_calls}") + 1 )); printf '%s' "${n}" >"${sequence_calls}"
      case "${n}" in
        1|2) printf '1\tcompleted\n' ;;
        3) printf '2\tqueued\n' ;;
        *) printf '2\tcompleted\n' ;;
      esac
      ;;
  esac
}
now_value=6610
rc=0
train_wait_for_run_completion 123 || rc=$?
[[ "${rc}" == "1" ]] || fail "deadline exhaustion did not fail closed"
pass "deadline exhaustion"

# First timeout retries failed jobs only.
TRAIN_RUN_LOG_TEXT='Error: Process completed with exit code 124.'
: >"${record}"
gh_mode=attempt
train_classify_timeout 123 0 || fail "first exit-124 failure was not retried"
grep -Fqx 'gh run rerun 123 --failed' "${record}" || fail "retry did not target failed jobs only"
pass "failed-job-only timeout retry"

# Command failure is propagated distinctly for the main loop to fail closed.
side_effect_fails=1
rc=0
train_classify_timeout 123 0 || rc=$?
[[ "${rc}" == "3" ]] || fail "rerun command failure was swallowed"
side_effect_fails=0
pass "rerun command failure propagation"

# GitHub can expose completed(old) repeatedly, then queued(new), then
# completed(new). Only the strictly newer completed attempt is accepted.
sleep() { :; }
gh_mode=sequence
printf '0' >"${sequence_calls}"
now_value=100
train_wait_for_new_run_attempt 123 1 || fail "new attempt was not observed through delayed visibility"
[[ "$(cat "${sequence_calls}")" == "4" ]] || fail "old completed attempt was accepted as retry evidence"
pass "completed(old) to queued(new) to completed(new)"

# Cancellation after persisted intent is idempotent: resume does not issue a
# second rerun and reconciles by observing the newer attempt.
: >"${record}"
export TRAIN_RERUN_RESUME_STATE_JSON='{"active_batch":{"run_id":123,"phase":"timeout-retry-intent","rerun_kind":"timeout","rerun_base_attempt":1}}'
gh_mode=sequence
printf '0' >"${sequence_calls}"
train_request_failed_job_rerun 123 timeout 1 || fail "persisted rerun intent did not reconcile"
[[ ! -s "${record}" ]] || fail "resume issued a duplicate rerun"
train_wait_for_new_run_attempt 123 "${TRAIN_RERUN_BASE_ATTEMPT}" || fail "resume did not observe accepted rerun"
unset TRAIN_RERUN_RESUME_STATE_JSON
pass "cancellation/resume idempotency"

# Persistent timeout wins over an overlapping known-flake signature.
flake_called=0
train_classify_flake() { flake_called=1; return 2; }
TRAIN_RUN_LOG_TEXT='Testcontainers timed out after 20 minutes; Process completed with exit code 124.'
rc=0
train_classify_retry_candidate 123 1 1 'Server Tests (OData Core)' || rc=$?
[[ "${rc}" == "1" ]] || fail "persistent overlapping timeout was not real"
[[ "${flake_called}" == "0" ]] || fail "persistent timeout reached merge-through flake classifier"
pass "persistent timeout precedence"

# Main-loop classifier selects timeout first and known flake only otherwise.
: >"${record}"
gh_mode=attempt
TRAIN_RUN_LOG_TEXT='timeout after 20 minutes'
rc=0
train_classify_retry_candidate 123 0 0 'Server Tests (OData Core)' || rc=$?
[[ "${rc}" == "0" && "${TRAIN_RETRY_KIND}" == "timeout" ]] || fail "main policy did not select timeout retry"

TRAIN_RUN_LOG_TEXT='40P01 deadlock detected'
train_classify_flake() { TRAIN_RETRY_KIND=flake; return 0; }
rc=0
train_classify_retry_candidate 123 0 0 'Server Tests (OData Core)' || rc=$?
[[ "${rc}" == "0" && "${TRAIN_RETRY_KIND}" == "flake" ]] || fail "main policy did not fall through to known flake"
pass "main-loop classifier behavior"
