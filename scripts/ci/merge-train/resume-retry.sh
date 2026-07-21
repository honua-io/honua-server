#!/usr/bin/env bash
# Restore an interrupted failed-job rerun before selection/assembly. A valid
# retry intent resumes the existing batch/run; any state/run/branch mismatch is
# a hard fail-closed error. This path never dispatches CI or requests a rerun.

# _train_resume_is_ancestor <ancestor> <descendant>: test seam for fixtures.
_train_resume_is_ancestor() {
  if [[ -n "${TRAIN_RESUME_ANCESTRY_CHECKER:-}" ]]; then
    "${TRAIN_RESUME_ANCESTRY_CHECKER}" "$1" "$2"
  else
    git -C "${TRAIN_REPO_ROOT}" merge-base --is-ancestor "$1" "$2"
  fi
}

# _train_resume_member_head <pr> <trunk> <batch>: derive the exact PR head
# incorporated by assembly. Generated-artifact commits may sit above the merge
# commits, so locate the unique train merge on immutable first-parent history
# and return its second parent.
_train_resume_member_head() {
  local pr="$1" trunk="$2" batch="$3" matches commit row self first second extra
  if [[ -n "${TRAIN_RESUME_MEMBER_HEAD_RESOLVER:-}" ]]; then
    "${TRAIN_RESUME_MEMBER_HEAD_RESOLVER}" "${pr}" "${trunk}" "${batch}"
    return
  fi
  matches="$(git -C "${TRAIN_REPO_ROOT}" log --first-parent --format='%H%x09%s' \
    "${trunk}..${batch}" | awk -F '\t' -v subject="train: merge #${pr}" '$2 == subject { print $1 }')" || return 1
  [[ "$(sed '/^$/d' <<<"${matches}" | wc -l | tr -d ' ')" == "1" ]] || return 1
  commit="$(sed '/^$/d' <<<"${matches}")"
  row="$(git -C "${TRAIN_REPO_ROOT}" rev-list --parents -n 1 "${commit}")" || return 1
  read -r self first second extra <<<"${row}"
  [[ "${self}" == "${commit}" && "${first}" =~ ^[0-9a-fA-F]{40}$ \
    && "${second}" =~ ^[0-9a-fA-F]{40}$ && -z "${extra:-}" ]] || return 1
  printf '%s\n' "${second}"
}

# train_restore_retry_members <state-json> <batch-sha>: reconstruct the included
# file from persisted member ids and the immutable merge parents incorporated
# into the batch. Every current GitHub head must equal its exact merge parent.
train_restore_retry_members() {
  local state="$1" trunk="$2" batch_sha="$3" tmp selected='[]' pr row expected_head current_head
  tmp="$(mktemp)"
  while IFS= read -r pr; do
    expected_head="$(_train_resume_member_head "${pr}" "${trunk}" "${batch_sha}")" || {
      rm -f "${tmp}"; return 1;
    }
    [[ "${expected_head}" =~ ^[0-9a-fA-F]{40}$ ]] || { rm -f "${tmp}"; return 1; }
    row="$(gh pr view "${pr}" --json number,state,headRefOid,createdAt,author 2>/dev/null)" || {
      rm -f "${tmp}"; return 1;
    }
    jq -e --argjson pr "${pr}" '
      .number == $pr and .state == "OPEN"
      and (.headRefOid | type == "string" and test("^[0-9a-fA-F]{40}$"))
    ' >/dev/null <<<"${row}" || { rm -f "${tmp}"; return 1; }
    current_head="$(jq -r '.headRefOid' <<<"${row}")"
    [[ "${current_head}" == "${expected_head}" ]] || { rm -f "${tmp}"; return 1; }
    printf '%s\t%s\n' "${pr}" "${expected_head}" >>"${tmp}"
    selected="$(jq -c --argjson row "${row}" '. + [{
      number: $row.number,
      headRefOid: $row.headRefOid,
      createdAt: ($row.createdAt // ""),
      gate: "SUCCESS",
      author: ($row.author.login // $row.author.name // "?")
    }]' <<<"${selected}")"
  done < <(jq -r '.active_batch.included[]' <<<"${state}")
  [[ -s "${tmp}" ]] || { rm -f "${tmp}"; return 1; }
  mv "${tmp}" "${TRAIN_INCLUDED_FILE}"
  printf '%s\n' "${selected}"
}

# train_restore_retry_intent: emit state JSON augmented with gate/descriptor
# and the exact reconstructed member snapshot.
# Returns 1 when no retry intent exists and 2 for malformed/mismatched intent.
train_restore_retry_intent() {
  local state phase batch trunk run_id kind base row run_branch run_head current_attempt batch_sha selected
  state="$(train_state_read 2>/dev/null || echo "")"
  [[ -n "${state}" ]] || return 1
  jq -e . >/dev/null 2>&1 <<<"${state}" || return 2
  phase="$(jq -r '.active_batch.phase // empty' <<<"${state}")"
  case "${phase}" in timeout-retry-intent|flake-retry-intent) ;; *) return 1 ;; esac

  batch="$(jq -r '.active_batch.branch // empty' <<<"${state}")"
  trunk="$(jq -r '.active_batch.trunk_base // empty' <<<"${state}")"
  run_id="$(jq -r '.active_batch.run_id // empty' <<<"${state}")"
  kind="$(jq -r '.active_batch.rerun_kind // empty' <<<"${state}")"
  base="$(jq -r '.active_batch.rerun_base_attempt // empty' <<<"${state}")"
  [[ "${batch}" == train/batch/* && "${trunk}" =~ ^[0-9a-fA-F]{40}$ \
    && "${run_id}" =~ ^[0-9]+$ && "${base}" =~ ^[0-9]+$ ]] || return 2
  [[ "${phase}" == "${kind}-retry-intent" ]] || return 2
  jq -e '.active_batch.included | type == "array" and length > 0
    and all(.[]; type == "number" and floor == .)
    and (unique | length) == length' >/dev/null <<<"${state}" || return 2

  if [[ -n "${TRAIN_RESUME_FETCHER:-}" ]]; then
    batch_sha="$("${TRAIN_RESUME_FETCHER}" "${batch}" "${trunk}")" || return 2
  else
    git -C "${TRAIN_REPO_ROOT}" fetch "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}" "${batch}" >/dev/null 2>&1 || return 2
    batch_sha="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${batch}" 2>/dev/null)" || return 2
    git -C "${TRAIN_REPO_ROOT}" branch -f "${batch}" "${TRAIN_REMOTE}/${batch}" >/dev/null 2>&1 || return 2
  fi
  [[ "${batch_sha}" =~ ^[0-9a-fA-F]{40}$ ]] || return 2
  _train_resume_is_ancestor "${trunk}" "${batch_sha}" || return 2

  row="$(gh run view "${run_id}" --json headBranch,headSha,attempt \
    --jq '[.headBranch, .headSha, .attempt] | @tsv' 2>/dev/null || echo "")"
  IFS=$'\t' read -r run_branch run_head current_attempt <<<"${row}"
  [[ "${run_branch}" == "${batch}" && "${run_head}" == "${batch_sha}" \
    && "${current_attempt}" =~ ^[0-9]+$ && "${current_attempt}" -ge "${base}" ]] || return 2

  selected="$(train_restore_retry_members "${state}" "${trunk}" "${batch_sha}")" || return 2

  TRAIN_RERUN_KIND="${kind}"
  TRAIN_RERUN_BASE_ATTEMPT="${base}"
  export TRAIN_RERUN_KIND TRAIN_RERUN_BASE_ATTEMPT
  train_wait_for_new_run_attempt "${run_id}" "${base}" || return 2

  local gate descriptor
  gate="$(gh run view "${run_id}" --json jobs \
    --jq '[.jobs[] | select(.name=="CI Gate")][0].conclusion // "missing"' \
    2>/dev/null | tr '[:lower:]' '[:upper:]')"
  [[ "${gate}" == "SUCCESS" || "${gate}" == "FAILURE" ]] || return 2
  descriptor="$(train_smart_ci_shards "${batch}")" || return 2

  jq -c --arg gate "${gate}" --argjson descriptor "${descriptor}" --argjson selected "${selected}" \
    '. + {resume_gate: $gate, resume_shard_descriptor: $descriptor, resume_selected: $selected}' <<<"${state}"
}
