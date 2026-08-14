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
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core","Infra and Security"]}'

fake_log() {
  case "$1" in
    101) printf 'Failed! Expected: 200 Actual: 500\n' ;;
    102) printf 'HONUA_SHARD_CAPACITY_EXHAUSTED timeout after 39 minutes\n' ;;
    *) printf 'runner disappeared\n' ;;
  esac
}
export TRAIN_EARLY_FAILURE_LOG_READER=fake_log

active_snapshot='{
  "status":"in_progress",
  "updatedAt":"2026-08-14T00:02:00Z",
  "jobs":[
    {"databaseId":101,"name":"Server Tests (Core)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:01:00Z"},
    {"databaseId":999,"name":"Server Tests (Unselected)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:00:30Z"},
    {"databaseId":103,"name":"Server Tests (Infra and Security)","status":"in_progress","conclusion":null,"completedAt":null}
  ]
}'
train_early_failure_observe_snapshot 55 "${active_snapshot}"
jq -e '
  .schema == "honua.merge-train.early-failure-observation/v1" and
  .mode == "observe" and .mutation == "none" and .run_id == 55 and
  .first_blocking_failure.job_id == 101 and
  .first_blocking_failure.category == "deterministic-candidate" and
  .first_blocking_failure.would_cancel == true
' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null

terminal_snapshot='{
  "status":"completed",
  "updatedAt":"2026-08-14T00:11:00Z",
  "jobs":[
    {"databaseId":101,"name":"Server Tests (Core)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:01:00Z"},
    {"databaseId":103,"name":"Server Tests (Infra and Security)","status":"completed","conclusion":"success","completedAt":"2026-08-14T00:10:50Z"}
  ]
}'
train_early_failure_finalize_snapshot 56 "${terminal_snapshot}"
jq -e 'has("run_completed_at") | not' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
train_early_failure_finalize_snapshot 55 "${terminal_snapshot}"
jq -e '.avoidable_wait_seconds == 600 and .run_completed_at == "2026-08-14T00:11:00Z"' \
  "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null
export TRAIN_METRICS_KV="${fixture}/metrics.kv"
export TRAIN_TIMINGS_FILE="${fixture}/timings.kv"
: >"${TRAIN_METRICS_KV}"
: >"${TRAIN_TIMINGS_FILE}"
train_metrics_render '2026-08-14T00:12:00Z' LIVE 'a' '' failed '{}' \
  | jq -e '.early_failure_observation.avoidable_wait_seconds == 600' >/dev/null

[[ "$(train_early_failure_classify_log 'HONUA_SHARD_CAPACITY_EXHAUSTED timeout')" == capacity ]]
[[ "$(train_early_failure_classify_log 'process completed with exit code 124')" == timeout ]]

rm -f "${TRAIN_EARLY_FAILURE_FILE}"
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core"]}'
unselected_snapshot='{
  "status":"in_progress",
  "updatedAt":"2026-08-14T00:02:00Z",
  "jobs":[
    {"databaseId":999,"name":"Server Tests (Unselected)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:00:30Z"}
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
  "status":"in_progress",
  "updatedAt":"2026-08-14T00:03:00Z",
  "jobs":[
    {"databaseId":101,"name":"Server Tests (Core)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:01:00Z"},
    {"databaseId":102,"name":"Server Tests (Infra and Security)","status":"completed","conclusion":"failure","completedAt":"2026-08-14T00:02:00Z"}
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

# A failed optional jobs snapshot cannot hide an authoritative terminal status.
gh() {
  if [[ "$*" == *"--json status --jq .status"* ]]; then
    printf 'completed\n'
    return 0
  fi
  return 1
}
export TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR='{"shards":["Core"]}'
if ! train_wait_for_run_completion 57 30 1; then
  echo "optional observation read changed authoritative completion" >&2
  exit 1
fi

mutation_pattern='gh[[:space:]]+run[[:space:]]+cancel|train_side_'
mutation_pattern+='effect'
if grep -Eq "${mutation_pattern}" \
  "${TRAIN_DIR}/early-failure-observe.sh"; then
  echo "observe-only early-failure module gained mutation authority" >&2
  exit 1
fi
grep -Fq 'TRAIN_EARLY_FAILURE_POLL_SECONDS: "120"' \
  "${TRAIN_DIR}/../../../.github/workflows/merge-train.yml"
grep -Fq 'one bounded exception per' \
  "${TRAIN_DIR}/../../../.github/workflows/merge-train.yml"
grep -Fq 'adds at most one request per run' \
  "${TRAIN_DIR}/../../../docs/internal/ci/merge-train-early-failure-observe.md"
grep -Fq 'now - last_observation_epoch >= observation_interval' \
  "${TRAIN_DIR}/smart-ci.sh"
grep -Fq 'status="$(gh run view "${run_id}" --json status --jq' \
  "${TRAIN_DIR}/smart-ci.sh"

echo "early-failure-observe=ok mode=observe"
