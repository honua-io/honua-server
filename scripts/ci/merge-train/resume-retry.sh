#!/usr/bin/env bash
# Restore an interrupted failed-job rerun before selection/assembly. A valid
# retry intent resumes the existing batch/run; any state/run/branch mismatch is
# a hard fail-closed error. This path never dispatches CI or requests a rerun.

# train_restore_retry_intent: emit state JSON augmented with gate/descriptor.
# Returns 1 when no retry intent exists and 2 for malformed/mismatched intent.
train_restore_retry_intent() {
  local state phase batch trunk run_id kind base row run_branch current_attempt
  state="$(train_state_read 2>/dev/null || echo "")"
  [[ -n "${state}" ]] && jq -e . >/dev/null 2>&1 <<<"${state}" || return 1
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
  jq -e '.active_batch.included | type == "array" and length > 0' >/dev/null <<<"${state}" || return 2

  row="$(gh run view "${run_id}" --json headBranch,attempt \
    --jq '[.headBranch, .attempt] | @tsv' 2>/dev/null || echo "")"
  IFS=$'\t' read -r run_branch current_attempt <<<"${row}"
  [[ "${run_branch}" == "${batch}" && "${current_attempt}" =~ ^[0-9]+$ \
    && "${current_attempt}" -ge "${base}" ]] || return 2

  if [[ -n "${TRAIN_RESUME_FETCHER:-}" ]]; then
    "${TRAIN_RESUME_FETCHER}" "${batch}" "${trunk}" || return 2
  else
    git -C "${TRAIN_REPO_ROOT}" fetch "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}" "${batch}" >/dev/null 2>&1 || return 2
    [[ "$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}" 2>/dev/null)" == "${trunk}" ]] || return 2
    git -C "${TRAIN_REPO_ROOT}" branch -f "${batch}" "${TRAIN_REMOTE}/${batch}" >/dev/null 2>&1 || return 2
  fi

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

  jq -c --arg gate "${gate}" --argjson descriptor "${descriptor}" \
    '. + {resume_gate: $gate, resume_shard_descriptor: $descriptor}' <<<"${state}"
}
