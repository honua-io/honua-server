#!/usr/bin/env bash
# Observe the interval between the first blocking selected-shard failure and
# terminal Smart CI classification. This module is intentionally read-only:
# it never cancels a run and no merge-train decision reads its output.

: "${TRAIN_EARLY_FAILURE_MODE:=observe}"

train_early_failure_classify_log() {
  local text="$1"
  if grep -Fq 'HONUA_SHARD_CAPACITY_EXHAUSTED' <<<"${text}"; then
    printf 'capacity\n'
  elif train_log_is_timeout "${text}"; then
    printf 'timeout\n'
  elif train_log_is_flake "${text}"; then
    printf 'known-flake\n'
  elif grep -Eiq 'Failed!|Expected:|Assert([A-Za-z]+)?Exception|error CS[0-9]+|Process completed with exit code [1-9][0-9]*' <<<"${text}"; then
    printf 'deterministic-candidate\n'
  else
    printf 'unknown\n'
  fi
}

train_early_failure_log() {
  local job_id="$1"
  if [[ -n "${TRAIN_EARLY_FAILURE_LOG_READER:-}" ]]; then
    "${TRAIN_EARLY_FAILURE_LOG_READER}" "${job_id}"
  else
    gh run view --job "${job_id}" --log 2>/dev/null
  fi
}

train_early_failure_selected_names() {
  local descriptor="${TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR:-}"
  jq -r '.shards[]? | "Server Tests (\(.))"' <<<"${descriptor}" 2>/dev/null
}

train_early_failure_observe_snapshot() {
  local run_id="$1" snapshot="$2"
  [[ "${TRAIN_EARLY_FAILURE_MODE}" == "observe" ]] || return 0
  [[ -n "${TRAIN_EARLY_FAILURE_FILE:-}" ]] || return 0
  [[ ! -s "${TRAIN_EARLY_FAILURE_FILE}" ]] || return 0
  jq -e '.jobs | type == "array"' <<<"${snapshot}" >/dev/null 2>&1 || return 0

  local selected failed job_id job_name completed_at log category observed_at tab observation_tmp
  tab="$(printf '\tX')"; tab="${tab%X}"
  selected="$(train_early_failure_selected_names)"
  [[ -n "${selected}" ]] || return 0
  failed="$(
    jq -r '.jobs[]
      | select((.status | ascii_downcase) == "completed")
      | select((.conclusion | ascii_downcase) == "failure")
      | [.completedAt, (.databaseId | tostring), .name] | @tsv' <<<"${snapshot}" \
      | sort
  )"
  while IFS="${tab}" read -r completed_at job_id job_name; do
    [[ -n "${job_id}" && -n "${job_name}" ]] || continue
    grep -Fxq -- "${job_name}" <<<"${selected}" || continue
    if ! log="$(train_early_failure_log "${job_id}")"; then
      train_log "early-failure observe: log unavailable for ${job_name}; retrying on a later snapshot"
      return 0
    fi
    category="$(train_early_failure_classify_log "${log}")"
    observed_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    observation_tmp="${TRAIN_EARLY_FAILURE_FILE}.new"
    jq -n \
      --argjson run_id "${run_id}" \
      --arg job_id "${job_id}" \
      --arg job_name "${job_name}" \
      --arg completed_at "${completed_at}" \
      --arg observed_at "${observed_at}" \
      --arg category "${category}" \
      '{
        schema: "honua.merge-train.early-failure-observation/v1",
        mode: "observe",
        run_id: $run_id,
        mutation: "none",
        first_blocking_failure: {
          job_id: ($job_id | tonumber),
          job_name: $job_name,
          completed_at: $completed_at,
          observed_at: $observed_at,
          category: $category,
          would_cancel: ($category == "deterministic-candidate")
        }
      }' >"${observation_tmp}"
    mv "${observation_tmp}" "${TRAIN_EARLY_FAILURE_FILE}"
    train_log "early-failure observe: ${job_name} => ${category}; no cancellation performed"
    return 0
  done <<<"${failed}"
}

train_early_failure_finalize_snapshot() {
  local run_id="$1" snapshot="$2"
  [[ -n "${TRAIN_EARLY_FAILURE_FILE:-}" && -s "${TRAIN_EARLY_FAILURE_FILE}" ]] || return 0
  local observed_run_id completed_at first_epoch completed_epoch wait_seconds updated
  observed_run_id="$(jq -r '.run_id | tostring' "${TRAIN_EARLY_FAILURE_FILE}" 2>/dev/null || echo '')"
  [[ "${observed_run_id}" == "${run_id}" ]] || return 0
  completed_at="$(jq -r '.updatedAt // empty' <<<"${snapshot}" 2>/dev/null)"
  [[ -n "${completed_at}" ]] || return 0
  first_epoch="$(jq -r '.first_blocking_failure.completed_at | fromdateiso8601' "${TRAIN_EARLY_FAILURE_FILE}" 2>/dev/null || echo '')"
  completed_epoch="$(jq -nr --arg value "${completed_at}" '$value | fromdateiso8601' 2>/dev/null || echo '')"
  [[ "${first_epoch}" =~ ^[0-9]+$ && "${completed_epoch}" =~ ^[0-9]+$ ]] || return 0
  wait_seconds=$(( completed_epoch - first_epoch ))
  (( wait_seconds < 0 )) && wait_seconds=0
  updated="${TRAIN_EARLY_FAILURE_FILE}.updated"
  jq \
    --arg completed_at "${completed_at}" \
    --argjson avoidable_wait_seconds "${wait_seconds}" \
    '. + {run_completed_at: $completed_at, avoidable_wait_seconds: $avoidable_wait_seconds}' \
    "${TRAIN_EARLY_FAILURE_FILE}" >"${updated}"
  mv "${updated}" "${TRAIN_EARLY_FAILURE_FILE}"
}
