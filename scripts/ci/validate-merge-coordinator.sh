#!/usr/bin/env bash
# Unit tests for the merge-coordinator decision engine (ADR-0055).
# Feeds decide_action mock CI-run/job state documents and asserts the chosen
# action. No network: it sources merge-coordinator.sh and calls the pure core
# directly. Run: scripts/ci/validate-merge-coordinator.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Source the coordinator for its pure functions without running main.
# shellcheck source=/dev/null
source "${SCRIPT_DIR}/merge-coordinator.sh"

PASS=0
FAIL=0

# assert_action <expected-action> <description> <state-json>
assert_action() {
  local expected="$1" desc="$2" state="$3"
  local got
  got="$(decide_action "$state" 2 | cut -f1)"
  if [[ "$got" == "$expected" ]]; then
    PASS=$((PASS + 1))
    printf 'ok   %-14s %s\n' "[$got]" "$desc"
  else
    FAIL=$((FAIL + 1))
    printf 'FAIL expected=%-12s got=%-12s %s\n' "$expected" "$got" "$desc"
    printf '     state: %s\n' "$state"
  fi
}

# --- 1. all-green, mergeable -> merge -------------------------------------
assert_action merge "all gates + shards green, mergeable" '{
  "run_status":"completed","run_conclusion":"success","mergeable":true,"behind":false,
  "jobs":[
    {"name":"Build & Format Check","conclusion":"success"},
    {"name":".NET Foundation Tests","conclusion":"success"},
    {"name":"Server Tests (Core)","conclusion":"success"}
  ]}'

# --- 2. flake-only shard failure -> merge-through (still "merge") ----------
assert_action merge "40P01 deadlock flake on Core shard -> merge through" '{
  "run_status":"completed","run_conclusion":"failure","mergeable":true,"behind":false,
  "jobs":[
    {"name":"Build & Format Check","conclusion":"success"},
    {"name":".NET Foundation Tests","conclusion":"success"},
    {"name":"Server Tests (Core)","conclusion":"failure","flake":true},
    {"name":"Server Tests (Migration)","conclusion":"failure","flake":true}
  ]}'

# --- 3. real-gate failure (build) -> escalate -----------------------------
assert_action escalate "Build & Format Check failed -> escalate" '{
  "run_status":"completed","run_conclusion":"failure","mergeable":true,"behind":false,
  "jobs":[
    {"name":"Build & Format Check","conclusion":"failure"},
    {"name":"Server Tests (Core)","conclusion":"success"}
  ]}'

# --- 3b. real-gate failure (foundation/architecture-drift) -> escalate -----
assert_action escalate ".NET Foundation Tests (drift guard) failed -> escalate" '{
  "run_status":"completed","run_conclusion":"failure","mergeable":true,"behind":false,
  "jobs":[
    {"name":"Build & Format Check","conclusion":"success"},
    {"name":".NET Foundation Tests","conclusion":"failure"}
  ]}'

# --- 4. real (non-flake) shard failure -> escalate ------------------------
assert_action escalate "genuine Core shard test failure (not a flake) -> escalate" '{
  "run_status":"completed","run_conclusion":"failure","mergeable":true,"behind":false,
  "jobs":[
    {"name":"Build & Format Check","conclusion":"success"},
    {"name":"Server Tests (Core)","conclusion":"failure","flake":false}
  ]}'

# --- 4b. mixed flake + real shard failure -> escalate (real wins) ---------
assert_action escalate "one flake + one real shard fail -> escalate (real wins)" '{
  "run_status":"completed","run_conclusion":"failure","mergeable":true,"behind":false,
  "jobs":[
    {"name":"Server Tests (Migration)","conclusion":"failure","flake":true},
    {"name":"Server Tests (WFS)","conclusion":"failure","flake":false}
  ]}'

# --- 5. cancelled run, under cap -> rerun-full ----------------------------
assert_action rerun-full "cancelled run, rerun_count=0 -> rerun-full" '{
  "run_status":"completed","run_conclusion":"cancelled","mergeable":true,"behind":false,
  "rerun_count":0,"jobs":[]}'

# --- 5b. cancelled run, at cap -> escalate --------------------------------
assert_action escalate "cancelled run, rerun_count=2 (cap) -> escalate" '{
  "run_status":"completed","run_conclusion":"cancelled","mergeable":true,"behind":false,
  "rerun_count":2,"jobs":[]}'

# --- 6. in-progress -> skip -----------------------------------------------
assert_action skip "run in_progress -> skip" '{
  "run_status":"in_progress","run_conclusion":"","mergeable":true,"behind":false,"jobs":[]}'

# --- 6b. queued -> skip ----------------------------------------------------
assert_action skip "run queued -> skip" '{
  "run_status":"queued","run_conclusion":"","mergeable":true,"behind":false,"jobs":[]}'

# --- 7. behind trunk, not freshened -> freshen-once -----------------------
assert_action freshen-once "behind trunk, not yet freshened -> freshen-once" '{
  "run_status":"completed","run_conclusion":"success","mergeable":true,"behind":true,"freshened":false,
  "jobs":[{"name":"Server Tests (Core)","conclusion":"success"}]}'

# --- 7b. behind trunk, ALREADY freshened -> merge (never re-freshen) -------
assert_action merge "behind but already freshened (current) -> merge, no re-freshen" '{
  "run_status":"completed","run_conclusion":"success","mergeable":true,"behind":true,"freshened":true,
  "jobs":[{"name":"Server Tests (Core)","conclusion":"success"}]}'

# --- 8. conflicts (not mergeable), not behind -> escalate -----------------
assert_action escalate "merge conflicts -> escalate" '{
  "run_status":"completed","run_conclusion":"success","mergeable":false,"behind":false,
  "jobs":[{"name":"Server Tests (Core)","conclusion":"success"}]}'

# --- 9. flake-only failure but NOT mergeable -> escalate (conflict wins) ----
assert_action escalate "flake-only fail + conflict -> escalate" '{
  "run_status":"completed","run_conclusion":"failure","mergeable":false,"behind":false,
  "jobs":[{"name":"Server Tests (Core)","conclusion":"failure","flake":true}]}'

# --- 10. non-gating job failure (docker/js) with gates green -> merge ------
# Docker/JS/python jobs are NOT real gates and NOT shards; a failure there does
# not block land-fast (post-merge convergence catches cross-protocol fallout).
assert_action merge "non-gating Docker job failed, gates+shards green -> merge" '{
  "run_status":"completed","run_conclusion":"failure","mergeable":true,"behind":false,
  "jobs":[
    {"name":"Build & Format Check","conclusion":"success"},
    {"name":"Server Tests (Core)","conclusion":"success"},
    {"name":"Docker Build & Integration Test","conclusion":"failure"}
  ]}'

# --- 11. flake signature matching (is_known_flake) ------------------------
test_signature() {
  local desc="$1" name="$2" log="$3" expected="$4"
  local got; got="$(is_known_flake "$name" "$log")"
  if [[ "$got" == "$expected" ]]; then
    PASS=$((PASS + 1)); printf 'ok   [sig=%-5s]    %s\n' "$got" "$desc"
  else
    FAIL=$((FAIL + 1)); printf 'FAIL expected=%-5s got=%-5s %s\n' "$expected" "$got" "$desc"
  fi
}
test_signature "40P01 deadlock on Core shard is a flake" \
  "Server Tests (Core)" "Npgsql.PostgresException: 40P01: deadlock detected" "true"
test_signature "ryuk image-pull is a flake" \
  "Server Tests (OData Core)" "failed to pull image testcontainers.org/ryuk: toomanyrequests" "true"
test_signature "DockerUnavailable is a flake" \
  ".NET Foundation Tests" "DockerUnavailableException: Cannot connect to the Docker daemon" "true"
test_signature "ordinary assertion failure is NOT a flake" \
  "Server Tests (Core)" "Assert.Equal() Failure: Expected 200 got 500" "false"
test_signature "40P01 text on an UNscoped shard is NOT auto-flake" \
  "Server Tests (Geocoding)" "40P01 deadlock detected" "false"

echo "------------------------------------------------------------"
printf 'PASS=%d FAIL=%d\n' "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]]
