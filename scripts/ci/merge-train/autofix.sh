#!/usr/bin/env bash
# Roll-forward AI fix-agent (the capstone) — replaces escalation for FIXABLE
# batch-introduced failures. GATED behind TRAIN_AUTOFIX (default 0, off), exactly
# like TRAIN_LLM. When a batch has REAL, batch-introduced failures (survived the
# pre-existing filter, not a known flake), instead of escalating, the train asks
# an AI fix-agent (Claude via Bedrock, run by the merge-train.yml claude-code
# step) to PATCH the batch branch FORWARD, then surgically re-verifies only the
# failed tests, then lands. Capped at TRAIN_AUTOFIX_CAP (default 2) attempts;
# still-failing after the cap => THEN escalate as genuinely-hard.
#
# SPLIT OF RESPONSIBILITY (script vs workflow):
#   * This script DECIDES (gate + cap), PREPARES the fix request (branch, failing
#     tests + error output, batch diff) into a request file, and CONSUMES the
#     result (did the agent commit?). It makes ZERO Bedrock calls itself.
#   * merge-train.yml's `autofix` step runs anthropics/claude-code-action wired
#     for Bedrock (use_bedrock=true + aws-actions/configure-aws-credentials with
#     the BEDROCK_AWS_* secrets, AWS_REGION=us-west-2, --model the configurable
#     fix model). The action reads the request file as its prompt, edits the
#     working tree on the batch branch, and commits (authored Mike McDougall, no
#     bot attribution). The script then checks whether a new commit landed.
#
# This file is SOURCEABLE: defines functions only; no work at source time. With
# TRAIN_AUTOFIX=0 every gate is inert and the train behaves exactly like today
# (escalate on a real, attributable, non-flake failure).

# --- configuration knobs (env-overridable) -----------------------------------
# TRAIN_AUTOFIX gates the ENTIRE roll-forward AI fix layer. 0 (default) => the
# train escalates fixable failures exactly like Phase 1. 1 => fixable failures
# route to the AI fix-agent (the merge-train.yml autofix step) before escalation.
: "${TRAIN_AUTOFIX:=0}"
# Max AI fix attempts per batch before escalating as genuinely-hard.
: "${TRAIN_AUTOFIX_CAP:=2}"
# The fix model. Configurable; default a Sonnet-class model for code edits (the
# fix-agent edits code, unlike the cheap Haiku classification gates). Operators
# override via the workflow input / repo var.
: "${TRAIN_AUTOFIX_MODEL:=us.anthropic.claude-sonnet-4-5-20250929-v1:0}"

# autofix_enabled: is the roll-forward AI fix layer turned on? (TRAIN_AUTOFIX=1)
autofix_enabled() { [[ "${TRAIN_AUTOFIX:-0}" == "1" ]]; }

# train_autofix_request_file: where the fix request (the prompt the claude-code
# action consumes) is written. Defaults under TRAIN_WORK; the workflow points the
# action's prompt at this path.
: "${TRAIN_AUTOFIX_REQUEST_FILE:=${TRAIN_WORK:-/tmp}/autofix-request.md}"
# Where the script records the batch HEAD sha BEFORE the fix, so it can detect
# whether the action produced a new commit.
: "${TRAIN_AUTOFIX_PREHEAD_FILE:=${TRAIN_WORK:-/tmp}/autofix-prehead}"

# train_autofix_write_request <batch-branch> <introduced-failing-jobs-newline> \
#   <failing-test-fqns-newline> <error-output> : render the fix-request prompt
# the claude-code action consumes. Includes the batch branch, the
# BATCH-INTRODUCED failing tests + their error output, and the batch diff. The
# instruction is FIX-FORWARD: patch brittle/wrong tests OR a genuine mechanical
# bug, commit to the batch branch (authored Mike McDougall, NO bot attribution),
# touch ONLY what is needed. READ-ONLY w.r.t. git (just renders a file).
train_autofix_write_request() {
  local batch="$1" jobs="$2" fqns="$3" errout="$4"
  local req="${TRAIN_AUTOFIX_REQUEST_FILE}"
  mkdir -p "$(dirname "${req}")" 2>/dev/null || true

  local diff
  if [[ -n "${TRAIN_AUTOFIX_DIFF_CMD:-}" ]]; then
    diff="$("${TRAIN_AUTOFIX_DIFF_CMD}" "${batch}")"
  else
    diff="$(git -C "${TRAIN_REPO_ROOT}" diff "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}...${batch}" 2>/dev/null || echo "")"
  fi
  # Cap the diff we embed so a huge batch can't blow the prompt.
  diff="$(printf '%s' "${diff}" | head -c "${TRAIN_AUTOFIX_DIFF_CAP:-60000}")"
  errout="$(printf '%s' "${errout}" | tail -c "${TRAIN_AUTOFIX_ERR_CAP:-12000}")"

  {
    printf '# Merge-train roll-forward fix request\n\n'
    printf 'You are fixing a failing batch on the merge train. The batch branch is `%s`, ' "${batch}"
    printf 'already checked out. The batch combines several PRs that each passed CI alone but '
    printf 'the assembled batch has the BATCH-INTRODUCED failures below (pre-existing trunk '
    printf 'failures have already been subtracted — do NOT chase those).\n\n'

    printf '## Fix forward — your task\n\n'
    printf -- '- Make the BATCH-INTRODUCED failing tests pass by patching FORWARD.\n'
    printf -- '- A failure is fixable in two shapes: (a) a brittle/wrong test (e.g. a test '
    printf 'asserting an Esri-only field this server does not owe, or an over-specific snapshot) '
    printf '— correct or relax the test to the contract we actually implement; OR (b) a genuine '
    printf 'mechanical bug in the change — fix the code.\n'
    printf -- '- Touch ONLY what is needed to make the listed tests pass. Do not refactor, '
    printf 'reformat unrelated files, or change behavior beyond the failure.\n'
    printf -- '- Commit your fix to the batch branch. Author the commit as `Mike McDougall '
    printf '<mike@honua.io>`. Add NO bot attribution: no Co-Authored-By, no "Generated with", no emoji.\n'
    printf -- '- Use a conventional-commit message, e.g. `fix(merge-train): ...` or `test: ...`.\n'
    printf -- '- If the failure is NOT safely fixable forward (a real product regression that '
    printf 'needs author judgment), make NO commit and say so — the train will escalate to a human.\n\n'

    printf '## Batch-introduced failing jobs\n\n```\n%s\n```\n\n' "$(printf '%s\n' "${jobs}" | sed '/^$/d')"

    if [[ -n "$(printf '%s' "${fqns}" | sed '/^$/d')" ]]; then
      printf '## Batch-introduced failing tests (FQNs)\n\n```\n%s\n```\n\n' "$(printf '%s\n' "${fqns}" | sed '/^$/d')"
    fi

    printf '## Failing test error output\n\n```\n%s\n```\n\n' "${errout}"

    printf '## Batch diff (vs %s/%s)\n\n```diff\n%s\n```\n' "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}" "${diff}"
  } >"${req}"

  printf '%s' "${req}"
}

# train_autofix_record_prehead <batch-branch>: stash the batch HEAD sha before
# the AI fix step runs, so train_autofix_committed can tell if a fix commit
# landed. READ-ONLY w.r.t. remote (local rev-parse only).
train_autofix_record_prehead() {
  local batch="$1"
  local sha
  sha="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${batch}" 2>/dev/null || echo "")"
  printf '%s\n' "${sha}" >"${TRAIN_AUTOFIX_PREHEAD_FILE}"
  printf '%s' "${sha}"
}

# train_autofix_committed <batch-branch>: did the AI fix step produce a NEW
# commit on the batch branch since train_autofix_record_prehead? Returns 0 (yes,
# a fix was committed) / 1 (no change — the agent declined or failed).
# Test override: TRAIN_AUTOFIX_POSTHEAD forces the post-fix sha (offline fixtures).
train_autofix_committed() {
  local batch="$1"
  local pre post
  pre="$(cat "${TRAIN_AUTOFIX_PREHEAD_FILE}" 2>/dev/null || echo "")"
  if [[ -n "${TRAIN_AUTOFIX_POSTHEAD:-}" ]]; then
    post="${TRAIN_AUTOFIX_POSTHEAD}"
  else
    post="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${batch}" 2>/dev/null || echo "")"
  fi
  [[ -n "${post}" && "${post}" != "${pre}" ]]
}

# train_run_autofix_step <batch> <request-file>: invoke the AI fix-agent. The
# REAL invocation is the merge-train.yml claude-code-action step (Bedrock); this
# function is the seam the orchestrator calls so the whole loop is testable
# offline. When TRAIN_AUTOFIX_STEP_CMD is set (the workflow exports it to point at
# the action wrapper, or a fixture sets it to a fake), it is invoked as
#   "${TRAIN_AUTOFIX_STEP_CMD}" <batch> <request-file>
# and is expected to (live) edit + commit on the batch branch. Side-effecting; in
# dry-run we LOG only and make no commit (so a dry-run is provably read-only).
train_run_autofix_step() {
  local batch="$1" req="$2"
  if [[ "${TRAIN_APPLY:-0}" != "1" ]]; then
    train_log "DRY-RUN (skipped): invoke AI fix-agent on ${batch} (request: ${req})"
    return 0
  fi
  if [[ -n "${TRAIN_AUTOFIX_STEP_CMD:-}" ]]; then
    "${TRAIN_AUTOFIX_STEP_CMD}" "${batch}" "${req}"
    return 0
  fi
  # No step command wired (e.g. running the script directly outside the workflow
  # that provides the claude-code-action). Treat as "no fix produced" so the
  # caller falls back to escalation rather than wedging.
  train_warn "no TRAIN_AUTOFIX_STEP_CMD wired; AI fix step is a no-op (caller will escalate)"
  return 0
}

# train_autofix_attempt <batch> <introduced-jobs> <fqns> <errout> <attempt>:
# one full fix attempt: write the request, record pre-head, run the AI fix step,
# detect a commit. Returns:
#   0  => a fix commit was produced (caller surgically re-verifies).
#   1  => no commit produced / cap reached (caller escalates).
# Honors TRAIN_AUTOFIX_CAP.
train_autofix_attempt() {
  local batch="$1" jobs="$2" fqns="$3" errout="$4" attempt="${5:-0}"
  if ! autofix_enabled; then
    train_log "autofix disabled (TRAIN_AUTOFIX=0); not attempting AI fix"
    return 1
  fi
  if [[ "${attempt}" -ge "${TRAIN_AUTOFIX_CAP}" ]]; then
    train_warn "autofix cap (${TRAIN_AUTOFIX_CAP}) reached; escalating"
    return 1
  fi

  local req; req="$(train_autofix_write_request "${batch}" "${jobs}" "${fqns}" "${errout}")"
  train_log "autofix attempt $((attempt + 1)): request written to ${req} (model ${TRAIN_AUTOFIX_MODEL})"
  train_autofix_record_prehead "${batch}" >/dev/null
  train_run_autofix_step "${batch}" "${req}"

  if train_autofix_committed "${batch}"; then
    train_decision "autofix attempt $((attempt + 1)) produced a fix commit on ${batch}; surgically re-verifying"
    return 0
  fi
  train_warn "autofix attempt $((attempt + 1)) produced no commit; treating as unfixable"
  return 1
}
