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

# train_extract_error_paths <errlog>: parse repo-relative FILE PATHS out of a
# failing job's log text. These are the NON-SHARD failures the shard map can't
# resolve: `Build & Format Check` (compile/build errors name the offending .cs)
# and `CI Router Validation` (a PR added a source/test path not mapped in
# ci-shards.json, named in the validator output). We match paths ending in a
# source/config extension that appear:
#   - in C# build errors:  src/Honua.Server/Foo.cs(12,34): error CS1002: ...
#     (strip the trailing `(line,col)` so the path matches `git diff` output),
#   - in CI Router Validation output naming the unmapped path/file,
#   - in `dotnet format` / generic `error`/`Failed` lines that name a file.
# Paths are made repo-relative (strip leading `./` and any runner-absolute
# `/home/runner/work/honua-server/honua-server/` prefix) so they match
# `git diff --name-only`. Emits unique, sorted paths.
train_extract_error_paths() {
  local errlog="$1"
  printf '%s' "${errlog}" \
    | grep -oE '(/home/runner/work/honua-server/honua-server/)?(\./)?[A-Za-z0-9_.][A-Za-z0-9_./-]*\.(cs|csproj|ts|tsx|js|py|json|sln|props|yml|yaml)(\([0-9]+,[0-9]+\))?' \
    | sed -E 's#\([0-9]+,[0-9]+\)$##' \
    | sed -E 's#^/home/runner/work/honua-server/honua-server/##; s#^\./##' \
    | sed -E '/^$/d' \
    | sort -u
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

# train_attribute <failing-job-names> <included-file> [errlog]: print the culprit
# PR numbers (newline) or the literal "ESCALATE_BATCH" when no member is
# attributable. <included-file> is the TRAIN_INCLUDED_FILE: "<pr>\t<preSha>".
#
# Two attribution strategies, in order:
#   1. SHARD-based (unchanged): failing job name -> shard -> ci-shards.json paths
#      -> which member diff touched them. This handles the per-shard test jobs.
#   2. PATH-based (errlog): for NON-SHARD failures (`Build & Format Check` build
#      errors, `CI Router Validation` unmapped-path errors) no shard name matches,
#      so the shard map yields nothing and the WHOLE batch used to be dropped. We
#      instead parse the offending FILE PATHS out of the failing job's log and map
#      THOSE to the member diff that touched them — dropping only the culprit.
# An ENVIRONMENTAL failure (nuget/registry 401/403, restore could-not-resolve,
# connection-refused, deadlock, timeout) is NOT a PR's fault: if path-based
# attribution finds no source-file culprit either, escalate to a human rather
# than blame a member for infra. We are deliberately conservative — prefer
# ESCALATE_BATCH over a wrong drop.
train_attribute() {
  local failing="$1" included_file="$2" errlog="${3:-}"

  # Build the "<pr>\t<file>" table across all INCLUDED PRs once (shared by both
  # the shard-based and the path-based attribution paths).
  local pr_files="" pr head
  while IFS=$'\t' read -r pr head; do
    [[ -z "${pr}" ]] && continue
    pr_files+="$(train_pr_changed_files "${pr}" "${head}")"$'\n'
  done <"${included_file}"

  # --- (1) shard-based attribution (the existing path) ------------------------
  local shards
  shards="$(train_failing_shards_from_jobs "${failing}")"
  if [[ -n "${shards}" ]]; then
    # Collect the union of all failing shards' paths.
    local shard_paths=""
    local s
    while IFS= read -r s; do
      [[ -z "${s}" ]] && continue
      shard_paths+="$(train_shard_paths "${s}")"$'\n'
    done <<<"${shards}"

    local culprits
    culprits="$(train_attribute_culprits "${shard_paths}" "${pr_files}")"
    if [[ -n "${culprits}" ]]; then
      printf '%s\n' "${culprits}"
      return 0
    fi
  fi

  # --- (2) path-based attribution for NON-SHARD failures ----------------------
  # Reached when the failing jobs map to no shard, OR a failing shard's paths
  # match no member diff. If we have an error log, parse the offending file
  # paths and attribute by path (train_attribute_culprits prefix-matches a PR's
  # changed file against each path; full paths match exactly).
  if [[ -n "${errlog}" ]]; then
    local paths path_culprits
    paths="$(train_extract_error_paths "${errlog}")"
    path_culprits="$(train_attribute_culprits "${paths}" "${pr_files}")"
    if [[ -n "${path_culprits}" ]]; then
      train_log "non-shard failure attributed to member diff by file path; dropping culprit(s) only"
      printf '%s\n' "${path_culprits}"
      return 0
    fi
    # ENVIRONMENTAL GUARD: a restore/network/infra failure that names no member
    # source file is NOT a PR's fault — escalate to a human (or the flake path).
    if printf '%s' "${errlog}" | grep -Eq 'nuget\.pkg\.github|401|Unauthorized|403|Forbidden|Could not resolve|Unable to load the service index|Retrying|Connection refused|TaskCanceled|timed out|deadlock detected|40P01'; then
      train_warn "non-shard failure looks environmental (restore/network/infra) and names no member source path; escalating whole batch (not a PR's fault)"
      echo "ESCALATE_BATCH"; return 0
    fi
  fi

  train_warn "real failure not attributable to any member diff; escalating whole batch"
  echo "ESCALATE_BATCH"; return 0
}

# train_drop_pr <pr> <reason>: label train:escalated + comment, side-effecting.
train_drop_pr() {
  local pr="$1" reason="$2"
  train_side_effect gh pr edit "${pr}" --add-label "${TRAIN_LABEL_ESCALATED}"
  train_side_effect gh pr comment "${pr}" --body \
    "Merge train dropped this PR from the batch: ${reason}. Rebuild the batch will exclude it until the ${TRAIN_LABEL_ESCALATED} label is removed."
}

# train_escalate_batch <included-csv> <reason>: the loop-bug fix. When the train
# escalates a WHOLE batch (a real, unfixable failure that is not attributable to,
# or not fixable in, a single member), it MUST do BOTH of the following or the
# next scheduled run re-selects the same doomed batch forever:
#   1. apply train:escalated to EVERY culprit PR (so select excludes them), and
#   2. remove the transient train:landing label from each (no longer in flight),
#      and comment so the author knows why.
# The caller is responsible for separately CLEARING active_batch from the state
# issue (train.sh does this via _write_state "" ... "select" after escalating) so
# the next run starts a fresh selection instead of resuming the escalated batch.
# Side-effecting (gated by TRAIN_APPLY via train_side_effect).
train_escalate_batch() {
  local included_csv="$1" reason="$2"
  local pr
  for pr in $(tr ',' ' ' <<<"${included_csv}"); do
    [[ -z "${pr}" ]] && continue
    train_side_effect gh pr edit "${pr}" --add-label "${TRAIN_LABEL_ESCALATED}"
    train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
    train_side_effect gh pr comment "${pr}" --body \
      "Merge train escalated this batch to a human: ${reason}. This PR is held out of future batches until the ${TRAIN_LABEL_ESCALATED} label is removed."
    train_decision "ESCALATE #${pr}: labeled ${TRAIN_LABEL_ESCALATED}, removed ${TRAIN_LABEL_LANDING}"
  done
}
