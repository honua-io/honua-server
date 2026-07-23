#!/usr/bin/env bash
HONUA_TAB="$(printf '\tX')"; HONUA_TAB="${HONUA_TAB%X}"
# Resume a successful rerun only when it is still the active immutable batch.

train_recovery_batch_pr_records() {
  local batch="$1" recorded_base="${2:-}"
  if [[ -n "${TRAIN_RECOVERY_PR_RECORDS_FOR_BRANCH:-}" ]]; then
    "${TRAIN_RECOVERY_PR_RECORDS_FOR_BRANCH}" "${batch}" "${recorded_base}" | sed '/^$/d'; return 0
  fi
  [[ -z "${batch}" ]] && return 0
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" \
    "+refs/heads/${batch}:refs/heads/${batch}" 2>/dev/null || return 0
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" \
    "refs/heads/${TRAIN_BASE_BRANCH}:refs/remotes/${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}" 2>/dev/null || true
  local range="${batch}" base_ref="${recorded_base}"
  [[ -z "${base_ref}" ]] && base_ref="${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}"
  git -C "${TRAIN_REPO_ROOT}" rev-parse --verify --quiet "${base_ref}^{commit}" >/dev/null \
    && range="${base_ref}..${batch}"
  git -C "${TRAIN_REPO_ROOT}" log --reverse --format='%H%x09%s' "${range}" |
    while IFS=${HONUA_TAB} read -r merge_commit subject; do
      if [[ "${subject}" =~ ^train:\ merge\ \#([0-9]+)$ ]]; then
        local pr="${BASH_REMATCH[1]}" validated
        validated="$(git -C "${TRAIN_REPO_ROOT}" rev-list --parents -n 1 "${merge_commit}" | awk '{print $3}')"
        [[ -n "${validated}" ]] && printf '%s\t%s\n' "${pr}" "${validated}"
      fi
    done | awk -F '\t' '!seen[$1]++'
}

train_recovery_pr_info() {
  if [[ -n "${TRAIN_RECOVERY_PR_INFO_FOR:-}" ]]; then
    "${TRAIN_RECOVERY_PR_INFO_FOR}" "$1"; return 0
  fi
  gh pr view "$1" --json headRefOid,state,labels \
    --jq '[.headRefOid,.state,([.labels[].name]|join(","))]|@tsv'
}

train_recovery_run_info() {
  if [[ -n "${TRAIN_RECOVERY_RUN_INFO_FOR:-}" ]]; then
    "${TRAIN_RECOVERY_RUN_INFO_FOR}" "$1"; return 0
  fi
  gh run view "$1" --json workflowName,status,conclusion,headBranch,headSha \
    --jq '[.workflowName,.status,.conclusion,.headBranch,.headSha]|@tsv'
}

# train_recovery_close_pr <pr> <admitted-sha>: exact-head final close for a PR
# this recovery path has already re-validated (current head == admitted_sha,
# state == OPEN). Gated on --match-head-commit so it can never merge a head
# that moved since validation. land.sh's own normal path stays observation-only
# (its post-CAS bookkeeping never calls gh pr merge); this is recovery-local
# because it is the one path that explicitly needs an assertive close rather
# than waiting on GitHub's automatic merge detection.
train_recovery_close_pr() {
  local pr="$1" admitted_sha="$2"
  if [[ -n "${TRAIN_RECOVERY_PR_MERGE_CMD:-}" ]]; then
    "${TRAIN_RECOVERY_PR_MERGE_CMD}" "${pr}" "${admitted_sha}"
  elif [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_side_effect gh pr merge "${pr}" --merge --match-head-commit "${admitted_sha}"
  else
    gh pr merge "${pr}" --merge --match-head-commit "${admitted_sha}"
  fi
}

train_recovery_state_json() {
  [[ -n "${TRAIN_RECOVERY_STATE_JSON:-}" ]] && printf '%s\n' "${TRAIN_RECOVERY_STATE_JSON}" || train_state_read
}

train_recovery_remote_head() {
  local branch="$1"
  if [[ -n "${TRAIN_RECOVERY_REMOTE_HEAD_FOR:-}" ]]; then
    "${TRAIN_RECOVERY_REMOTE_HEAD_FOR}" "${branch}"; return 0
  fi
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" \
    "+refs/heads/${branch}:refs/heads/${branch}" 2>/dev/null || return 1
  git -C "${TRAIN_REPO_ROOT}" rev-parse "refs/heads/${branch}"
}

train_recovery_trunk_head() {
  if [[ -n "${TRAIN_RECOVERY_TRUNK_HEAD_FOR:-}" ]]; then
    "${TRAIN_RECOVERY_TRUNK_HEAD_FOR}"; return 0
  fi
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}"
}

train_recovery_has_label() { tr ',' '\n' <<<"$1" | grep -Fxq "$2"; }

train_recovery_write_state() {
  local branch="$1" trunk_base="$2" included="$3" phase="$4" run_id="$5" last="$6" \
        records="${7:-}" batch_sha="${8:-}" body heads='[]'
  if [[ -n "${records}" ]]; then
    heads="$(jq -Rn '[inputs | split("\t") | {number:(.[0]|tonumber),head:.[1]}]' <<<"${records}")"
  fi
  if [[ "${phase}" == land && ( -z "${records}" || ! "${batch_sha}" =~ ^[0-9a-fA-F]{40}$ ) ]]; then
    train_err "recovery land state requires immutable member heads and batch SHA"
    return 2
  fi
  body="$(mktemp)"
  train_state_render "${branch}" "${trunk_base}" "${included}" "${phase}" "${run_id}" 0 0 "${last}" \
    "${heads}" "${batch_sha}" >"${body}"
  train_state_write "${body}"; rm -f "${body}"
}

train_recovery_continuation_exists() {
  local key="$1"
  if [[ -n "${TRAIN_RECOVERY_CONTINUATION_EXISTS_FOR:-}" ]]; then
    "${TRAIN_RECOVERY_CONTINUATION_EXISTS_FOR}" "${key}"; return $?
  fi
  gh api "repos/${GITHUB_REPOSITORY:-honua-io/honua-server}/actions/workflows/merge-train.yml/runs?per_page=100" \
    | jq -e --arg title "Merge Train [${key}]" \
      '.workflow_runs | any(.display_title == $title)' >/dev/null
}

train_recovery_dispatch_live() {
  local key="$1"
  if train_recovery_continuation_exists "${key}"; then
    train_log "recovery continuation ${key} is already durably dispatched"
    return 0
  fi
  if [[ -n "${TRAIN_RECOVERY_DISPATCH_CMD:-}" ]]; then
    "${TRAIN_RECOVERY_DISPATCH_CMD}" "${key}"; return 0
  fi
  train_side_effect gh workflow run merge-train.yml \
    --repo "${GITHUB_REPOSITORY:-honua-io/honua-server}" --ref "${TRAIN_BASE_BRANCH}" \
    -f train_apply=true -f max_batch="${MAX_BATCH}" -f recovery_key="${key}"
}

train_recovery_clear_labels() {
  local records="$1" state_included="${2:-}" clear_escalated="${3:-0}" pr info _sha _state labels
  while IFS= read -r pr; do
    [[ -z "${pr}" ]] && continue
    info="$(train_recovery_pr_info "${pr}" 2>/dev/null || true)"
    IFS=${HONUA_TAB} read -r _sha _state labels <<<"${info}"
    train_recovery_has_label "${labels}" "${TRAIN_LABEL_LANDING}" \
      && train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
    if [[ "${clear_escalated}" == 1 ]] && train_recovery_has_label "${labels}" "${TRAIN_LABEL_ESCALATED}"; then
      train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_ESCALATED}"
    fi
  done < <({ cut -f1 <<<"${records}"; tr ',' '\n' <<<"${state_included}"; } | sed '/^$/d' | sort -nu)
}

train_recovery_complete_continuation() {
  local target="$1" batch="$2" trunk="$3" included="$4" run_id="$5" last="$6" event_sha="$7"
  local key="recovery-${run_id}-${event_sha:0:12}"
  train_recovery_write_state "${batch}" "${trunk}" "${included}" requeue "${run_id}" "${last}"
  if [[ -n "${TRAIN_RECOVERY_BEFORE_DISPATCH_CMD:-}" ]]; then
    "${TRAIN_RECOVERY_BEFORE_DISPATCH_CMD}" "${target}" "${key}" || return $?
  fi
  train_recovery_dispatch_live "${key}"
  if [[ "${target}" == done ]]; then
    train_recovery_write_state "" "${event_sha}" "" done "" "${event_sha}"
  else
    train_recovery_write_state "" "${trunk}" "" select "" null
  fi
  train_decision "RECOVERY CONTINUATION: ${key} durably dispatched; state=${target}"
}

train_recovery_reassemble() {
  local records="$1" current="$2" reason="$3" included="${4:-}" batch="$5" run_id="$6" last="$7" event_sha="$8"
  train_warn "green rerun cannot land the recorded batch: ${reason}; resetting selection"
  # A stale batch is being made selectable again. Clear both transient labels
  # across state + commit-derived membership or selection will still exclude it.
  train_recovery_clear_labels "${records}" "${included}" 1
  local rc=0
  train_recovery_complete_continuation select "${batch}" "${current}" "${included}" "${run_id}" "${last}" "${event_sha}" || rc=$?
  [[ "${rc}" == 0 ]] || return "${rc}"
  train_decision "RECOVERY REASSEMBLE: ${reason}; queued one live merge-train run"
}

train_recovery_finalize() {
  local records="$1" batch_sha="$2" batch="$3" included="$4" run_id="$5" last="$6" record pr validated info sha state labels
  while IFS= read -r record; do
    IFS=${HONUA_TAB} read -r pr validated <<<"${record}"; [[ -z "${pr}" ]] && continue
    info="$(train_recovery_pr_info "${pr}" 2>/dev/null || true)"
    IFS=${HONUA_TAB} read -r sha state labels <<<"${info}"
    if [[ "${state}" == OPEN && "${sha}" == "${validated}" ]]; then
      if ! train_recovery_close_pr "${pr}" "${validated}"; then
        train_warn "recovery did not close #${pr}: exact-head final close was refused"
      fi
    elif [[ "${state}" == OPEN && "${sha}" != "${validated}" ]]; then
      train_warn "recovery did not close #${pr}: head changed after batch push"
    fi
  done <<<"${records}"
  train_recovery_clear_labels "${records}" "${included}" 1
  train_notice "RECOVERY LANDED: finalized ${batch} at ${batch_sha:0:12}"
  train_recovery_complete_continuation done "${batch}" "${batch_sha}" "${included}" "${run_id}" "${last}" "${batch_sha}"
}

train_recover_green_batch_rerun() {
  local run_id="$1" batch="$2" event_sha="$3" run_url="${4:-}"
  if [[ -z "${batch}" || "${batch}" != train/batch/* || -z "${event_sha}" ]]; then
    train_log "recovery skipped: invalid batch event"; return 0
  fi
  local info workflow status conclusion run_branch run_sha
  info="$(train_recovery_run_info "${run_id}" 2>/dev/null || true)"
  IFS=${HONUA_TAB} read -r workflow status conclusion run_branch run_sha <<<"${info}"
  if [[ "${workflow}" != CI || "${status}" != completed || "${conclusion}" != success \
     || "${run_branch}" != "${batch}" || "${run_sha}" != "${event_sha}" ]]; then
    train_warn "recovery skipped: run ${run_id} is not successful CI for the supplied batch head"; return 0
  fi

  local state active_branch trunk_base phase active_run included last
  state="$(train_recovery_state_json)"
  jq -e '.active_batch and (.active_batch.included|type=="array")' >/dev/null 2>&1 <<<"${state}" \
    || { train_warn "recovery skipped: merge-train state is missing or invalid"; return 0; }
  active_branch="$(jq -r '.active_batch.branch//""' <<<"${state}")"
  trunk_base="$(jq -r '.active_batch.trunk_base//""' <<<"${state}")"
  phase="$(jq -r '.active_batch.phase//""' <<<"${state}")"
  active_run="$(jq -r '.active_batch.run_id//""' <<<"${state}")"
  included="$(jq -r '.active_batch.included|map(tostring)|join(",")' <<<"${state}")"
  last="$(jq -r '.last_landed_trunk//"null"' <<<"${state}")"
  if [[ "${active_branch}" != "${batch}" || "${active_run}" != "${run_id}" \
     || ( "${phase}" != ci-incomplete && "${phase}" != land && "${phase}" != requeue ) ]]; then
    train_log "recovery skipped: run is not the active recoverable batch"; return 0
  fi

  local remote_sha records record_prs state_prs current
  current="$(train_recovery_trunk_head 2>/dev/null || true)"
  [[ -n "${current}" ]] || { train_err "recovery could not resolve trunk"; return 1; }
  if [[ "${phase}" == requeue ]]; then
    if [[ "${current}" == "${event_sha}" ]]; then
      train_recovery_complete_continuation done "${batch}" "${current}" "${included}" "${run_id}" "${last}" "${event_sha}"
    else
      train_recovery_complete_continuation select "${batch}" "${current}" "${included}" "${run_id}" "${last}" "${event_sha}"
    fi
    return 0
  fi
  remote_sha="$(train_recovery_remote_head "${batch}" 2>/dev/null || true)"
  if [[ "${remote_sha}" != "${event_sha}" ]]; then
    records="$(train_recovery_batch_pr_records "${batch}" "${trunk_base}")"
    train_recovery_reassemble "${records}" "${current}" \
      "batch branch is missing or no longer equals successful run head" "${included}" "${batch}" "${run_id}" "${last}" "${event_sha}"
    return $?
  fi
  records="$(train_recovery_batch_pr_records "${batch}" "${trunk_base}")"
  record_prs="$(cut -f1 <<<"${records}" | sed '/^$/d' | sort -n | paste -sd, -)"
  state_prs="$(tr ',' '\n' <<<"${included}" | sed '/^$/d' | sort -n | paste -sd, -)"
  if [[ -z "${records}" || "${record_prs}" != "${state_prs}" ]]; then
    train_recovery_reassemble "${records}" "${current}" "batch members differ from active state" "${included}" "${batch}" "${run_id}" "${last}" "${event_sha}"; return $?
  fi
  if [[ "${phase}" == land && "${current}" == "${event_sha}" ]]; then
    train_recovery_finalize "${records}" "${event_sha}" "${batch}" "${included}" "${run_id}" "${last}"; return $?
  fi
  if [[ "${current}" != "${trunk_base}" ]]; then
    train_recovery_reassemble "${records}" "${current}" "trunk advanced from recorded base" "${included}" "${batch}" "${run_id}" "${last}" "${event_sha}"; return $?
  fi

  local record pr validated sha pr_state labels
  while IFS= read -r record; do
    IFS=${HONUA_TAB} read -r pr validated <<<"${record}"
    info="$(train_recovery_pr_info "${pr}" 2>/dev/null || true)"
    IFS=${HONUA_TAB} read -r sha pr_state labels <<<"${info}"
    if [[ "${pr_state}" != OPEN || "${sha}" != "${validated}" ]]; then
      train_recovery_reassemble "${records}" "${current}" "PR #${pr} no longer matches validated head" "${included}" "${batch}" "${run_id}" "${last}" "${event_sha}"; return $?
    fi
  done <<<"${records}"

  train_recovery_write_state "${batch}" "${trunk_base}" "${included}" land "${run_id}" "${last}" \
    "${records}" "${event_sha}"
  local included_file rc=0; included_file="$(mktemp)"; printf '%s\n' "${records}" >"${included_file}"
  if [[ -n "${TRAIN_RECOVERY_LAND_CMD:-}" ]]; then
    "${TRAIN_RECOVERY_LAND_CMD}" "${batch}" "${trunk_base}" "${included_file}" || rc=$?
  else
    train_land "${batch}" "${trunk_base}" "${included_file}" || rc=$?
  fi
  rm -f "${included_file}"
  if [[ "${rc}" == 10 ]]; then
    current="$(train_recovery_trunk_head 2>/dev/null || true)"
    train_recovery_reassemble "${records}" "${current}" "land admission or FF-CAS changed" "${included}" "${batch}" "${run_id}" "${last}" "${event_sha}"; return $?
  fi
  [[ "${rc}" == 0 ]] || return "${rc}"
  current="$(train_recovery_trunk_head 2>/dev/null || true)"
  if [[ "${TRAIN_APPLY}" == 1 && "${current}" != "${event_sha}" ]]; then
    train_err "land returned success but trunk is not the validated batch head"; return 1
  fi
  train_recovery_finalize "${records}" "${event_sha}" "${batch}" "${included}" "${run_id}" "${last}"
}
