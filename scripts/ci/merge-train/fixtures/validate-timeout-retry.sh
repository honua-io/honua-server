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
trap 'rm -f "${record}"' EXIT
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
gh() { printf 'in_progress\n'; }
now_value=6610
rc=0
train_wait_for_run_completion 123 || rc=$?
[[ "${rc}" == "1" ]] || fail "deadline exhaustion did not fail closed"
pass "deadline exhaustion"

# First timeout retries failed jobs only.
TRAIN_RUN_LOG_TEXT='Error: Process completed with exit code 124.'
: >"${record}"
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
