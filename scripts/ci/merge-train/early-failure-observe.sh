#!/usr/bin/env bash
# Observe the interval between the first blocking selected-shard failure and
# terminal Smart CI classification. This module is intentionally read-only:
# it never cancels a run and no merge-train decision reads its output.

: "${TRAIN_EARLY_FAILURE_MODE:=observe}"

train_early_failure_now_iso() {
  if [[ -n "${TRAIN_EARLY_FAILURE_CLOCK:-}" ]]; then
    "${TRAIN_EARLY_FAILURE_CLOCK}"
  else
    date -u +%Y-%m-%dT%H:%M:%SZ
  fi
}

train_early_failure_publish_raw() {
  local source="${TRAIN_EARLY_FAILURE_FILE:-}"
  local destination="${TRAIN_EARLY_FAILURE_RAW_OUT:-}"
  [[ -n "${source}" && -s "${source}" && -n "${destination}" \
    && "${source}" != "${destination}" ]] || return 0
  if ! cp "${source}" "${destination}.new" 2>/dev/null \
    || ! mv "${destination}.new" "${destination}" 2>/dev/null; then
    rm -f "${destination}.new" 2>/dev/null || true
    train_log "early-failure observe: raw evidence copy failed; merge authority unchanged"
  fi
  return 0
}

# One status read is already required on every controller poll. In observe mode
# make that same read carry immutable run identity; the separately throttled
# jobs request is explicitly paginated and remains the only added request.
train_early_failure_run_snapshot() {
  local run_id="$1"
  if [[ -n "${TRAIN_EARLY_FAILURE_RUN_READER:-}" ]]; then
    "${TRAIN_EARLY_FAILURE_RUN_READER}" "${run_id}"
  else
    gh run view "${run_id}" \
      --json databaseId,attempt,event,headBranch,headSha,status,updatedAt,workflowName \
      2>/dev/null
  fi
}

train_early_failure_jobs() {
  local run_id="$1"
  if [[ -n "${TRAIN_EARLY_FAILURE_JOBS_READER:-}" ]]; then
    "${TRAIN_EARLY_FAILURE_JOBS_READER}" "${run_id}"
    return
  fi

  gh api --paginate --slurp \
    "repos/${GITHUB_REPOSITORY:-honua-io/honua-server}/actions/runs/${run_id}/jobs?filter=latest&per_page=100" \
    2>/dev/null \
    | jq -c '[.[]?.jobs[]? | {
        databaseId: .id,
        runAttempt: .run_attempt,
        name,
        status,
        conclusion,
        startedAt: .started_at,
        completedAt: .completed_at
      }]'
}

train_early_failure_attach_jobs() {
  local run_id="$1" run_snapshot="$2" jobs
  jobs="$(train_early_failure_jobs "${run_id}")" || return 1
  jq -e 'type == "array"' >/dev/null 2>&1 <<<"${jobs}" || return 1
  jq -c --argjson jobs "${jobs}" '. + {jobs: $jobs}' <<<"${run_snapshot}"
}

train_early_failure_selected_names_json() {
  local descriptor="${TRAIN_EARLY_FAILURE_SHARD_DESCRIPTOR:-}"
  jq -c '[.shards[]? | "Server Tests (\(.))"]' <<<"${descriptor}" 2>/dev/null
}

train_early_failure_snapshot_matches_expected() {
  local run_id="$1" snapshot="$2"
  local expected_branch="${TRAIN_EARLY_FAILURE_BATCH_BRANCH:-}"
  local expected_sha="${TRAIN_EARLY_FAILURE_BATCH_SHA:-}"
  [[ "${run_id}" =~ ^[0-9]+$ && "${expected_branch}" == train/batch/* \
    && "${expected_sha}" =~ ^[0-9a-fA-F]{40}$ ]] || return 1
  jq -e --argjson run_id "${run_id}" --arg branch "${expected_branch}" --arg sha "${expected_sha}" '
    .databaseId == $run_id
    and (.attempt | type == "number" and . >= 1 and floor == .)
    and .event == "workflow_dispatch"
    and .workflowName == "CI"
    and .headBranch == $branch
    and .headSha == $sha
    and (.jobs | type == "array")
    and (.attempt as $attempt
      | all(.jobs[];
          (.runAttempt | type == "number" and . >= 1 and floor == .)
          and .runAttempt == $attempt))
  ' >/dev/null 2>&1 <<<"${snapshot}"
}

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
    printf 'infra-or-unknown\n'
  fi
}

train_early_failure_log() {
  local job_id="$1" annotations
  if [[ -n "${TRAIN_EARLY_FAILURE_LOG_READER:-}" ]]; then
    "${TRAIN_EARLY_FAILURE_LOG_READER}" "${job_id}"
  else
    if annotations="$(train_read_job_annotations "${job_id}")" \
      && { train_log_is_capacity_exhaustion "${annotations}" \
        || train_log_is_timeout "${annotations}"; }; then
      printf '%s\n' "${annotations}"
      return 0
    fi
    train_read_job_log "${job_id}"
  fi
}

train_early_failure_selected_names() {
  train_early_failure_selected_names_json | jq -r '.[]'
}

train_early_failure_observe_snapshot() {
  local run_id="$1" snapshot="$2"
  [[ "${TRAIN_EARLY_FAILURE_MODE}" == "observe" ]] || return 0
  [[ -n "${TRAIN_EARLY_FAILURE_FILE:-}" ]] || return 0
  [[ ! -s "${TRAIN_EARLY_FAILURE_FILE}" ]] || return 0
  train_early_failure_snapshot_matches_expected "${run_id}" "${snapshot}" || {
    train_log "early-failure observe: exact run identity unavailable or mismatched; observation skipped"
    return 0
  }

  local selected failed job_id job_name completed_at log category observed_at tab observation_tmp
  tab="$(printf '\tX')"; tab="${tab%X}"
  selected="$(train_early_failure_selected_names)"
  [[ -n "${selected}" ]] || return 0
  failed="$(
    jq -r '.jobs[]
      | select(((.status // "") | ascii_downcase) == "completed")
      | select(((.conclusion // "") | ascii_downcase) == "failure")
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
    observed_at="$(train_early_failure_now_iso)"
    observation_tmp="${TRAIN_EARLY_FAILURE_FILE}.new"
    jq -n \
      --argjson run_id "${run_id}" \
      --arg job_id "${job_id}" \
      --arg job_name "${job_name}" \
      --arg completed_at "${completed_at}" \
      --arg observed_at "${observed_at}" \
      --arg category "${category}" \
      --argjson run "${snapshot}" \
      '{
        schema: "honua.merge-train.early-failure-observation/v2",
        mode: "observe",
        run_id: $run_id,
        mutation: "none",
        run: {
          id: $run.databaseId,
          attempt: $run.attempt,
          head_sha: $run.headSha,
          head_branch: $run.headBranch,
          event: $run.event,
          workflow: $run.workflowName
        },
        first_blocking_failure: {
          job_id: ($job_id | tonumber),
          run_attempt: $run.attempt,
          job_name: $job_name,
          completed_at: $completed_at,
          observed_at: $observed_at,
          category: $category,
          would_cancel: ($category == "deterministic-candidate")
        }
      }' >"${observation_tmp}"
    mv "${observation_tmp}" "${TRAIN_EARLY_FAILURE_FILE}"
    train_early_failure_publish_raw
    train_log "early-failure observe: ${job_name} => ${category}; no cancellation performed"
    return 0
  done <<<"${failed}"
}

train_early_failure_finalize_snapshot() {
  local run_id="$1" snapshot="$2"
  [[ -n "${TRAIN_EARLY_FAILURE_FILE:-}" && -s "${TRAIN_EARLY_FAILURE_FILE}" ]] || return 0
  local observed_run_id completed_at first_epoch observed_epoch completed_epoch
  local wait_seconds detection_delay_seconds updated
  local selected_names selected_jobs selected_complete runner_windows
  local runner_after_failure runner_after_observation runner_timing_complete would_cancel
  observed_run_id="$(jq -r '.run_id | tostring' "${TRAIN_EARLY_FAILURE_FILE}" 2>/dev/null || echo '')"
  [[ "${observed_run_id}" == "${run_id}" ]] || return 0
  train_early_failure_snapshot_matches_expected "${run_id}" "${snapshot}" || return 0
  jq -e --argjson run "${snapshot}" '
    .run.id == $run.databaseId
    and .run.attempt == $run.attempt
    and .run.head_sha == $run.headSha
    and .run.head_branch == $run.headBranch
    and .run.event == $run.event
    and .run.workflow == $run.workflowName
    and $run.status == "completed"
  ' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null 2>&1 || return 0
  completed_at="$(jq -r '.updatedAt // empty' <<<"${snapshot}" 2>/dev/null)"
  [[ -n "${completed_at}" ]] || return 0
  first_epoch="$(jq -r '.first_blocking_failure.completed_at | fromdateiso8601' "${TRAIN_EARLY_FAILURE_FILE}" 2>/dev/null || echo '')"
  observed_epoch="$(jq -r '.first_blocking_failure.observed_at | fromdateiso8601' "${TRAIN_EARLY_FAILURE_FILE}" 2>/dev/null || echo '')"
  completed_epoch="$(jq -nr --arg value "${completed_at}" '$value | fromdateiso8601' 2>/dev/null || echo '')"
  [[ "${first_epoch}" =~ ^[0-9]+$ && "${observed_epoch}" =~ ^[0-9]+$ \
    && "${completed_epoch}" =~ ^[0-9]+$ ]] || return 0
  wait_seconds=$(( completed_epoch - first_epoch ))
  (( wait_seconds < 0 )) && wait_seconds=0
  detection_delay_seconds=$(( observed_epoch - first_epoch ))
  (( detection_delay_seconds < 0 )) && detection_delay_seconds=0
  selected_names="$(train_early_failure_selected_names_json)" || return 0
  selected_jobs="$(jq -c --argjson names "${selected_names}" '
    [.jobs[]
      | select(.name as $name | ($names | index($name)) != null)
      | {job_id: .databaseId, run_attempt: .runAttempt, name, status, conclusion, completed_at: .completedAt}]
    | sort_by(.name, .job_id)
  ' <<<"${snapshot}")" || return 0
  selected_complete="$(jq -n --argjson names "${selected_names}" --argjson jobs "${selected_jobs}" '
    ($names | length) > 0
    and all($names[];
      . as $name
      | ([$jobs[]
          | select(.name == $name)
          | select(.job_id | type == "number" and . >= 1 and floor == .)
          | select(.status == "completed")
          | select((.conclusion // "") != "")]
        | length) == 1)
  ')" || return 0
  runner_windows="$(jq -c \
    --argjson failure "${first_epoch}" \
    --argjson observed "${observed_epoch}" \
    --argjson completed "${completed_epoch}" '
    def epoch:
      if type != "string" or . == "" then null
      else (try (sub("\\.[0-9]+Z$"; "Z") | fromdateiso8601) catch null)
      end;
    [.jobs[]
      | (.startedAt | epoch) as $started
      | (.completedAt | epoch) as $ended
      | select($started != null and $ended != null)
      | ([$started, $failure] | max) as $failure_start
      | ([$started, $observed] | max) as $observation_start
      | ([$ended, $completed] | min) as $stop
      | (($stop - $failure_start) | if . > 0 then . else 0 end) as $after_failure
      | (($stop - $observation_start) | if . > 0 then . else 0 end) as $after_observation
      | select($after_failure > 0 or $after_observation > 0)
      | {
          job_id: .databaseId,
          run_attempt: .runAttempt,
          name,
          status,
          conclusion,
          runner_seconds_after_failure: $after_failure,
          runner_seconds_after_observation: $after_observation
        }]
      | sort_by(.name, .job_id)
  ' <<<"${snapshot}")" || return 0
  runner_after_failure="$(jq '[.[].runner_seconds_after_failure] | add // 0' <<<"${runner_windows}")" || return 0
  runner_after_observation="$(jq '[.[].runner_seconds_after_observation] | add // 0' <<<"${runner_windows}")" || return 0
  runner_timing_complete="$(jq --argjson completed "${completed_epoch}" '
    def epoch:
      if type != "string" or . == "" then null
      else (try (sub("\\.[0-9]+Z$"; "Z") | fromdateiso8601) catch null)
      end;
    all(.jobs[];
      (.databaseId | type == "number" and . >= 1 and floor == .)
      and (.name | type == "string" and length > 0)
      and .status == "completed"
      and ((.conclusion // "") != "")
      and (
        .conclusion == "skipped"
        or (.conclusion == "cancelled" and (.startedAt // "") == "")
        or ((.startedAt | epoch) as $started
          | (.completedAt | epoch) as $ended
          | $started != null and $ended != null
          and $started <= $ended and $ended <= $completed)
      ))
  ' <<<"${snapshot}")" || return 0
  would_cancel="$(jq -r '.first_blocking_failure.would_cancel' "${TRAIN_EARLY_FAILURE_FILE}")" || return 0
  updated="${TRAIN_EARLY_FAILURE_FILE}.updated"
  jq \
    --arg completed_at "${completed_at}" \
    --argjson avoidable_wait_seconds "${wait_seconds}" \
    --argjson detection_delay_seconds "${detection_delay_seconds}" \
    --argjson selected_jobs "${selected_jobs}" \
    --argjson selected_complete "${selected_complete}" \
    --argjson runner_windows "${runner_windows}" \
    --argjson runner_after_failure "${runner_after_failure}" \
    --argjson runner_after_observation "${runner_after_observation}" \
    --argjson runner_timing_complete "${runner_timing_complete}" \
    --argjson would_cancel "${would_cancel}" \
    '. + {
      run_completed_at: $completed_at,
      avoidable_wait_seconds: $avoidable_wait_seconds,
      detection_delay_seconds: $detection_delay_seconds,
      avoidable_runner_seconds: (if $would_cancel then $runner_after_failure else 0 end),
      actionable_runner_seconds: (if $would_cancel then $runner_after_observation else 0 end),
      terminal: {
        run_completed_at: $completed_at,
        selected_jobs_complete: $selected_complete,
        selected_jobs: $selected_jobs,
        runner_timing_complete: $runner_timing_complete,
        runner_seconds_after_failure: $runner_after_failure,
        runner_seconds_after_observation: $runner_after_observation,
        runner_windows: $runner_windows
      }
    }' \
    "${TRAIN_EARLY_FAILURE_FILE}" >"${updated}"
  mv "${updated}" "${TRAIN_EARLY_FAILURE_FILE}"
  train_early_failure_publish_raw
}

# Record the ordinary train classifier's terminal interpretation of the exact
# observed failure. This is local telemetry only: callers ignore failures and
# no train decision reads this file. A sample is promotion-countable only when
# both terminal selected-job evidence and this exact-run classification exist.
train_early_failure_record_classification() {
  local run_id="$1" outcome="$2" countable="$3" remained_real="$4"
  [[ "${TRAIN_EARLY_FAILURE_MODE}" == "observe" ]] || return 0
  [[ -n "${TRAIN_EARLY_FAILURE_FILE:-}" && -s "${TRAIN_EARLY_FAILURE_FILE}" ]] || return 0
  jq -e 'has("classification") | not' "${TRAIN_EARLY_FAILURE_FILE}" >/dev/null 2>&1 || return 0
  [[ "${run_id}" =~ ^[0-9]+$ && "${outcome}" =~ ^[a-z][a-z0-9-]*$ ]] || return 1
  [[ "${countable}" == "true" || "${countable}" == "false" ]] || return 1
  [[ "${remained_real}" == "true" || "${remained_real}" == "false" ]] || return 1
  [[ "${remained_real}" != "true" || "${countable}" == "true" ]] || return 1

  local identity classified_at first_epoch classified_epoch delay disposition updated
  identity="$(train_early_failure_run_snapshot "${run_id}")" || return 1
  jq -e --argjson run_id "${run_id}" --argjson observed "$(jq -c '.run' "${TRAIN_EARLY_FAILURE_FILE}")" '
    .databaseId == $run_id
    and (.attempt | type == "number" and . >= $observed.attempt and floor == .)
    and .status == "completed"
    and .event == $observed.event
    and .workflowName == $observed.workflow
    and .headBranch == $observed.head_branch
    and .headSha == $observed.head_sha
  ' >/dev/null 2>&1 <<<"${identity}" || return 1

  classified_at="$(train_early_failure_now_iso)"
  first_epoch="$(jq -r '.first_blocking_failure.completed_at | fromdateiso8601' \
    "${TRAIN_EARLY_FAILURE_FILE}" 2>/dev/null || echo '')"
  classified_epoch="$(jq -nr --arg value "${classified_at}" '$value | fromdateiso8601' 2>/dev/null || echo '')"
  [[ "${first_epoch}" =~ ^[0-9]+$ && "${classified_epoch}" =~ ^[0-9]+$ ]] || return 1
  delay=$(( classified_epoch - first_epoch )); (( delay < 0 )) && delay=0
  if [[ "${countable}" != "true" ]]; then
    disposition="inconclusive"
  elif [[ "$(jq -r '.first_blocking_failure.would_cancel' "${TRAIN_EARLY_FAILURE_FILE}")" != "true" ]]; then
    disposition="not-a-cancellation-candidate"
  elif [[ "${remained_real}" == "true" ]]; then
    disposition="consistent"
  else
    disposition="contradiction"
  fi

  updated="${TRAIN_EARLY_FAILURE_FILE}.classified"
  jq \
    --arg outcome "${outcome}" \
    --arg classified_at "${classified_at}" \
    --arg disposition "${disposition}" \
    --argjson countable "${countable}" \
    --argjson remained_real "${remained_real}" \
    --argjson delay "${delay}" \
    --argjson identity "${identity}" \
    '. + {
      classification: {
        outcome: $outcome,
        countable: ($countable
          and (.terminal.selected_jobs_complete == true)
          and (.terminal.runner_timing_complete == true)),
        remained_real_blocking: $remained_real,
        classified_at: $classified_at,
        delay_seconds: $delay,
        run_attempt: $identity.attempt,
        disposition: (if ($countable
          and (.terminal.selected_jobs_complete == true)
          and (.terminal.runner_timing_complete == true))
          then $disposition else "inconclusive" end)
      }
    }' "${TRAIN_EARLY_FAILURE_FILE}" >"${updated}" || return 1
  mv "${updated}" "${TRAIN_EARLY_FAILURE_FILE}"
  train_early_failure_publish_raw
}
