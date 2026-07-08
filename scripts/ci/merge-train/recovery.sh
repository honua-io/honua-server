#!/usr/bin/env bash
# Recovery for a manually-rerun batch CI that turns green after the train already
# escalated the batch. The normal train loop lands a green run while it is still
# waiting. This helper handles the later workflow_run case: clear stale
# train:escalated labels and stamp CI Gate on the original member PR heads so the
# regular PR merge train can drain them.

train_recovery_batch_prs() {
  local batch="$1"
  if [[ -n "${TRAIN_RECOVERY_PRS_FOR_BRANCH:-}" ]]; then
    "${TRAIN_RECOVERY_PRS_FOR_BRANCH}" "${batch}"
    return 0
  fi

  [[ -z "${batch}" ]] && return 0
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" \
    "refs/heads/${batch}:refs/remotes/${TRAIN_REMOTE}/${batch}" 2>/dev/null || return 0
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" \
    "refs/heads/${TRAIN_BASE_BRANCH}:refs/remotes/${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}" 2>/dev/null || true

  local batch_ref="${TRAIN_REMOTE}/${batch}"
  local base_ref="${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}"
  local range="${batch_ref}"
  if git -C "${TRAIN_REPO_ROOT}" rev-parse --verify --quiet "${base_ref}" >/dev/null; then
    range="${base_ref}..${batch_ref}"
  fi

  git -C "${TRAIN_REPO_ROOT}" log --reverse --format=%s "${range}" \
    | sed -nE 's/^train: merge #([0-9]+)$/\1/p' \
    | awk '!seen[$0]++'
}

train_recovery_pr_info() {
  local pr="$1"
  if [[ -n "${TRAIN_RECOVERY_PR_INFO_FOR:-}" ]]; then
    "${TRAIN_RECOVERY_PR_INFO_FOR}" "${pr}"
    return 0
  fi

  gh pr view "${pr}" --json headRefOid,state,labels \
    --jq '[.headRefOid, .state, ([.labels[].name] | join(","))] | @tsv'
}

train_recovery_labels_contain() {
  local labels_csv="$1" label="$2"
  tr ',' '\n' <<<"${labels_csv}" | grep -Fxq "${label}"
}

train_recovery_stamp_ci_gate() {
  local pr="$1" sha="$2" run_url="$3" batch="$4" labels="$5"
  local repo="${GITHUB_REPOSITORY:-honua-io/honua-server}"

  train_side_effect gh api "repos/${repo}/statuses/${sha}" \
    -f state=success \
    -f context="CI Gate" \
    -f description="Merge train batch rerun passed" \
    -f target_url="${run_url}"
  train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_ESCALATED}"
  if train_recovery_labels_contain "${labels}" "${TRAIN_LABEL_LANDING}"; then
    train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
  fi
  train_side_effect gh pr comment "${pr}" --body \
    "Merge train batch \`${batch}\` passed after rerun. Cleared stale merge-train labels where present and stamped \`CI Gate\` on this PR head."
}

train_recover_green_batch_rerun() {
  local run_id="$1" batch="$2" run_url="${3:-}"

  if [[ -z "${batch}" || "${batch}" != train/batch/* ]]; then
    train_log "recovery skipped: ${batch:-<none>} is not a train/batch branch"
    return 0
  fi

  if [[ -z "${run_url}" ]]; then
    run_url="https://github.com/${GITHUB_REPOSITORY:-honua-io/honua-server}/actions/runs/${run_id}"
  fi

  local recovered=0 pr
  while IFS= read -r pr; do
    [[ -z "${pr}" ]] && continue

    local info sha state labels
    info="$(train_recovery_pr_info "${pr}" 2>/dev/null || true)"
    [[ -z "${info}" ]] && { train_warn "recovery skipped #${pr}: could not read PR info"; continue; }

    IFS=$'\t' read -r sha state labels <<<"${info}"
    if [[ "${state}" != "OPEN" ]]; then
      train_log "recovery skipped #${pr}: state=${state}"
      continue
    fi
    if ! train_recovery_labels_contain "${labels}" "${TRAIN_LABEL_ESCALATED}"; then
      train_log "recovery skipped #${pr}: not labeled ${TRAIN_LABEL_ESCALATED}"
      continue
    fi
    if [[ -z "${sha}" || "${sha}" == "null" ]]; then
      train_warn "recovery skipped #${pr}: missing head SHA"
      continue
    fi

    train_recovery_stamp_ci_gate "${pr}" "${sha}" "${run_url}" "${batch}" "${labels}"
    recovered=$((recovered + 1))
    train_decision "RECOVERED #${pr}: green batch rerun ${run_id} stamped CI Gate and cleared ${TRAIN_LABEL_ESCALATED}"
  done < <(train_recovery_batch_prs "${batch}")

  if [[ "${recovered}" -eq 0 ]]; then
    train_log "recovery complete: no escalated open PRs matched ${batch}"
  else
    train_notice "recovery complete: restored ${recovered} PR(s) from green batch rerun ${run_id}"
  fi
}
