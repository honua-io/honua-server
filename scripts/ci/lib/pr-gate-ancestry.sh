#!/usr/bin/env bash

# Restore only the immediate history hidden by actions/checkout's depth-one
# pull-request checkout, then prove the declared base and head are the exact
# two parents of the synthetic merge commit. The proof remains fail-closed if
# GitHub ever stops exposing that relationship.
honua_ensure_pr_gate_ancestry() {
  local repo_root="$1"
  local base_sha="$2"
  local head_sha="$3"
  local merge_sha="$4"

  if [[ "$(git -C "${repo_root}" rev-parse HEAD)" != "${merge_sha}" ]]; then
    echo "::error::PR Gate checkout does not match the declared merge commit." >&2
    return 1
  fi

  if [[ "$(git -C "${repo_root}" rev-parse --is-shallow-repository)" == "true" ]]; then
    git -C "${repo_root}" fetch \
      --no-tags \
      --filter=blob:none \
      --deepen=1 \
      origin "${merge_sha}"
  fi

  local -a merge_record
  read -r -a merge_record <<<"$(git -C "${repo_root}" rev-list --parents -n 1 "${merge_sha}")"
  if [[ "${#merge_record[@]}" -ne 3 ]] ||
     [[ "${merge_record[0]}" != "${merge_sha}" ]] ||
     [[ "${merge_record[1]}" != "${base_sha}" ]] ||
     [[ "${merge_record[2]}" != "${head_sha}" ]]; then
    echo "::error::Declared base/head do not match the PR Gate merge commit's exact parents." >&2
    return 1
  fi
}
