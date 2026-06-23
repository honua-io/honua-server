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
