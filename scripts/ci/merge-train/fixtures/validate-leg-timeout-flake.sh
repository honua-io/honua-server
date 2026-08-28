#!/usr/bin/env bash
# Focused offline validation for the matrix-leg inner-timeout classification.
#
# Incident shape (run 33109819708, job 98649469683): the postgres-compat leg on
# postgis/postgis:16-3.4 hung for 24.4 minutes until the job cap killed it,
# while the 17-3.5 and 18-3.6 legs of the SAME step in the SAME run passed in
# ~34s. The batch was rejected for a pure infrastructure flake. ci.yml now caps
# each leg with an inner `timeout` that emits HONUA_MATRIX_LEG_INNER_TIMEOUT,
# and the classifier turns that marker into a retryable infra flake ONLY when a
# sibling leg passed — the load-bearing guard: with no passing sibling (a
# code-introduced deadlock hangs every leg, or the only leg), the failure stays
# REAL and rejects the batch.
set -euo pipefail

TRAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export TRAIN_APPLY=0
. "${TRAIN_DIR}/lib.sh"
. "${TRAIN_DIR}/smart-ci.sh"
. "${TRAIN_DIR}/classify-timeout.sh"
. "${TRAIN_DIR}/state.sh"
. "${TRAIN_DIR}/resume-retry.sh"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
pass() { printf 'PASS: %s\n' "$1"; }
record="$(mktemp)"
fixture_metrics="$(mktemp)"
export TRAIN_METRICS_OUT="${fixture_metrics}"
trap 'rm -f "${record}" "${fixture_metrics}"' EXIT
train_side_effect() { printf '%s\n' "$*" >>"${record}"; }
export TRAIN_STATE_ISSUE_OVERRIDE=1
export TRAIN_STATE_BODY_OVERRIDE
TRAIN_STATE_BODY_OVERRIDE="$(train_state_render '' '' '' select '' 0 0 null)"
gh() { printf '1\n'; }

# The inner-timeout marker exactly as ci.yml's postgres-compat step emits it,
# followed by the runner's own step-failure annotation for exit 124.
leg_marker() {
  printf "::error::HONUA_MATRIX_LEG_INNER_TIMEOUT image='%s' step='Run Postgres compatibility tests' exceeded its 10m inner test budget (healthy legs finish in under a minute); killed early instead of burning the job cap.\nError: Process completed with exit code 124.\n" "$1"
}

# Snapshot/annotation/log readers, driven per-scenario.
FIXTURE_SNAPSHOT=""
declare -A FIXTURE_ANNOTATIONS=()
leg_snapshot_reader() { printf '%s\n' "${FIXTURE_SNAPSHOT}"; }
leg_annotation_reader() { printf '%s' "${FIXTURE_ANNOTATIONS[$1]:-}"; }
leg_log_reader() { return 1; }
export TRAIN_FAILED_JOB_SNAPSHOT_READER=leg_snapshot_reader
export TRAIN_JOB_ANNOTATION_READER=leg_annotation_reader
export TRAIN_JOB_LOG_READER=leg_log_reader
unset TRAIN_RUN_LOG_TEXT

# --- Direction 1: one leg hit its inner timeout, siblings passed -> flake ----
FIXTURE_SNAPSHOT='{"attempt":1,"status":"completed","jobs":[
  {"databaseId":41,"name":"Postgres Compatibility (postgis/postgis:16-3.4)","conclusion":"failure"},
  {"databaseId":42,"name":"Postgres Compatibility (postgis/postgis:17-3.5)","conclusion":"success"},
  {"databaseId":43,"name":"Postgres Compatibility (postgis/postgis:18-3.6)","conclusion":"success"},
  {"databaseId":44,"name":"CI Gate","conclusion":"failure"}]}'
FIXTURE_ANNOTATIONS=([41]="$(leg_marker postgis/postgis:16-3.4)" [44]="CI Gate observed a failed needs job.")
: >"${record}"
rc=0
train_classify_timeout 33109819708 0 'Postgres Compatibility (postgis/postgis:16-3.4)' || rc=$?
[[ "${rc}" == "0" ]] || fail "sibling-passed inner timeout was not retried (rc=${rc})"
[[ "${TRAIN_TIMEOUT_KIND}" == "leg-flake" ]] || fail "timeout kind was '${TRAIN_TIMEOUT_KIND}', expected leg-flake"
grep -Fqx 'gh run rerun 33109819708 --failed' "${record}" || fail "leg flake retry did not target failed jobs only"
pass "inner-timeout leg with passing siblings is a retryable infra flake"

# The single retry is bounded: at the cap the same shape is real.
: >"${record}"
rc=0
train_classify_timeout 33109819708 1 'Postgres Compatibility (postgis/postgis:16-3.4)' || rc=$?
[[ "${rc}" == "2" ]] || fail "leg flake was retried past the rerun cap (rc=${rc})"
[[ ! -s "${record}" ]] || fail "capped leg flake still consumed a rerun"
pass "leg flake retry is bounded by the timeout rerun cap"

# --- Direction 2: EVERY leg hit its inner timeout -> real failure ------------
# The guard is load-bearing: a code-introduced deadlock hangs every leg, so
# without the sibling-passed signal the batch must still be rejected.
FIXTURE_SNAPSHOT='{"attempt":1,"status":"completed","jobs":[
  {"databaseId":41,"name":"Postgres Compatibility (postgis/postgis:16-3.4)","conclusion":"failure"},
  {"databaseId":42,"name":"Postgres Compatibility (postgis/postgis:17-3.5)","conclusion":"failure"},
  {"databaseId":43,"name":"Postgres Compatibility (postgis/postgis:18-3.6)","conclusion":"failure"},
  {"databaseId":45,"name":"Test Suite Summary","conclusion":"success"}]}'
FIXTURE_ANNOTATIONS=(
  [41]="$(leg_marker postgis/postgis:16-3.4)"
  [42]="$(leg_marker postgis/postgis:17-3.5)"
  [43]="$(leg_marker postgis/postgis:18-3.6)")
: >"${record}"
rc=0
train_classify_timeout 33109819708 0 'Postgres Compatibility (postgis/postgis:16-3.4)
Postgres Compatibility (postgis/postgis:17-3.5)
Postgres Compatibility (postgis/postgis:18-3.6)' || rc=$?
[[ "${rc}" == "2" ]] || fail "all-legs-hung was not treated as real (rc=${rc})"
[[ "${TRAIN_TIMEOUT_KIND}" == "leg-hang" ]] || fail "timeout kind was '${TRAIN_TIMEOUT_KIND}', expected leg-hang"
[[ ! -s "${record}" ]] || fail "all-legs-hung consumed a rerun"
# An unrelated successful job (Test Suite Summary) must not satisfy the
# sibling guard — only a leg of the SAME matrix family counts.
pass "all legs hung -> real failure, no retry, batch rejected"

# The orchestration policy maps that rc to REAL, which also proves it can
# never reach the known-flake merge-through path.
rc=0
train_classify_retry_candidate 33109819708 0 0 'Postgres Compatibility (postgis/postgis:16-3.4)
Postgres Compatibility (postgis/postgis:17-3.5)
Postgres Compatibility (postgis/postgis:18-3.6)' || rc=$?
[[ "${rc}" == "1" ]] || fail "all-legs-hung escaped the real-failure path (rc=${rc})"
pass "all-legs-hung is REAL through the orchestration policy (no flake merge-through)"

# --- Only leg of a single-leg matrix hung -> real failure --------------------
FIXTURE_SNAPSHOT='{"attempt":1,"status":"completed","jobs":[
  {"databaseId":41,"name":"Postgres Compatibility (postgis/postgis:16-3.4)","conclusion":"failure"},
  {"databaseId":45,"name":"Test Suite Summary","conclusion":"success"}]}'
FIXTURE_ANNOTATIONS=([41]="$(leg_marker postgis/postgis:16-3.4)")
: >"${record}"
rc=0
train_classify_timeout 33109819708 0 'Postgres Compatibility (postgis/postgis:16-3.4)' || rc=$?
[[ "${rc}" == "2" ]] || fail "single-leg hang was not treated as real (rc=${rc})"
[[ "${TRAIN_TIMEOUT_KIND}" == "leg-hang" ]] || fail "timeout kind was '${TRAIN_TIMEOUT_KIND}', expected leg-hang"
[[ ! -s "${record}" ]] || fail "single-leg hang consumed a rerun"
pass "the only leg hanging is a real failure, not a flake"

# --- Mixed evidence keeps the historical hang classification -----------------
# A generic timeout in another failing job means the failure is not explained
# by one leg's environment; the leg rule must stand down.
FIXTURE_SNAPSHOT='{"attempt":1,"status":"completed","jobs":[
  {"databaseId":41,"name":"Postgres Compatibility (postgis/postgis:16-3.4)","conclusion":"failure"},
  {"databaseId":42,"name":"Postgres Compatibility (postgis/postgis:17-3.5)","conclusion":"success"},
  {"databaseId":46,"name":"Server Tests (Other)","conclusion":"failure"}]}'
FIXTURE_ANNOTATIONS=(
  [41]="$(leg_marker postgis/postgis:16-3.4)"
  [46]="Error: Process completed with exit code 124.")
: >"${record}"
rc=0
train_classify_timeout 33109819708 0 'Postgres Compatibility (postgis/postgis:16-3.4)
Server Tests (Other)' || rc=$?
[[ "${rc}" == "0" ]] || fail "mixed leg+generic timeout lost the bounded hang retry (rc=${rc})"
[[ "${TRAIN_TIMEOUT_KIND}" == "hang" ]] || fail "timeout kind was '${TRAIN_TIMEOUT_KIND}', expected hang"
pass "generic timeout evidence alongside a leg marker disables the leg rule"

# --- Injected log text (no snapshot) can never authorize the flake path ------
export TRAIN_RUN_LOG_TEXT="$(leg_marker postgis/postgis:16-3.4)"
rc=0
train_classify_capacity_guard 33109819708 '' || rc=$?
[[ "${rc}" == "9" && "${TRAIN_TIMEOUT_KIND}" == "hang" ]] \
  || fail "snapshot-less marker text was not downgraded to the generic hang (rc=${rc} kind=${TRAIN_TIMEOUT_KIND})"
unset TRAIN_RUN_LOG_TEXT
pass "without sibling evidence the marker downgrades to the generic bounded hang"

# The marker predicate is anchored: prose that merely names the token (like
# this validator's own output) must not match.
train_log_is_matrix_leg_inner_timeout "the HONUA_MATRIX_LEG_INNER_TIMEOUT rule rejected the batch" \
  && fail "unanchored prose matched the leg marker"
train_log_is_matrix_leg_inner_timeout "$(leg_marker postgis/postgis:16-3.4)" \
  || fail "the real marker line did not match"
pass "leg marker matching is anchored"

printf 'validate-leg-timeout-flake: all scenarios passed\n'
