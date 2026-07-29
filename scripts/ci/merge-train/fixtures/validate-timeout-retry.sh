#!/usr/bin/env bash
# Focused offline validation for controller deadline and timeout precedence.
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
fixture_included="$(mktemp)"
fixture_metrics="$(mktemp)"
export TRAIN_METRICS_OUT="${fixture_metrics}"
history_repo="$(mktemp -d)"
fetch_root="$(mktemp -d)"
phase_log="$(mktemp)"
trap 'rm -f "${record}" "${sequence_calls:-}" "${fixture_included}" "${fixture_metrics}" "${phase_log}"; rm -rf "${history_repo}" "${fetch_root}"' EXIT
side_effect_fails=0
train_side_effect() {
  [[ "${side_effect_fails}" == "1" ]] && return 42
  printf '%s\n' "$*" >>"${record}"
}
export TRAIN_STATE_ISSUE_OVERRIDE=1
export TRAIN_STATE_BODY_OVERRIDE
TRAIN_STATE_BODY_OVERRIDE="$(train_state_render '' '' '' select '' 0 0 null)"

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

# Discovery can temporarily expose a concurrent push run, a non-dispatch run,
# and the stale old head before the exact dispatched batch tip becomes visible.
printf '0' >"${sequence_calls}"
now_value=100
sleep() { now_value=$((now_value + 1)); }
saved_gh_definition="$(declare -f gh)"
gh() {
  local discovery_poll
  discovery_poll=$(( $(cat "${sequence_calls}") + 1 ))
  printf '%s' "${discovery_poll}" >"${sequence_calls}"
  case "${discovery_poll}" in
    1) printf '201\ttrain/batch/abc/2\tworkflow_dispatch\toldoldold\tCI merge-train:nonce-b\n' ;;
    2) printf '202\ttrain/batch/abc/2\tpush\tbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tCI merge-train:nonce-b\n' ;;
    *) printf '203\ttrain/batch/abc/2\tworkflow_dispatch\tbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tCI merge-train:nonce-a\n204\ttrain/batch/abc/2\tworkflow_dispatch\tbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tCI merge-train:nonce-b\n' ;;
  esac
}
export TRAIN_SMART_CI_DISCOVERY_TIMEOUT_SECONDS=10 TRAIN_SMART_CI_DISCOVERY_POLL_SECONDS=1
discovered="$(train_discover_dispatched_run train/batch/abc/2 bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '200' 'CI merge-train:nonce-b')" \
  || fail "exact dispatched batch run was not discovered"
[[ "${discovered}" == "204" ]] || fail "discovery accepted stale head, wrong event, or concurrent run"
unset TRAIN_SMART_CI_DISCOVERY_TIMEOUT_SECONDS TRAIN_SMART_CI_DISCOVERY_POLL_SECONDS
eval "${saved_gh_definition}"
sleep() { :; }
pass "smart-CI discovery requires exact workflow_dispatch head"

# Cancellation after persisted intent is idempotent: resume does not issue a
# second rerun and reconciles by observing the newer attempt.
: >"${record}"
export TRAIN_RERUN_RESUME_STATE_JSON='{"active_batch":{"run_id":123,"phase":"timeout-retry-accepted","rerun_kind":"timeout","rerun_base_attempt":1}}'
gh_mode=sequence
printf '0' >"${sequence_calls}"
train_request_failed_job_rerun 123 timeout 1 || fail "persisted rerun intent did not reconcile"
[[ ! -s "${record}" ]] || fail "resume issued a duplicate rerun"
train_wait_for_new_run_attempt 123 "${TRAIN_RERUN_BASE_ATTEMPT}" || fail "resume did not observe accepted rerun"
unset TRAIN_RERUN_RESUME_STATE_JSON
pass "cancellation/resume idempotency"

# Two-phase request durability closes both crash windows. Requesting is written
# before send; accepted is written only after success/conflict or observed
# attempt advancement.
request_mode=success
requester() {
  printf 'request %s\n' "$1" >>"${record}"
  case "${request_mode}" in success) return 0 ;; conflict) return 4 ;; rejected) return 5 ;; hard) return 1 ;; esac
}
state_callback() { printf '%s\n' "$5" >>"${phase_log}"; }
export TRAIN_RERUN_REQUESTER=requester TRAIN_RERUN_VISIBILITY_GRACE_SECONDS=0
: >"${record}"; : >"${phase_log}"
gh_mode=attempt
train_request_failed_job_rerun 123 timeout 1 state_callback || fail "normal two-phase request failed"
[[ "$(paste -sd, "${phase_log}")" == "requesting,accepted" ]] || fail "normal request did not persist both phases in order"
[[ "$(grep -c '^request 123$' "${record}")" == "1" ]] || fail "normal request count was not one"

# A requesting record with an unchanged attempt is ambiguous: the POST may have
# succeeded even when visibility is delayed. Restart must preserve it and never
# repeat the non-idempotent request.
: >"${record}"; : >"${phase_log}"
export TRAIN_RERUN_RESUME_STATE_JSON='{"active_batch":{"run_id":123,"phase":"timeout-retry-requesting","rerun_kind":"timeout","rerun_base_attempt":1}}'
train_run_attempt_status() { printf '1\tcompleted\n'; }
request_mode=success
rc=0
train_request_failed_job_rerun 123 timeout 1 state_callback || rc=$?
[[ "${rc}" == "4" ]] || fail "ambiguous requesting state did not fail closed"
[[ ! -s "${record}" ]] || fail "ambiguous requesting state repeated the rerun POST"
[[ ! -s "${phase_log}" ]] || fail "ambiguous requesting state was falsely advanced"

# Crash after GitHub accepts but before accepted-state persistence observes the
# advanced attempt and must not send again.
: >"${record}"; : >"${phase_log}"
train_run_attempt_status() { printf '2\tqueued\n'; }
train_request_failed_job_rerun 123 timeout 1 state_callback || fail "post-accept crash did not reconcile"
[[ ! -s "${record}" ]] || fail "post-accept crash issued a duplicate request"
[[ "$(cat "${phase_log}")" == "accepted" ]] || fail "post-accept recovery did not persist accepted"

# Delayed visibility inside the bounded grace also suppresses a duplicate send.
: >"${record}"; : >"${phase_log}"
export TRAIN_RERUN_VISIBILITY_GRACE_SECONDS=10
now_value=100
printf '0' >"${sequence_calls}"
train_run_attempt_status() {
  local n; n=$(( $(cat "${sequence_calls}") + 1 )); printf '%s' "${n}" >"${sequence_calls}"
  [[ "${n}" -lt 3 ]] && printf '1\tcompleted\n' || printf '2\tqueued\n'
}
train_request_failed_job_rerun 123 timeout 1 state_callback || fail "delayed acceptance visibility did not reconcile"
[[ ! -s "${record}" ]] || fail "delayed visibility issued a duplicate request"

# Visibility delayed beyond the grace remains ambiguous and must still never
# issue a second POST.
: >"${record}"; : >"${phase_log}"
export TRAIN_RERUN_VISIBILITY_GRACE_SECONDS=0
train_run_attempt_status() { printf '1\tcompleted\n'; }
rc=0
train_request_failed_job_rerun 123 timeout 1 state_callback || rc=$?
[[ "${rc}" == "4" && ! -s "${record}" ]] || fail "beyond-grace ambiguity repeated the rerun POST"

# A strict already-running response is accepted only with observed attempt
# advancement; conflict text alone is never evidence.
: >"${record}"; : >"${phase_log}"
unset TRAIN_RERUN_RESUME_STATE_JSON
export TRAIN_RERUN_VISIBILITY_GRACE_SECONDS=0
train_run_attempt_status() { printf '2\tqueued\n'; }
request_mode=conflict
train_request_failed_job_rerun 123 timeout 1 state_callback || fail "rerun conflict was not reconciled as accepted"
[[ "$(paste -sd, "${phase_log}")" == "requesting,accepted" ]] || fail "rerun conflict did not persist two-phase acceptance"

# Production stderr with a bare HTTP 409 and no attempt advancement remains
# ambiguous/requesting and cannot be promoted to accepted.
: >"${record}"; : >"${phase_log}"
unset TRAIN_RERUN_REQUESTER
export TRAIN_APPLY=1
train_run_attempt_status() { printf '1\tcompleted\n'; }
gh() {
  if [[ "$*" == *'--json attempt'* ]]; then printf '1\n'
  elif [[ "$*" == 'run rerun 123 --failed' ]]; then printf 'HTTP 409: Conflict\n' >&2; return 1
  else fail "bare-409 attempted unexpected gh operation: $*"
  fi
}
rc=0
train_request_failed_job_rerun 123 timeout 1 state_callback || rc=$?
[[ "${rc}" == "4" ]] || fail "bare HTTP 409 was not kept ambiguous"
[[ "$(cat "${phase_log}")" == "requesting" ]] || fail "bare HTTP 409 was falsely persisted accepted"
export TRAIN_APPLY=0
unset TRAIN_RERUN_RESUME_STATE_JSON TRAIN_RERUN_REQUESTER TRAIN_RERUN_VISIBILITY_GRACE_SECONDS
train_run_attempt_status() {
  gh run view "$1" --json attempt,status --jq '[.attempt, .status] | @tsv' 2>/dev/null
}
pass "two-phase rerun crash, visibility, and conflict recovery"

# State authority is trustworthy only after a successful lookup returning zero
# or one issue. Failures/duplicates block reads and writes; successful zero is
# the only path allowed to create the initial issue.
unset TRAIN_STATE_ISSUE_OVERRIDE TRAIN_STATE_BODY_OVERRIDE
state_body_file="$(mktemp)"
printf 'state\n' >"${state_body_file}"
state_case=list_fail
gh() {
  if [[ "$*" == issue\ list* ]]; then
    case "${state_case}" in list_fail) return 1 ;; duplicate) printf '11\n12\n' ;; zero) : ;; *) printf '11\n' ;; esac
  elif [[ "$*" == 'issue view 11 --json body --jq .body' ]]; then
    [[ "${state_case}" == "read_fail" ]] && return 1
    printf '```json\n{"active_batch":{"phase":"select"}}\n```\n'
  else
    fail "state authority attempted unexpected gh operation: $*"
  fi
}
rc=0; train_state_read >/dev/null || rc=$?
[[ "${rc}" != "0" ]] || fail "state list failure was treated as no state"
: >"${record}"; rc=0; train_state_write "${state_body_file}" || rc=$?
[[ "${rc}" != "0" && ! -s "${record}" ]] || fail "state list failure allowed issue creation"
state_case=read_fail
rc=0; train_state_read >/dev/null || rc=$?
[[ "${rc}" != "0" ]] || fail "state body read failure was treated as no state"
state_case=duplicate
rc=0; train_state_read >/dev/null || rc=$?
[[ "${rc}" != "0" ]] || fail "duplicate state issues were accepted for read"
: >"${record}"; rc=0; train_state_write "${state_body_file}" || rc=$?
[[ "${rc}" != "0" && ! -s "${record}" ]] || fail "duplicate state issues allowed edit/create"
state_case=zero
: >"${record}"; train_state_write "${state_body_file}" || fail "proven initial state absence did not allow creation"
grep -Fq 'gh issue create' "${record}" || fail "successful zero-result lookup did not use initial creation path"
# Live invocations cannot race or ambiguously create state authorities: without
# the pre-provisioned fixed ID, every simultaneous zero lookup fails before a
# create side effect.
export TRAIN_APPLY=1
: >"${record}"
rc_a=0; train_state_write "${state_body_file}" || rc_a=$?
rc_b=0; train_state_write "${state_body_file}" || rc_b=$?
[[ "${rc_a}" != "0" && "${rc_b}" != "0" && ! -s "${record}" ]] || fail "simultaneous live zero lookups attempted ambiguous state creation"
export TRAIN_APPLY=0
rm -f "${state_body_file}"
pass "state lookup/read/write authority fails closed"

# Production startup restoration: matching persisted intent waits on the old
# run id, accepts only completed(new), and performs no dispatch/rerun side effect.
: >"${record}"
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/1","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"timeout-retry-accepted","run_id":123,"fwdfix_attempts":0,"flake_reruns":0,"timeout_reruns":1,"rerun_kind":"timeout","rerun_base_attempt":1}}\n```'
export TRAIN_STATE_ISSUE_OVERRIDE=1
fixture_batch_sha=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
fixture_member_sha=cccccccccccccccccccccccccccccccccccccccc
resume_fetcher() {
  [[ "$1" == "train/batch/abc/1" && "$2" == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ]] \
    || fail "resume fetcher received the wrong batch/base"
  printf '%s\n' "${fixture_batch_sha}"
}
resume_ancestry() {
  [[ "$1:$2" == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:${fixture_batch_sha}" ]]
}
resume_member_head() {
  [[ "$1:$2:$3" == "101:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:${fixture_batch_sha}" ]] \
    || fail "member-head resolver received the wrong PR/base/batch"
  printf '%s\n' "${fixture_member_sha}"
}
export TRAIN_RESUME_FETCHER=resume_fetcher
export TRAIN_RESUME_ANCESTRY_CHECKER=resume_ancestry
export TRAIN_RESUME_MEMBER_HEAD_RESOLVER=resume_member_head
resume_identity_event=workflow_dispatch
resume_identity_path=.github/workflows/ci.yml
resume_identity_head="${fixture_batch_sha}"
resume_run_identity() {
  printf 'train/batch/abc/1\t%s\t1\t%s\t%s\n' "${resume_identity_head}" "${resume_identity_event}" "${resume_identity_path}"
}
export TRAIN_RESUME_RUN_IDENTITY_READER=resume_run_identity
export TRAIN_INCLUDED_FILE="${fixture_included}"
train_smart_ci_shards() { printf '{"run_all":false,"shards":["OData Core"],"reason":"resume"}\n'; }
gh_mode=resume
printf '0' >"${sequence_calls}"
gh() {
  if [[ "$*" == *'--json headBranch,headSha,attempt'* ]]; then
    printf 'train/batch/abc/1\t%s\t1\n' "${fixture_batch_sha}"
  elif [[ "$*" == 'pr view 101 --json number,state,headRefOid,createdAt,author' ]]; then
    printf '{"number":101,"state":"OPEN","headRefOid":"%s","createdAt":"2026-01-01T00:00:00Z","author":{"login":"alice"}}\n' "${fixture_member_sha}"
  elif [[ "$*" == *'--json attempt,status'* ]]; then
    local n; n=$(( $(cat "${sequence_calls}") + 1 )); printf '%s' "${n}" >"${sequence_calls}"
    case "${n}" in 1) printf '1\tcompleted\n' ;; 2) printf '2\tqueued\n' ;; *) printf '2\tcompleted\n' ;; esac
  elif [[ "$*" == *'--json jobs'* ]]; then
    printf 'success\n'
  else
    fail "resume startup attempted unexpected gh operation: $*"
  fi
}
resumed_json="$(train_restore_retry_intent)" || fail "production startup did not restore retry intent"
[[ "$(jq -r '.resume_gate' <<<"${resumed_json}")" == "SUCCESS" ]] || fail "resumed main path did not recover new-attempt gate"
[[ "$(cat "${fixture_included}")" == $'101\t'"${fixture_member_sha}" ]] || fail "resume did not reconstruct the exact included member head"
[[ ! -s "${record}" ]] || fail "resumed main path dispatched a batch or duplicate rerun"
pass "restarted-main production resume path"

# The production fetch path must refresh the remote-tracking batch ref rather
# than leave a newer command-line fetch reachable only through FETCH_HEAD.
git -C "${fetch_root}" init -q --bare remote.git
git -C "${fetch_root}" init -q -b trunk source
git -C "${fetch_root}/source" config user.email fixture@example.invalid
git -C "${fetch_root}/source" config user.name fixture
printf 'base\n' >"${fetch_root}/source/data"
git -C "${fetch_root}/source" add data
git -C "${fetch_root}/source" commit -q -m base
git -C "${fetch_root}/source" remote add fixture "${fetch_root}/remote.git"
git -C "${fetch_root}/source" push -q fixture trunk
git -C "${fetch_root}/source" checkout -q -b train/batch/abc/1
printf 'batch-v1\n' >>"${fetch_root}/source/data"
git -C "${fetch_root}/source" commit -qam batch-v1
git -C "${fetch_root}/source" push -q fixture train/batch/abc/1
git -C "${fetch_root}" init -q checkout
git -C "${fetch_root}/checkout" remote add fixture "${fetch_root}/remote.git"
git -C "${fetch_root}/checkout" fetch -q fixture \
  refs/heads/trunk:refs/remotes/fixture/trunk \
  refs/heads/train/batch/abc/1:refs/remotes/fixture/train/batch/abc/1
git -C "${fetch_root}/source" reset -q --hard trunk
printf 'batch-v2\n' >>"${fetch_root}/source/data"
git -C "${fetch_root}/source" commit -qam batch-v2
git -C "${fetch_root}/source" push -q --force fixture train/batch/abc/1
expected_fetch_sha="$(git -C "${fetch_root}/source" rev-parse HEAD)"
saved_repo_root="${TRAIN_REPO_ROOT}"
saved_remote="${TRAIN_REMOTE}"
TRAIN_REPO_ROOT="${fetch_root}/checkout"
TRAIN_REMOTE=fixture
unset TRAIN_RESUME_FETCHER
fetched_sha="$(_train_resume_fetch_batch train/batch/abc/1)" || fail "production batch fetch failed"
[[ "${fetched_sha}" == "${expected_fetch_sha}" ]] || fail "production batch fetch returned a stale remote-tracking ref"
[[ "$(git -C "${TRAIN_REPO_ROOT}" rev-parse fixture/train/batch/abc/1)" == "${expected_fetch_sha}" ]] \
  || fail "production batch fetch did not refresh its remote-tracking destination"
[[ "$(git -C "${TRAIN_REPO_ROOT}" rev-parse train/batch/abc/1)" == "${expected_fetch_sha}" ]] \
  || fail "production batch fetch did not refresh the local batch branch"
TRAIN_REPO_ROOT="${saved_repo_root}"
TRAIN_REMOTE="${saved_remote}"
export TRAIN_RESUME_FETCHER=resume_fetcher
pass "production resume refreshes the remote-tracking batch ref"

# Production resolver follows immutable first-parent history, ignores a later
# generated-artifact commit, and returns the train merge's exact second parent.
git -C "${history_repo}" init -q
git -C "${history_repo}" config user.email fixture@example.invalid
git -C "${history_repo}" config user.name fixture
printf 'base\n' >"${history_repo}/data"
git -C "${history_repo}" add data
git -C "${history_repo}" commit -q -m base
history_trunk="$(git -C "${history_repo}" rev-parse HEAD)"
git -C "${history_repo}" checkout -q -b member
printf 'member\n' >>"${history_repo}/data"
git -C "${history_repo}" commit -qam member
history_member="$(git -C "${history_repo}" rev-parse HEAD)"
git -C "${history_repo}" checkout -q master
git -C "${history_repo}" merge -q --no-ff -m 'train: merge #101' member
printf 'generated\n' >"${history_repo}/generated"
git -C "${history_repo}" add generated
git -C "${history_repo}" commit -q -m 'chore(ci): refresh generated merge-train artifacts'
history_batch="$(git -C "${history_repo}" rev-parse HEAD)"
saved_repo_root="${TRAIN_REPO_ROOT}"
TRAIN_REPO_ROOT="${history_repo}"
unset TRAIN_RESUME_MEMBER_HEAD_RESOLVER
[[ "$(_train_resume_member_head 101 "${history_trunk}" "${history_batch}")" == "${history_member}" ]] \
  || fail "production resolver did not derive the train merge's exact second parent"
TRAIN_REPO_ROOT="${saved_repo_root}"
export TRAIN_RESUME_MEMBER_HEAD_RESOLVER=resume_member_head
pass "immutable batch-history member-head derivation"

# A force-push back to an older ancestor must not replace the exact merge
# parent recorded in the validated batch.
force_pushed_back_sha=dddddddddddddddddddddddddddddddddddddddd
gh() {
  if [[ "$*" == *'--json headBranch,headSha,attempt'* ]]; then
    printf 'train/batch/abc/1\t%s\t1\n' "${fixture_batch_sha}"
  elif [[ "$*" == 'pr view 101 --json number,state,headRefOid,createdAt,author' ]]; then
    printf '{"number":101,"state":"OPEN","headRefOid":"%s","createdAt":"2026-01-01T00:00:00Z","author":{"login":"alice"}}\n' "${force_pushed_back_sha}"
  else
    fail "force-push-back attempted unexpected gh operation: $*"
  fi
}
rc=0
train_restore_retry_intent >/dev/null || rc=$?
[[ "${rc}" == "2" ]] || fail "force-pushed-back PR head replaced the validated merge parent"

# The Actions run must belong to the exact fetched batch SHA, and the fetched
# batch must descend from the stored trunk base.
gh() {
  if [[ "$*" == *'--json headBranch,headSha,attempt'* ]]; then
    printf 'train/batch/abc/1\tdddddddddddddddddddddddddddddddddddddddd\t1\n'
  else
    fail "exact-head mismatch attempted unexpected gh operation: $*"
  fi
}
resume_identity_head=dddddddddddddddddddddddddddddddddddddddd
rc=0
train_restore_retry_intent >/dev/null || rc=$?
[[ "${rc}" == "2" ]] || fail "Actions head SHA mismatch did not fail closed"
resume_identity_head="${fixture_batch_sha}"
resume_identity_event=push
rc=0
train_restore_retry_intent >/dev/null || rc=$?
[[ "${rc}" == "2" ]] || fail "non-workflow_dispatch retry run did not fail closed"
resume_identity_event=workflow_dispatch
resume_identity_path=.github/workflows/other.yml
rc=0
train_restore_retry_intent >/dev/null || rc=$?
[[ "${rc}" == "2" ]] || fail "wrong workflow path did not fail closed"
resume_identity_path=.github/workflows/ci.yml
reject_base() { [[ "$1" != "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ]]; }
export TRAIN_RESUME_ANCESTRY_CHECKER=reject_base
rc=0
train_restore_retry_intent >/dev/null || rc=$?
[[ "${rc}" == "2" ]] || fail "non-descendant batch did not fail closed"
export TRAIN_RESUME_ANCESTRY_CHECKER=resume_ancestry
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":\n```'
rc=0
train_restore_retry_intent >/dev/null || rc=$?
[[ "${rc}" == "2" ]] || fail "malformed nonempty retry state was treated as no retry"
unset TRAIN_STATE_BODY_OVERRIDE
pass "resume exact-head, descendant-base, and malformed-state guards"
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"phase":"select"}}\n```'

# Restore the simple attempt reader for the remaining classifier cases.
gh() { printf '1\n'; }

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

# End-to-end restarted main: controller A already consumed one timeout retry,
# then run B accepted its own retry and controller B restarted. Prove startup
# restores cumulative telemetry while consuming B without dispatch or duplicate
# rerun.
export TRAIN_SOURCE_ONLY=1 TRAIN_APPLY=1 TRAIN_RESUME_STARTUP_TEST_ONLY=0
. "${TRAIN_DIR}/train.sh"
train_side_effect() { printf '%s\n' "$*" >>"${record}"; }
unset TRAIN_STATE_ISSUE_OVERRIDE TRAIN_STATE_BODY_OVERRIDE
: >"${record}"
gh() { [[ "$*" == issue\ list* ]] && return 1; fail "state-list startup failure attempted unexpected gh operation: $*"; }
train_select() { fail "state-list startup failure reached selection"; }
rc=0
main || rc=$?
[[ "${rc}" == "1" && ! -s "${record}" ]] || fail "state-list failure did not stop controller before selection"
pass "startup state-list failure stops before selection"
export TRAIN_STATE_ISSUE_OVERRIDE=1

# Known terminal phases from the production controller must release every member
# and clear state before selection. This shape mirrors live #2044, including the
# older schema without timeout/rerun fields.
for terminal_phase in ci-incomplete rerun-command-failed; do
  printf -v TRAIN_STATE_BODY_OVERRIDE '```json\n{"active_batch":{"branch":"train/batch/32e1094/1784629905","trunk_base":"32e109480c146422748037fe6854e6ec2d8c391c","included":[2960,2961],"phase":"%s","run_id":29823085973,"fwdfix_attempts":0,"flake_reruns":0},"config":{"max_batch":10},"last_landed_trunk":null}\n```\n' "${terminal_phase}"
  export TRAIN_STATE_BODY_OVERRIDE
  : >"${record}"
  train_select() { printf 'selection-entered\n' >>"${record}"; }
  unset TRAIN_CONTROLLER_DEADLINE_EPOCH
  main || fail "${terminal_phase} startup recovery failed"
  for terminal_pr in 2960 2961; do
    grep -Fqx "gh pr edit ${terminal_pr} --add-label ${TRAIN_LABEL_ESCALATED}" "${record}" || fail "${terminal_phase} did not escalate #${terminal_pr}"
    grep -Fqx "gh pr edit ${terminal_pr} --remove-label ${TRAIN_LABEL_LANDING}" "${record}" || fail "${terminal_phase} did not release #${terminal_pr}"
  done
  grep -Fq 'gh issue edit 1 --body-file' "${record}" || fail "${terminal_phase} did not clear singleton state"
  grep -Fqx 'selection-entered' "${record}" || fail "${terminal_phase} did not continue after cleanup"
  ! grep -Eq 'gh (workflow run|pr merge)|git push' "${record}" || fail "${terminal_phase} requeued or landed stale batch"
done
pass "known terminal active phases recover before selection"

# Restart after an all-conflict normal exit sees the durable cleared select
# state and may select again instead of failing on stale assemble.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[],"phase":"select","run_id":null},"last_landed_trunk":null}\n```'
: >"${record}"
train_select() { printf 'selection-entered\n' >>"${record}"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "restart after all-conflict clear failed"
grep -Fqx 'selection-entered' "${record}" || fail "restart after all-conflict clear did not select"
pass "all-conflict cleared state restarts safely"

# Restart after a crash anywhere in trunk-moved cleanup releases landing labels
# without escalation, clears state, and reselects for fresh assembly.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/7","trunk_base":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","included":[101],"phase":"trunk-moved-reassemble","run_id":123,"fwdfix_attempts":0,"flake_reruns":0,"timeout_reruns_total":0},"last_landed_trunk":null}\n```'
: >"${record}"
train_select() { printf 'selection-entered\n' >>"${record}"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "restart after trunk-moved phase failed"
grep -Fqx "gh pr edit 101 --remove-label ${TRAIN_LABEL_LANDING}" "${record}" || fail "trunk-moved restart did not release landing label"
! grep -Fq -- "--add-label ${TRAIN_LABEL_ESCALATED}" "${record}" || fail "trunk-moved restart incorrectly escalated member"
grep -Fq 'gh issue edit 1 --body-file' "${record}" || fail "trunk-moved restart did not clear state"
grep -Fqx 'selection-entered' "${record}" || fail "trunk-moved restart did not reselect"
pass "trunk-moved state restarts through release-only recovery"

# --- #3045: no accepted phase may be unrecoverable ---------------------------
# A run that ended during `attribute` used to strand the train: the read schema
# accepted the phase, terminal recovery had no branch for it, and every later
# dispatch failed closed before selection until #2044 was hand-edited. This
# mirrors the live state of batch train/batch/f012686/1785307743.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/f012686/1785307743","trunk_base":"f0126862e2f4f4bd1f60ea25b2a3f83d3e37e1b7","included":[3040,3042,3043],"phase":"attribute","run_id":30435781232,"fwdfix_attempts":0,"flake_reruns":0,"timeout_reruns_total":0},"config":{"max_batch":10},"last_landed_trunk":null}\n```'
: >"${record}"
train_select() { printf 'selection-entered\n' >>"${record}"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "restart after an interrupted attribution failed"
for stranded_pr in 3040 3042 3043; do
  grep -Fqx "gh pr edit ${stranded_pr} --remove-label ${TRAIN_LABEL_LANDING}" "${record}" \
    || fail "interrupted attribution did not release #${stranded_pr}"
done
! grep -Fq -- "--add-label ${TRAIN_LABEL_ESCALATED}" "${record}" \
  || fail "interrupted attribution escalated a member it never attributed"
! grep -Fq -- "--remove-label ${TRAIN_LABEL_ESCALATED}" "${record}" \
  || fail "interrupted attribution discarded an escalation a member already received"
grep -Fq 'gh issue edit 1 --body-file' "${record}" || fail "interrupted attribution did not clear state"
grep -Fqx 'selection-entered' "${record}" || fail "interrupted attribution did not reselect"
pass "run stranded mid-attribute recovers and selection proceeds"

# The attribution rebuild persists the surviving members under "assemble" with
# no branch yet. A crash in that window leaves members holding train:landing
# with nothing assembled, and must still release rather than fail closed.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101,102],"phase":"assemble","run_id":null,"fwdfix_attempts":0,"flake_reruns":0}}\n```'
: >"${record}"
train_select() { printf 'selection-entered\n' >>"${record}"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "restart after a branchless rebuild-assemble failed"
grep -Fqx "gh pr edit 102 --remove-label ${TRAIN_LABEL_LANDING}" "${record}" \
  || fail "branchless rebuild-assemble did not release its members"
! grep -Fq -- "--add-label ${TRAIN_LABEL_ESCALATED}" "${record}" \
  || fail "branchless rebuild-assemble escalated an unattributed member"
grep -Fqx 'selection-entered' "${record}" || fail "branchless rebuild-assemble did not reselect"
pass "branchless rebuild-assemble state releases and reselects"

# TOTALITY: every phase the read schema accepts must have a recovery owner, and
# a terminal one must actually clear state and let selection proceed. Without
# this, a phase added to the schema alone re-creates the #3045 deadlock.
for accepted_phase in "${TRAIN_STATE_PHASES[@]}"; do
  case "${TRAIN_PHASE_RECOVERY[${accepted_phase}]:-}" in
    escalate|release) ;;
    retry|post-land) continue ;;
    *) fail "phase ${accepted_phase} has no recovery class" ;;
  esac
  printf -v TRAIN_STATE_BODY_OVERRIDE '```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"%s","run_id":123,"fwdfix_attempts":0,"flake_reruns":0}}\n```\n' "${accepted_phase}"
  export TRAIN_STATE_BODY_OVERRIDE
  : >"${record}"
  train_select() { printf 'selection-entered\n' >>"${record}"; }
  unset TRAIN_CONTROLLER_DEADLINE_EPOCH
  main || fail "accepted phase ${accepted_phase} was unrecoverable at startup"
  grep -Fqx "gh pr edit 101 --remove-label ${TRAIN_LABEL_LANDING}" "${record}" \
    || fail "accepted phase ${accepted_phase} did not release its members"
  grep -Fq 'gh issue edit 1 --body-file' "${record}" \
    || fail "accepted phase ${accepted_phase} did not clear state"
  grep -Fqx 'selection-entered' "${record}" \
    || fail "accepted phase ${accepted_phase} did not continue to selection"
  ! grep -Eq 'gh (workflow run|pr merge)|git push' "${record}" \
    || fail "accepted phase ${accepted_phase} requeued or landed a stale batch"
  if [[ "${TRAIN_PHASE_RECOVERY[${accepted_phase}]}" == "release" ]]; then
    ! grep -Fq -- "--add-label ${TRAIN_LABEL_ESCALATED}" "${record}" \
      || fail "release-class phase ${accepted_phase} added an unattributed escalation"
  else
    grep -Fqx "gh pr edit 101 --add-label ${TRAIN_LABEL_ESCALATED}" "${record}" \
      || fail "escalate-class phase ${accepted_phase} did not escalate its member"
  fi
done
pass "every accepted terminal phase recovers before selection"

# Retry-class phases are owned by train_restore_retry_intent. Terminal recovery
# must defer to it — never release the batch or overwrite the retry intent.
for retry_phase in timeout-retry-intent timeout-retry-requesting timeout-retry-accepted \
                   flake-retry-intent flake-retry-requesting flake-retry-accepted; do
  printf -v TRAIN_STATE_BODY_OVERRIDE '```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"%s","run_id":123,"fwdfix_attempts":0,"flake_reruns":0}}\n```\n' "${retry_phase}"
  export TRAIN_STATE_BODY_OVERRIDE
  : >"${record}"
  train_select() { fail "retry-class phase ${retry_phase} reached selection"; }
  unset TRAIN_CONTROLLER_DEADLINE_EPOCH
  rc=0
  main || rc=$?
  [[ "${rc}" == "1" && ! -s "${record}" ]] \
    || fail "retry-class phase ${retry_phase} did not defer to retry restoration"
done
pass "retry-class phases defer to retry restoration without mutation"

# DRIFT GUARD: the read schema's phase list (state.sh) and the recovery dispatch
# table (train.sh) are asserted consistent in BOTH directions, with negative
# controls proving the guard actually detects each drift direction.
[[ -z "$(train_state_phase_recovery_drift)" ]] \
  || fail "phase recovery drift: $(train_state_phase_recovery_drift | tr '\n' ' ')"
TRAIN_STATE_PHASES+=(fixture-unclassified-phase)
grep -Fqx 'unrecoverable-phase fixture-unclassified-phase' <<<"$(train_state_phase_recovery_drift)" \
  || fail "drift guard missed a schema phase with no recovery class"
unset 'TRAIN_STATE_PHASES[-1]'
TRAIN_PHASE_RECOVERY[fixture-orphan-phase]=release
grep -Fqx 'orphan-recovery-class fixture-orphan-phase' <<<"$(train_state_phase_recovery_drift)" \
  || fail "drift guard missed a recovery class for a phase the schema rejects"
unset 'TRAIN_PHASE_RECOVERY[fixture-orphan-phase]'
TRAIN_PHASE_RECOVERY[attribute]=nonsense
grep -Fqx 'unknown-recovery-class attribute=nonsense' <<<"$(train_state_phase_recovery_drift)" \
  || fail "drift guard missed an unknown recovery class value"
TRAIN_PHASE_RECOVERY[attribute]=release
[[ -z "$(train_state_phase_recovery_drift)" ]] || fail "drift guard did not restore cleanly"
pass "schema phases and recovery dispatch cannot drift apart"

# Non-vacuity: TRAIN_STATE_PHASES is really what the read schema enforces, so
# the drift guard is comparing the live list rather than a decorative copy.
for accepted_phase in "${TRAIN_STATE_PHASES[@]}"; do
  case "${accepted_phase}" in
    land|pre-land-cleanup|post-land-finalize)
      printf -v TRAIN_STATE_BODY_OVERRIDE '```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"%s","run_id":123,"included_heads":[{"number":101,"head":"cccccccccccccccccccccccccccccccccccccccc"}],"batch_sha":"dddddddddddddddddddddddddddddddddddddddd"}}\n```\n' "${accepted_phase}" ;;
    *)
      printf -v TRAIN_STATE_BODY_OVERRIDE '```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"%s","run_id":123}}\n```\n' "${accepted_phase}" ;;
  esac
  export TRAIN_STATE_BODY_OVERRIDE
  train_state_read >/dev/null || fail "read schema rejected accepted phase ${accepted_phase}"
done
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"not-a-real-phase","run_id":123}}\n```'
rc=0
train_state_read >/dev/null || rc=$?
[[ "${rc}" == "3" ]] || fail "read schema accepted a phase outside TRAIN_STATE_PHASES"
pass "read schema accepts exactly TRAIN_STATE_PHASES"

# State the read schema itself rejects still has no recovery contract and must
# stop without overwriting state, touching labels, or entering selection.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"not-a-real-phase","run_id":123}}\n```'
: >"${record}"
train_select() { fail "unreadable active state reached selection"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
rc=0
main || rc=$?
[[ "${rc}" == "1" && ! -s "${record}" ]] || fail "unreadable active state did not fail closed without mutation"
pass "state outside the accepted schema stops before overwrite or selection"

# --- sanctioned reset path (#3045 AC4) ---------------------------------------
# The operator escape hatch replaces hand-editing the machine-managed state
# issue. It clears the batch to the exact shape train_state_render emits, stops
# before selection, and refuses while durable land intent is outstanding.
export TRAIN_RESET_STATE=1
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/f012686/1785307743","trunk_base":"f0126862e2f4f4bd1f60ea25b2a3f83d3e37e1b7","included":[3040,3042],"phase":"attribute","run_id":30435781232},"config":{"max_batch":10},"last_landed_trunk":null}\n```'
: >"${record}"
reset_capture="$(mktemp)"
train_side_effect() {
  printf '%s\n' "$*" >>"${record}"
  [[ "$1 $2 $3 $5" == "gh issue edit --body-file" ]] && cp "$6" "${reset_capture}"
  return 0
}
train_select() { fail "state reset continued into selection"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "operator state reset failed"
for reset_pr in 3040 3042; do
  grep -Fqx "gh pr edit ${reset_pr} --remove-label ${TRAIN_LABEL_LANDING}" "${record}" \
    || fail "state reset did not release #${reset_pr}"
done
! grep -Fq -- "--add-label" "${record}" || fail "state reset added a label"
[[ -s "${reset_capture}" ]] || fail "state reset never wrote the state issue"
reset_json="$(sed -n '/^```json$/,/^```$/p' "${reset_capture}" | sed '1d;$d')"
[[ "$(jq -r '.active_batch | type' <<<"${reset_json}")" == "object" ]] \
  || fail "state reset wrote an active_batch the read schema rejects"
[[ "$(jq -r '.active_batch.branch' <<<"${reset_json}")" == "" ]] || fail "state reset kept a branch"
[[ "$(jq -c '.active_batch.included' <<<"${reset_json}")" == "[]" ]] || fail "state reset kept members"
[[ "$(jq -r '.active_batch.phase' <<<"${reset_json}")" == "select" ]] || fail "state reset did not return to select"
[[ "$(jq -r '.active_batch.run_id' <<<"${reset_json}")" == "null" ]] || fail "state reset kept a run id"
[[ "$(jq -r '.config.max_batch' <<<"${reset_json}")" == "${MAX_BATCH}" ]] || fail "state reset dropped config"
TRAIN_STATE_BODY_OVERRIDE="$(printf '```json\n%s\n```' "${reset_json}")"
export TRAIN_STATE_BODY_OVERRIDE
train_state_read >/dev/null || fail "state reset wrote a body the read schema cannot parse"
pass "operator state reset clears the batch to a readable cleared shape"

# Schema-INVALID state is exactly what an emergency hand edit leaves behind
# (the live `active_batch: null` repair). The reset must repair it too, or the
# operator is sent straight back to editing the machine-managed issue by hand.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":null,"config":{"max_batch":10},"last_landed_trunk":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"}\n```'
rc=0
train_state_read >/dev/null 2>&1 || rc=$?
[[ "${rc}" == "3" ]] || fail "fixture premise wrong: active_batch:null is readable"
: >"${record}"
: >"${reset_capture}"
train_select() { fail "salvaging state reset continued into selection"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "operator state reset could not repair schema-invalid state"
[[ -s "${reset_capture}" ]] || fail "salvaging reset never wrote the state issue"
reset_json="$(sed -n '/^```json$/,/^```$/p' "${reset_capture}" | sed '1d;$d')"
[[ "$(jq -r '.active_batch | type' <<<"${reset_json}")" == "object" ]] \
  || fail "salvaging reset left active_batch non-object"
[[ "$(jq -r '.active_batch.phase' <<<"${reset_json}")" == "select" ]] || fail "salvaging reset did not return to select"
[[ "$(jq -r '.last_landed_trunk' <<<"${reset_json}")" == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" ]] \
  || fail "salvaging reset lost the last-landed record it could still read"
TRAIN_STATE_BODY_OVERRIDE="$(printf '```json\n%s\n```' "${reset_json}")"
export TRAIN_STATE_BODY_OVERRIDE
train_state_read >/dev/null || fail "salvaging reset wrote a body the read schema cannot parse"
pass "operator state reset repairs schema-invalid state"

# Salvage still releases members and preserves telemetry it can read.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101,102],"phase":"not-a-real-phase","run_id":123,"timeout_reruns_total":4},"last_landed_trunk":null}\n```'
: >"${record}"
: >"${reset_capture}"
train_select() { fail "salvaging state reset continued into selection"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "operator state reset could not repair an unknown persisted phase"
for reset_pr in 101 102; do
  grep -Fqx "gh pr edit ${reset_pr} --remove-label ${TRAIN_LABEL_LANDING}" "${record}" \
    || fail "salvaging reset did not release #${reset_pr}"
done
reset_json="$(sed -n '/^```json$/,/^```$/p' "${reset_capture}" | sed '1d;$d')"
[[ "$(jq -r '.active_batch.trunk_base' <<<"${reset_json}")" == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ]] \
  || fail "salvaging reset lost the recorded trunk base"
[[ "$(jq -r '.active_batch.timeout_reruns_total' <<<"${reset_json}")" == "4" ]] \
  || fail "salvaging reset lost cumulative rerun telemetry"
pass "operator state reset salvages members and telemetry from invalid state"

# Salvage must refuse durable land intent even when the body is schema-invalid
# or not JSON at all — the raw-text guard, not the parser, is what protects it.
for salvage_land_body in \
  $'```json\n{"active_batch":{"phase":"land","included":["oops"]}}\n```' \
  $'```json\n{"active_batch":{"phase":"not-a-real-phase","batch_sha":"dddddddddddddddddddddddddddddddddddddddd"}}\n```' \
  $'```json\n{"active_batch": {"phase": "post-land-finalize", NOT VALID JSON\n```'
do
  TRAIN_STATE_BODY_OVERRIDE="${salvage_land_body}"
  export TRAIN_STATE_BODY_OVERRIDE
  : >"${record}"
  train_select() { fail "refused salvage reached selection"; }
  unset TRAIN_CONTROLLER_DEADLINE_EPOCH
  rc=0
  main || rc=$?
  [[ "${rc}" == "1" && ! -s "${record}" ]] \
    || fail "salvage did not refuse durable land intent without mutation"
done
pass "salvage refuses durable land intent in unreadable state"

# Totally illegible state still resets: there is no detectable land intent to
# protect, and this is the last resort that replaces a hand edit.
export TRAIN_STATE_BODY_OVERRIDE='this body has no machine-state block at all'
: >"${record}"
: >"${reset_capture}"
train_select() { fail "illegible-state reset continued into selection"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "operator state reset could not repair an illegible body"
reset_json="$(sed -n '/^```json$/,/^```$/p' "${reset_capture}" | sed '1d;$d')"
[[ "$(jq -r '.active_batch.phase' <<<"${reset_json}")" == "select" ]] || fail "illegible-state reset did not clear"
TRAIN_STATE_BODY_OVERRIDE="$(printf '```json\n%s\n```' "${reset_json}")"
export TRAIN_STATE_BODY_OVERRIDE
train_state_read >/dev/null || fail "illegible-state reset wrote an unreadable body"
pass "operator state reset repairs a body with no machine-state block"

# A durable land intent must reconcile against trunk before any reset.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"land","run_id":123,"included_heads":[{"number":101,"head":"cccccccccccccccccccccccccccccccccccccccc"}],"batch_sha":"dddddddddddddddddddddddddddddddddddddddd"}}\n```'
: >"${record}"
train_select() { fail "refused state reset reached selection"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
rc=0
main || rc=$?
[[ "${rc}" == "1" && ! -s "${record}" ]] || fail "state reset did not refuse durable land intent without mutation"
pass "state reset refuses while durable land intent is outstanding"
unset TRAIN_RESET_STATE
rm -f "${reset_capture}"
train_side_effect() {
  [[ "${side_effect_fails}" == "1" ]] && return 42
  printf '%s\n' "$*" >>"${record}"
}

# Validate every value needed to render cleared state before mutating labels.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/9","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"ci-incomplete","run_id":123,"timeout_reruns_total":"bad"},"last_landed_trunk":"not-a-sha"}\n```'
: >"${record}"
train_select() { fail "malformed terminal state reached selection"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
rc=0
main || rc=$?
[[ "${rc}" == "1" && ! -s "${record}" ]] || fail "malformed terminal render inputs mutated labels or state"
pass "malformed terminal render inputs fail before label mutation"

export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/1","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"timeout-retry-accepted","run_id":123,"fwdfix_attempts":0,"flake_reruns":0,"timeout_reruns":1,"timeout_reruns_total":2,"rerun_kind":"timeout","rerun_base_attempt":1}}\n```'
export TRAIN_RESUME_FETCHER=resume_fetcher
train_select() { fail "restarted main incorrectly entered selection"; }
train_smart_ci_shards() { printf '{"run_all":false,"shards":["OData Core"],"reason":"resume"}\n'; }
gh() {
  if [[ "$*" == *'--json headBranch,headSha,attempt'* ]]; then printf 'train/batch/abc/1\t%s\t1\n' "${fixture_batch_sha}"
  elif [[ "$*" == 'pr view 101 --json number,state,headRefOid,createdAt,author' ]]; then printf '{"number":101,"state":"OPEN","headRefOid":"%s","createdAt":"2026-01-01T00:00:00Z","author":{"login":"alice"}}\n' "${fixture_member_sha}"
  elif [[ "$*" == 'pr view 101 --json headRefOid,state --jq [.headRefOid,.state] | @tsv' ]]; then printf '%s\tMERGED\n' "${fixture_member_sha}"
  elif [[ "$*" == *'--json attempt,status'* ]]; then printf '2\tcompleted\n'
  elif [[ "$*" == *'--json jobs'* ]]; then printf 'success\n'
  else fail "restarted main attempted unexpected gh operation: $*"
  fi
}
fixture_pushed=0
train_pr_admission() {
  [[ "$1" == "101" && "$2" == "${fixture_member_sha}" ]] \
    || fail "resumed land re-attested the wrong member head"
}
train_aggregate_update() { :; }
git() {
  case "$*" in
    *'fetch --quiet origin trunk') return 0 ;;
    *'rev-parse origin/trunk')
      [[ "${fixture_pushed}" == "1" ]] && printf '%s\n' "${fixture_batch_sha}" \
        || printf '%s\n' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
      ;;
    *'rev-parse train/batch/abc/1') printf '%s\n' "${fixture_batch_sha}" ;;
    *'push origin train/batch/abc/1:trunk') fixture_pushed=1 ;;
    *) fail "restarted main attempted unexpected git operation: $*" ;;
  esac
}
: >"${record}"
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
now_value=100
main || fail "restarted production main failed to consume retry intent"
! grep -Fq 'gh pr merge' "${record}" || fail "resumed SUCCESS invoked a second merge authority"
grep -Fqx "gh pr edit 101 --remove-label ${TRAIN_LABEL_LANDING}" "${record}" || fail "resumed SUCCESS did not remove the landing label"
[[ "$(jq -r '.outcome' "${fixture_metrics}")" == "landed" ]] || fail "resumed SUCCESS did not emit landed metrics"
[[ "$(jq -r '.counts.landed' "${fixture_metrics}")" == "1" ]] || fail "resumed SUCCESS metrics lost reconstructed membership"
[[ "$(jq -r '.counts.timeout_reruns' "${fixture_metrics}")" == "2" ]] || fail "controller B restart lost cumulative A-to-B timeout telemetry"
pass "end-to-end resumed SUCCESS restores cross-controller timeout telemetry"

# A resumed failed attempt must retain the original run id and trunk-base
# context through timeout precedence and attribution. It must not dispatch a
# new batch or request another rerun.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/1","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"timeout-retry-accepted","run_id":123,"fwdfix_attempts":0,"flake_reruns":0,"timeout_reruns":1,"rerun_kind":"timeout","rerun_base_attempt":1}}\n```'
: >"${record}"
fixture_pushed=0
export TRAIN_RUN_LOG_TEXT='Testcontainers timed out after 20 minutes; Process completed with exit code 124.'
gh() {
  if [[ "$*" == *'--json headBranch,headSha,attempt'* ]]; then printf 'train/batch/abc/1\t%s\t1\n' "${fixture_batch_sha}"
  elif [[ "$*" == 'pr view 101 --json number,state,headRefOid,createdAt,author' ]]; then printf '{"number":101,"state":"OPEN","headRefOid":"%s","createdAt":"2026-01-01T00:00:00Z","author":{"login":"alice"}}\n' "${fixture_member_sha}"
  elif [[ "$*" == *'--json attempt,status'* ]]; then printf '2\tcompleted\n'
  elif [[ "$*" == *'--json jobs'* ]]; then printf 'failure\n'
  else fail "resumed failure attempted unexpected gh operation: $*"
  fi
}
train_ci_jobs_are_terminal() { [[ "$1" == "123" ]] || fail "failure classification lost resumed run id"; }
train_expected_shards_are_classifiable() { [[ "$1" == "123" ]] || fail "shard classification lost resumed run id"; }
train_failing_jobs() { [[ "$1" == "123" ]] || fail "failure reader lost resumed run id"; printf 'Server Tests (OData Core)\n'; }
train_preexisting_filter() {
  [[ "$1" == "123" ]] || fail "pre-existing filter lost resumed run id"
  printf '%s\n' "$2"
}
flake_called=0
train_classify_flake() { flake_called=1; return 2; }
train_attribute() {
  printf 'attribute-called\n' >>"${record}"
  [[ "$(cat "${TRAIN_RUN_ID_FILE}")" == "123" ]] || fail "attribution lost persisted run id"
  [[ "${trunk_sha7:-}" == "aaaaaaa" ]] || fail "attribution lost restored trunk base"
  [[ "$(cat "$2")" == $'101\t'"${fixture_member_sha}" ]] || fail "attribution lost reconstructed member head"
  printf '101\n'
}
train_run_batch_ci() { fail "resumed failure dispatched a new batch"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
now_value=100
main || fail "resumed failure did not complete attribution path"
[[ "${flake_called}" == "0" ]] || fail "persistent resumed timeout reached flake merge-through"
grep -Fqx 'attribute-called' "${record}" || fail "resumed persistent timeout did not reach attribution"
! grep -Eq 'gh (workflow run|run rerun)' "${record}" || fail "resumed failure dispatched or reran work"
pass "end-to-end resumed FAILURE preserves run/base context through attribution"

# Run A has consumed its timeout retry. Attribution rebuild creates fresh run B,
# which gets a new budget and request identity; restarting B preserves its own
# accepted request and never sends it twice.
timeout_reruns=1
timeout_reruns_total=1
TRAIN_RERUN_KIND=timeout
TRAIN_RERUN_BASE_ATTEMPT=7
train_metric_set timeout_reruns 1
printf '111\n' >"${TRAIN_RUN_ID_FILE}"
train_reset_rerun_state_for_fresh_run
[[ "${timeout_reruns}" == "0" && -z "${TRAIN_RERUN_KIND}" && -z "${TRAIN_RERUN_BASE_ATTEMPT}" ]] \
  || fail "fresh attribution run inherited run A retry state"
[[ ! -s "${TRAIN_RUN_ID_FILE}" ]] || fail "fresh run transition retained run A id"
[[ "$(train_metric_get timeout_reruns 0)" == "1" ]] || fail "fresh run reset decremented cumulative retry telemetry"
fresh_state="$(train_state_render train/batch/abc/2 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 101 smart-ci '' 0 0 null \
  '[]' '' "${timeout_reruns}" "${TRAIN_RERUN_KIND}" null "${timeout_reruns_total}")"
fresh_state="$(awk '/^```json/{on=1;next}/^```/{if(on)exit}on' <<<"${fresh_state}")"
jq -e '.active_batch.run_id == null and .active_batch.timeout_reruns == 0
  and .active_batch.timeout_reruns_total == 1
  and .active_batch.rerun_kind == null and .active_batch.rerun_base_attempt == null' \
  >/dev/null <<<"${fresh_state}" || fail "fresh run state persisted stale run A policy identity"

# Regeneration/dispatch can fail before discovery writes run B. The empty file
# must force ci-incomplete and make stale run A classification unreachable.
classified_old_run=0
run_id="$(cat "${TRAIN_RUN_ID_FILE}")"
if train_failure_has_current_run_id FAILURE "${run_id}"; then classified_old_run=1; fi
[[ "${classified_old_run}" == "0" ]] || fail "run B early failure classified stale run A"
: >"${record}"; : >"${phase_log}"
export TRAIN_RERUN_REQUESTER=requester
request_mode=success
TRAIN_RUN_LOG_TEXT='Process completed with exit code 124.'
gh() { [[ "$*" == *'--json attempt'* ]] && printf '1\n' || fail "run B attempted unexpected gh operation: $*"; }
batch=train/batch/abc/2
trunk_sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
included=101
fwdfix=0
flake_reruns=0
train_classify_retry_candidate 222 "${timeout_reruns}" 0 'Server Tests (OData Core)' _persist_retry_intent \
  || fail "first timeout on fresh run B did not receive its retry"
[[ "$(grep -c '^request 222$' "${record}")" == "1" ]] || fail "fresh run B did not request exactly one retry"
grep -Fq '"phase": "timeout-retry-accepted"' "${TRAIN_WORK}/state.md" || fail "fresh run B did not persist accepted state"
[[ "$(train_metric_get timeout_reruns 0)" == "2" ]] || fail "run B acceptance did not increment cumulative retry telemetry"
[[ "${timeout_reruns_total}" == "2" ]] || fail "run B acceptance did not persist cumulative retry telemetry"

: >"${record}"
export TRAIN_RERUN_RESUME_STATE_JSON='{"active_batch":{"run_id":222,"phase":"timeout-retry-accepted","rerun_kind":"timeout","rerun_base_attempt":1}}'
train_request_failed_job_rerun 222 timeout 1 state_callback || fail "same-run B restart did not reconcile"
[[ ! -s "${record}" ]] || fail "same-run B restart duplicated the accepted rerun"
unset TRAIN_RERUN_RESUME_STATE_JSON TRAIN_RERUN_REQUESTER
pass "timeout retry budget and request identity are scoped per Actions run"

# A definitive API rejection uses the production persistence callback. Simulate
# a crash immediately after rejected persistence: the next controller must
# escalate/remove landing, clear state, and only then enter selection.
: >"${record}"; : >"${phase_log}"
export TRAIN_RERUN_REQUESTER=requester
request_mode=rejected
TRAIN_RUN_LOG_TEXT='Process completed with exit code 124.'
timeout_reruns=0
TRAIN_RERUN_KIND=""
TRAIN_RERUN_BASE_ATTEMPT=""
batch=train/batch/abc/2
trunk_sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
included=101
fwdfix=0
flake_reruns=0
gh() { [[ "$*" == *'--json attempt'* ]] && printf '1\n' || fail "rejected rerun attempted unexpected gh operation: $*"; }
rc=0
train_classify_retry_candidate 333 0 0 'Server Tests (OData Core)' _persist_retry_intent || rc=$?
[[ "${rc}" == "5" ]] || fail "definitive requester rejection was not classified separately"
grep -Fq '"phase": "timeout-retry-rejected"' "${TRAIN_WORK}/state.md" || fail "definitive rejection did not persist terminal state"
rejected_state_body="$(cat "${TRAIN_WORK}/state.md")"
export TRAIN_STATE_BODY_OVERRIDE="${rejected_state_body}" TRAIN_RESUME_STARTUP_TEST_ONLY=1
train_select() { printf 'selection-entered\n' >>"${record}"; }
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "later controller failed after terminal rerun rejection"
grep -Fqx "gh pr edit 101 --add-label ${TRAIN_LABEL_ESCALATED}" "${record}" || fail "rejected recovery did not escalate member"
grep -Fqx "gh pr edit 101 --remove-label ${TRAIN_LABEL_LANDING}" "${record}" || fail "rejected recovery did not remove landing label"
grep -Fq 'gh issue edit 1 --body-file' "${record}" || fail "rejected recovery did not clear active state after labels"
grep -Fqx 'selection-entered' "${record}" || fail "later controller remained trapped behind terminal rerun rejection"
[[ "$(grep -n "remove-label ${TRAIN_LABEL_LANDING}\|selection-entered" "${record}" | cut -d: -f2- | paste -sd, -)" == *"remove-label ${TRAIN_LABEL_LANDING}"*selection-entered* ]] \
  || fail "selection occurred before rejected member cleanup"
! grep -Eq 'gh (workflow run|pr merge)|git push' "${record}" || fail "rejected recovery unsafely landed or requeued the rejected batch"
unset TRAIN_RERUN_REQUESTER TRAIN_RESUME_STARTUP_TEST_ONLY
pass "definitive rerun rejection is terminal and later controller progresses"

# A definitive rejection does not authorize cleanup until `retry-rejected` is
# durable. Simulate persistence failing after the API response: requesting must
# remain authoritative and no escalation/clear side effects may occur.
: >"${record}"
export TRAIN_RERUN_REQUESTER=requester
request_mode=rejected
reject_persist_fails() {
  if [[ "$5" == "rejected" ]]; then return 1; fi
  _persist_retry_intent "$@"
}
timeout_reruns=0
TRAIN_RERUN_KIND=""
TRAIN_RERUN_BASE_ATTEMPT=""
rc=0
train_classify_retry_candidate 444 0 0 'Server Tests (OData Core)' reject_persist_fails || rc=$?
[[ "${rc}" == "6" ]] || fail "rejected-state persistence failure did not fail closed distinctly"
grep -Fq '"phase": "timeout-retry-requesting"' "${TRAIN_WORK}/state.md" || fail "persistence failure did not preserve requesting authority"
! grep -Eq 'add-label train:escalated|remove-label train:landing|"phase": "select"' "${record}" || fail "unpersistence rejection triggered unauthorized cleanup"
unset TRAIN_RERUN_REQUESTER
pass "rejection persistence failure cannot authorize cleanup"

# Controller A expires while the accepted attempt is still queued. It must
# leave the retry intent untouched. Controller B then consumes that same run
# and newer attempt before selection, without dispatching or rerunning.
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/1","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"timeout-retry-accepted","run_id":123,"fwdfix_attempts":0,"flake_reruns":0,"timeout_reruns":1,"rerun_kind":"timeout","rerun_base_attempt":1}}\n```'
export TRAIN_RESUME_STARTUP_TEST_ONLY=1
: >"${record}"
restart_attempt=queued
train_select() { fail "deadline restart entered selection"; }
gh() {
  if [[ "$*" == *'--json headBranch,headSha,attempt'* ]]; then printf 'train/batch/abc/1\t%s\t1\n' "${fixture_batch_sha}"
  elif [[ "$*" == 'pr view 101 --json number,state,headRefOid,createdAt,author' ]]; then printf '{"number":101,"state":"OPEN","headRefOid":"%s","createdAt":"2026-01-01T00:00:00Z","author":{"login":"alice"}}\n' "${fixture_member_sha}"
  elif [[ "$*" == *'--json attempt,status'* ]]; then
    if [[ "${restart_attempt}" == "queued" ]]; then
      printf '2\tqueued\n'
    else
      printf 'attempt-consumed\n' >>"${record}"
      printf '2\tcompleted\n'
    fi
  elif [[ "$*" == *'--json jobs'* ]]; then printf 'success\n'
  else fail "deadline restart attempted unexpected gh operation: $*"
  fi
}
export TRAIN_CONTROLLER_DEADLINE_EPOCH=1
rc=0
main || rc=$?
[[ "${rc}" == "1" ]] || fail "queued rerun deadline did not fail closed"
[[ "${TRAIN_STATE_BODY_OVERRIDE}" == *'"phase":"timeout-retry-accepted"'* ]] || fail "queued rerun intent was overwritten"
[[ ! -s "${record}" ]] || fail "expired controller dispatched, reran, or rewrote retry state"

restart_attempt=completed
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
main || fail "next controller did not consume the accepted rerun attempt"
grep -Fqx 'attempt-consumed' "${record}" || fail "next controller did not consume the same newer attempt"
! grep -Eq 'gh (workflow run|run rerun)' "${record}" || fail "restart dispatched a batch or duplicate rerun"
unset TRAIN_RESUME_STARTUP_TEST_ONLY
pass "deadline expiry preserves retry intent for restart consumption"
