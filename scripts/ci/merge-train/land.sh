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
    "${TRAIN_LAND_CLEAR_LABEL_CMD}" "${pr}" || train_warn "could not clear landing label for #${pr}"
  else
    train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}" \
      || train_warn "could not clear landing label for #${pr}"
  fi
  return 0
}

# train_land <batch-branch> <trunk-sha-at-assembly> <included-file>
# Returns 0 once the exact batch lands, 10 when trunk CAS/FF rejects before any
# landing, and 1 on a pre-push error. Post-push finalization never replays trunk.
train_land() {
  local batch="$1" trunk_at_assembly="$2" included_file="$3"
  TRAIN_LAND_FINALIZED_FILE="${TRAIN_LAND_FINALIZED_FILE:-${included_file}.finalized}"
  TRAIN_LAND_ADVANCED_FILE="${TRAIN_LAND_ADVANCED_FILE:-${included_file}.advanced}"
  TRAIN_LAND_PENDING_FILE="${TRAIN_LAND_PENDING_FILE:-${included_file}.pending}"
  export TRAIN_LAND_FINALIZED_FILE TRAIN_LAND_ADVANCED_FILE TRAIN_LAND_PENDING_FILE
  : >"${TRAIN_LAND_FINALIZED_FILE}"
  : >"${TRAIN_LAND_ADVANCED_FILE}"
  : >"${TRAIN_LAND_PENDING_FILE}"

  # Re-attest mutable admission before touching trunk. This reduces wasted work;
  # safety itself comes from landing only the already-built immutable batch SHA.
  local admission_pr admission_sha
  while IFS=$'\t' read -r admission_pr admission_sha; do
    [[ -z "${admission_pr}" ]] && continue
    if ! train_pr_admission "${admission_pr}" "${admission_sha}"; then
      train_warn "pre-land admission failed for #${admission_pr}; re-select"
      return 10
    fi
  done <"${included_file}"

  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  local current
  current="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  if [[ "${current}" != "${trunk_at_assembly}" ]]; then
    train_warn "trunk advanced (${trunk_at_assembly:0:7} -> ${current:0:7}); re-assemble"
    return 10
  fi

  # Plain FF push is the atomic landing boundary. Never force or rebuild here.
  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_side_effect git push "${TRAIN_REMOTE}" "${batch}:${TRAIN_BASE_BRANCH}"
  elif ! git -C "${TRAIN_REPO_ROOT}" push "${TRAIN_REMOTE}" "${batch}:${TRAIN_BASE_BRANCH}"; then
    train_warn "FF push rejected (trunk moved in race window); re-assemble"
    return 10
  fi

  local pr admitted_sha info current_head current_state merge_rc
  while IFS=$'\t' read -r pr admitted_sha; do
    [[ -z "${pr}" ]] && continue
    if [[ "${TRAIN_APPLY}" != "1" ]]; then
      train_side_effect gh pr merge "${pr}" --merge --match-head-commit "${admitted_sha}"
      printf '%s\t%s\n' "${pr}" "${admitted_sha}" >>"${TRAIN_LAND_FINALIZED_FILE}"
      train_land_clear_landing_label "${pr}"
      continue
    fi

    info="$(train_land_pr_info "${pr}" 2>/dev/null)" || info=""
    IFS=$'\t' read -r current_head current_state <<<"${info}"
    if [[ "${current_head}" != "${admitted_sha}" ]]; then
      printf '%s\t%s\t%s\n' "${pr}" "${admitted_sha}" "${current_head:-unknown}" >>"${TRAIN_LAND_ADVANCED_FILE}"
      train_land_clear_landing_label "${pr}"
      train_warn "snapshot for #${pr} landed, but head advanced (${admitted_sha:0:7} -> ${current_head:0:7}); leaving PR open for its delta"
      continue
    fi
    if [[ "${current_state}" == "MERGED" ]]; then
      printf '%s\t%s\n' "${pr}" "${admitted_sha}" >>"${TRAIN_LAND_FINALIZED_FILE}"
      train_land_clear_landing_label "${pr}"
      continue
    fi
    if [[ "${current_state}" != "OPEN" ]]; then
      printf '%s\t%s\t%s\n' "${pr}" "${admitted_sha}" "${current_state:-unknown}" >>"${TRAIN_LAND_PENDING_FILE}"
      train_warn "snapshot for #${pr} landed, but PR state ${current_state:-unknown} cannot be finalized"
      continue
    fi

    merge_rc=0
    if [[ -n "${TRAIN_LAND_PR_MERGE_CMD:-}" ]]; then
      "${TRAIN_LAND_PR_MERGE_CMD}" "${pr}" "${admitted_sha}" || merge_rc=$?
    else
      gh pr merge "${pr}" --merge --match-head-commit "${admitted_sha}" || merge_rc=$?
    fi
    if [[ "${merge_rc}" != "0" ]]; then
      info="$(train_land_pr_info "${pr}" 2>/dev/null)" || info=""
      IFS=$'\t' read -r current_head current_state <<<"${info}"
      if [[ "${current_head}" != "${admitted_sha}" ]]; then
        printf '%s\t%s\t%s\n' "${pr}" "${admitted_sha}" "${current_head:-unknown}" >>"${TRAIN_LAND_ADVANCED_FILE}"
        train_land_clear_landing_label "${pr}"
        train_warn "snapshot for #${pr} landed; match-head finalization refused after head advance, leaving delta open"
      elif [[ "${current_state}" == "MERGED" ]]; then
        printf '%s\t%s\n' "${pr}" "${admitted_sha}" >>"${TRAIN_LAND_FINALIZED_FILE}"
        train_land_clear_landing_label "${pr}"
      else
        printf '%s\t%s\t%s\n' "${pr}" "${admitted_sha}" "${current_state:-unknown}" >>"${TRAIN_LAND_PENDING_FILE}"
        train_warn "snapshot for #${pr} landed, but exact-head finalization failed; leaving PR untouched for recovery"
      fi
      continue
    fi
    printf '%s\t%s\n' "${pr}" "${admitted_sha}" >>"${TRAIN_LAND_FINALIZED_FILE}"
    train_land_clear_landing_label "${pr}"
    train_log "finalized landed snapshot for #${pr} at ${admitted_sha:0:7}"
  done <"${included_file}"

  return 0
}
