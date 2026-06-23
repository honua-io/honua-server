#!/usr/bin/env bash
# Step 5: classify-flake — run BEFORE attribute. Scan the failing jobs' logs for
# the flake signature regex (40P01 / deadlock detected / ryuk / Testcontainers
# timeouts/connection-refused). On a match, do a SINGLE `gh run rerun <id>
# --failed` (cap TRAIN_FLAKE_RERUN_CAP, default 1) — never a bisection. If the
# same signature reproduces on the rerun, treat it as a REAL failure and fall
# through to attribute.

# train_run_logs_match_flake <run-id>: fetch the failed jobs' logs and test them
# against the flake regex. Returns 0 (flake) / 1 (not). Test override:
# TRAIN_RUN_LOG_TEXT supplies log text directly (offline fixtures).
train_run_logs_match_flake() {
  local run_id="$1"
  local text
  if [[ -n "${TRAIN_RUN_LOG_TEXT:-}" ]]; then
    text="${TRAIN_RUN_LOG_TEXT}"
  else
    text="$(gh run view "${run_id}" --log-failed 2>/dev/null || echo "")"
  fi
  train_log_is_flake "${text}"
}

# train_classify_flake <run-id> <rerun-count>: if the failure looks like a flake
# and we are under the rerun cap, issue ONE rerun and return 0 (caller re-polls).
# Otherwise return 1 (treat as real -> attribute). The rerun is side-effecting
# (gated by TRAIN_APPLY).
train_classify_flake() {
  local run_id="$1" rerun_count="${2:-0}"
  if ! train_run_logs_match_flake "${run_id}"; then
    # Deterministic path found NO known flake signature. This is the ambiguous
    # condition for the Phase-2 unknown-signature gate: optionally ask Bedrock
    # whether it's a transient infra flake or a real failure (and learn a regex).
    if train_classify_flake_unknown "${run_id}"; then
      if [[ "${rerun_count}" -ge "${TRAIN_FLAKE_RERUN_CAP}" ]]; then
        train_warn "llm-classified flake reproduced (rerun cap ${TRAIN_FLAKE_RERUN_CAP} reached); treating as real"
        return 1
      fi
      train_log "llm[flake.unknown] classified TRANSIENT; issuing single rerun of failed jobs"
      train_side_effect gh run rerun "${run_id}" --failed
      return 0
    fi
    train_log "no flake signature in failing logs; treating as real failure"
    return 1
  fi
  if [[ "${rerun_count}" -ge "${TRAIN_FLAKE_RERUN_CAP}" ]]; then
    train_warn "flake reproduced (rerun cap ${TRAIN_FLAKE_RERUN_CAP} reached); treating as real"
    return 1
  fi
  train_log "flake signature matched; issuing single rerun of failed jobs"
  train_side_effect gh run rerun "${run_id}" --failed
  return 0
}

# --- Phase 2 gate: unknown-signature judgment (gated LLM) ---------------------
# Invoked ONLY when the deterministic flake regex matched NO known signature
# (the ambiguous case). Asks Bedrock: transient infra flake or real failure?
# Returns 0 = TRANSIENT (caller may rerun once), 1 = REAL.
#
# Deterministic fallback (TRAIN_LLM=0 or Bedrock error): treat unknown as REAL
# (return 1) — exactly the conservative Phase-1 behavior.
#
# Cheap learning: if Bedrock proposes a new flake regex line on a second output
# line, we append it to the signature file at TRAIN_FLAKE_REGEX_LEARN_FILE (when
# set and we are in live mode), so a future run recognizes the same signature
# deterministically without an LLM call.
train_classify_flake_unknown() {
  local run_id="$1"
  if ! declare -F bedrock_enabled >/dev/null 2>&1 || ! bedrock_enabled; then
    return 1   # fallback: unknown => REAL (Phase-1 conservative default)
  fi

  local text
  if [[ -n "${TRAIN_RUN_LOG_TEXT:-}" ]]; then
    text="${TRAIN_RUN_LOG_TEXT}"
  else
    text="$(gh run view "${run_id}" --log-failed 2>/dev/null || echo "")"
  fi
  # Cap the log we send (these are classification calls, not generation).
  text="$(printf '%s' "${text}" | tail -c 6000)"

  local sys usr ans verdict regex
  sys="You triage CI failures for a merge train. Given a failing job log that matched no known transient-flake pattern, decide if it is a TRANSIENT infrastructure flake (network blip, container/db startup race, runner eviction, deadlock) or a REAL code/test failure. Respond on the FIRST line with exactly one word: TRANSIENT or REAL. If TRANSIENT, you MAY add a SECOND line containing a single grep -E regex (no surrounding slashes) that would match this flake signature in future logs; otherwise omit the second line."
  usr="Failing job log (tail):
${text}

Is this a transient infra flake or a real failure? First line: TRANSIENT or REAL. Optional second line: a grep -E regex for the flake signature."
  ans="$(bedrock_ask "${sys}" "${usr}")"

  if bedrock_is_error "${ans}"; then
    train_log "llm[flake.unknown] bedrock error; fallback=REAL"
    return 1
  fi

  verdict="$(printf '%s\n' "${ans}" | head -1 | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')"
  if [[ "${verdict}" != transient* ]]; then
    train_log "llm[flake.unknown] decision=REAL"
    return 1
  fi

  # Optional learned regex on line 2 — append for future deterministic matches.
  regex="$(printf '%s\n' "${ans}" | sed -n '2p' | sed 's/^ *//; s/ *$//')"
  if [[ -n "${regex}" ]]; then
    train_log "llm[flake.unknown] decision=TRANSIENT learned-regex=[${regex}]"
    if [[ -n "${TRAIN_FLAKE_REGEX_LEARN_FILE:-}" ]]; then
      # Recording the learned signature is a state mutation; gate it on apply.
      if [[ "${TRAIN_APPLY:-0}" == "1" ]]; then
        printf '%s\n' "${regex}" >>"${TRAIN_FLAKE_REGEX_LEARN_FILE}"
      else
        train_log "DRY-RUN (skipped): append learned flake regex to ${TRAIN_FLAKE_REGEX_LEARN_FILE}"
      fi
    fi
  else
    train_log "llm[flake.unknown] decision=TRANSIENT (no regex offered)"
  fi
  return 0
}
