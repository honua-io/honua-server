#!/usr/bin/env bash
# Step 6: attribute — for a REAL (non-flake) failure, map the failing shard back
# to the batched PR(s) responsible, using the REVERSE of the smart-CI routing:
#
#   failing shard name --(ci-shards.json .paths[])--> path prefixes
#   path prefixes --(each INCLUDED PR's `git diff --name-only base...head`)-->
#       which PR's changeset touches those prefixes
#
# Decision:
#   1 suspect  -> drop that one PR.
#   >=2 suspects -> drop ALL suspects (can't disambiguate cheaply in Phase 1).
#   0 suspects -> escalate the WHOLE batch (the failure isn't attributable to a
#                 specific member diff, e.g. an integration/infra failure).
#
# Dropped PRs get the train:escalated label + a comment, then the batch is
# rebuilt minus the culprits and re-CI'd (orchestrated by train.sh).

# train_failing_shards_from_jobs <failing-job-names-newline>: map failing CI job
# names to shard NAMES in ci-shards.json. The server-tests matrix job names are
# the shard `shard_name`/`name`; we match by substring so "server-tests (Core)"
# style names resolve. Emits unique shard names.
train_failing_shards_from_jobs() {
  local failing="$1" config="${2:-${TRAIN_SHARDS_CONFIG}}"
  local shard
  while IFS= read -r shard; do
    [[ -z "${shard}" ]] && continue
    # A job is attributable to this shard if the shard name appears in the job
    # name (the matrix job is named with the shard's shard_name).
    if printf '%s\n' "${failing}" | grep -Fq -- "${shard}"; then
      printf '%s\n' "${shard}"
    fi
  done < <(jq -r '.shards[].name' "${config}") | sort -u
}

# train_pr_changed_files <pr> <head-ref>: "<pr>\t<file>" lines for one PR's diff
# vs origin/<base>. Test override: TRAIN_DIFF_FOR_PR command.
train_pr_changed_files() {
  local pr="$1" head="$2"
  local files
  if [[ -n "${TRAIN_DIFF_FOR_PR:-}" ]]; then
    files="$("${TRAIN_DIFF_FOR_PR}" "${pr}")"
  else
    files="$(git -C "${TRAIN_REPO_ROOT}" diff --name-only \
      "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}...${head}")"
  fi
  local f
  while IFS= read -r f; do
    [[ -z "${f}" ]] && continue
    printf '%s\t%s\n' "${pr}" "${f}"
  done <<<"${files}"
}

# train_attribute <failing-job-names> <included-file>: print the culprit PR
# numbers (newline) or the literal "ESCALATE_BATCH" when no member is
# attributable. <included-file> is the TRAIN_INCLUDED_FILE: "<pr>\t<preSha>".
train_attribute() {
  local failing="$1" included_file="$2"

  local shards
  shards="$(train_failing_shards_from_jobs "${failing}")"
  if [[ -z "${shards}" ]]; then
    train_warn "failing jobs map to no known shard; escalating whole batch"
    echo "ESCALATE_BATCH"; return 0
  fi

  # Collect the union of all failing shards' paths.
  local shard_paths=""
  local s
  while IFS= read -r s; do
    [[ -z "${s}" ]] && continue
    shard_paths+="$(train_shard_paths "${s}")"$'\n'
  done <<<"${shards}"

  # Build the "<pr>\t<file>" table across all INCLUDED PRs.
  local pr_files="" pr head
  while IFS=$'\t' read -r pr head; do
    [[ -z "${pr}" ]] && continue
    pr_files+="$(train_pr_changed_files "${pr}" "${head}")"$'\n'
  done <"${included_file}"

  local culprits
  culprits="$(train_attribute_culprits "${shard_paths}" "${pr_files}")"
  if [[ -z "${culprits}" ]]; then
    train_warn "real failure not attributable to any member diff; escalating whole batch"
    echo "ESCALATE_BATCH"; return 0
  fi
  printf '%s\n' "${culprits}"
}

# train_drop_pr <pr> <reason>: label train:escalated + comment, side-effecting.
train_drop_pr() {
  local pr="$1" reason="$2"
  train_side_effect gh pr edit "${pr}" --add-label "${TRAIN_LABEL_ESCALATED}"
  train_side_effect gh pr comment "${pr}" --body \
    "Merge train dropped this PR from the batch: ${reason}. Rebuild the batch will exclude it until the ${TRAIN_LABEL_ESCALATED} label is removed."
}
