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
history_repo="$(mktemp -d)"
phase_log="$(mktemp)"
trap 'rm -f "${record}" "${sequence_calls:-}" "${fixture_included}" "${fixture_metrics}" "${phase_log}"; rm -rf "${history_repo}"' EXIT
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

# A GitHub conflict/already-running response is asynchronous acceptance, not a
# hard failure; accepted state is persisted and normal attempt waiting resumes.
: >"${record}"; : >"${phase_log}"
unset TRAIN_RERUN_RESUME_STATE_JSON
export TRAIN_RERUN_VISIBILITY_GRACE_SECONDS=0
train_run_attempt_status() { printf '1\tcompleted\n'; }
request_mode=conflict
train_request_failed_job_rerun 123 timeout 1 state_callback || fail "rerun conflict was not reconciled as accepted"
[[ "$(paste -sd, "${phase_log}")" == "requesting,accepted" ]] || fail "rerun conflict did not persist two-phase acceptance"
unset TRAIN_RERUN_RESUME_STATE_JSON TRAIN_RERUN_REQUESTER TRAIN_RERUN_VISIBILITY_GRACE_SECONDS
train_run_attempt_status() {
  gh run view "$1" --json attempt,status --jq '[.attempt, .status] | @tsv' 2>/dev/null
}
pass "two-phase rerun crash, visibility, and conflict recovery"

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
export TRAIN_STATE_ISSUE_OVERRIDE=1
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n{"active_batch":{"branch":"train/batch/abc/1","trunk_base":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","included":[101],"phase":"timeout-retry-accepted","run_id":123,"fwdfix_attempts":0,"flake_reruns":0,"timeout_reruns":1,"timeout_reruns_total":2,"rerun_kind":"timeout","rerun_base_attempt":1}}\n```'
export TRAIN_RESUME_FETCHER=resume_fetcher
train_select() { fail "restarted main incorrectly entered selection"; }
train_smart_ci_shards() { printf '{"run_all":false,"shards":["OData Core"],"reason":"resume"}\n'; }
gh() {
  if [[ "$*" == *'--json headBranch,headSha,attempt'* ]]; then printf 'train/batch/abc/1\t%s\t1\n' "${fixture_batch_sha}"
  elif [[ "$*" == 'pr view 101 --json number,state,headRefOid,createdAt,author' ]]; then printf '{"number":101,"state":"OPEN","headRefOid":"%s","createdAt":"2026-01-01T00:00:00Z","author":{"login":"alice"}}\n' "${fixture_member_sha}"
  elif [[ "$*" == *'--json attempt,status'* ]]; then printf '2\tcompleted\n'
  elif [[ "$*" == *'--json jobs'* ]]; then printf 'success\n'
  else fail "restarted main attempted unexpected gh operation: $*"
  fi
}
fixture_pushed=0
git() {
  case "$*" in
    *'fetch --quiet origin trunk') return 0 ;;
    *'rev-parse origin/trunk')
      [[ "${fixture_pushed}" == "1" ]] && printf '%s\n' "${fixture_batch_sha}" \
        || printf '%s\n' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
      ;;
    *'push origin train/batch/abc/1:trunk') fixture_pushed=1 ;;
    *) fail "restarted main attempted unexpected git operation: $*" ;;
  esac
}
: >"${record}"
export TRAIN_METRICS_OUT="${fixture_metrics}"
unset TRAIN_CONTROLLER_DEADLINE_EPOCH
now_value=100
main || fail "restarted production main failed to consume retry intent"
grep -Fqx 'gh pr merge 101 --merge' "${record}" || fail "resumed SUCCESS did not close the included PR"
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
fresh_state="$(train_state_render train/batch/abc/2 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 101 smart-ci '' 0 0 null "${timeout_reruns}" "${TRAIN_RERUN_KIND}" null "${timeout_reruns_total}")"
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

# A definitive API rejection uses the production persistence callback to write
# terminal rejected state. The next controller must not treat it as an in-flight
# request and must proceed into selection rather than deadlocking forever.
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
grep -Fqx 'selection-entered' "${record}" || fail "later controller remained trapped behind terminal rerun rejection"
unset TRAIN_RERUN_REQUESTER TRAIN_RESUME_STARTUP_TEST_ONLY
pass "definitive rerun rejection is terminal and later controller progresses"

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
