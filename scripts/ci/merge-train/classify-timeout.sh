#!/usr/bin/env bash
# A generic timeout/exit-124 failure gets one failed-job-only rerun. If it
# reproduces, it is real and must continue to attribution; unlike a recognized
# environmental flake, a persistent timeout is never eligible to merge through.

train_log_is_timeout() {
  grep -Eiq 'process completed with exit code 124|exit(ed)?( with)?( code)?[ =:]124|tim(e|ed)[ -]?out after|timeout after|command timed out|execution timed out' <<<"$1"
}

# train_wait_for_rerun_visibility <run-id> <base-attempt>: bounded grace for an
# asynchronously accepted request to expose attempt > base.
train_wait_for_rerun_visibility() {
  local run_id="$1" base="$2" grace="${TRAIN_RERUN_VISIBILITY_GRACE_SECONDS:-30}"
  local interval="${TRAIN_RERUN_VISIBILITY_POLL_SECONDS:-5}" deadline row attempt status
  deadline=$(( $(train_now) + grace ))
  while :; do
    row="$(train_run_attempt_status "${run_id}" || echo $'0\t')"
    IFS=$'\t' read -r attempt status <<<"${row}"
    [[ "${attempt}" =~ ^[0-9]+$ && "${attempt}" -gt "${base}" ]] && return 0
    [[ "$(train_now)" -ge "${deadline}" ]] && return 1
    sleep "${interval}"
  done
}

# train_send_failed_job_rerun <run-id>: 0=accepted, 4=conflict/async acceptance,
# 5=definitive HTTP rejection, 1=ambiguous transport/unknown failure. Tests may
# provide TRAIN_RERUN_REQUESTER.
train_send_failed_job_rerun() {
  local run_id="$1" err rc=0
  if [[ -n "${TRAIN_RERUN_REQUESTER:-}" ]]; then
    "${TRAIN_RERUN_REQUESTER}" "${run_id}"
    return $?
  fi
  if [[ "${TRAIN_APPLY:-0}" != "1" ]]; then
    train_side_effect gh run rerun "${run_id}" --failed
    return $?
  fi
  err="$(mktemp)"
  gh run rerun "${run_id}" --failed 2>"${err}" || rc=$?
  if [[ "${rc}" == "0" ]]; then rm -f "${err}"; return 0; fi
  if grep -Eiq 'HTTP 409|already.*(queued|in progress|requested)|cannot rerun.*(queued|in progress|not completed)' "${err}"; then
    train_warn "rerun API reported an already queued/in-progress request for run ${run_id}; reconciling asynchronously"
    rm -f "${err}"; return 4
  fi
  if grep -Eiq 'HTTP (401|403|404|422)|status code: (401|403|404|422)' "${err}"; then
    train_err "rerun API definitively rejected run ${run_id}; manual correction is required"
    cat "${err}" >&2; rm -f "${err}"; return 5
  fi
  cat "${err}" >&2; rm -f "${err}"; return 1
}

# train_request_failed_job_rerun <run-id> <kind> <next-count> [state-callback]
# Callback args are kind,count,base,run,requesting|accepted. The requesting
# phase is durable before the API call; accepted is durable after success or a
# conflict that means GitHub already has the rerun.
train_request_failed_job_rerun() {
  local run_id="$1" kind="$2" next_count="$3" callback="${4:-}"
  local state="${TRAIN_RERUN_RESUME_STATE_JSON:-}" base="" state_kind="" state_run="" state_phase=""
  TRAIN_RERUN_RECONCILED=0

  if [[ -z "${state}" ]] && declare -F train_state_read >/dev/null 2>&1; then
    state="$(train_state_read 2>/dev/null || echo "")"
  fi
  if [[ -n "${state}" ]] && jq -e . >/dev/null 2>&1 <<<"${state}"; then
    state_run="$(jq -r '.active_batch.run_id // empty' <<<"${state}")"
    state_kind="$(jq -r '.active_batch.rerun_kind // empty' <<<"${state}")"
    state_phase="$(jq -r '.active_batch.phase // empty' <<<"${state}")"
    base="$(jq -r '.active_batch.rerun_base_attempt // empty' <<<"${state}")"
  fi

  if [[ "${state_run}" == "${run_id}" && "${state_kind}" == "${kind}" \
    && "${state_phase}" == "${kind}-retry-accepted" && "${base}" =~ ^[0-9]+$ ]]; then
    TRAIN_RERUN_BASE_ATTEMPT="${base}"
    TRAIN_RERUN_KIND="${kind}"
    TRAIN_RERUN_RECONCILED=1
    train_warn "reconciling accepted ${kind} rerun for run ${run_id} from attempt ${base}; refusing duplicate rerun"
    return 0
  fi

  if [[ "${state_run}" == "${run_id}" && "${state_kind}" == "${kind}" \
    && ( "${state_phase}" == "${kind}-retry-requesting" || "${state_phase}" == "${kind}-retry-intent" ) \
    && "${base}" =~ ^[0-9]+$ ]]; then
    TRAIN_RERUN_BASE_ATTEMPT="${base}"
    TRAIN_RERUN_KIND="${kind}"
    TRAIN_RERUN_RECONCILED=1
    if train_wait_for_rerun_visibility "${run_id}" "${base}"; then
      train_warn "reconciled ${kind} rerun accepted before persisted state advanced"
    else
      train_err "requesting ${kind} rerun for run ${run_id} is ambiguous; preserving state without repeating the non-idempotent rerun request"
      return 4
    fi
    if [[ -n "${callback}" ]] && ! "${callback}" "${kind}" "${next_count}" "${base}" "${run_id}" accepted; then
      train_err "rerun was accepted but accepted state could not be persisted; requesting state remains recoverable"
      return 4
    fi
    return 0
  fi

  base="$(gh run view "${run_id}" --json attempt --jq '.attempt' 2>/dev/null || echo "")"
  if [[ ! "${base}" =~ ^[0-9]+$ ]]; then
    if [[ "${TRAIN_APPLY:-0}" != "1" ]]; then
      base=1
    else
      train_err "could not record Actions attempt before rerunning ${kind} run ${run_id}"
      return 3
    fi
  fi
  TRAIN_RERUN_BASE_ATTEMPT="${base}"
  TRAIN_RERUN_KIND="${kind}"
  if [[ -n "${callback}" ]] && ! "${callback}" "${kind}" "${next_count}" "${base}" "${run_id}" requesting; then
    train_err "could not persist requesting ${kind} rerun before side effect for run ${run_id}"
    return 3
  fi

  local send_rc=0
  train_send_failed_job_rerun "${run_id}" || send_rc=$?
  if [[ "${send_rc}" == "5" ]]; then
    if [[ -n "${callback}" ]] && ! "${callback}" "${kind}" "${next_count}" "${base}" "${run_id}" rejected; then
      train_err "definitive rerun rejection could not be persisted"
    fi
    return 5
  fi
  if [[ "${send_rc}" == "4" ]]; then
    if ! train_wait_for_rerun_visibility "${run_id}" "${base}"; then
      train_warn "rerun API conflict for run ${run_id} has no newer-attempt evidence; preserving requesting state"
      return 4
    fi
    train_warn "rerun API conflict reconciled only after observing attempt advancement for run ${run_id}"
  fi
  if [[ "${send_rc}" != "0" && "${send_rc}" != "4" ]]; then
    train_err "failed to request failed-job rerun for ${kind} run ${run_id}; requesting state remains recoverable"
    [[ -n "${callback}" ]] && return 4 || return 3
  fi
  if [[ -n "${callback}" ]] && ! "${callback}" "${kind}" "${next_count}" "${base}" "${run_id}" accepted; then
    train_err "rerun accepted but accepted state could not be persisted; requesting state remains recoverable"
    return 4
  fi
  return 0
}

# train_run_logs_match_timeout <run-id> [failing-job-names]
# Test override: TRAIN_RUN_LOG_TEXT supplies the log text directly.
train_run_logs_match_timeout() {
  local run_id="$1" failing_names="${2:-}"
  if [[ -n "${TRAIN_RUN_LOG_TEXT:-}" ]]; then
    train_log_is_timeout "${TRAIN_RUN_LOG_TEXT}"
    return $?
  fi

  local rows jid name saw_job=0
  rows="$(gh run view "${run_id}" --json jobs \
    --jq '.jobs[] | select(.conclusion=="failure") | [.databaseId, .name] | @tsv' \
    2>/dev/null || echo "")"
  while IFS=$'\t' read -r jid name; do
    [[ -n "${jid}" ]] || continue
    if [[ -n "${failing_names}" ]] && ! grep -Fqx -- "${name}" <<<"${failing_names}"; then
      continue
    fi
    saw_job=1
    if train_log_is_timeout "$(gh run view --job "${jid}" --log 2>/dev/null || echo "")"; then
      return 0
    fi
  done <<<"${rows}"

  if [[ "${saw_job}" == "0" ]]; then
    train_log_is_timeout "$(gh run view "${run_id}" --log-failed 2>/dev/null || echo "")"
    return $?
  fi
  return 1
}

# train_classify_timeout <run-id> <retry-count> [failing-job-names]
# Returns 0 after issuing a retry, 1 when this is not a timeout, and 2 when a
# timeout persisted past the cap and must be handled as a real failure.
train_classify_timeout() {
  local run_id="$1" retry_count="${2:-0}" failing_names="${3:-}" callback="${4:-}"
  train_run_logs_match_timeout "${run_id}" "${failing_names}" || return 1
  if [[ "${retry_count}" -ge "${TRAIN_TIMEOUT_RERUN_CAP}" ]]; then
    train_warn "timeout/exit-124 failure persisted after ${TRAIN_TIMEOUT_RERUN_CAP} failed-job retry; treating as real"
    return 2
  fi
  train_log "timeout/exit-124 signature matched; rerunning failed jobs once"
  train_request_failed_job_rerun "${run_id}" timeout "$((retry_count + 1))" "${callback}"
}

# train_classify_retry_candidate <run-id> <timeout-count> <flake-count> [jobs]
# Orchestration policy used by the main loop and focused tests. Timeout has
# strict precedence: a persistent timeout is REAL even when its log also
# matches a known-flake regex, so the known-flake merge-through path is skipped.
# Returns 0=rerun accepted, 1=real, 2=known-flake merge-through,
# 3=pre-request failure, 4=ambiguous requesting state preserved, 5=definitive
# API rejection persisted for terminal recovery.
# TRAIN_RETRY_KIND is set to timeout or flake for successful rerun requests.
train_classify_retry_candidate() {
  local run_id="$1" timeout_count="${2:-0}" flake_count="${3:-0}" jobs="${4:-}" callback="${5:-}"
  local rc=0
  TRAIN_RETRY_KIND=""
  train_classify_timeout "${run_id}" "${timeout_count}" "${jobs}" "${callback}" || rc=$?
  case "${rc}" in
    0) TRAIN_RETRY_KIND=timeout; return 0 ;;
    2) return 1 ;;
    3|4|5) return "${rc}" ;;
  esac

  rc=0
  train_classify_flake "${run_id}" "${flake_count}" "${callback}" || rc=$?
  [[ "${rc}" == "0" ]] && TRAIN_RETRY_KIND=flake
  return "${rc}"
}
