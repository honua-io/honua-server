#!/usr/bin/env bash
# Orchestrator for the honua-server optimistic batch merge train (Phase 1).
#
# Wires the eight sourceable steps together. DRY-RUN BY DEFAULT (TRAIN_APPLY=0):
# real local git assembly + real CI-status reads + real shard computation, but
# NO pushes/merges/comments/issue-writes (those route through train_side_effect
# and are only logged). MAX_BATCH defaults to 3.
#
# Phases (also the resume points written to the state issue before each
# side-effecting step):
#   select -> assemble -> smart-ci -> [forward-fix] -> [classify-flake] ->
#   [attribute -> rebuild] -> land -> done
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
# shellcheck source=classify-flake.sh
. "${TRAIN_DIR}/classify-flake.sh"
# shellcheck source=attribute.sh
. "${TRAIN_DIR}/attribute.sh"
# shellcheck source=land.sh
. "${TRAIN_DIR}/land.sh"
# shellcheck source=state.sh
. "${TRAIN_DIR}/state.sh"

train_require git jq gh || { train_err "missing prerequisites"; exit 2; }

# Scratch files for the run.
TRAIN_WORK="$(mktemp -d)"
trap 'rm -rf "${TRAIN_WORK}"' EXIT
export TRAIN_INCLUDED_FILE="${TRAIN_WORK}/included.tsv"
export TRAIN_SKIPPED_FILE="${TRAIN_WORK}/skipped.tsv"
export TRAIN_RUN_ID_FILE="${TRAIN_WORK}/run_id"

main() {
  train_log "mode: $( [[ "${TRAIN_APPLY}" == "1" ]] && echo LIVE || echo DRY-RUN ) MAX_BATCH=${MAX_BATCH}"

  # --- select ----------------------------------------------------------------
  local selected
  selected="$(train_select | jq -s '.')"
  local prs
  prs="$(jq -r '.[].number' <<<"${selected}")"
  if [[ -z "${prs}" ]]; then
    train_log "no ready PRs; nothing to do"
    echo
    echo "=== MERGE TRAIN DECISION (dry-run=$([[ "${TRAIN_APPLY}" == "1" ]] && echo 0 || echo 1)) ==="
    echo "selected batch: (none)"
    return 0
  fi
  train_log "candidate batch (oldest-first, capped): $(tr '\n' ' ' <<<"${prs}")"

  # --- trunk base ------------------------------------------------------------
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  local trunk_sha trunk_sha7
  trunk_sha="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  trunk_sha7="${trunk_sha:0:7}"

  # --- assemble (transactional detect-and-skip) ------------------------------
  # state: write BEFORE assembling (resumable at assemble phase).
  _write_state "" "${trunk_sha}" "" "assemble" "" 0 0
  local batch
  # shellcheck disable=SC2086
  batch="$(train_assemble "${trunk_sha7}" ${prs})"
  train_log "batch branch: ${batch}"

  local included skipped
  included="$(cut -f1 "${TRAIN_INCLUDED_FILE}" | tr '\n' ',' | sed 's/,$//')"
  skipped="$(cut -f1 "${TRAIN_SKIPPED_FILE}" | tr '\n' ' ')"
  train_log "INCLUDED: ${included:-<none>}"
  [[ -n "${skipped// /}" ]] && train_log "SKIPPED_CONFLICT: ${skipped}"

  if [[ -z "${included}" ]]; then
    train_warn "no PRs assembled cleanly; nothing to land"
    _decision_report "${batch}" "${selected}" "${trunk_sha}" "" "(all skipped)"
    return 0
  fi

  # state: landing labels + phase smart-ci before CI side effects.
  _write_state "${batch}" "${trunk_sha}" "${included}" "smart-ci" "" 0 0
  local pr
  for pr in $(tr ',' ' ' <<<"${included}"); do
    train_side_effect gh pr edit "${pr}" --add-label "${TRAIN_LABEL_LANDING}"
  done

  # --- smart-ci + forward-fix + flake + attribute loop -----------------------
  local shard_descriptor
  shard_descriptor="$(train_smart_ci_shards "${batch}")"
  train_log "smart-CI shard subset: ${shard_descriptor}"

  local gate fwdfix=0 flake_reruns=0
  gate="$(train_smart_ci_run "${batch}")"

  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_log "dry-run: stopping before CI gate evaluation/land (no pushes happened)"
    _decision_report "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" ""
    return 0
  fi

  # Live-mode gate handling (forward-fix -> flake -> attribute -> land).
  while [[ "${gate}" != "SUCCESS" ]]; do
    local run_id; run_id="$(cat "${TRAIN_RUN_ID_FILE}" 2>/dev/null || echo "")"
    local failing; failing="$(train_failing_jobs "${run_id}")"

    # forward-fix: only when the SOLE failure is the format-verify step.
    if train_is_format_only_failure "${failing}"; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "forward-fix" "${run_id}" "${fwdfix}" "${flake_reruns}"
      if train_forward_fix "${batch}" "${fwdfix}"; then
        fwdfix=$((fwdfix + 1))
        gate="$(train_smart_ci_run "${batch}")"
        continue
      fi
    fi

    # classify-flake BEFORE attribute.
    _write_state "${batch}" "${trunk_sha}" "${included}" "classify-flake" "${run_id}" "${fwdfix}" "${flake_reruns}"
    if train_classify_flake "${run_id}" "${flake_reruns}"; then
      flake_reruns=$((flake_reruns + 1))
      # Re-poll the same run after rerun.
      while :; do
        local st; st="$(gh run view "${run_id}" --json status --jq '.status' 2>/dev/null || echo "")"
        [[ "${st}" == "completed" ]] && break; sleep 30
      done
      gate="$(gh run view "${run_id}" --json jobs \
        --jq '[.jobs[] | select(.name=="CI Gate")][0].conclusion // "missing"' | tr '[:lower:]' '[:upper:]')"
      continue
    fi

    # attribute (real failure).
    _write_state "${batch}" "${trunk_sha}" "${included}" "attribute" "${run_id}" "${fwdfix}" "${flake_reruns}"
    local culprits; culprits="$(train_attribute "${failing}" "${TRAIN_INCLUDED_FILE}")"
    if [[ "${culprits}" == "ESCALATE_BATCH" ]]; then
      train_warn "escalating entire batch; manual triage required"
      for pr in $(tr ',' ' ' <<<"${included}"); do
        train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
      done
      return 0
    fi
    # Drop culprits, rebuild minus them, re-CI.
    local remaining="${included}"
    for pr in ${culprits}; do
      train_drop_pr "${pr}" "real CI failure attributed to its diff"
      remaining="$(tr ',' '\n' <<<"${remaining}" | grep -vx "${pr}" | tr '\n' ',' | sed 's/,$//')"
    done
    if [[ -z "${remaining}" ]]; then
      train_warn "all members dropped; batch empty"
      return 0
    fi
    included="${remaining}"
    _write_state "" "${trunk_sha}" "${included}" "assemble" "" "${fwdfix}" "${flake_reruns}"
    # shellcheck disable=SC2086
    batch="$(train_assemble "${trunk_sha7}" $(tr ',' ' ' <<<"${included}"))"
    included="$(cut -f1 "${TRAIN_INCLUDED_FILE}" | tr '\n' ',' | sed 's/,$//')"
    gate="$(train_smart_ci_run "${batch}")"
  done

  # --- land (FF-only CAS) ----------------------------------------------------
  _write_state "${batch}" "${trunk_sha}" "${included}" "land" "$(cat "${TRAIN_RUN_ID_FILE}" 2>/dev/null)" "${fwdfix}" "${flake_reruns}"
  local rc=0
  train_land "${batch}" "${trunk_sha}" "${TRAIN_INCLUDED_FILE}" || rc=$?
  if [[ "${rc}" == "10" ]]; then
    train_warn "land deferred: trunk moved; the next scheduled run re-assembles"
    return 0
  fi
  [[ "${rc}" != "0" ]] && { train_err "land failed (rc=${rc})"; return "${rc}"; }

  local new_trunk; new_trunk="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  _write_state "" "${new_trunk}" "" "done" "" 0 0 "${batch_landed:-${new_trunk}}"
  train_log "landed batch ${batch}; trunk now ${new_trunk:0:7}"
}

# _write_state branch trunk_base included-csv phase run_id fwdfix flake [last_landed]
_write_state() {
  local body="${TRAIN_WORK}/state.md"
  train_state_render "$1" "$2" "$3" "$4" "$5" "$6" "$7" "${8:-null}" >"${body}"
  train_state_write "${body}"
}

# _decision_report: human-readable dry-run summary (the shadow-run output).
_decision_report() {
  local batch="$1" selected="$2" trunk_sha="$3" shard_descriptor="$4" note="$5"
  echo
  echo "=== MERGE TRAIN DECISION (dry-run) ==="
  echo "trunk base:        ${trunk_sha} (${trunk_sha:0:7})"
  echo "batch branch:      ${batch}"
  echo
  echo "candidate PRs (oldest-first, capped at MAX_BATCH=${MAX_BATCH}):"
  jq -r '.[] | "  #\(.number)  created=\(.createdAt)  ci-gate=\(.gate)"' <<<"${selected}"
  echo
  echo "WOULD INCLUDE (assembled clean):"
  if [[ -s "${TRAIN_INCLUDED_FILE}" ]]; then
    while IFS=$'\t' read -r pr sha; do echo "  #${pr}  head=${sha:0:7}"; done <"${TRAIN_INCLUDED_FILE}"
  else
    echo "  (none)"
  fi
  echo
  echo "WOULD SKIP (conflict, aborted clean — zero residue):"
  if [[ -s "${TRAIN_SKIPPED_FILE}" ]]; then
    while IFS=$'\t' read -r pr why; do echo "  #${pr}  ${why}"; done <"${TRAIN_SKIPPED_FILE}"
  else
    echo "  (none)"
  fi
  echo
  echo "COMPUTED SMART-CI SHARD SUBSET (run on the cumulative batch diff):"
  echo "  ${shard_descriptor:-<none>}"
  [[ -n "${note}" ]] && { echo; echo "note: ${note}"; }
  echo "======================================"
}

main "$@"
