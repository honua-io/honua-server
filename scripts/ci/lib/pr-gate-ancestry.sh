#!/usr/bin/env bash

# Restore only the immediate history hidden by actions/checkout's depth-one
# pull-request checkout, then prove the declared base and head belong to the
# exact synthetic merge commit. The proof remains fail-closed if GitHub ever
# stops exposing that relationship.
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

  local sha
  for sha in "${base_sha}" "${head_sha}"; do
    if ! git -C "${repo_root}" cat-file -e "${sha}^{commit}" 2>/dev/null; then
      git -C "${repo_root}" fetch --no-tags --filter=blob:none origin "${sha}"
    fi
    git -C "${repo_root}" merge-base --is-ancestor "${sha}" "${merge_sha}" || {
      echo "::error::${sha} is not an ancestor of the PR Gate merge commit." >&2
      return 1
    }
  done
}
