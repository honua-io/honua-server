#!/usr/bin/env bash
HONUA_TAB="$(printf '\tX')"; HONUA_TAB="${HONUA_TAB%X}"
HONUA_NL="$(printf '\nX')"; HONUA_NL="${HONUA_NL%X}"
# Orchestrator for the honua-server optimistic batch merge train (Phase 1).
#
# Wires the eight sourceable steps together. DRY-RUN BY DEFAULT (TRAIN_APPLY=0):
# real local git assembly + real CI-status reads + real shard computation, but
# NO pushes/merges/comments/issue-writes (those route through train_side_effect
# and are only logged). MAX_BATCH defaults to 3.
#
# Phases (also the resume points written to the state issue before each
# side-effecting step):
#   select -> assemble -> smart-ci -> [forward-fix] -> [pre-existing-filter] ->
#   [classify-timeout -> classify-flake] ->
#   [autofix (Bedrock fix-agent + surgical re-verify)] ->
#   [attribute -> rebuild | escalate] -> land -> done
#
# Roll-forward auto-fix loop (autonomous): the ci-gate eval no longer dead-ends a
# non-format failure straight to a human. It (1) subtracts trunk's pre-existing
# failures (land if zero batch-introduced), (3) surgically re-verifies only the
# failed tests, and (4) — when TRAIN_AUTOFIX=1 — patches the batch FORWARD via an
# AI fix-agent (Claude/Bedrock), landing if green and escalating only when truly
# stuck (and THEN labeling culprits + clearing active_batch so it never loops).
#
# Usage:
#   TRAIN_APPLY=0 scripts/ci/merge-train/train.sh            # shadow / dry-run
#   TRAIN_APPLY=1 scripts/ci/merge-train/train.sh            # live (human-gated)

set -euo pipefail

TRAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "${TRAIN_DIR}/lib.sh"
# Phase 2 (OPTIONAL, off by default): gated Bedrock LLM judgment helpers. Sourced
# before the gated steps so bedrock_* exists; inert unless TRAIN_LLM=1.
# shellcheck source=bedrock-invoke.sh
. "${TRAIN_DIR}/bedrock-invoke.sh"
# shellcheck source=select.sh
. "${TRAIN_DIR}/select.sh"
# shellcheck source=assemble.sh
. "${TRAIN_DIR}/assemble.sh"
# shellcheck source=smart-ci.sh
. "${TRAIN_DIR}/smart-ci.sh"
# shellcheck source=forward-fix.sh
. "${TRAIN_DIR}/forward-fix.sh"
# shellcheck source=classify-timeout.sh
. "${TRAIN_DIR}/classify-timeout.sh"
# shellcheck source=classify-flake.sh
. "${TRAIN_DIR}/classify-flake.sh"
# shellcheck source=surgical.sh
. "${TRAIN_DIR}/surgical.sh"
# shellcheck source=preexisting.sh
. "${TRAIN_DIR}/preexisting.sh"
# shellcheck source=autofix.sh
. "${TRAIN_DIR}/autofix.sh"
# shellcheck source=attribute.sh
. "${TRAIN_DIR}/attribute.sh"
# shellcheck source=land.sh
. "${TRAIN_DIR}/land.sh"
# shellcheck source=state.sh
. "${TRAIN_DIR}/state.sh"
# shellcheck source=resume-retry.sh
. "${TRAIN_DIR}/resume-retry.sh"

train_require git jq gh || { train_err "missing prerequisites"; exit 2; }

# Scratch files for the run.
TRAIN_WORK="$(mktemp -d)"
trap 'rm -rf "${TRAIN_WORK}"' EXIT
export TRAIN_INCLUDED_FILE="${TRAIN_WORK}/included.tsv"
export TRAIN_SKIPPED_FILE="${TRAIN_WORK}/skipped.tsv"
export TRAIN_RUN_ID_FILE="${TRAIN_WORK}/run_id"
# Instrumentation sinks (additive observability — no decision logic reads these).
export TRAIN_TIMINGS_FILE="${TRAIN_WORK}/timings.kv"
export TRAIN_METRICS_KV="${TRAIN_WORK}/metrics.kv"
: >"${TRAIN_TIMINGS_FILE}"; : >"${TRAIN_METRICS_KV}"
# Where the machine-readable metrics doc is written (uploaded as an artifact).
: "${TRAIN_METRICS_OUT:=${TRAIN_REPO_ROOT}/merge-train-metrics.json}"
# Run timestamp: prefer one injected by the workflow; else generate.
: "${TRAIN_RUN_TIMESTAMP:=$(date -u +%Y-%m-%dT%H:%M:%SZ)}"

# _train_mode_label: human label for the run mode.
_train_mode_label() { [[ "${TRAIN_APPLY}" == "1" ]] && echo LIVE || echo DRY-RUN; }

# _emit_metrics <outcome> <trunk_sha> <last_landed> [shard_descriptor]: render the
# machine-readable metrics JSON to TRAIN_METRICS_OUT (best-effort; never fatal).
_emit_metrics() {
  local outcome="$1" trunk_sha="$2" last_landed="$3" shard_desc="${4:-}"
  train_metrics_render "${TRAIN_RUN_TIMESTAMP}" "$(_train_mode_label)" \
    "${trunk_sha}" "${last_landed}" "${outcome}" "${shard_desc}" \
    >"${TRAIN_METRICS_OUT}" 2>/dev/null \
    && train_log "metrics written: ${TRAIN_METRICS_OUT} (outcome=${outcome})" \
    || train_warn "could not write metrics JSON"
}

# train_regenerate_derived_artifacts <batch>:
# Regenerate merge-train sensitive derived assets on the batch branch and commit any
# drift if changes are introduced.
train_regenerate_derived_artifacts() {
  local batch="$1"
  local repo_root="${TRAIN_REPO_ROOT}"
  local feature_catalog="docs/gis/data/feature-catalog.json"
  local geoservices_parity="docs/gis/data/geoservices-rest-parity.json"
  local capability_matrix="docs/gis/data/capability-matrix.v1.json"

  local status

  train_log "regenerating derived artifacts on ${batch} (feature-catalog, geoservices-parity, capability-matrix)"
  if ! bash "${repo_root}/scripts/generate-feature-catalog.sh" 1>&2; then
    train_err "feature-catalog generation failed for ${batch}"
    return 1
  fi
  if ! bash "${repo_root}/scripts/generate-geoservices-parity.sh" 1>&2; then
    train_err "GeoServices parity generation failed for ${batch}"
    return 1
  fi
  if ! python3 "${repo_root}/scripts/ci/generate-capability-matrix.py" 1>&2; then
    train_err "capability-matrix generation failed for ${batch}"
    return 1
  fi

  if ! status="$(git -C "${repo_root}" status --short -- "${feature_catalog}" "${geoservices_parity}" "${capability_matrix}")"; then
    train_err "could not inspect regenerated artifacts for ${batch}"
    return 1
  fi
  if [[ -z "${status}" ]]; then
    train_log "derived artifacts already up to date on ${batch}"
    return 0
  fi

  if ! git -C "${repo_root}" add -- "${feature_catalog}" "${geoservices_parity}" "${capability_matrix}"; then
    train_err "could not stage regenerated artifacts for ${batch}"
    return 1
  fi
  if ! git -C "${repo_root}" commit -m "chore(ci): refresh generated merge-train artifacts" \
    >/dev/null; then
    train_err "could not commit regenerated artifacts for ${batch}"
    return 1
  fi
  train_metric_inc derived_artifact_refreshes
  train_decision "DERIVED ARTIFACTS refreshed on ${batch}"
}

# train_run_batch_ci <batch>:
# Before running CI for a batch branch, refresh deterministic derived artifacts and
# then dispatch + poll smart-CI. If refresh fails, we return FAILURE for a
# fail-closed path.
train_run_batch_ci() {
  local batch="$1"
  if [[ "${TRAIN_APPLY}" == "1" ]]; then
    local regeneration_rc=0
    train_regenerate_derived_artifacts "${batch}" || regeneration_rc=$?
    if [[ "${regeneration_rc}" != "0" ]]; then
      train_err "derived-artifact regeneration failed for ${batch}"
      echo "FAILURE"
      return 0
    fi
  fi

  train_smart_ci_run "${batch}"
}

# Restore the branch and scratch paths after an attribution probe.
_train_restore_attribute_probe_state() {
  local probe_inc="$1" probe_skp="$2" probe_run="$3" anchor_batch="$4"
  local prev_batch="$5" prev_included="$6" prev_skipped="$7" prev_run_id="$8"

  rm -f "${probe_inc}" "${probe_skp}" "${probe_run}"
  git -C "${TRAIN_REPO_ROOT}" checkout -q "${anchor_batch}" || true
  TRAIN_BATCH_BRANCH="${prev_batch}"
  TRAIN_INCLUDED_FILE="${prev_included}"
  TRAIN_SKIPPED_FILE="${prev_skipped}"
  TRAIN_RUN_ID_FILE="${prev_run_id}"
}

# train_reset_rerun_state_for_fresh_run: every newly dispatched Actions run has
# its own timeout retry budget and two-phase request identity. Resume paths do
# not call this helper, so a restart of the same run preserves both.
train_reset_rerun_state_for_fresh_run() {
  timeout_reruns=0
  TRAIN_RERUN_KIND=""
  TRAIN_RERUN_BASE_ATTEMPT=""
  export TRAIN_RERUN_KIND TRAIN_RERUN_BASE_ATTEMPT
  : >"${TRAIN_RUN_ID_FILE}"
}

# A failed fresh dispatch is classifiable only when that dispatch wrote its own
# Actions run id. An empty/non-numeric id can never fall back to a previous run.
train_failure_has_current_run_id() {
  [[ "$1" != "FAILURE" || "$2" =~ ^[0-9]+$ ]]
}

# train_attribute_probe_gate <comma-separated-prs> <trunk-sha7> <anchor-batch>:
# Build just those PRs into a disposable batch branch and run batch CI. Prints the
# resulting gate (FAILURE / SUCCESS / etc.) and preserves the caller's working
# branch + TRAIN_* scratch state.
train_attribute_probe_gate() {
  local suspects_csv="$1" trunk_sha7="$2" anchor_batch="$3"
  local probe_text probe_inc probe_skp probe_run probe_branch gate include_count
  local -a probe_prs
  local -a suspects
  probe_text="$(printf '%s' "${suspects_csv}" | tr ',\r' ' ' | tr '\n' ' ')"
  # shellcheck disable=SC2207
  suspects=( $(printf '%s' "${probe_text}") )
  probe_inc="$(mktemp)"
  probe_skp="$(mktemp)"
  probe_run="$(mktemp)"
  probe_branch="train/attribute-probe/${trunk_sha7}/$(date -u +%s%N)"

  local prev_included="${TRAIN_INCLUDED_FILE}" prev_skipped="${TRAIN_SKIPPED_FILE}"
  local prev_batch="${TRAIN_BATCH_BRANCH:-}"
  local prev_run_id="${TRAIN_RUN_ID_FILE}"
  local pr
  for pr in "${suspects[@]}"; do
    [[ -z "${pr//[[:space:]]/}" ]] && continue
    probe_prs+=("${pr}")
  done
  if [[ "${#probe_prs[@]}" -eq 0 ]]; then
    rm -f "${probe_inc}" "${probe_skp}" "${probe_run}"
    echo "SUCCESS"
    return 0
  fi

  export TRAIN_BATCH_BRANCH="${probe_branch}"
  export TRAIN_INCLUDED_FILE="${probe_inc}"
  export TRAIN_SKIPPED_FILE="${probe_skp}"
  export TRAIN_RUN_ID_FILE="${probe_run}"

  local probe_batch
  if ! probe_batch="$(train_assemble "${trunk_sha7}" "${probe_prs[@]}" )"; then
    _train_restore_attribute_probe_state \
      "${probe_inc}" "${probe_skp}" "${probe_run}" "${anchor_batch}" \
      "${prev_batch}" "${prev_included}" "${prev_skipped}" "${prev_run_id}"
    echo "NO_RUN"
    return 0
  fi
  include_count="$(cut -f1 "${probe_inc}" | sed '/^$/d' | wc -l | tr -d ' ')"
  if [[ "${include_count}" -eq 0 ]]; then
    _train_restore_attribute_probe_state \
      "${probe_inc}" "${probe_skp}" "${probe_run}" "${anchor_batch}" \
      "${prev_batch}" "${prev_included}" "${prev_skipped}" "${prev_run_id}"
    echo "NO_RUN"
    return 0
  fi

  gate="$(train_run_batch_ci "${probe_batch}")"
  _train_restore_attribute_probe_state \
    "${probe_inc}" "${probe_skp}" "${probe_run}" "${anchor_batch}" \
    "${prev_batch}" "${prev_included}" "${prev_skipped}" "${prev_run_id}"
  echo "${gate:-FAILURE}"
}

# train_refine_attribute_candidates <suspect-csv> <trunk-sha7> <anchor-batch>:
# Run bounded bisection probes on a suspect set to avoid escalating the whole batch
# when a single PR can be isolated.
train_refine_attribute_candidates() {
  local suspects_csv="$1" trunk_sha7="$2" anchor_batch="$3"
  local suspect_text
  local -a queue
  suspect_text="$(printf '%s' "${suspects_csv}" | tr ',\r' ' ' | tr '\n' ' ')"
  # shellcheck disable=SC2207
  queue=( $(printf '%s' "${suspect_text}") )
  if [[ "${#queue[@]}" -le 1 ]]; then
    printf '%s\n' "${queue[@]}"
    return 0
  fi

  local max_depth="${TRAIN_ATTRIBUTE_REFINE_MAX_DEPTH:-2}"

  # A fixed, bounded breadth-limited sweep that halves candidate sets while
  # probing evidence.
  local attempt changed left_count gate_left gate_right
  for (( attempt=1; attempt<=max_depth; attempt+=1 )); do
    if [[ "${#queue[@]}" -le 1 ]]; then
      break
    fi

    local -a next=()
    local -a left=() right=()
    local changed=0
    local left_csv="" right_csv=""

    left_count=$(( ${#queue[@]} / 2 ))
    [[ "${left_count}" -lt 1 ]] && left_count=1
    left=( "${queue[@]:0:left_count}" )
    right=( "${queue[@]:left_count}" )

    for p in "${left[@]}"; do left_csv+="${left_csv:+,}${p}"; done
    for p in "${right[@]}"; do right_csv+="${right_csv:+,}${p}"; done

    gate_left="$(train_attribute_probe_gate "${left_csv}" "${trunk_sha7}" "${anchor_batch}")"
    if [[ "${gate_left}" == "FAILURE" ]]; then
      next+=( "${left[@]}" )
      changed=1
    fi
    if [[ -n "${right_csv}" ]]; then
      gate_right="$(train_attribute_probe_gate "${right_csv}" "${trunk_sha7}" "${anchor_batch}")"
      if [[ "${gate_right}" == "FAILURE" ]]; then
        next+=( "${right[@]}" )
        changed=1
      fi
    fi

    [[ "${changed}" == "0" ]] && break
    if [[ "${#next[@]}" -eq 0 ]]; then
      break
    fi

    queue=( "${next[@]}" )
  done

  printf '%s\n' "${queue[@]}" | sort -u
}

# TRAIN_PHASE_RECOVERY: the startup-recovery disposition for EVERY phase the
# persisted-state schema accepts (TRAIN_STATE_PHASES in state.sh). Keeping this
# table TOTAL is what stops the train from persisting a state it cannot recover
# from: `attribute` used to be an accepted phase with no recovery branch, so a
# run that ended mid-attribution (cancellation, or attribute probes that all
# failed on infrastructure) deadlocked every later dispatch repo-wide until the
# machine-managed state issue was hand-edited (#3045, twice).
#
# Classes:
#   post-land  durable land intent. Owned by train_restore_post_land (land.sh),
#              which runs FIRST in main, so terminal recovery must never see
#              one; if it does, that is an invariant violation and it fails
#              closed rather than let selection overwrite a half-landed batch.
#   retry      an in-flight failed-job rerun. Owned by train_restore_retry_intent
#              (resume-retry.sh), which runs AFTER terminal recovery; terminal
#              recovery defers by reporting "no terminal recovery".
#   escalate   terminal, and the batch's CI evidence is unusable rather than
#              merely incomplete: escalate every member (excluding them from
#              future batches), release the landing label, clear the batch.
#   release    terminal, and nothing in the interrupted phase condemns an
#              individual member: members KEEP any escalation already applied,
#              the landing label is released, and the batch is cleared for
#              reassembly. Never adds an unattributed escalation.
declare -A TRAIN_PHASE_RECOVERY=(
  [select]=release
  [assemble]=release
  [smart-ci]=release
  [forward-fix]=release
  [preexisting-filter]=release
  [classify-timeout]=release
  [classify-flake]=release
  [autofix]=release
  [attribute]=release
  [trunk-moved-reassemble]=release
  [requeue]=release
  [done]=release
  [ci-incomplete]=escalate
  [rerun-command-failed]=escalate
  [timeout-retry-rejected]=escalate
  [flake-retry-rejected]=escalate
  [timeout-retry-intent]=retry
  [timeout-retry-requesting]=retry
  [timeout-retry-accepted]=retry
  [flake-retry-intent]=retry
  [flake-retry-requesting]=retry
  [flake-retry-accepted]=retry
  [land]=post-land
  [pre-land-cleanup]=post-land
  [post-land-finalize]=post-land
)

# train_phase_recovery_reason <phase>: the human explanation recorded against
# each released member. Cosmetic only; the disposition comes from the table.
train_phase_recovery_reason() {
  case "$1" in
    timeout-retry-rejected|flake-retry-rejected)
      printf 'Actions definitively rejected the failed-job rerun request; manual CI correction required\n' ;;
    ci-incomplete)
      printf 'Batch CI evidence was incomplete or unusable; fresh explicit validation is required\n' ;;
    rerun-command-failed)
      printf 'Failed-job rerun command failed before safe completion; manual CI correction required\n' ;;
    trunk-moved-reassemble)
      printf 'Trunk moved before FF-CAS landing; release members for fresh reassembly\n' ;;
    attribute)
      printf 'Attribution was interrupted; keeping any escalation already attributed and releasing the batch for reassembly\n' ;;
    *)
      printf 'Controller stopped during %s before the batch was released; releasing members for fresh reassembly\n' "$1" ;;
  esac
}

# train_state_phase_recovery_drift: emit one line per phase whose recovery
# disposition has drifted from the persisted-state schema. Empty output means no
# drift. The fixtures assert emptiness, so a phase cannot be added to
# TRAIN_STATE_PHASES (state.sh) without a TRAIN_PHASE_RECOVERY class, a class
# cannot survive removal of its phase, and no class value can be a typo.
train_state_phase_recovery_drift() {
  local phase
  for phase in "${TRAIN_STATE_PHASES[@]}"; do
    case "${TRAIN_PHASE_RECOVERY[${phase}]:-}" in
      escalate|release|retry|post-land) ;;
      "") printf 'unrecoverable-phase %s\n' "${phase}" ;;
      *)  printf 'unknown-recovery-class %s=%s\n' "${phase}" "${TRAIN_PHASE_RECOVERY[${phase}]}" ;;
    esac
  done
  for phase in "${!TRAIN_PHASE_RECOVERY[@]}"; do
    printf '%s\n' "${TRAIN_STATE_PHASES[@]}" | grep -Fxq -- "${phase}" \
      || printf 'orphan-recovery-class %s\n' "${phase}"
  done
}

# train_recover_terminal_batch: finish known terminal cleanup transactions after
# a controller crashed before releasing the batch. Required label mutations
# precede active-state clear, so any crash remains safely retryable. Dispatch is
# driven by TRAIN_PHASE_RECOVERY, which covers every accepted phase; only state
# the read schema itself rejects can reach the fail-closed default.
# Returns 0=recovered, 1=no terminal recovery, 2=unknown/malformed/cleanup failure.
train_recover_terminal_batch() {
  local state phase class branch trunk included included_count total last body pr reason escalate=1 state_rc=0
  state="$(train_state_read 2>/dev/null)" || state_rc=$?
  [[ "${state_rc}" == "0" ]] || return 2
  [[ -n "${state}" ]] || return 1
  jq -e . >/dev/null 2>&1 <<<"${state}" || return 2
  phase="$(jq -r '.active_batch.phase // empty' <<<"${state}")"
  class="${TRAIN_PHASE_RECOVERY[${phase}]:-}"
  branch="$(jq -r '.active_batch.branch // empty' <<<"${state}")"
  included_count="$(jq -r '(.active_batch.included // []) | length' <<<"${state}" 2>/dev/null)" || return 2
  if [[ -z "${branch}" && "${included_count}" == "0" ]]; then
    # No branch and no members: there is nothing durable to release, and the
    # phase produced zero label/state mutations to undo (e.g. the pre-assembly
    # "assemble" write, or "every selected PR conflicted"). Selection may safely
    # overwrite. The one exception is a durable land intent, which land.sh owns
    # and which must never be discarded here.
    [[ "${class}" == "post-land" ]] && return 2
    [[ -n "${class}" || -z "${phase}" ]] && return 1
    return 2
  fi
  case "${class}" in
    retry)     return 1 ;;
    post-land) return 2 ;;
    escalate)  escalate=1 ;;
    release)   escalate=0 ;;
    *)         return 2 ;;
  esac
  reason="$(train_phase_recovery_reason "${phase}")"
  # An empty branch is legitimate here: the attribution rebuild persists
  # "assemble" with the surviving members but no branch yet, so a crash in that
  # window leaves members holding train:landing with nothing assembled. Any
  # other branch value is corrupt state and still fails closed.
  jq -e '.active_batch.branch | type == "string" and (length == 0 or startswith("train/batch/"))' >/dev/null <<<"${state}" || return 2
  jq -e '.active_batch.trunk_base | type == "string" and test("^[0-9a-fA-F]{40}$")' >/dev/null <<<"${state}" || return 2
  jq -e '.active_batch.run_id == null or
    ((.active_batch.run_id | type) == "number" and (.active_batch.run_id | floor) == .active_batch.run_id and .active_batch.run_id > 0)' >/dev/null <<<"${state}" || return 2
  jq -e '.active_batch.included | type == "array"
    and all(.[]; type == "number" and floor == .) and (unique | length) == length' >/dev/null <<<"${state}" || return 2
  jq -e '(.active_batch.timeout_reruns_total // 0) as $t
    | ($t | type) == "number" and $t >= 0 and ($t | floor) == $t' >/dev/null <<<"${state}" || return 2
  jq -e '.last_landed_trunk == null or
    ((.last_landed_trunk | type) == "string" and (.last_landed_trunk | test("^[0-9a-fA-F]{40}$")))' >/dev/null <<<"${state}" || return 2

  trunk="$(jq -r '.active_batch.trunk_base' <<<"${state}")"
  included="$(jq -r '.active_batch.included | map(tostring) | join(",")' <<<"${state}")"
  total="$(jq -r '.active_batch.timeout_reruns_total // 0' <<<"${state}")"
  last="$(jq -r '.last_landed_trunk // "null"' <<<"${state}")"
  body="$(mktemp)"
  train_state_render "" "${trunk}" "" select "" 0 0 "${last}" \
    '[]' '' 0 "" null "${total}" >"${body}" || { rm -f "${body}"; return 2; }
  for pr in $(tr ',' ' ' <<<"${included}"); do
    if [[ "${escalate}" == "1" ]]; then
      train_side_effect gh pr edit "${pr}" --add-label "${TRAIN_LABEL_ESCALATED}" || { rm -f "${body}"; return 2; }
    fi
    train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}" || { rm -f "${body}"; return 2; }
    train_decision "TERMINAL RECOVERY #${pr}: ${reason}"
  done

  train_state_write "${body}" || { rm -f "${body}"; return 2; }
  rm -f "${body}"
  train_notice "completed terminal ${phase} cleanup for batch members ${included}"
}

# train_reset_active_batch: the sanctioned operator escape hatch, reached only
# via `gh workflow run merge-train.yml -f train_apply=true -f reset_state=true`.
# It clears the active batch to EXACTLY the shape train_state_render emits for
# no batch (branch "", included [], phase "select", null run/batch fields),
# preserving config and last_landed_trunk, and releases the landing label from
# every recorded member. It exists so a stuck train is never repaired by
# hand-editing the machine-managed state issue — the workaround used twice for
# #3045, once with an `active_batch: null` body that the read schema rejects,
# which turned a recovery deadlock into a "durable state lookup failed" one.
# Refuses while durable land intent is outstanding: land.sh must reconcile a
# half-landed batch against trunk, and discarding that record could lose the
# record of what already landed. Returns 0=reset, 2=refused or failed.
train_reset_active_batch() {
  local state phase trunk included last total body pr state_rc=0
  state="$(train_state_read 2>/dev/null)" || state_rc=$?
  [[ "${state_rc}" == "0" ]] || return 2
  if [[ -z "${state}" ]]; then
    train_notice "state reset requested but no state issue exists; nothing to clear"
    return 0
  fi
  phase="$(jq -r '.active_batch.phase // empty' <<<"${state}")"
  if [[ "${TRAIN_PHASE_RECOVERY[${phase}]:-}" == "post-land" ]]; then
    train_err "refusing to reset state in phase ${phase}: durable land intent must reconcile against trunk first"
    return 2
  fi
  trunk="$(jq -r '.active_batch.trunk_base // empty' <<<"${state}")"
  included="$(jq -r '(.active_batch.included // []) | map(tostring) | join(",")' <<<"${state}")"
  last="$(jq -r '.last_landed_trunk // "null"' <<<"${state}")"
  total="$(jq -r '.active_batch.timeout_reruns_total // 0' <<<"${state}")"
  [[ "${total}" =~ ^[0-9]+$ ]] || total=0
  body="$(mktemp)"
  train_state_render "" "${trunk}" "" select "" 0 0 "${last}" \
    '[]' '' 0 "" null "${total}" >"${body}" || { rm -f "${body}"; return 2; }
  for pr in $(tr ',' ' ' <<<"${included}"); do
    train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}" \
      || { rm -f "${body}"; return 2; }
    train_decision "STATE RESET #${pr}: released by an operator-requested merge-train state reset"
  done
  train_state_write "${body}" || { rm -f "${body}"; return 2; }
  rm -f "${body}"
  train_notice "operator state reset cleared the active batch (phase=${phase:-none}, members=${included:-none})"
}

main() {
  train_init_controller_deadline || { train_err "invalid controller polling budget"; return 2; }
  train_log "mode: $(_train_mode_label) MAX_BATCH=${MAX_BATCH} run=${TRAIN_RUN_TIMESTAMP}"

  local resume_state="" resume_rc=1 resumed=0 rejected_rc=1
  if [[ "${TRAIN_APPLY}" == "1" ]]; then
    # Operator escape hatch. Deliberately terminal: a reset is one auditable
    # action, and the operator dispatches an ordinary live run afterwards.
    if [[ "${TRAIN_RESET_STATE:-0}" == "1" ]]; then
      train_reset_active_batch || {
        train_err "operator-requested merge-train state reset was refused or failed; state left untouched"
        return 1
      }
      return 0
    fi

    local post_land_rc=1
    if train_restore_post_land; then post_land_rc=0; else post_land_rc=$?; fi
    if [[ "${post_land_rc}" == "4" ]]; then
      _write_state "" "${TRAIN_POST_LAND_OBSERVED_TRUNK}" "" "select" "" 0 0 null
      train_notice "completed durable pre-land cleanup; returning to selection without recording a landing"
      return 0
    elif [[ "${post_land_rc}" == "0" || "${post_land_rc}" == "3" ]]; then
      local recovered_phase=done
      if [[ "${post_land_rc}" == "3" ]]; then
        recovered_phase="${TRAIN_POST_LAND_RECOVERY_PHASE:-post-land-finalize}"
        _write_state "${TRAIN_POST_LAND_BATCH}" "${TRAIN_POST_LAND_TRUNK_BASE}" "${TRAIN_POST_LAND_INCLUDED}" "${recovered_phase}" "" 0 0 "${TRAIN_POST_LAND_BATCH_SHA}"
      else
        _write_state "" "${TRAIN_POST_LAND_OBSERVED_TRUNK}" "" "done" "" 0 0 "${TRAIN_POST_LAND_BATCH_SHA}"
      fi
      train_notice "reconciled durable post-land state before selection (phase=${recovered_phase})"
      return 0
    elif [[ "${post_land_rc}" == "2" ]]; then
      train_err "durable post-land state mismatches trunk or batch; failing closed"
      return 1
    elif [[ "${post_land_rc}" == "5" ]]; then
      train_err "durable state lookup failed; refusing selection or state overwrite"
      return 1
    fi

    train_recover_terminal_batch || rejected_rc=$?
    if [[ "${rejected_rc}" == "2" ]]; then
      train_err "active merge-train state is unknown, malformed, or incompletely recovered; failing closed before selection"
      return 1
    fi
    if resume_state="$(train_restore_retry_intent)"; then resume_rc=0; else resume_rc=$?; fi
    if [[ "${resume_rc}" == "0" ]]; then
      resumed=1
      train_notice "restoring interrupted failed-job rerun before selection; no new batch or rerun will be dispatched"
    elif [[ "${resume_rc}" == "2" ]]; then
      train_err "persisted retry intent does not match its Actions run/batch; failing closed"
      return 1
    elif [[ "${resume_rc}" == "3" ]]; then
      train_warn "accepted failed-job rerun is still pending at the controller deadline; preserving retry intent for the next controller"
      return 1
    elif [[ "${resume_rc}" == "4" ]]; then
      train_warn "failed-job rerun remains in recoverable requesting state; preserving it for the next controller"
      return 1
    fi
  fi

  # Offline fixture seam: proves startup selects the production resume path
  # before selection/assembly without executing the remainder of a live land.
  [[ "${resumed}" == "1" && "${TRAIN_RESUME_STARTUP_TEST_ONLY:-0}" == "1" ]] && return 0

  if [[ "${resumed}" != "1" ]]; then

  # --- select ----------------------------------------------------------------
  train_group select "pick ready PRs (oldest-first, capped at ${MAX_BATCH})"
  train_step_begin select
  local selected
  selected="$(train_select | jq -s '.')"
  local prs
  prs="$(jq -r '.[].number' <<<"${selected}")"
  local n_selected; n_selected="$(jq 'length' <<<"${selected}")"
  train_metric_set selected "${n_selected}"
  # candidates = total open PRs the selector evaluated (selected ones surface in
  # the batch; the rest were skipped at select for draft/hold/conflict/CI-gate).
  # TRAIN_CANDIDATE_COUNT lets the caller inject the raw count; else fall back to
  # the selected count so the metric is never larger than reality.
  train_metric_set candidates "${TRAIN_CANDIDATE_COUNT:-${n_selected}}"
  train_metric_set skipped_select "$(( ${TRAIN_CANDIDATE_COUNT:-${n_selected}} - n_selected ))"
  train_step_end select >/dev/null
  if [[ -z "${prs}" ]]; then
    train_decision "no ready PRs; nothing to do"
    train_endgroup
    _emit_metrics "nothing-ready" "" "" ""
    _dashboard "" "${selected}" "" "" "no ready PRs — nothing to select"
    return 0
  fi
  train_decision "candidate batch (oldest-first, capped): $(tr '\n' ' ' <<<"${prs}")"
  train_endgroup

  # --- trunk base ------------------------------------------------------------
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  local trunk_sha trunk_sha7
  trunk_sha="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  trunk_sha7="${trunk_sha:0:7}"
  train_log "trunk base: ${trunk_sha} (${trunk_sha7})"

  # --- assemble (transactional detect-and-skip) ------------------------------
  train_group assemble "merge PR heads onto a fresh branch off ${TRAIN_BASE_BRANCH}"
  train_step_begin assemble
  # state: write BEFORE assembling (resumable at assemble phase).
  _write_state "" "${trunk_sha}" "" "assemble" "" 0 0
  local batch
  # shellcheck disable=SC2086
  batch="$(train_assemble "${trunk_sha7}" ${prs})"
  train_log "batch branch: ${batch}"

  local included skipped
  included="$(cut -f1 "${TRAIN_INCLUDED_FILE}" | tr '\n' ',' | sed 's/,$//')"
  skipped="$(cut -f1 "${TRAIN_SKIPPED_FILE}" | tr '\n' ' ')"
  train_metric_set included "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
  train_metric_set skipped_conflict "$(grep -c . "${TRAIN_SKIPPED_FILE}" 2>/dev/null || echo 0)"
  for pr in $(tr ',' ' ' <<<"${included}"); do
    [[ -n "${pr}" ]] && train_decision "INCLUDE #${pr} (assembled clean)"
  done
  for pr in ${skipped}; do
    [[ -n "${pr}" ]] && train_decision "SKIP #${pr} (conflict; aborted clean)"
  done
  train_step_end assemble >/dev/null
  train_endgroup

  if [[ -z "${included}" ]]; then
    train_annotate_warn "no PRs assembled cleanly; nothing to land"
    _write_state "" "${trunk_sha}" "" "select" "" 0 0
    _emit_metrics "nothing-ready" "${trunk_sha}" "" ""
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "" "all candidates skipped on conflict — nothing to land"
    return 0
  fi

  # state: landing labels + phase smart-ci before CI side effects.
  _write_state "${batch}" "${trunk_sha}" "${included}" "smart-ci" "" 0 0
  local pr
  for pr in $(tr ',' ' ' <<<"${included}"); do
    train_side_effect gh pr edit "${pr}" --add-label "${TRAIN_LABEL_LANDING}"
  done

  # --- smart-ci ---------------------------------------------------------------
  # PR Gate and Review Gate admit a PR, but are not integration evidence.
  # Every assembled combination runs batch CI; only its CI Gate can authorize
  # landing the exact batch bytes.
  train_group smart-ci "compute shard subset for the batch's cumulative diff"
  train_step_begin smart-ci
  local shard_descriptor gate fwdfix=0 flake_reruns=0 timeout_reruns=0 timeout_reruns_total=0
  train_metric_set direct_merge 0
  shard_descriptor="$(train_smart_ci_shards "${batch}")"
  train_metric_set smartci_shard_count "$(jq -r '(.shards // []) | length' <<<"${shard_descriptor}" 2>/dev/null || echo 0)"
  train_decision "smart-CI shard subset: ${shard_descriptor}"
  train_reset_rerun_state_for_fresh_run
  _write_state "${batch}" "${trunk_sha}" "${included}" "smart-ci" "" "${fwdfix}" "${flake_reruns}"
  gate="$(train_run_batch_ci "${batch}")"
  train_step_end smart-ci >/dev/null
  train_endgroup

  else
    # Restore the existing batch directly into the common CI-gate loop. The
    # startup helper already waited for attempt > baseline and validated the
    # Actions run, batch branch, and trunk CAS base.
    local batch trunk_sha trunk_sha7 included selected skipped="" shard_descriptor gate fwdfix flake_reruns timeout_reruns timeout_reruns_total
    batch="$(jq -r '.active_batch.branch' <<<"${resume_state}")"
    trunk_sha="$(jq -r '.active_batch.trunk_base' <<<"${resume_state}")"
    trunk_sha7="${trunk_sha:0:7}"
    jq -r '.active_batch.run_id' <<<"${resume_state}" >"${TRAIN_RUN_ID_FILE}"
    included="$(jq -r '.active_batch.included | map(tostring) | join(",")' <<<"${resume_state}")"
    fwdfix="$(jq -r '.active_batch.fwdfix_attempts // 0' <<<"${resume_state}")"
    flake_reruns="$(jq -r '.active_batch.flake_reruns // 0' <<<"${resume_state}")"
    timeout_reruns="$(jq -r '.active_batch.timeout_reruns // 0' <<<"${resume_state}")"
    timeout_reruns_total="$(jq -r '.active_batch.timeout_reruns_total // .active_batch.timeout_reruns // 0' <<<"${resume_state}")"
    gate="$(jq -r '.resume_gate' <<<"${resume_state}")"
    shard_descriptor="$(jq -c '.resume_shard_descriptor' <<<"${resume_state}")"
    selected="$(jq -c '.resume_selected' <<<"${resume_state}")"
    [[ -s "${TRAIN_INCLUDED_FILE}" \
      && "$(jq 'length' <<<"${selected}")" == "$(tr ',' '\n' <<<"${included}" | sed '/^$/d' | wc -l | tr -d ' ')" ]] || {
      train_err "resumed batch member snapshot is incomplete; failing closed"
      return 1
    }
    train_metric_set included "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
    train_metric_set flake_reruns "${flake_reruns}"
    train_metric_set timeout_reruns "${timeout_reruns_total}"
  fi

  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_log "dry-run: stopping before CI gate evaluation/land (no pushes happened)"
    _emit_metrics "dry-run" "${trunk_sha}" "" "${shard_descriptor}"
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
      "dry-run: stopped before CI gate/land (no pushes happened)"
    return 0
  fi

  # Live-mode gate handling. Roll-forward pipeline (each step independently
  # valuable, evaluated in this order):
  #   forward-fix(format) -> PRE-EXISTING FILTER -> classify-timeout -> classify-flake ->
  #   [AUTOFIX (Bedrock fix-agent + surgical re-verify)] -> attribute/escalate.
  train_group ci-gate "evaluate CI Gate; forward-fix / pre-existing filter / flake / autofix / attribute"
  train_step_begin ci-gate
  local autofix_attempts=0
  while [[ "${gate}" != "SUCCESS" ]]; do
    local run_id; run_id="$(cat "${TRAIN_RUN_ID_FILE}" 2>/dev/null || echo "")"
    if ! train_failure_has_current_run_id "${gate}" "${run_id}"; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "ci-incomplete" "" "${fwdfix}" "${flake_reruns}"
      train_annotate_warn "fresh batch CI failed before publishing a new run id; refusing stale-run classification"
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "ci-incomplete" "${trunk_sha}" "" "${shard_descriptor}"
      return 1
    fi

    # Only an ordinary FAILURE has actionable failed jobs. A cancelled,
    # missing, timed-out, stale, neutral, or otherwise incomplete gate must
    # never flow into failure subtraction/classification: doing so can turn an
    # empty failure list into a false success and land unvalidated code.
    if [[ "${gate}" != "FAILURE" ]] \
      || ! train_ci_jobs_are_terminal "${run_id}" \
      || ! train_expected_shards_are_classifiable "${run_id}" "${shard_descriptor}"; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "ci-incomplete" "${run_id}" "${fwdfix}" "${flake_reruns}"
      train_annotate_warn "CI Gate/run is ${gate} with incomplete or unusable jobs; failing closed without landing or dropping PRs"
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "ci-incomplete" "${trunk_sha}" "" "${shard_descriptor}"
      _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
        "STOPPED: CI Gate/run ${gate} is incomplete or unusable; explicit successful validation required"
      return 1
    fi

    local failing; failing="$(train_failing_jobs "${run_id}")"

    # --- (0) NON-BLOCKING aux/aggregator filter (deterministic) --------------
    # Strip the heavy aux jobs that run on every batch + flake on environment
    # (Docker/JS/Python/Esri integration) and the CI Gate / Test Suite Summary
    # aggregators. The train lands on its SHARD results; real regressions in the
    # aux jobs are caught by post-merge trunk CI. If nothing real-fails after
    # stripping, the batch is green-enough → land.
    local nonblocking_only=0
    train_nonblocking_failures_are_safe "${run_id}" "${shard_descriptor}" && nonblocking_only=1
    failing="$(train_subtract_lines "${TRAIN_NONBLOCKING_JOBS}" "${failing}")"
    if [[ -z "${failing//[${HONUA_NL}${HONUA_TAB} ]/}" ]]; then
      if [[ "${nonblocking_only}" == "1" ]]; then
        train_metric_set nonblocking_passes 1
        train_notice "only non-blocking aux/aggregator jobs failed and every selected shard explicitly succeeded; landing on shard results"
        gate="SUCCESS"; continue
      fi

      _write_state "${batch}" "${trunk_sha}" "${included}" "ci-incomplete" "${run_id}" "${fwdfix}" "${flake_reruns}"
      train_annotate_warn "CI has no blocking failures to classify but selected-shard evidence is missing or skipped; failing closed"
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "ci-incomplete" "${trunk_sha}" "" "${shard_descriptor}"
      _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
        "STOPPED: selected-shard evidence missing or skipped; explicit successful validation required"
      return 1
    fi

    # forward-fix: only when the SOLE failure is the format-verify step.
    if train_is_format_only_failure "${failing}"; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "forward-fix" "${run_id}" "${fwdfix}" "${flake_reruns}"
      if train_forward_fix "${batch}" "${fwdfix}"; then
        fwdfix=$((fwdfix + 1))
        train_metric_set forward_fixes "${fwdfix}"
        train_decision "forward-fix #${fwdfix} applied (dotnet format); re-running CI"
        train_reset_rerun_state_for_fresh_run
        _write_state "${batch}" "${trunk_sha}" "${included}" "smart-ci" "" "${fwdfix}" "${flake_reruns}"
        gate="$(train_run_batch_ci "${batch}")"
        continue
      fi
    fi

    # --- (1) PRE-EXISTING-FAILURE FILTER (deterministic, no AI) --------------
    # Subtract trunk's latest-CI failing jobs from the batch's failing jobs. If
    # ZERO batch-introduced failures remain, the batch is red ONLY because trunk
    # is already red (e.g. a STAC api-validator conformance test) — treat the
    # batch as PASS and land. Otherwise narrow the working set to the
    # batch-introduced failures for flake/autofix/attribute.
    _write_state "${batch}" "${trunk_sha}" "${included}" "preexisting-filter" "${run_id}" "${fwdfix}" "${flake_reruns}"
    local introduced rc_pe=0
    introduced="$(train_preexisting_filter "${run_id}" "${failing}")" || rc_pe=$?
    if [[ "${rc_pe}" == "11" ]]; then
      train_metric_set preexisting_passes 1
      train_notice "pre-existing filter: all batch failures are pre-existing on trunk; landing"
      gate="SUCCESS"
      continue
    fi
    # From here on, evaluate only the BATCH-INTRODUCED failing jobs.
    failing="${introduced}"

    # --- timeout retry (on batch-introduced failures) ------------------------
    # Timeout/exit-124 failures get one failed-job-only rerun. A second timeout
    # is real and deliberately bypasses known-flake merge-through.
    _write_state "${batch}" "${trunk_sha}" "${included}" "classify-timeout" "${run_id}" "${fwdfix}" "${flake_reruns}"
    local rc_retry=0
    train_classify_retry_candidate "${run_id}" "${timeout_reruns}" "${flake_reruns}" "${failing}" _persist_retry_intent || rc_retry=$?
    if [[ "${rc_retry}" == "3" ]]; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "rerun-command-failed" "${run_id}" "${fwdfix}" "${flake_reruns}"
      train_annotate_warn "failed-job rerun command failed; stopping without landing or attribution"
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "ci-rerun-failed" "${trunk_sha}" "" "${shard_descriptor}"
      return 1
    fi
    if [[ "${rc_retry}" == "4" ]]; then
      train_annotate_warn "failed-job rerun request remains in recoverable requesting state; stopping without overwriting it"
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "ci-rerun-requesting" "${trunk_sha}" "" "${shard_descriptor}"
      return 1
    fi
    if [[ "${rc_retry}" == "6" ]]; then
      train_annotate_warn "Actions rejected the rerun but terminal state persistence failed; stopping without cleanup or state overwrite"
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "ci-rerun-rejection-persist-failed" "${trunk_sha}" "" "${shard_descriptor}"
      return 1
    fi
    if [[ "${rc_retry}" == "5" ]]; then
      train_annotate_warn "Actions definitively rejected the failed-job rerun; escalating this batch and clearing it so the queue can progress"
      train_metric_inc escalated "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
      train_escalate_batch "${included}" "Actions definitively rejected the failed-job rerun request; manual CI correction required"
      _write_state "" "${trunk_sha}" "" "select" "" 0 0
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "ci-rerun-rejected" "${trunk_sha}" "" "${shard_descriptor}"
      return 0
    fi
    if [[ "${rc_retry}" == "0" ]]; then
      if [[ "${TRAIN_RETRY_KIND}" == "timeout" ]]; then
        train_decision "timeout/exit-124 signature matched; failed-job retry #${timeout_reruns}"
      else
        train_decision "flake signature matched; rerun #${flake_reruns} of failed jobs"
      fi
      # The same run id still exposes the old completed attempt briefly. Accept
      # retry evidence only after its attempt strictly increases and completes.
      if train_wait_for_new_run_attempt "${run_id}" "${TRAIN_RERUN_BASE_ATTEMPT}"; then
        gate="$(gh run view "${run_id}" --json jobs \
          --jq '[.jobs[] | select(.name=="CI Gate")][0].conclusion // "missing"' | tr '[:lower:]' '[:upper:]')"
      else
        # The rerun command was accepted and its intent is already durable.
        # Never replace that resumable state with ci-incomplete merely because
        # this controller's shared deadline expired while the attempt remained
        # queued/running; the next controller must consume the same attempt.
        train_annotate_warn "accepted failed-job rerun remains pending at the controller deadline; preserving retry intent for restart"
        train_step_end ci-gate >/dev/null; train_endgroup
        _emit_metrics "ci-rerun-pending" "${trunk_sha}" "" "${shard_descriptor}"
        _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
          "STOPPED: accepted failed-job rerun still pending; retry intent preserved for next controller"
        return 1
      fi
      continue
    elif [[ "${rc_retry}" == "2" ]]; then
      # Recognized flake persisted across the rerun => consistent environmental
      # failure (e.g. the schema-setup race). Merge through: land the batch.
      train_metric_set flake_mergethrough 1
      train_notice "recognized environmental flake persisted across rerun; MERGING THROUGH — landing the batch (optimistic model)"
      gate="SUCCESS"; continue
    fi
    # rc_retry == 1 => a real failure, including any persistent timeout.

    # --- (4) ROLL-FORWARD AI FIX-AGENT (capstone; gated TRAIN_AUTOFIX) -------
    # A REAL, batch-introduced, non-flake failure. With autofix enabled, ask the
    # Bedrock fix-agent to patch the batch branch forward, then SURGICALLY
    # re-verify only the failed tests. On green, re-run smart-CI and continue;
    # if still failing after the cap, fall through to escalate.
    if autofix_enabled; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "autofix" "${run_id}" "${fwdfix}" "${flake_reruns}"
      local fqns errout
      fqns="$(train_failed_test_names "${run_id}")"
      # Per-FAILING-JOB logs, not `--log-failed` (which is EMPTY on a run_all batch
      # CI — the #2060 bug). Without this the autofix had no error context and fixed
      # blind, so its commits never resolved the failure (esp. non-shard jobs like
      # Build & Format / CI Router Validation that have no test FQNs at all).
      errout="$(train_failing_job_logs "${run_id}")"
      if train_autofix_attempt "${batch}" "${failing}" "${fqns}" "${errout}" "${autofix_attempts}"; then
        autofix_attempts=$((autofix_attempts + 1))
        train_metric_set autofix_attempts "${autofix_attempts}"
        # Surgically re-verify ONLY the failed tests (never a full shard rerun).
        # On PASS: LAND the batch immediately — the failed tests now pass and the
        # rest of the batch was already green in the first run, so re-running the
        # full smart-CI is exactly the waste we're avoiding. If the fix
        # incidentally broke a previously-passing test, the optimistic model
        # catches it on trunk's next batch (accept-some-failure for throughput).
        local verification_rc=0
        train_autofix_verification_action "${run_id}" "${fqns}" || verification_rc=$?
        case "${verification_rc}" in
          0)
            train_metric_set autofix_fixes "$(( $(train_metric_get autofix_fixes 0) + 1 ))"
            train_notice "autofix verified by surgical rerun of the failed tests; landing the batch (no full re-run)"
            gate="SUCCESS"
            continue
            ;;
          1)
            # Fix didn't hold: loop back to retry autofix against the SAME failed
            # tests (gate is still non-SUCCESS) up to the cap, then escalate — still
            # NO wasteful full re-run. The surgical rerun is the only re-check.
            train_warn "autofix commit did not pass surgical re-verify; retrying autofix (no full re-run) up to the cap"
            continue
            ;;
          2)
            train_warn "autofix produced a commit but no failed test names were available for surgical re-verify; escalating"
            ;;
          *)
            train_warn "autofix surgical verification returned an unexpected status; escalating"
            ;;
        esac
      fi
      # autofix declined / produced no commit / cap reached => escalate below.
      train_warn "autofix did not produce a landable fix; escalating as genuinely-hard"
    fi

    # --- (2) attribute + escalate (real failure, not autofixed) -------------
    _write_state "${batch}" "${trunk_sha}" "${included}" "attribute" "${run_id}" "${fwdfix}" "${flake_reruns}"
    local culprits; culprits="$(train_attribute "${failing}" "${TRAIN_INCLUDED_FILE}")"
    if [[ "${culprits}" != "ESCALATE_BATCH" && "${TRAIN_APPLY}" == "1" ]]; then
      local culprit_count
      culprit_count="$(printf '%s\n' "${culprits}" | sed '/^$/d' | wc -l | tr -d ' ')"
      if [[ "${culprit_count}" -gt 1 ]]; then
        local narrowed
        narrowed="$(train_refine_attribute_candidates "${culprits}" "${trunk_sha7}" "${batch}")"
        if [[ -n "${narrowed}" ]]; then
          local narrowed_count
          narrowed_count="$(printf '%s\n' "${narrowed}" | sed '/^$/d' | wc -l | tr -d ' ')"
          if [[ "${narrowed_count}" -lt "${culprit_count}" ]]; then
            train_notice "attribute isolation reduced suspects from ${culprit_count} to ${narrowed_count}; continuing with narrowed set"
            culprits="$(printf '%s' "${narrowed}" | tr '\n' ' ')"
          else
            train_warn "attribute isolation did not reduce suspects"
          fi
        fi
      fi
    fi
    if [[ "${culprits}" == "ESCALATE_BATCH" ]]; then
      train_metric_inc escalated "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
      train_annotate_warn "escalating entire batch; manual triage required"
      # LOOP-BUG FIX: label EVERY member train:escalated (so select excludes them)
      # AND clear active_batch from the state issue, or the next run re-selects
      # the same doomed batch forever.
      train_escalate_batch "${included}" "CI failure not attributable to a single member diff (and not autofixable)"
      _write_state "" "${trunk_sha}" "" "select" "" 0 0
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "escalated-batch" "${trunk_sha}" "" "${shard_descriptor}"
      _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
        "ESCALATED whole batch: CI failure not attributable to a member diff"
      return 0
    fi
    # Drop culprits, rebuild minus them, re-CI.
    local remaining="${included}"
    for pr in ${culprits}; do
      train_drop_pr "${pr}" "real CI failure attributed to its diff"
      train_metric_inc attribution_drops
      train_metric_inc escalated
      train_notice "DROP #${pr}: real CI failure attributed to its diff; rebuilding batch without it"
      remaining="$(tr ',' '\n' <<<"${remaining}" | grep -vx "${pr}" | tr '\n' ',' | sed 's/,$//')"
    done
    if [[ -z "${remaining}" ]]; then
      train_annotate_warn "all members dropped; batch empty"
      # Clear active_batch so the next run starts a fresh selection (the dropped
      # PRs already carry train:escalated and are excluded by select).
      _write_state "" "${trunk_sha}" "" "select" "" 0 0
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "all-dropped" "${trunk_sha}" "" "${shard_descriptor}"
      _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
        "all batch members dropped on attribution — batch empty"
      return 0
    fi
    included="${remaining}"
    train_reset_rerun_state_for_fresh_run
    _write_state "" "${trunk_sha}" "${included}" "assemble" "" "${fwdfix}" "${flake_reruns}"
    # shellcheck disable=SC2086
    batch="$(train_assemble "${trunk_sha7}" $(tr ',' ' ' <<<"${included}"))"
    included="$(cut -f1 "${TRAIN_INCLUDED_FILE}" | tr '\n' ',' | sed 's/,$//')"
    _write_state "${batch}" "${trunk_sha}" "${included}" "smart-ci" "" "${fwdfix}" "${flake_reruns}"
    gate="$(train_run_batch_ci "${batch}")"
  done
  train_step_end ci-gate >/dev/null
  train_endgroup

  # --- land (FF-only CAS) ----------------------------------------------------
  train_group land "fast-forward-only CAS push of the CI-green batch onto ${TRAIN_BASE_BRANCH}"
  train_step_begin land
  _write_state "${batch}" "${trunk_sha}" "${included}" "land" "$(cat "${TRAIN_RUN_ID_FILE}" 2>/dev/null)" "${fwdfix}" "${flake_reruns}"
  local rc=0
  train_land "${batch}" "${trunk_sha}" "${TRAIN_INCLUDED_FILE}" || rc=$?
  if [[ "${rc}" == "10" ]]; then
    if [[ -s "${TRAIN_LAND_PENDING_FILE:-}" ]]; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "pre-land-cleanup" "" "${fwdfix}" "${flake_reruns}"
    else
      _write_state "" "${trunk_sha}" "" "select" "" 0 0
    fi
    train_annotate_warn "land deferred: trunk moved; the next scheduled run re-assembles"
    # train_land's rc=10 is returned strictly BEFORE any push/merge (CAS
    # precondition miss, or a rejected FF push) — zero side effects have
    # happened yet, so it's safe to persist the recoverable phase immediately
    # using the already-known (stale) trunk_sha. Do this BEFORE the refetch
    # below: that fetch/rev-parse is only for an accurate informational trunk
    # value and is itself fallible, and if it fails or the controller is
    # cancelled here, state must already be at the recoverable
    # "trunk-moved-reassemble" phase rather than stuck at "land" (which fails
    # closed on restart).
    _write_state "${batch}" "${trunk_sha}" "${included}" "trunk-moved-reassemble" "$(cat "${TRAIN_RUN_ID_FILE}" 2>/dev/null)" "${fwdfix}" "${flake_reruns}"
    local moved_trunk="${trunk_sha}"
    if git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"; then
      moved_trunk="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}" 2>/dev/null || echo "${trunk_sha}")"
    fi
    for pr in $(tr ',' ' ' <<<"${included}"); do
      train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
    done
    _write_state "" "${moved_trunk}" "" "select" "" 0 0
    train_step_end land >/dev/null; train_endgroup
    _emit_metrics "trunk-moved-reassemble" "${trunk_sha}" "" "${shard_descriptor}"
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
      "land deferred: trunk moved (FF-CAS) — next run re-assembles"
    return 0
  fi
  if [[ "${rc}" == "11" ]]; then
    train_annotate_warn "land outcome ambiguous; retaining durable phase=land for restart reconciliation"
    train_step_end land >/dev/null; train_endgroup
    _emit_metrics "land-ambiguous" "${trunk_sha}" "" "${shard_descriptor}"
    return 1
  fi
  if [[ "${rc}" != "0" ]]; then
    train_err "land failed (rc=${rc})"
    train_step_end land >/dev/null; train_endgroup
    _emit_metrics "land-error" "${trunk_sha}" "" "${shard_descriptor}"
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
      "land FAILED (rc=${rc})"
    return "${rc}"
  fi

  local new_trunk; new_trunk="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  train_metric_set snapshot_landed "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
  train_metric_set landed "$(grep -c . "${TRAIN_LAND_FINALIZED_FILE}" 2>/dev/null || echo 0)"
  train_metric_set advanced_after_snapshot "$(grep -c . "${TRAIN_LAND_ADVANCED_FILE}" 2>/dev/null || echo 0)"
  train_metric_set finalization_pending "$(grep -c . "${TRAIN_LAND_PENDING_FILE}" 2>/dev/null || echo 0)"
  if [[ -s "${TRAIN_LAND_PENDING_FILE}" ]]; then
    _write_state "${batch}" "${trunk_sha}" "${included}" "post-land-finalize" "" 0 0 "${new_trunk}"
  else
    _write_state "" "${new_trunk}" "" "done" "" 0 0 "${batch_landed:-${new_trunk}}"
  fi
  train_notice "LANDED batch ${batch} ($(tr ',' ' ' <<<"${included}")); trunk now ${new_trunk:0:7}"
  train_step_end land >/dev/null
  train_endgroup
  _emit_metrics "landed" "${new_trunk}" "${new_trunk}" "${shard_descriptor}"
  _dashboard "${batch}" "${selected}" "${new_trunk}" "${shard_descriptor}" \
    "LANDED ${included} via FF-CAS; trunk now ${new_trunk:0:7}"
  # Persistent over-time dashboard (state issue): aggregate after a successful land.
  train_aggregate_update "${new_trunk}" "${new_trunk}"
}

# _write_state branch trunk_base included-csv phase run_id fwdfix flake [last_landed]
_write_state() {
  local body="${TRAIN_WORK}/state.md" heads='[]' batch_sha=""
  if [[ -n "$1" && -s "${TRAIN_INCLUDED_FILE:-}" ]]; then
    heads="$(jq -Rn '[inputs | split("\t") | {number:(.[0]|tonumber),head:.[1]}]' <"${TRAIN_INCLUDED_FILE}")"
    batch_sha="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "$1" 2>/dev/null || echo '')"
  fi
  train_state_render "$1" "$2" "$3" "$4" "$5" "$6" "$7" "${8:-null}" \
    "${heads}" "${batch_sha}" "${timeout_reruns:-0}" "${TRAIN_RERUN_KIND:-}" \
    "${TRAIN_RERUN_BASE_ATTEMPT:-null}" "${timeout_reruns_total:-0}" >"${body}"
  train_state_write "${body}"
}

# Persist two-phase rerun state around the Actions side effect. `rejected` is a
# terminal, proven API response; unlike ambiguous `requesting`, startup does not
# resume it as an in-flight side effect.
_persist_retry_intent() {
  local kind="$1" next_count="$2" base_attempt="$3" run_id="$4" request_phase="${5:-requesting}"
  TRAIN_RERUN_KIND="${kind}"
  TRAIN_RERUN_BASE_ATTEMPT="${base_attempt}"
  if [[ "${kind}" == "timeout" ]]; then
    timeout_reruns="${next_count}"
    if [[ "${request_phase}" == "accepted" ]]; then
      timeout_reruns_total=$(( ${timeout_reruns_total:-0} + 1 ))
      train_metric_set timeout_reruns "${timeout_reruns_total}"
    fi
    _write_state "${batch}" "${trunk_sha}" "${included}" "timeout-retry-${request_phase}" "${run_id}" "${fwdfix}" "${flake_reruns}"
  else
    flake_reruns="${next_count}"
    train_metric_set flake_reruns "${flake_reruns}"
    _write_state "${batch}" "${trunk_sha}" "${included}" "flake-retry-${request_phase}" "${run_id}" "${fwdfix}" "${flake_reruns}"
  fi
}

# _pr_decision <pr>: classify a candidate PR's outcome for the dashboard table.
# Reads the included/skipped scratch files (decision logic already ran). Returns
# "included" / "skipped (conflict)" / "escalated".
_pr_decision() {
  local pr="$1"
  if grep -qE "^${pr}	" "${TRAIN_INCLUDED_FILE}" 2>/dev/null; then
    echo "✅ included"
  elif grep -qE "^${pr}	" "${TRAIN_SKIPPED_FILE}" 2>/dev/null; then
    echo "⏭️ skipped — assemble conflict (aborted clean)"
  else
    echo "⏹️ not in batch"
  fi
}

# _dashboard <batch> <selected-json> <trunk_sha> <shard_descriptor> <outcome-note>
# The headline deliverable: a Markdown report appended to $GITHUB_STEP_SUMMARY
# (and mirrored to stdout for local dry-runs). Reads only the scratch files /
# metrics the decision logic already produced — it makes NO decisions.
_dashboard() {
  local batch="$1" selected="$2" trunk_sha="$3" shard_descriptor="$4" note="$5"
  local mode; mode="$(_train_mode_label)"

  train_summary "## 🚂 Merge Train — ${mode} run"
  train_summary ""
  train_summary "| | |"
  train_summary "|---|---|"
  train_summary "| **Mode** | ${mode} ($([[ "${TRAIN_APPLY}" == "1" ]] && echo 'writes ACT' || echo 'no pushes/merges/comments')) |"
  train_summary "| **Run timestamp** | ${TRAIN_RUN_TIMESTAMP} |"
  train_summary "| **Trunk base** | \`${trunk_sha:-<none>}\` ${trunk_sha:+(${trunk_sha:0:7})} |"
  train_summary "| **Batch branch** | \`${batch:-<none>}\` |"
  train_summary "| **MAX_BATCH** | ${MAX_BATCH} |"
  train_summary "| **Outcome** | ${note:-—} |"
  train_summary ""

  # --- candidates table ------------------------------------------------------
  train_summary "### Candidates"
  train_summary ""
  if [[ "$(jq 'length' <<<"${selected}")" -gt 0 ]]; then
    train_summary "| PR | Author | CI-Gate | Decision |"
    train_summary "|---|---|---|---|"
    local rows; rows="$(jq -r '.[] | "\(.number)\t\(.author // "?")\t\(.gate)"' <<<"${selected}")"
    local num author gate decision
    while IFS=${HONUA_TAB} read -r num author gate; do
      [[ -z "${num}" ]] && continue
      decision="$(_pr_decision "${num}")"
      train_summary "| #${num} | ${author} | ${gate} | ${decision} |"
    done <<<"${rows}"
  else
    train_summary "_No PRs were eligible for selection this run._"
  fi
  train_summary ""

  # --- batch -----------------------------------------------------------------
  local included_csv=""
  [[ -s "${TRAIN_INCLUDED_FILE}" ]] && included_csv="$(cut -f1 "${TRAIN_INCLUDED_FILE}" 2>/dev/null | sed 's/^/#/' | tr '\n' ' ')"
  train_summary "### Batch"
  train_summary ""
  train_summary "- **Branch:** \`${batch:-<none>}\`"
  train_summary "- **Included PRs:** ${included_csv:-_(none)_}"
  if [[ -s "${TRAIN_SKIPPED_FILE}" ]]; then
    local skipped_csv; skipped_csv="$(cut -f1 "${TRAIN_SKIPPED_FILE}" | sed 's/^/#/' | tr '\n' ' ')"
    train_summary "- **Skipped (conflict):** ${skipped_csv}"
  fi
  train_summary ""

  # --- smart-CI shard set ----------------------------------------------------
  train_summary "### Smart-CI shard set"
  train_summary ""
  if [[ -n "${shard_descriptor}" ]] && jq -e . >/dev/null 2>&1 <<<"${shard_descriptor}"; then
    local run_all reason shards
    run_all="$(jq -r '.run_all' <<<"${shard_descriptor}")"
    reason="$(jq -r '.reason // ""' <<<"${shard_descriptor}")"
    shards="$(jq -r '(.shards // []) | join(", ")' <<<"${shard_descriptor}")"
    if [[ "${run_all}" == "true" ]]; then
      train_summary "- **run_all:** \`true\` — ${reason:-full matrix}"
    else
      local shard_n; shard_n="$(jq -r '(.shards // []) | length' <<<"${shard_descriptor}")"
      train_summary "- **Targeted (run_all=false), ${shard_n} shards:** ${shards:-_(none)_}"
      [[ -n "${reason}" ]] && train_summary "- **Reason:** ${reason}"
    fi
  else
    train_summary "_(not computed — no batch reached smart-CI)_"
  fi
  train_summary ""

  # --- gate / heals / land ---------------------------------------------------
  train_summary "### Run actions"
  train_summary ""
  train_summary "| Metric | Count |"
  train_summary "|---|---|"
  train_summary "| Candidates selected | $(train_metric_get selected 0) |"
  train_summary "| Included | $(train_metric_get included 0) |"
  train_summary "| Skipped (conflict) | $(train_metric_get skipped_conflict 0) |"
  train_summary "| Forward-fixes applied | $(train_metric_get forward_fixes 0) |"
  train_summary "| Pre-existing-only passes | $(train_metric_get preexisting_passes 0) |"
  train_summary "| Flake reruns | $(train_metric_get flake_reruns 0) |"
  train_summary "| Timeout reruns | $(train_metric_get timeout_reruns 0) |"
  train_summary "| Autofix attempts | $(train_metric_get autofix_attempts 0) |"
  train_summary "| Autofix fixes landed | $(train_metric_get autofix_fixes 0) |"
  train_summary "| Attribution drops | $(train_metric_get attribution_drops 0) |"
  train_summary "| Escalated | $(train_metric_get escalated 0) |"
  train_summary "| Validated member snapshots landed | $(train_metric_get snapshot_landed 0) |"
  train_summary "| Heads advanced after snapshot | $(train_metric_get advanced_after_snapshot 0) |"
  train_summary "| Finalization pending | $(train_metric_get finalization_pending 0) |"
  train_summary "| Landed | $(train_metric_get landed 0) |"
  train_summary ""

  # --- per-phase timings -----------------------------------------------------
  if [[ -s "${TRAIN_TIMINGS_FILE:-/dev/null}" ]]; then
    train_summary "### Per-phase timings"
    train_summary ""
    train_summary "| Phase | Seconds |"
    train_summary "|---|---|"
    awk -F= 'NF>=2 {t[$1]=$2} END {for (k in t) print k"\t"t[k]}' "${TRAIN_TIMINGS_FILE}" \
      | while IFS=${HONUA_TAB} read -r ph secs; do train_summary "| ${ph} | ${secs} |"; done
    train_summary ""
  fi

  train_summary "_Machine-readable metrics: \`merge-train-metrics.json\` (workflow artifact). Aggregate over-time dashboard: the **Merge Train State** issue._"
}

if [[ "${TRAIN_SOURCE_ONLY:-0}" != "1" ]]; then
  main "$@"
fi
