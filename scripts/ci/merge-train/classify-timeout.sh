#!/usr/bin/env bash
# A generic timeout/exit-124 failure gets one failed-job-only rerun. If it
# reproduces, it is real and must continue to attribution; unlike a recognized
# environmental flake, a persistent timeout is never eligible to merge through.

TRAIN_TIMEOUT_TAB="$(printf '\tX')"; TRAIN_TIMEOUT_TAB="${TRAIN_TIMEOUT_TAB%X}"

# Guard state, initialized at source time so `set -u` is safe on every path.
# TRAIN_GUARD_KIND is the classification the ordering guard reached;
# TRAIN_GUARD_SCAN_* is its single-reuse evidence memo (see the guard).
TRAIN_GUARD_KIND=""
TRAIN_GUARD_SCAN_ARMED=0
TRAIN_GUARD_SCAN_KEY=""
TRAIN_GUARD_SCAN_RC=""
TRAIN_GUARD_SCAN_KIND=""
TRAIN_GUARD_SCAN_ATTEMPT=""
TRAIN_GUARD_SCAN_EVIDENCE_DIR=""
TRAIN_FAILURE_EVIDENCE_RUN_ID=""
TRAIN_FAILURE_EVIDENCE_RUN_ATTEMPT=""
TRAIN_FAILURE_EVIDENCE_DIR=""
TRAIN_FAILURE_EVIDENCE_READY=0
TRAIN_FLAKE_EVIDENCE_RUN_ID=""
TRAIN_FLAKE_EVIDENCE_DIR=""

train_log_is_timeout() {
  grep -Eiq 'process completed with exit code 124|exit(ed)?( with)?( code)?[ =:]124|tim(e|ed)[ -]?out after|timeout after|command timed out|execution timed out' <<<"$1"
}

# #3054: a shard that ran out of its CONFIGURED budget while still executing
# tests is a capacity failure, not a hang. The marker predicate itself lives in
# lib.sh (train_log_is_capacity_exhaustion) because the pre-existing-failure
# filter and the early-failure observer must agree with this classifier on what
# counts as capacity evidence.

# train_wait_for_rerun_visibility <run-id> <base-attempt>: bounded grace for an
# asynchronously accepted request to expose attempt > base.
train_wait_for_rerun_visibility() {
  local run_id="$1" base="$2" grace="${TRAIN_RERUN_VISIBILITY_GRACE_SECONDS:-30}"
  local interval="${TRAIN_RERUN_VISIBILITY_POLL_SECONDS:-5}" deadline row attempt status
  deadline=$(( $(train_now) + grace ))
  while :; do
    row="$(train_run_attempt_status "${run_id}" || printf '0\t\n')"
    IFS="${TRAIN_TIMEOUT_TAB}" read -r attempt status <<<"${row}"
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
  # GitHub secondary rate limits surface on this non-idempotent call the same
  # way train_side_effect documents for label/comment writes: 401 "Bad
  # credentials", 403, or 429, even with a valid token and quota remaining.
  # Retry those transient signatures with backoff BEFORE the terminal
  # 401/403/404/422 classification below, or a mid-burst rate limit gets
  # misread as a definitive rejection and escalates the whole batch.
  local attempt=1 max="${TRAIN_SIDE_EFFECT_RETRIES:-4}" delay="${TRAIN_SIDE_EFFECT_BACKOFF:-5}"
  while :; do
    rc=0
    gh run rerun "${run_id}" --failed 2>"${err}" || rc=$?
    if [[ "${rc}" == "0" ]]; then rm -f "${err}"; return 0; fi
    if grep -Eiq 'HTTP 409|already.*(queued|in progress|requested)|cannot rerun.*(queued|in progress|not completed)' "${err}"; then
      train_warn "rerun API reported an already queued/in-progress request for run ${run_id}; reconciling asynchronously"
      rm -f "${err}"; return 4
    fi
    if [[ "${attempt}" -lt "${max}" ]] && grep -qiE \
      "Bad credentials|secondary rate limit|rate limit|submitted too quickly|retry your request|abuse detection|status code: 40[39]|status code: 429|HTTP 40[39]|HTTP 429" \
      "${err}"; then
      train_warn "transient GitHub API failure requesting rerun for run ${run_id} (attempt ${attempt}/${max}, rc=${rc}); backing off ${delay}s and retrying"
      sleep "${delay}"
      attempt=$(( attempt + 1 )); delay=$(( delay * 3 )); : > "${err}"
      continue
    fi
    break
  done
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
    local state_rc=0
    state="$(train_state_read 2>/dev/null)" || state_rc=$?
    if [[ "${state_rc}" != "0" ]]; then
      if [[ "${TRAIN_APPLY:-0}" == "1" ]]; then
        train_err "could not read authoritative merge-train state before rerun"
        return 3
      fi
      # Dry-run/offline callers (shadow mode, the fixture harness invoking the
      # classifier directly with no production state issue/plumbing) have no
      # authoritative state to reconcile against and never issue the real,
      # non-idempotent rerun request below; proceed with no persisted state
      # instead of hard-failing, matching the TRAIN_APPLY-gated fallback the
      # Actions-attempt lookup below already uses.
      state=""
    fi
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

  base="$(gh run view "${run_id}" \
    --repo "${GITHUB_REPOSITORY:-honua-io/honua-server}" \
    --json attempt --jq '.attempt' 2>/dev/null || echo "")"
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
      return 6
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

# train_match_timeout_text <log-text>: 0 when the text shows the shard could not
# finish executing its tests, and sets TRAIN_TIMEOUT_KIND for the caller:
#   capacity  over its configured budget       (terminal, never rerun)
#   killed    SIGKILLed test host, e.g. OOM    (terminal, not a timeout at all)
#   hang      stalled shard or generic exit-124/timeout text (bounded rerun)
# The explicit HONUA_SHARD_* markers are checked BEFORE the generic timeout
# regex: `killed` reports exit 137 and no timeout wording at all, so a
# text-only test would classify it as an ordinary product failure and let it
# reach the pre-existing filter and per-PR attribution (#3213).
train_match_timeout_text() {
  local text="$1"
  TRAIN_TIMEOUT_KIND=""
  if train_log_is_capacity_exhaustion "${text}"; then TRAIN_TIMEOUT_KIND=capacity; return 0; fi
  if train_log_is_shard_killed "${text}"; then TRAIN_TIMEOUT_KIND=killed; return 0; fi
  if train_log_is_shard_hang "${text}"; then TRAIN_TIMEOUT_KIND=hang; return 0; fi
  train_log_is_timeout "${text}" || return 1
  TRAIN_TIMEOUT_KIND=hang
  return 0
}

# train_timeout_kind_is_terminal <kind>: kinds that must never be rerun and are
# never attributable to a batch member.
train_timeout_kind_is_terminal() {
  [[ "$1" == "capacity" || "$1" == "killed" ]]
}

# train_failure_evidence_reset: clear references to the exact-attempt evidence
# shared by the capacity, timeout and flake classifiers. A caller must never
# retain a partial or older-attempt bundle after a failed read.
train_failure_evidence_reset() {
  local failure_dir="${TRAIN_FAILURE_EVIDENCE_DIR:-}"
  local flake_dir="${TRAIN_FLAKE_EVIDENCE_DIR:-}"
  local guard_dir="${TRAIN_GUARD_SCAN_EVIDENCE_DIR:-}"

  if [[ -n "${failure_dir}" && "${failure_dir}" != "${guard_dir}" ]]; then
    train_failure_evidence_discard "${failure_dir}"
  fi
  if [[ -n "${flake_dir}" && "${flake_dir}" != "${failure_dir}" \
    && "${flake_dir}" != "${guard_dir}" ]]; then
    train_failure_evidence_discard "${flake_dir}"
  fi

  TRAIN_FAILURE_EVIDENCE_RUN_ID=""
  TRAIN_FAILURE_EVIDENCE_RUN_ATTEMPT=""
  TRAIN_FAILURE_EVIDENCE_DIR=""
  TRAIN_FAILURE_EVIDENCE_READY=0
  TRAIN_FLAKE_EVIDENCE_RUN_ID=""
  TRAIN_FLAKE_EVIDENCE_DIR=""
}

# Evidence bundles are controller-owned temporary directories. Restrict cleanup
# to the exact mktemp prefix even if a corrupted environment reaches this helper.
train_failure_evidence_discard() {
  local evidence_dir="${1:-}"
  case "${evidence_dir}" in
    "${RUNNER_TEMP:-/tmp}"/honua-train-failure-evidence.*)
      [[ -d "${evidence_dir}" ]] && rm -rf -- "${evidence_dir}"
      ;;
  esac
}

train_failure_evidence_bundle_has_files() {
  local evidence_dir="$1" evidence_file
  [[ -d "${evidence_dir}" ]] || return 1
  for evidence_file in "${evidence_dir}"/job-*.log; do
    [[ -s "${evidence_file}" ]] && return 0
  done
  return 1
}

# train_read_failed_job_snapshot <run-id>: return one JSON snapshot containing
# the current run attempt, terminal run status and its jobs. Reading all three
# together prevents a rerun transition from pairing one attempt's identity with
# another attempt's job ids. Tests may inject TRAIN_FAILED_JOB_SNAPSHOT_READER.
train_read_failed_job_snapshot() {
  local run_id="$1"
  if [[ -n "${TRAIN_FAILED_JOB_SNAPSHOT_READER:-}" ]]; then
    "${TRAIN_FAILED_JOB_SNAPSHOT_READER}" "${run_id}"
    return
  fi
  gh run view "${run_id}" \
    --repo "${GITHUB_REPOSITORY:-honua-io/honua-server}" \
    --json attempt,status,jobs 2>/dev/null
}

# train_run_logs_match_timeout <run-id> [failing-job-names]
# Sets TRAIN_TIMEOUT_KIND to capacity|killed|hang on a match (empty otherwise).
# Returns 2 when any selected failing job's log is unavailable. Missing failure
# evidence must never fall through to per-PR attribution: without the log we
# cannot distinguish product failure, timeout, capacity exhaustion, or runner
# loss. Capacity still wins immediately when another readable log proves it.
# Test override: TRAIN_RUN_LOG_TEXT supplies the log text directly.
train_run_logs_match_timeout() {
  local run_id="$1" failing_names="${2:-}"
  TRAIN_TIMEOUT_KIND=""
  train_failure_evidence_reset
  if [[ -n "${TRAIN_RUN_LOG_TEXT:-}" ]]; then
    TRAIN_FAILURE_EVIDENCE_RUN_ID="${run_id}"
    TRAIN_FAILURE_EVIDENCE_RUN_ATTEMPT="fixture"
    TRAIN_FAILURE_EVIDENCE_READY=1
    train_match_timeout_text "${TRAIN_RUN_LOG_TEXT}"
    return $?
  fi

  local snapshot attempt status rows jid name conclusion text annotations
  local evidence_dir evidence_file match_kind terminal_kind=""
  local saw_job=0 saw_evidence=0 saw_timeout=0 logs_complete=1
  snapshot="$(train_read_failed_job_snapshot "${run_id}" 2>/dev/null || echo "")"
  if ! train_has_content "${snapshot}" || ! jq -e . >/dev/null 2>&1 <<<"${snapshot}"; then
    return 2
  fi
  attempt="$(jq -r '.attempt // empty' <<<"${snapshot}")"
  status="$(jq -r '.status // empty' <<<"${snapshot}")"
  if [[ ! "${attempt}" =~ ^[0-9]+$ || "${status}" != "completed" ]]; then
    return 2
  fi
  if ! rows="$(jq -er '.jobs | arrays | .[]
      | select(.conclusion=="failure" or .conclusion=="cancelled"
               or .conclusion=="timed_out" or .conclusion=="startup_failure")
      | [(.databaseId // ""), (.name // ""), (.conclusion // "")] | @tsv' \
      <<<"${snapshot}")"; then
    return 2
  fi
  evidence_dir="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-train-failure-evidence.XXXXXX")" || return 2
  # Scan EVERY selected failing job before deciding the kind. A generic timeout
  # in one job must not shortcut past a capacity-exhausted shard in another, or
  # the caller reruns the very job this change promises never to retry. Capacity
  # has precedence and is terminal. The scan still completes every selected job
  # so the preserved bundle is complete and each job remains an isolated record.
  while IFS="${TRAIN_TIMEOUT_TAB}" read -r jid name conclusion; do
    [[ -n "${jid}" ]] || continue
    if [[ -n "${failing_names}" ]] && ! grep -Fqx -- "${name}" <<<"${failing_names}"; then
      continue
    fi
    saw_job=$(( saw_job + 1 ))
    printf -v evidence_file '%s/job-%06d.log' "${evidence_dir}" "${saw_job}"
    # Actions uses these terminal conclusions for job-level timeout, runner
    # loss, and startup failure. They have no reliable exit-124 log to grep,
    # but a single failed-job rerun is still fail-closed: no code lands unless
    # the newer attempt completes with explicit success.
    case "${conclusion}" in
      cancelled|timed_out|startup_failure)
        printf '[job %s | %s | attempt %s] terminal conclusion: %s\n' \
          "${jid}" "${name}" "${attempt}" "${conclusion}" >"${evidence_file}"
        saw_evidence=1
        saw_timeout=1
        continue
        ;;
    esac
    # Workflow `::error::` markers are exact-job annotations. Read that small,
    # paginated surface first so capacity classification does not wait on or
    # download a 20 MB aggregate log. A complete generic timeout annotation is
    # also sufficient for the existing bounded hang retry.
    if annotations="$(train_read_job_annotations "${jid}")"; then
      if train_has_content "${annotations}"; then
        printf '[job %s | %s | attempt %s | annotations]\n%s\n' \
          "${jid}" "${name}" "${attempt}" "${annotations}" >>"${evidence_file}"
        saw_evidence=1
      fi
      if train_match_timeout_text "${annotations}"; then
        match_kind="${TRAIN_TIMEOUT_KIND}"
        if train_timeout_kind_is_terminal "${match_kind}"; then
          if [[ "${match_kind}" == "capacity" || -z "${terminal_kind}" ]]; then
            terminal_kind="${match_kind}"
          fi
        fi
        saw_timeout=1
        continue
      fi
    fi
    if text="$(train_read_job_log "${jid}")"; then
      if ! train_has_content "${text}"; then
        logs_complete=0
        continue
      fi
      printf '[job %s | %s | attempt %s | log]\n%s\n' \
        "${jid}" "${name}" "${attempt}" "${text}" >>"${evidence_file}"
      saw_evidence=1
      if train_match_timeout_text "${text}"; then
        match_kind="${TRAIN_TIMEOUT_KIND}"
        saw_timeout=1
        if train_timeout_kind_is_terminal "${match_kind}"; then
          if [[ "${match_kind}" == "capacity" || -z "${terminal_kind}" ]]; then
            terminal_kind="${match_kind}"
          fi
        fi
      fi
    else
      logs_complete=0
    fi
  done <<<"${rows}"

  if [[ "${logs_complete}" != "1" ]]; then
    TRAIN_TIMEOUT_KIND=""
    train_failure_evidence_discard "${evidence_dir}"
    train_failure_evidence_reset
    return 2
  fi
  if [[ "${saw_job}" == "0" || "${saw_evidence}" == "0" ]] \
    || ! train_failure_evidence_bundle_has_files "${evidence_dir}"; then
    TRAIN_TIMEOUT_KIND=""
    train_failure_evidence_discard "${evidence_dir}"
    train_failure_evidence_reset
    return 2
  fi
  TRAIN_FAILURE_EVIDENCE_RUN_ID="${run_id}"
  TRAIN_FAILURE_EVIDENCE_RUN_ATTEMPT="${attempt}"
  TRAIN_FAILURE_EVIDENCE_DIR="${evidence_dir}"
  TRAIN_FAILURE_EVIDENCE_READY=1
  if [[ -n "${terminal_kind}" ]]; then
    TRAIN_TIMEOUT_KIND="${terminal_kind}"
    return 0
  fi
  if [[ "${saw_timeout}" == "1" ]]; then
    TRAIN_TIMEOUT_KIND=hang
    return 0
  fi
  TRAIN_TIMEOUT_KIND=""
  return 1
}

# train_classify_capacity_guard <run-id> [failing-job-names]
# #3213 ORDERING GUARD, and the single evidence-reading entrypoint for a failed
# batch. It answers one question for the whole failing set BEFORE any other step
# is allowed to reinterpret it: did these jobs produce a comparable failure
# CAUSE at all?
#
# Sets TRAIN_GUARD_KIND and returns:
#   0  ordinary comparable failures            (TRAIN_GUARD_KIND="")
#   7  terminal and NOT attributable to a PR   (capacity | shard-killed)
#   8  no readable failure evidence            (evidence-unavailable)
#   9  shard-terminal but retryable            (shard-timeout: stall/exit-124)
#
# rc 7 and rc 8 stop the batch outright. rc 9 does NOT stop it, but the shard
# still never finished executing its tests, so its failure may not be subtracted
# as pre-existing either — the caller must bypass that filter and let the
# bounded hang rerun decide.
#
# Transient Actions read failures are retried with backoff before concluding
# evidence-unavailable: a single flaky `gh run view` would otherwise convert a
# batch that should have landed into a whole-batch escalation with sticky
# train:escalated labels. Only a persistently unreadable surface returns 8.
# READ-ONLY: never requests a rerun and never mutates state.
train_classify_capacity_guard() {
  local run_id="$1" failing_names="${2:-}"
  local attempt=1 max="${TRAIN_EVIDENCE_READ_RETRIES:-3}"
  local delay="${TRAIN_EVIDENCE_READ_BACKOFF_SECONDS:-5}" scan_rc=0
  TRAIN_GUARD_KIND=""
  # Single-reuse evidence memo. train_classify_timeout delegates here, so the
  # same annotations and job logs were downloaded twice per failed batch: once
  # by the ordering guard and once by the retry classifier (#3213). The memo is
  # OPT-IN — train.sh arms it once per ci-gate iteration — because a stale reuse
  # would be far worse than a duplicate read: any caller that reuses a run id
  # with different evidence (every focused fixture does) must always rescan.
  # It is consumed on first reuse, so a later attempt's evidence is never served
  # from an earlier scan even within an armed iteration.
  if [[ "${TRAIN_GUARD_SCAN_ARMED:-0}" == "1" && -n "${TRAIN_GUARD_SCAN_KEY}" \
    && "${TRAIN_GUARD_SCAN_KEY}" == "${run_id}|${failing_names}" ]]; then
    scan_rc="${TRAIN_GUARD_SCAN_RC}"
    TRAIN_TIMEOUT_KIND="${TRAIN_GUARD_SCAN_KIND}"
    train_failure_evidence_reset
    if [[ -n "${TRAIN_GUARD_SCAN_ATTEMPT}" ]] \
      && train_failure_evidence_bundle_has_files "${TRAIN_GUARD_SCAN_EVIDENCE_DIR}"; then
      TRAIN_FAILURE_EVIDENCE_RUN_ID="${run_id}"
      TRAIN_FAILURE_EVIDENCE_RUN_ATTEMPT="${TRAIN_GUARD_SCAN_ATTEMPT}"
      TRAIN_FAILURE_EVIDENCE_DIR="${TRAIN_GUARD_SCAN_EVIDENCE_DIR}"
      TRAIN_FAILURE_EVIDENCE_READY=1
    fi
    train_guard_scan_reset
    train_log "reusing this pass's evidence scan for run ${run_id}"
  else
    while :; do
      scan_rc=0
      train_run_logs_match_timeout "${run_id}" "${failing_names}" || scan_rc=$?
      [[ "${scan_rc}" == "2" && "${attempt}" -lt "${max}" ]] || break
      train_warn "failed-job evidence for run ${run_id} was unreadable (attempt ${attempt}/${max}); backing off ${delay}s before concluding it is unavailable"
      sleep "${delay}"
      attempt=$(( attempt + 1 )); delay=$(( delay * 2 ))
    done
    if [[ "${TRAIN_GUARD_SCAN_ARMED:-0}" == "1" ]]; then
      # A narrowed failing-name set can deliberately miss the one-use memo key
      # and produce a fresh exact-attempt bundle. Release the superseded memo
      # before replacing its only remaining directory reference.
      train_guard_scan_reset
      TRAIN_GUARD_SCAN_KEY="${run_id}|${failing_names}"
      TRAIN_GUARD_SCAN_RC="${scan_rc}"
      TRAIN_GUARD_SCAN_KIND="${TRAIN_TIMEOUT_KIND}"
      TRAIN_GUARD_SCAN_ATTEMPT="${TRAIN_FAILURE_EVIDENCE_RUN_ATTEMPT}"
      TRAIN_GUARD_SCAN_EVIDENCE_DIR="${TRAIN_FAILURE_EVIDENCE_DIR}"
    fi
  fi

  if [[ "${scan_rc}" == "2" ]]; then
    TRAIN_GUARD_KIND=evidence-unavailable
    return 8
  fi
  [[ "${scan_rc}" == "0" ]] || return 0
  case "${TRAIN_TIMEOUT_KIND}" in
    capacity) TRAIN_GUARD_KIND=capacity; return 7 ;;
    killed)   TRAIN_GUARD_KIND=shard-killed; return 7 ;;
    *)        TRAIN_GUARD_KIND=shard-timeout; return 9 ;;
  esac
}

# train_guard_scan_reset: drop the memoized guard scan without disarming.
train_guard_scan_reset() {
  local evidence_dir="${TRAIN_GUARD_SCAN_EVIDENCE_DIR:-}"
  if [[ -n "${evidence_dir}" \
    && "${evidence_dir}" != "${TRAIN_FAILURE_EVIDENCE_DIR:-}" \
    && "${evidence_dir}" != "${TRAIN_FLAKE_EVIDENCE_DIR:-}" ]]; then
    train_failure_evidence_discard "${evidence_dir}"
  fi

  TRAIN_GUARD_SCAN_KEY=""
  TRAIN_GUARD_SCAN_RC=""
  TRAIN_GUARD_SCAN_KIND=""
  TRAIN_GUARD_SCAN_ATTEMPT=""
  TRAIN_GUARD_SCAN_EVIDENCE_DIR=""
}

# train_guard_scan_arm: allow ONE downstream reuse of the next guard scan, and
# drop anything memoized by an earlier iteration. train.sh calls this once per
# ci-gate iteration; every other caller stays unarmed and always rescans.
train_guard_scan_arm() {
  TRAIN_GUARD_SCAN_ARMED=1
  train_guard_scan_reset
  train_failure_evidence_reset
}

# train_classify_timeout <run-id> <retry-count> [failing-job-names]
# Returns 0 after issuing a retry, 1 when this is not a shard-terminal failure,
# 2 when a timeout persisted past the cap and must be handled as a real failure,
# 7 (#3054/#3213) when the shard could not finish for a reason no batch member
# caused — over capacity, or its test host was SIGKILLed — and 8 when required
# failure-log evidence is unavailable. The evidence read itself is delegated to
# train_classify_capacity_guard so the two classifiers cannot disagree and the
# logs are read once.
train_classify_timeout() {
  local run_id="$1" retry_count="${2:-0}" failing_names="${3:-}" callback="${4:-}"
  local guard_rc=0
  train_classify_capacity_guard "${run_id}" "${failing_names}" || guard_rc=$?
  case "${guard_rc}" in
    8) return 8 ;;
    0) return 1 ;;
    7)
      # The shard was still executing tests when its configured budget expired,
      # or its host was killed outright. A rerun burns another full shard's
      # worth of runner time and reproduces the same condition, so this never
      # consumes a retry. It is also NOT attributable to one PR, so it must
      # bypass autofix/attribution (which would drop or escalate an arbitrary
      # batch member) and escalate the batch as a whole instead.
      if [[ "${TRAIN_GUARD_KIND}" == "shard-killed" ]]; then
        train_warn "shard test host killed (HONUA_SHARD_KILLED): the shard was SIGKILLed before the runner's own kill deadline, so it is not a timeout and not attributable to one PR. Suspect an out-of-memory kill or an external cancellation and check the runner size in .github/ci-shards.json."
      else
        train_warn "shard capacity exhausted (HONUA_SHARD_CAPACITY_EXHAUSTED): the test step used its whole configured budget while still running tests; this is not a hang and not attributable to one PR. Raise test_timeout_minutes/timeout_minutes or split the shard in .github/ci-shards.json instead of rerunning."
      fi
      return 7
      ;;
  esac
  # guard_rc == 9: a stalled shard or generic exit-124. This is the one
  # shard-terminal shape that still earns the historical bounded rerun.
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
# API rejection persisted for terminal recovery, 6=rejection known but terminal
# state persistence failed (cleanup is unauthorized and must not run),
# 7=the shard could not finish for a reason no batch member caused (#3054/#3213)
# — over capacity, or its test host was SIGKILLed — which must skip autofix and
# per-PR attribution entirely; 8=required failure evidence was not readable,
# which also must skip attribution and fail closed. TRAIN_GUARD_KIND carries the
# exact kind for 7 and 8 so the caller can report the right remediation.
# TRAIN_RETRY_KIND is set to timeout or flake for successful rerun requests.
train_classify_retry_candidate() {
  local run_id="$1" timeout_count="${2:-0}" flake_count="${3:-0}" jobs="${4:-}" callback="${5:-}"
  local rc=0
  TRAIN_RETRY_KIND=""
  train_classify_timeout "${run_id}" "${timeout_count}" "${jobs}" "${callback}" || rc=$?
  case "${rc}" in
    0) TRAIN_RETRY_KIND=timeout; return 0 ;;
    2) return 1 ;;
    3|4|5|6|7|8) return "${rc}" ;;
  esac

  rc=0
  train_classify_flake "${run_id}" "${flake_count}" "${callback}" || rc=$?
  [[ "${rc}" == "0" ]] && TRAIN_RETRY_KIND=flake
  return "${rc}"
}
