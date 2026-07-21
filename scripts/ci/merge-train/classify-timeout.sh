#!/usr/bin/env bash
# A generic timeout/exit-124 failure gets one failed-job-only rerun. If it
# reproduces, it is real and must continue to attribution; unlike a recognized
# environmental flake, a persistent timeout is never eligible to merge through.

train_log_is_timeout() {
  grep -Eiq 'process completed with exit code 124|exit(ed)?( with)?( code)?[ =:]124|tim(e|ed)[ -]?out after|timeout after|command timed out|execution timed out' <<<"$1"
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
  local run_id="$1" retry_count="${2:-0}" failing_names="${3:-}"
  train_run_logs_match_timeout "${run_id}" "${failing_names}" || return 1
  if [[ "${retry_count}" -ge "${TRAIN_TIMEOUT_RERUN_CAP}" ]]; then
    train_warn "timeout/exit-124 failure persisted after ${TRAIN_TIMEOUT_RERUN_CAP} failed-job retry; treating as real"
    return 2
  fi
  train_log "timeout/exit-124 signature matched; rerunning failed jobs once"
  train_side_effect gh run rerun "${run_id}" --failed
  return 0
}
