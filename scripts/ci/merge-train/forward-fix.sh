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
  # Aggregator / non-blocking meta-jobs (CI Gate, Test Suite Summary, and the
  # non-blocking aux jobs in TRAIN_NONBLOCKING_JOBS) fail as a CONSEQUENCE of the
  # real failure — they must NOT count toward "is the sole real failure a format
  # failure". Without this strip, a format-only batch reds on BOTH "Build & Format
  # Check" AND "CI Gate" (n=2 -> not format-only), so the forward-fix NEVER fired
  # and the batch escalated instead of auto-running `dotnet format`. Strip them,
  # then require exactly one remaining (real) failing job whose name is the format
  # job.
  local real
  real="$(printf '%s\n' "${failing}" | sed '/^$/d' | grep -vxiF "${TRAIN_NONBLOCKING_JOBS}" || true)"
  local n
  n="$(printf '%s\n' "${real}" | sed '/^$/d' | wc -l | tr -d ' ')"
  [[ "${n}" == "1" ]] || return 1
  printf '%s\n' "${real}" | grep -Eqi 'format'
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

# --- Phase 2 gate: drift-heal-safety judgment (gated LLM) ---------------------
# Invoked ONLY when a NON-format drift failure appears (proof-ledger / OpenAPI /
# feature-catalog drift) — the ambiguous case the deterministic train would
# otherwise escalate outright. Asks Bedrock whether the drift is SAFELY
# auto-healable by a known idempotent generator, or whether it needs a human.
#
# Returns 0 = a known generator is named on stdout (caller MAY run it), or
#         1 = ESCALATE (no safe generator).
#
# Deterministic fallback (TRAIN_LLM=0, Bedrock error, or anything other than a
# confident, allowlisted generator answer): ESCALATE — never auto-patch. This is
# both the fallback AND the expected usual answer; the gate exists to catch the
# rare, unambiguous "regenerate the OpenAPI snapshot" case, not to be clever.
#
# SAFETY: only generators on the TRAIN_HEAL_ALLOWLIST are ever accepted, so an
# LLM hallucination cannot make the train run an arbitrary command. The caller is
# still responsible for actually invoking the generator under the build lock and
# re-running CI; this gate only DECIDES, it does not execute.
: "${TRAIN_HEAL_ALLOWLIST:=update-openapi-snapshot|regenerate-feature-catalog|regenerate-proof-ledger}"
train_forward_fix_heal_safe() {
  local failing="$1" drift_detail="${2:-}"

  if ! declare -F bedrock_enabled >/dev/null 2>&1 || ! bedrock_enabled; then
    train_log "llm[forwardfix.heal] disabled; fallback=ESCALATE"
    return 1   # fallback: escalate, never auto-patch
  fi

  local sys usr ans verdict gen
  sys="You guard a merge train's auto-heal. A NON-format CI drift failure appeared (e.g. OpenAPI snapshot, feature catalog, or proof ledger out of date). Decide if it is SAFELY auto-healable by re-running a known deterministic generator, or if a human must fix it. Respond on the FIRST line with exactly one word: HEAL or ESCALATE. If HEAL, add a SECOND line naming exactly one generator from this allowlist: ${TRAIN_HEAL_ALLOWLIST//|/, }. If you are not certain, answer ESCALATE."
  usr="Failing job(s):
$(printf '%s\n' "${failing}" | sed '/^$/d')

Drift detail:
${drift_detail}

Is this safely auto-healable by a known generator, or must a human fix it? First line: HEAL or ESCALATE. Optional second line: the generator name."
  ans="$(bedrock_ask "${sys}" "${usr}")"

  if bedrock_is_error "${ans}"; then
    train_log "llm[forwardfix.heal] bedrock error; fallback=ESCALATE"
    return 1
  fi

  verdict="$(printf '%s\n' "${ans}" | head -1 | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')"
  if [[ "${verdict}" != heal* ]]; then
    train_log "llm[forwardfix.heal] decision=ESCALATE"
    return 1
  fi

  gen="$(printf '%s\n' "${ans}" | sed -n '2p' | sed 's/^ *//; s/ *$//')"
  if [[ -z "${gen}" ]] || ! printf '%s' "${gen}" | grep -Eq "^(${TRAIN_HEAL_ALLOWLIST})$"; then
    # HEAL with no/invalid generator is unsafe: fall back to escalation.
    train_warn "llm[forwardfix.heal] HEAL with non-allowlisted generator [${gen}]; fallback=ESCALATE"
    return 1
  fi
  train_log "llm[forwardfix.heal] decision=HEAL generator=[${gen}]"
  printf '%s\n' "${gen}"
  return 0
}
