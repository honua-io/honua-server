#!/usr/bin/env bash
# Step 3: smart-ci — compute the shard subset for the batch's cumulative diff
# and (live only) run + poll CI on the batch branch, reading the CI Gate job
# conclusion.
#
# CONCURRENCY NOTE: the batch branch is a BRANCH, not a PR. ci.yml's concurrency
# key is `ci-${{ pr-number || github.ref }}`, so a branch run keys on its ref
# (ci-<batch-ref>) — a DISTINCT group from each member PR's run (ci-<pr#>).
# Therefore dispatching CI on the batch CANNOT cancel-in-progress the members'
# own PR runs (cancel-in-progress only cancels within the same group). This is
# intentional: the train never disturbs member PRs' independent CI.

# train_smart_ci_shards <batch-branch>: emit the targeted-tests descriptor JSON
# ({run_all,shards,reason}) for the batch's cumulative diff vs origin/<base>.
# READ-ONLY; runs in both modes. This is what determines the smart-CI subset.
train_smart_ci_shards() {
  local batch="$1"
  # The targeted-tests script diffs ${BASE}...HEAD; point it at the batch tip by
  # checking the batch out is unnecessary — it accepts --base and diffs HEAD.
  # We compute the file list ourselves and feed it via --stdin so we never
  # depend on the current checkout.
  train_batch_diff_files "${batch}" \
    | "${TRAIN_TARGETED_SCRIPT}" --stdin --config "${TRAIN_SHARDS_CONFIG}"
}

# train_smart_ci_run <batch-branch>: live-mode push + dispatch + poll. In
# dry-run, logs the would-run actions and returns the shard descriptor only.
# Emits the CI Gate conclusion on stdout in live mode (SUCCESS/FAILURE/...),
# or "DRYRUN" in dry-run.
#
# Inputs (test override): TRAIN_RUN_POLLER — a command that, given a run id,
# prints the CI Gate conclusion (so the poll loop is testable offline).
train_smart_ci_run() {
  local batch="$1"
  local descriptor
  descriptor="$(train_smart_ci_shards "${batch}")"
  train_log "smart-ci shard descriptor: ${descriptor}"

  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_side_effect git push "${TRAIN_REMOTE}" "${batch}:${batch}"
    train_side_effect gh workflow run ci.yml --ref "${batch}"
    echo "DRYRUN"
    return 0
  fi

  # Live: push the branch and dispatch CI.
  git -C "${TRAIN_REPO_ROOT}" push "${TRAIN_REMOTE}" "${batch}:${batch}"
  gh workflow run ci.yml --ref "${batch}"

  # Find the dispatched run id (most recent ci.yml run on this ref).
  local run_id=""
  local i
  for i in $(seq 1 12); do
    run_id="$(gh run list --workflow ci.yml --branch "${batch}" \
      --json databaseId,headBranch,event \
      --jq '[.[] | select(.headBranch=="'"${batch}"'")][0].databaseId' 2>/dev/null || echo "")"
    [[ -n "${run_id}" && "${run_id}" != "null" ]] && break
    sleep 10
  done
  if [[ -z "${run_id}" || "${run_id}" == "null" ]]; then
    train_err "could not locate dispatched CI run for ${batch}"
    echo "FAILURE"; return 0
  fi
  train_log "smart-ci run id: ${run_id}"
  echo "${run_id}" >"${TRAIN_RUN_ID_FILE:-/dev/null}"

  # Poll until the CI Gate job completes.
  while :; do
    local status conclusion
    status="$(gh run view "${run_id}" --json status --jq '.status' 2>/dev/null || echo "")"
    if [[ "${status}" == "completed" ]]; then break; fi
    sleep 30
  done

  conclusion="$(gh run view "${run_id}" --json jobs \
    --jq '[.jobs[] | select(.name=="CI Gate")][0].conclusion // "missing"' 2>/dev/null || echo "missing")"
  train_log "CI Gate conclusion: ${conclusion}"
  # Normalize to upper-case workflow vocabulary.
  printf '%s\n' "${conclusion}" | tr '[:lower:]' '[:upper:]'
}

# train_failing_jobs <run-id>: emit the names of the failing jobs (live).
train_failing_jobs() {
  local run_id="$1"
  gh run view "${run_id}" --json jobs \
    --jq '.jobs[] | select(.conclusion=="failure") | .name'
}
