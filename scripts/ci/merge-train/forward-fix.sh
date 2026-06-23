#!/usr/bin/env bash
# Step 4: forward-fix — ONLY when the sole failure is the format-verify step
# (`dotnet format Honua.sln --verify-no-changes`). Then run the canonical
# auto-heal (`dotnet format Honua.sln`), commit it on the batch branch, and
# re-run smart-ci. Capped at TRAIN_FWDFIX_CAP (default 2).
#
# EVERYTHING ELSE escalates: proof-ledger / OpenAPI / feature-catalog drift,
# compile failures, test failures. The train NEVER auto-patches those — that is
# the domain of the PR author. Only deterministic, idempotent `dotnet format`
# reformatting is safe to apply on the train's behalf.

# train_is_format_only_failure <failing-job-names-newline>: true if the only
# failing job is the format-verify job (build). We detect the format-verify
# step failure by name "Format Verification" or by the job that owns the
# `dotnet format ... --verify-no-changes` step. Conservatively: returns true
# ONLY when exactly one job failed AND its name matches the format job.
train_is_format_only_failure() {
  local failing="$1"
  local n
  n="$(printf '%s\n' "${failing}" | sed '/^$/d' | wc -l | tr -d ' ')"
  [[ "${n}" == "1" ]] || return 1
  printf '%s\n' "${failing}" | grep -Eqi 'format'
}

# train_forward_fix <batch-branch> <attempt-count>: apply dotnet format, commit,
# return 0 if a change was produced (caller re-runs CI), 1 if cap reached or no
# change. The commit message carries NO bot attribution.
train_forward_fix() {
  local batch="$1" attempt="${2:-0}"
  if [[ "${attempt}" -ge "${TRAIN_FWDFIX_CAP}" ]]; then
    train_warn "forward-fix cap (${TRAIN_FWDFIX_CAP}) reached; escalating"
    return 1
  fi

  train_log "forward-fix attempt $((attempt + 1)): dotnet format Honua.sln"
  # Real build-lock'd format even in dry-run (it is a local, reversible edit and
  # validates the heal path); only the push/commit-propagation is side-effecting.
  if [[ -n "${TRAIN_FORMAT_CMD:-}" ]]; then
    # Test override: a fake formatter that touches files deterministically.
    "${TRAIN_FORMAT_CMD}" "${batch}"
  else
    ( cd "${TRAIN_REPO_ROOT}" && with-build-lock dotnet format Honua.sln )
  fi

  if git -C "${TRAIN_REPO_ROOT}" diff --quiet; then
    train_warn "forward-fix produced no changes; not a format-fixable failure"
    return 1
  fi

  git -C "${TRAIN_REPO_ROOT}" add -A
  git -C "${TRAIN_REPO_ROOT}" commit -q -m "style: dotnet format (train forward-fix)"
  train_log "forward-fix committed on ${batch}"
  return 0
}
