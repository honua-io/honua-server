#!/usr/bin/env bash
# Step 7: land the immutable CI-green batch with a fast-forward trunk CAS.
#
# The batch commit is the authoritative merge unit. GitHub does not expose an
# atomic transaction spanning trunk and multiple mutable PR head refs, so PR
# finalization is post-push bookkeeping. Only members whose current head still
# equals their admitted SHA are finalized with --match-head-commit. If a member
# advances during landing, the tested snapshot still lands but the PR remains
# open for its unreviewed delta. No unreviewed bytes can enter the batch.

train_land_pr_info() {
  local pr="$1"
  if [[ -n "${TRAIN_LAND_PR_INFO_FOR:-}" ]]; then
    "${TRAIN_LAND_PR_INFO_FOR}" "${pr}"
    return
  fi
  gh pr view "${pr}" --json headRefOid,state --jq '[.headRefOid,.state] | @tsv'
}

train_land_clear_landing_label() {
  local pr="$1"
  if [[ -n "${TRAIN_LAND_CLEAR_LABEL_CMD:-}" ]]; then
    "${TRAIN_LAND_CLEAR_LABEL_CMD}" "${pr}"
  else
    train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
  fi
}

train_prepare_land_journals() {
  local included_file="$1"
  TRAIN_LAND_FINALIZED_FILE="${TRAIN_LAND_FINALIZED_FILE:-${included_file}.finalized}"
  TRAIN_LAND_ADVANCED_FILE="${TRAIN_LAND_ADVANCED_FILE:-${included_file}.advanced}"
  TRAIN_LAND_PENDING_FILE="${TRAIN_LAND_PENDING_FILE:-${included_file}.pending}"
  export TRAIN_LAND_FINALIZED_FILE TRAIN_LAND_ADVANCED_FILE TRAIN_LAND_PENDING_FILE
  : >"${TRAIN_LAND_FINALIZED_FILE}"
  : >"${TRAIN_LAND_ADVANCED_FILE}"
  : >"${TRAIN_LAND_PENDING_FILE}"
}

train_cleanup_deferred_members() {
  local included_file="$1" pr admitted_sha
  while IFS=$'\t' read -r pr admitted_sha; do
    [[ -z "${pr}" ]] && continue
    if ! train_land_clear_landing_label "${pr}"; then
      printf '%s\t%s\tpre-land-label-cleanup\n' "${pr}" "${admitted_sha}" >>"${TRAIN_LAND_PENDING_FILE}"
      train_warn "pre-land deferral could not clear landing label for #${pr}; cleanup remains durable"
    fi
  done <"${included_file}"
}

# Post-CAS bookkeeping is observation-only with respect to trunk. GitHub will
# normally mark reachable PR heads merged itself. Exact OPEN heads remain pending
# for restart reconciliation; no endpoint here can create another trunk commit.
train_finalize_landed_members() {
  local included_file="$1" pr admitted_sha info current_head current_state
  while IFS=$'\t' read -r pr admitted_sha; do
    [[ -z "${pr}" ]] && continue
    info="$(train_land_pr_info "${pr}" 2>/dev/null)" || info=""
    IFS=$'\t' read -r current_head current_state <<<"${info}"
    if [[ "${current_head}" != "${admitted_sha}" && -n "${current_head}" ]]; then
      printf '%s\t%s\t%s\n' "${pr}" "${admitted_sha}" "${current_head}" >>"${TRAIN_LAND_ADVANCED_FILE}"
      if ! train_land_clear_landing_label "${pr}"; then
        printf '%s\t%s\tadvanced-label-cleanup\n' "${pr}" "${admitted_sha}" >>"${TRAIN_LAND_PENDING_FILE}"
      fi
      train_warn "snapshot for #${pr} landed, but head advanced; leaving PR open for its delta"
    elif [[ "${current_head}" == "${admitted_sha}" && ( "${current_state}" == "MERGED" || "${current_state}" == "CLOSED" ) ]]; then
      printf '%s\t%s\t%s\n' "${pr}" "${admitted_sha}" "${current_state}" >>"${TRAIN_LAND_FINALIZED_FILE}"
      if ! train_land_clear_landing_label "${pr}"; then
        printf '%s\t%s\tterminal-label-cleanup\n' "${pr}" "${admitted_sha}" >>"${TRAIN_LAND_PENDING_FILE}"
      fi
    else
      printf '%s\t%s\t%s\n' "${pr}" "${admitted_sha}" "${current_state:-unknown}" >>"${TRAIN_LAND_PENDING_FILE}"
      train_warn "landed snapshot for #${pr} awaits GitHub merged-state reconciliation"
    fi
  done <"${included_file}"
}

# Recover a crash after the durable land intent was written. Returns 0 when all
# members reconcile, 1 when the push never occurred, 2 on mismatch, 3 pending.
train_restore_post_land() {
  local state phase batch batch_sha trunk current remote_batch
  state="$(train_state_read 2>/dev/null || echo '')"
  [[ -n "${state}" ]] || return 1
  phase="$(jq -r '.active_batch.phase // empty' <<<"${state}")"
  [[ "${phase}" == "land" || "${phase}" == "pre-land-cleanup" || "${phase}" == "post-land-finalize" ]] || return 1
  batch="$(jq -r '.active_batch.branch // empty' <<<"${state}")"
  batch_sha="$(jq -r '.active_batch.batch_sha // empty' <<<"${state}")"
  trunk="$(jq -r '.active_batch.trunk_base // empty' <<<"${state}")"
  [[ "${batch}" == train/batch/* && "${batch_sha}" =~ ^[0-9a-fA-F]{40}$ && "${trunk}" =~ ^[0-9a-fA-F]{40}$ ]] || return 2
  jq -e '.active_batch.included_heads | type == "array" and length > 0 and
    all(.[]; (.number|type)=="number" and (.head|type)=="string" and (.head|test("^[0-9a-fA-F]{40}$"))) and
    ([.[].number] | unique | length) == length' <<<"${state}" >/dev/null || return 2
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}" "${batch}" || return 2
  current="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")" || return 2
  remote_batch="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${batch}")" || return 2
  [[ "${remote_batch}" == "${batch_sha}" ]] || return 2
  jq -r '.active_batch.included_heads[] | [.number,.head] | @tsv' <<<"${state}" >"${TRAIN_INCLUDED_FILE}"
  train_prepare_land_journals "${TRAIN_INCLUDED_FILE}"
  TRAIN_POST_LAND_BATCH="${batch}"
  TRAIN_POST_LAND_BATCH_SHA="${batch_sha}"
  TRAIN_POST_LAND_TRUNK_BASE="${trunk}"
  TRAIN_POST_LAND_INCLUDED="$(jq -r '.active_batch.included | map(tostring) | join(",")' <<<"${state}")"
  export TRAIN_POST_LAND_BATCH TRAIN_POST_LAND_BATCH_SHA TRAIN_POST_LAND_TRUNK_BASE TRAIN_POST_LAND_INCLUDED
  if [[ "${phase}" == "pre-land-cleanup" || ( "${phase}" == "land" && "${current}" != "${batch_sha}" ) ]]; then
    TRAIN_POST_LAND_RECOVERY_PHASE=pre-land-cleanup
    export TRAIN_POST_LAND_RECOVERY_PHASE
    train_cleanup_deferred_members "${TRAIN_INCLUDED_FILE}"
    [[ -s "${TRAIN_LAND_PENDING_FILE}" ]] && return 3
    return 4
  fi
  [[ "${current}" == "${batch_sha}" ]] || return 2
  TRAIN_POST_LAND_RECOVERY_PHASE=post-land-finalize
  export TRAIN_POST_LAND_RECOVERY_PHASE
  TRAIN_LANDED_BATCH_SHA="${batch_sha}"
  train_finalize_landed_members "${TRAIN_INCLUDED_FILE}"
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}" || return 2
  [[ "$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")" == "${batch_sha}" ]] || return 2
  [[ -s "${TRAIN_LAND_PENDING_FILE}" ]] && return 3
  return 0
}

# train_land <batch-branch> <trunk-sha-at-assembly> <included-file>
# Returns 0 once the exact batch lands, 10 when trunk CAS/FF rejects before any
# landing, and 1 on a pre-push error. Post-push finalization never replays trunk.
train_land() {
  local batch="$1" trunk_at_assembly="$2" included_file="$3"
  train_prepare_land_journals "${included_file}"

  # Re-attest mutable admission before touching trunk. This reduces wasted work;
  # safety itself comes from landing only the already-built immutable batch SHA.
  local admission_pr admission_sha
  while IFS=$'\t' read -r admission_pr admission_sha; do
    [[ -z "${admission_pr}" ]] && continue
    if ! train_pr_admission "${admission_pr}" "${admission_sha}"; then
      train_warn "pre-land admission failed for #${admission_pr}; re-select"
      TRAIN_LAND_PRE_CAS_DEFERRED=1
      export TRAIN_LAND_PRE_CAS_DEFERRED
      train_cleanup_deferred_members "${included_file}"
      return 10
    fi
  done <"${included_file}"

  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  local current
  current="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  if [[ "${current}" != "${trunk_at_assembly}" ]]; then
    train_warn "trunk advanced (${trunk_at_assembly:0:7} -> ${current:0:7}); re-assemble"
    TRAIN_LAND_PRE_CAS_DEFERRED=1
    export TRAIN_LAND_PRE_CAS_DEFERRED
    train_cleanup_deferred_members "${included_file}"
    return 10
  fi

  # Plain FF push is the atomic landing boundary. Never force or rebuild here.
  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_side_effect git push "${TRAIN_REMOTE}" "${batch}:${TRAIN_BASE_BRANCH}"
  elif ! git -C "${TRAIN_REPO_ROOT}" push "${TRAIN_REMOTE}" "${batch}:${TRAIN_BASE_BRANCH}"; then
    train_warn "FF push rejected (trunk moved in race window); re-assemble"
    TRAIN_LAND_PRE_CAS_DEFERRED=1
    export TRAIN_LAND_PRE_CAS_DEFERRED
    train_cleanup_deferred_members "${included_file}"
    return 10
  fi
  TRAIN_LANDED_BATCH_SHA="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${batch}")"
  export TRAIN_LANDED_BATCH_SHA
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  [[ "$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")" == "${TRAIN_LANDED_BATCH_SHA}" ]] || return 1
  train_finalize_landed_members "${included_file}"
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  [[ "$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")" == "${TRAIN_LANDED_BATCH_SHA}" ]] || return 1

  return 0
}
