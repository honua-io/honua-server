#!/usr/bin/env bash
# Step 7: land — fast-forward-only push of the CI-green batch onto trunk, guarded
# by a compare-and-swap on the trunk SHA. If trunk moved since assembly, the FF
# push is rejected (or pre-empted by the CAS check) and the caller re-assembles
# — the train NEVER lands bytes that were not the exact bytes CI validated.
#
#   git fetch origin trunk
#   if rev-parse origin/trunk == <trunkShaAtAssembly>:  # CAS
#       git push origin <batch>:trunk    # FF-only (no --force); non-FF rejected
#   then `gh pr merge <n> --merge` per INCLUDED PR.
#
# A non-FF rejection (someone else advanced trunk in the race window) means
# trunk moved => re-assemble. We never retry with force.

# train_land <batch-branch> <trunk-sha-at-assembly> <included-file>:
#   returns 0 on success (landed), 10 on CAS/FF rejection (re-assemble), 1 on error.
train_land() {
  local batch="$1" trunk_at_assembly="$2" included_file="$3"

  # Selection may be hours old after batch CI. Re-attest every member directly
  # before touching trunk; any mutable-state change invalidates this batch.
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

  # Compare-and-swap precondition: trunk must be exactly what we assembled onto.
  if [[ "${current}" != "${trunk_at_assembly}" ]]; then
    train_warn "trunk advanced (${trunk_at_assembly:0:7} -> ${current:0:7}); re-assemble"
    return 10
  fi

  # FF-only push. No --force, no --force-with-lease that could rewrite: a plain
  # `push origin <batch>:trunk` is rejected by the server if it is not a
  # fast-forward, which is exactly the guarantee we want.
  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_side_effect git push "${TRAIN_REMOTE}" "${batch}:${TRAIN_BASE_BRANCH}"
  else
    if ! git -C "${TRAIN_REPO_ROOT}" push "${TRAIN_REMOTE}" "${batch}:${TRAIN_BASE_BRANCH}"; then
      train_warn "FF push rejected (trunk moved in race window); re-assemble"
      return 10
    fi
  fi

  # Mark each INCLUDED PR merged. With the batch already on trunk, `gh pr merge
  # --merge` records the merge and closes the PR; GitHub recognizes the commits.
  local pr _sha
  while IFS=$'\t' read -r pr _sha; do
    [[ -z "${pr}" ]] && continue
    train_side_effect gh pr merge "${pr}" --merge
    train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
    train_log "landed #${pr} (#${pr})"
  done <"${included_file}"

  return 0
}
