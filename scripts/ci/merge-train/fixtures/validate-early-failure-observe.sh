#!/usr/bin/env bash
set -euo pipefail

TRAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=../lib.sh
. "${TRAIN_DIR}/lib.sh"
# shellcheck source=../early-failure-observe.sh
. "${TRAIN_DIR}/early-failure-observe.sh"
# shellcheck source=../classify-timeout.sh
. "${TRAIN_DIR}/classify-timeout.sh"
# shellcheck source=../smart-ci.sh
. "${TRAIN_DIR}/smart-ci.sh"

fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-early-failure.XXXXXX")"
trap 'rm -rf "${fixture}"' EXIT
export TRAIN_EARLY_FAILURE_MODE=observe
export TRAIN_EARLY_FAILURE_FILE="${fixture}/observation.json"
export TRAIN_EARLY_FAILURE_RAW_OUT="${fixture}/retained-observation.json"
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core","Infra and Security"]}'
export TRAIN_EARLY_FAILURE_BATCH_BRANCH='train/batch/abc/1'
export TRAIN_EARLY_FAILURE_BATCH_SHA='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'

run_attempt=1
run_status=completed
run_updated_at=2026-08-14T00:11:00Z
run_head_sha="${TRAIN_EARLY_FAILURE_BATCH_SHA}"
fixture_now=2026-08-14T00:01:30Z
fixture_clock() { printf '%s\n' "${fixture_now}"; }
fixture_run_reader() {
  jq -nc \
    --argjson id "$1" \
    --argjson attempt "${run_attempt}" \
    --arg status "${run_status}" \
    --arg updated_at "${run_updated_at}" \
    --arg head_sha "${run_head_sha}" \
    --arg head_branch "${TRAIN_EARLY_FAILURE_BATCH_BRANCH}" \
    '{databaseId:$id,attempt:$attempt,event:"workflow_dispatch",headBranch:$head_branch,
      headSha:$head_sha,status:$status,updatedAt:$updated_at,workflowName:"CI"}'
}
export TRAIN_EARLY_FAILURE_RUN_READER=fixture_run_reader
export TRAIN_EARLY_FAILURE_CLOCK=fixture_clock

fake_log() {
  case "$1" in
    101) printf 'Failed! Expected: 200 Actual: 500\n' ;;
    102) printf 'HONUA_SHARD_CAPACITY_EXHAUSTED timeout after 39 minutes\n' ;;
    *) printf 'runner disappeared\n' ;;
  esac
}
export TRAIN_EARLY_FAILURE_LOG_READER=fake_log

active_snapshot='{
  "databaseId":55,
  "attempt":1,
  "event":"workflow_dispatch",
  "headBranch":"train/batch/abc/1",
  "headSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "workflowName":"CI",
  "status":"in_progress",
  "updatedAt":"2026-08-14T00:02:00Z",
  "jobs":[
    {"databaseId":101,"runAttempt":1,"name":"Server Tests (Core)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:01:00Z"},
    {"databaseId":999,"runAttempt":1,"name":"Server Tests (Unselected)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:00:30Z"},
    {"databaseId":103,"runAttempt":1,"name":"Server Tests (Infra and Security)","status":"in_progress","conclusion":null,"completedAt":null}
  ]
}'
train_early_failure_observe_snapshot 55 "${active_snapshot}"
jq -e '
  .schema == "honua.merge-train.early-failure-observation/v2" and
  .mode == "observe" and .mutation == "none" and .run_id == 55 and
  .run.id == 55 and .run.attempt == 1 and
  .run.head_sha == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" and
  .first_blocking_failure.job_id == 101 and
  .first_blocking_failure.category == "deterministic-candidate" and
  .first_blocking_failure.would_cancel == true
' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
cp "${TRAIN_EARLY_FAILURE_FILE}" "${fixture}/observed-base.json"
cmp -s "${TRAIN_EARLY_FAILURE_FILE}" "${TRAIN_EARLY_FAILURE_RAW_OUT}"

terminal_snapshot='{
  "databaseId":55,
  "attempt":1,
  "event":"workflow_dispatch",
  "headBranch":"train/batch/abc/1",
  "headSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "workflowName":"CI",
  "status":"completed",
  "updatedAt":"2026-08-14T00:11:00Z",
  "jobs":[
    {"databaseId":101,"runAttempt":1,"name":"Server Tests (Core)","status":"completed","conclusion":"failure","startedAt":"2026-08-14T00:00:00Z","completedAt":"2026-08-14T00:01:00Z"},
    {"databaseId":103,"runAttempt":1,"name":"Server Tests (Infra and Security)","status":"completed","conclusion":"success","startedAt":"2026-08-14T00:00:00Z","completedAt":"2026-08-14T00:10:50Z"},
    {"databaseId":104,"runAttempt":1,"name":"Build and Format","status":"completed","conclusion":"success","startedAt":"2026-08-14T00:05:00Z","completedAt":"2026-08-14T00:10:00Z"}
  ]
}'
train_early_failure_finalize_snapshot 56 "${terminal_snapshot}"
jq -e 'has("run_completed_at") | not' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
attempt_two_snapshot="$(jq '.attempt = 2' <<<"${terminal_snapshot}")"
train_early_failure_finalize_snapshot 55 "${attempt_two_snapshot}"
jq -e 'has("run_completed_at") | not' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
train_early_failure_finalize_snapshot 55 "${terminal_snapshot}"
jq -e '
  .avoidable_wait_seconds == 600 and
  .detection_delay_seconds == 30 and
  .avoidable_runner_seconds == 890 and
  .actionable_runner_seconds == 860 and
  .run_completed_at == "2026-08-14T00:11:00Z" and
  .terminal.selected_jobs_complete == true and
  .terminal.runner_timing_complete == true and
  (.terminal.selected_jobs | length) == 2 and
  (.terminal.runner_windows | length) == 2
' \
  "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null

# The terminal classifier is recorded against the same immutable run/head and
# may use a newer failed-job retry attempt. Only complete, conclusive evidence
# can become promotion-countable.
cp "${TRAIN_EARLY_FAILURE_FILE}" "${fixture}/terminal-base.json"
run_attempt=2
fixture_now=2026-08-14T00:11:10Z
train_early_failure_record_classification 55 real-blocking true true
jq -e '
  .classification.outcome == "real-blocking" and
  .classification.countable == true and
  .classification.remained_real_blocking == true and
  .classification.run_attempt == 2 and
  .classification.delay_seconds == 610 and
  .classification.disposition == "consistent"
' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
jq -e '.classification.disposition == "consistent"' "${TRAIN_EARLY_FAILURE_RAW_OUT}" >/dev/null

cp "${fixture}/terminal-base.json" "${TRAIN_EARLY_FAILURE_FILE}"
train_early_failure_record_classification 55 preexisting true false
jq -e '.classification.countable == true and .classification.disposition == "contradiction"' \
  "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null

for safe_outcome in nonblocking known-flake capacity retry-passed; do
  cp "${fixture}/terminal-base.json" "${TRAIN_EARLY_FAILURE_FILE}"
  train_early_failure_record_classification 55 "${safe_outcome}" true false
  jq -e --arg outcome "${safe_outcome}" '
    .classification.outcome == $outcome and
    .classification.countable == true and
    .classification.disposition == "contradiction"
  ' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
done

# Incomplete selected-shard evidence can retain diagnostic classification but
# can never become a promotion-countable sample.
cp "${fixture}/terminal-base.json" "${TRAIN_EARLY_FAILURE_FILE}"
jq '.terminal.selected_jobs_complete = false' "${TRAIN_EARLY_FAILURE_FILE}" \
  >"${fixture}/incomplete.json"
mv "${fixture}/incomplete.json" "${TRAIN_EARLY_FAILURE_FILE}"
train_early_failure_record_classification 55 preexisting true false
jq -e '.classification.countable == false and .classification.disposition == "inconclusive"' \
  "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null

cp "${fixture}/observed-base.json" "${TRAIN_EARLY_FAILURE_FILE}"
invalid_timing_snapshot="$(jq '.jobs[1].startedAt = "not-a-timestamp"' <<<"${terminal_snapshot}")"
train_early_failure_finalize_snapshot 55 "${invalid_timing_snapshot}"
jq -e '.terminal.runner_timing_complete == false' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
train_early_failure_record_classification 55 real-blocking true true
jq -e '.classification.countable == false and .classification.disposition == "inconclusive"' \
  "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null

cp "${fixture}/terminal-base.json" "${TRAIN_EARLY_FAILURE_FILE}"
run_head_sha=dddddddddddddddddddddddddddddddddddddddd
train_early_failure_record_classification 55 real-blocking true true || true
jq -e 'has("classification") | not' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
run_head_sha="${TRAIN_EARLY_FAILURE_BATCH_SHA}"
run_status=in_progress
train_early_failure_record_classification 55 real-blocking true true || true
jq -e 'has("classification") | not' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
run_status=completed
run_attempt=1

# A canceled selected shard is a terminal API result, but the ordinary
# classifier marks it incomplete. It is retained diagnostically and cannot
# become countable evidence.
cp "${fixture}/observed-base.json" "${TRAIN_EARLY_FAILURE_FILE}"
cancelled_snapshot="$(jq '
  .jobs |= map(if .name == "Server Tests (Infra and Security)"
    then .conclusion = "cancelled" else . end)
' <<<"${terminal_snapshot}")"
train_early_failure_finalize_snapshot 55 "${cancelled_snapshot}"
jq -e '.terminal.selected_jobs_complete == true' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
train_early_failure_record_classification 55 incomplete false false
jq -e '.classification.countable == false and .classification.disposition == "inconclusive"' \
  "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null

export TRAIN_METRICS_KV="${fixture}/metrics.kv"
export TRAIN_TIMINGS_FILE="${fixture}/timings.kv"
: >"${TRAIN_METRICS_KV}"
: >"${TRAIN_TIMINGS_FILE}"
train_metrics_render '2026-08-14T00:12:00Z' LIVE 'a' '' failed '{}' \
  | jq -e '.early_failure_observation.schema == "honua.merge-train.early-failure-observation/v2"' >/dev/null

[[ "$(train_early_failure_classify_log 'HONUA_SHARD_CAPACITY_EXHAUSTED timeout')" == capacity ]]
[[ "$(train_early_failure_classify_log 'process completed with exit code 124')" == timeout ]]

rm -f "${TRAIN_EARLY_FAILURE_FILE}"
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core"]}'
unselected_snapshot='{
  "databaseId":56,
  "attempt":1,
  "event":"workflow_dispatch",
  "headBranch":"train/batch/abc/1",
  "headSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "workflowName":"CI",
  "status":"in_progress",
  "updatedAt":"2026-08-14T00:02:00Z",
  "jobs":[
    {"databaseId":999,"runAttempt":1,"name":"Server Tests (Unselected)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:00:30Z"}
  ]
}'
train_early_failure_observe_snapshot 56 "${unselected_snapshot}"
[[ ! -e "${TRAIN_EARLY_FAILURE_FILE}" ]]

# A transient log-read failure remains unobserved so a later poll can classify
# the same authoritative failed job once its logs are available.
transient_log() {
  if [[ "$1" == "101" && ! -e "${fixture}/log-ready" ]]; then
    return 1
  fi
  printf 'Failed! Expected: 200 Actual: 500\n'
}
export TRAIN_EARLY_FAILURE_LOG_READER=transient_log
multiple_failed_snapshot='{
  "databaseId":55,
  "attempt":1,
  "event":"workflow_dispatch",
  "headBranch":"train/batch/abc/1",
  "headSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "workflowName":"CI",
  "status":"in_progress",
  "updatedAt":"2026-08-14T00:03:00Z",
  "jobs":[
    {"databaseId":101,"runAttempt":1,"name":"Server Tests (Core)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:01:00Z"},
    {"databaseId":102,"runAttempt":1,"name":"Server Tests (Infra and Security)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:02:00Z"}
  ]
}'
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core","Infra and Security"]}'
train_early_failure_observe_snapshot 55 "${multiple_failed_snapshot}"
[[ ! -e "${TRAIN_EARLY_FAILURE_FILE}" ]]
touch "${fixture}/log-ready"
train_early_failure_observe_snapshot 55 "${multiple_failed_snapshot}"
jq -e '
  .run_id == 55 and
  .first_blocking_failure.job_id == 101 and
  .first_blocking_failure.category == "deterministic-candidate"
' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
export TRAIN_EARLY_FAILURE_LOG_READER=fake_log

# A selected failure beyond the first 100 jobs is still observed. Production
# retrieves the jobs endpoint with --paginate --slurp; this large synthetic
# page guards the selection logic against first-page truncation.
rm -f "${TRAIN_EARLY_FAILURE_FILE}"
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core"]}'
large_snapshot="$(jq -nc '
  {
    databaseId: 55,
    attempt: 1,
    event: "workflow_dispatch",
    headBranch: "train/batch/abc/1",
    headSha: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
    workflowName: "CI",
    status: "in_progress",
    updatedAt: "2026-08-14T00:02:00Z",
    jobs: ([range(1000;1101) | {databaseId:.,runAttempt:1,name:("filler-" + tostring),status:"completed",conclusion:"success",completedAt:"2026-08-14T00:00:00Z"}]
      + [{databaseId:101,runAttempt:1,name:"Server Tests (Core)",status:"completed",conclusion:"failure",completedAt:"2026-08-14T00:01:00Z"}])
  }
')"
train_early_failure_observe_snapshot 55 "${large_snapshot}"
jq -e '.first_blocking_failure.job_id == 101' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
grep -Fq -- '--paginate --slurp' "${TRAIN_DIR}/early-failure-observe.sh"
unset TRAIN_EARLY_FAILURE_JOBS_READER
gh() {
  if [[ "$*" == api\ --paginate\ --slurp* ]]; then
    printf '%s\n' '[{"jobs":[{"id":1,"run_attempt":1,"name":"first","status":"completed","conclusion":"success","started_at":"2026-08-14T00:00:00Z","completed_at":"2026-08-14T00:00:01Z"}]},{"jobs":[{"id":101,"run_attempt":1,"name":"Server Tests (Core)","status":"completed","conclusion":"failure","started_at":"2026-08-14T00:00:00Z","completed_at":"2026-08-14T00:01:00Z"}]}]'
    return 0
  fi
  return 1
}
paginated_jobs="$(train_early_failure_jobs 55)"
jq -e 'length == 2 and .[1].databaseId == 101 and .[1].runAttempt == 1' <<<"${paginated_jobs}" >/dev/null

# Stale or differently-triggered runs never create evidence.
rm -f "${TRAIN_EARLY_FAILURE_FILE}"
stale_snapshot="$(jq '.headSha = "dddddddddddddddddddddddddddddddddddddddd"' <<<"${active_snapshot}")"
train_early_failure_observe_snapshot 55 "${stale_snapshot}"
[[ ! -e "${TRAIN_EARLY_FAILURE_FILE}" ]]
push_snapshot="$(jq '.event = "push"' <<<"${active_snapshot}")"
train_early_failure_observe_snapshot 55 "${push_snapshot}"
[[ ! -e "${TRAIN_EARLY_FAILURE_FILE}" ]]

# A failed optional jobs read cannot hide an authoritative terminal status.
run_status=completed
run_updated_at=2026-08-14T00:11:00Z
failed_jobs_reader() { return 1; }
export TRAIN_EARLY_FAILURE_JOBS_READER=failed_jobs_reader
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core"]}'
if ! train_wait_for_run_completion 57 30 1; then
  echo "optional observation read changed authoritative completion" >&2
  exit 1
fi
unset TRAIN_EARLY_FAILURE_JOBS_READER

mutation_pattern='gh[[:space:]]+run[[:space:]]+cancel|train_side_'
mutation_pattern+='effect'
if grep -Eq "${mutation_pattern}" \
  "${TRAIN_DIR}/early-failure-observe.sh"; then
  echo "observe-only early-failure module gained mutation authority" >&2
  exit 1
fi
grep -Fq 'TRAIN_EARLY_FAILURE_POLL_SECONDS: "120"' \
  "${TRAIN_DIR}/../../../.github/workflows/merge-train.yml"
grep -Fq 'TRAIN_EARLY_FAILURE_RAW_OUT: ${{ github.workspace }}/merge-train-early-failure-observation.json' \
  "${TRAIN_DIR}/../../../.github/workflows/merge-train.yml"
[[ "$(grep -Fc '${{ github.workspace }}/merge-train-early-failure-observation.json' \
  "${TRAIN_DIR}/../../../.github/workflows/merge-train.yml")" == "2" ]]
grep -Fq 'retention-days: 30' \
  "${TRAIN_DIR}/../../../.github/workflows/merge-train.yml"
grep -Fq 'one bounded exception per' \
  "${TRAIN_DIR}/../../../.github/workflows/merge-train.yml"
grep -Fq 'adds at most one final' \
  "${TRAIN_DIR}/../../../docs/internal/ci/merge-train-early-failure-observe.md"
grep -Fq 'jobs request per run' \
  "${TRAIN_DIR}/../../../docs/internal/ci/merge-train-early-failure-observe.md"
grep -Fq 'now - last_observation_epoch >= observation_interval' \
  "${TRAIN_DIR}/smart-ci.sh"
grep -Fq 'run_snapshot="$(train_early_failure_run_snapshot "${run_id}"' \
  "${TRAIN_DIR}/smart-ci.sh"
grep -Fq 'train_early_failure_attach_jobs "${run_id}" "${run_snapshot}"' \
  "${TRAIN_DIR}/smart-ci.sh"
grep -Fq 'TRAIN_EARLY_FAILURE_MODE=off train_run_batch_ci' \
  "${TRAIN_DIR}/train.sh"
grep -Fq 'export TRAIN_EARLY_FAILURE_BATCH_SHA="${batch_sha}"' \
  "${TRAIN_DIR}/resume-retry.sh"

echo "early-failure-observe=ok mode=observe"
