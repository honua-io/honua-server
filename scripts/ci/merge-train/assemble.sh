#!/usr/bin/env bash
HONUA_NL="$(printf '\nX')"; HONUA_NL="${HONUA_NL%X}"
# Step 2: assemble — the git heart. Build the batch branch by merging each
# selected PR head onto a fresh branch off origin/<base>. Transactional
# detect-and-skip: a merge that conflicts is `git merge --abort`ed so it leaves
# ZERO residue, the PR is marked SKIPPED_CONFLICT, and assembly continues.
#
# This uses REAL local git in BOTH dry-run and live mode (conflict detection is
# the whole point of the dry run). The only thing gated by TRAIN_APPLY is the
# eventual push (step land), never the local assembly here.
#
# Outputs (written to the assoc-array-free way, via the named files passed in):
#   - prints the batch branch name on stdout (last line).
#   - appends to ${TRAIN_INCLUDED_FILE}:  "<pr>\t<preMergeHeadSha>" per included PR.
#   - appends to ${TRAIN_SKIPPED_FILE}:   "<pr>\tSKIPPED_CONFLICT" per skipped PR.

# train_assemble <base-trunk-sha7> <pr-numbers...>
#   Reads the per-PR head SHA from `gh` (or TRAIN_HEAD_FOR_PR override in tests).
#   Honors TRAIN_BATCH_BRANCH if preset (resume), else mints train/batch/<sha7>/<epoch>.
train_assemble() {
  local trunk_sha7="$1"; shift
  local prs=("$@")

  : "${TRAIN_INCLUDED_FILE:?TRAIN_INCLUDED_FILE must be set}"
  : "${TRAIN_SKIPPED_FILE:?TRAIN_SKIPPED_FILE must be set}"
  : >"${TRAIN_INCLUDED_FILE}"
  : >"${TRAIN_SKIPPED_FILE}"

  local epoch batch
  epoch="$(date -u +%s)"
  batch="${TRAIN_BATCH_BRANCH:-train/batch/${trunk_sha7}/${epoch}}"

  # Branch from origin/<base>. The caller is responsible for an up-to-date fetch
  # of the base; we resolve the base sha explicitly so the branch is pinned.
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  git -C "${TRAIN_REPO_ROOT}" checkout -q -B "${batch}" "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}"

  local pr head pre_sha
  for pr in "${prs[@]}"; do
    [[ -z "${pr}" ]] && continue

    # Fetch the PR head. In fixtures the caller injects a local ref resolver.
    if [[ -n "${TRAIN_FETCH_PR_REF:-}" ]]; then
      head="$("${TRAIN_FETCH_PR_REF}" "${pr}")"   # returns a resolvable ref
    else
      git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "pull/${pr}/head"
      head="FETCH_HEAD"
    fi

    pre_sha="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${head}")"

    # Attempt the no-fast-forward merge. On conflict, abort transactionally.
    local merge_err
    if merge_err="$(git -C "${TRAIN_REPO_ROOT}" merge --no-ff --no-edit \
         -m "train: merge #${pr}" "${head}" 2>&1)"; then
      printf '%s\t%s\n' "${pr}" "${pre_sha}" >>"${TRAIN_INCLUDED_FILE}"
      train_log "assembled #${pr} (head ${pre_sha:0:7})"
    else
      # Distinguish a REAL content conflict (unmerged paths) from an
      # environment/setup failure (e.g. missing git identity, dirty tree).
      # Only the former is a legitimate detect-and-skip; the latter must be
      # surfaced loudly — silently calling it a "conflict" would skip every PR
      # and the train would never land anything.
      local unmerged
      unmerged="$(git -C "${TRAIN_REPO_ROOT}" ls-files -u 2>/dev/null | wc -l | tr -d ' ')"
      git -C "${TRAIN_REPO_ROOT}" merge --abort >/dev/null 2>&1 || true
      git -C "${TRAIN_REPO_ROOT}" reset -q --hard HEAD
      if [ "${unmerged:-0}" -gt 0 ]; then
        printf '%s\t%s\n' "${pr}" "SKIPPED_CONFLICT" >>"${TRAIN_SKIPPED_FILE}"
        train_warn "SKIPPED_CONFLICT #${pr} (conflict vs batch; aborted clean)"
      else
        # Not a conflict — a merge/setup error. Fail loud, do NOT mask as conflict.
        train_warn "MERGE ERROR (not a conflict) #${pr}: ${merge_err##*${HONUA_NL}}"
        return 3
      fi
    fi
  done

  echo "${batch}"
}

# train_batch_diff_files <batch-branch>: cumulative diff filenames vs the merge
# base with origin/<base>. Used by smart-ci to compute the shard subset.
train_batch_diff_files() {
  local batch="$1"
  git -C "${TRAIN_REPO_ROOT}" diff --name-only \
    "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}...${batch}"
}
