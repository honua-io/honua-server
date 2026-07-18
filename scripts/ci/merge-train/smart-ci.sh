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

  # Live: push the branch and dispatch CI. This function's stdout IS the gate
  # value the caller captures (`gate="$(train_smart_ci_run ...)"`), and newer
  # gh releases print the created run's URL on stdout — route it to stderr or
  # the gate becomes "<url>\nSUCCESS", which matches neither SUCCESS nor
  # FAILURE and fail-closes every live batch.
  git -C "${TRAIN_REPO_ROOT}" push "${TRAIN_REMOTE}" "${batch}:${batch}"
  gh workflow run ci.yml --ref "${batch}" 1>&2

  local discovery_timeout="${TRAIN_SMART_CI_DISCOVERY_TIMEOUT_SECONDS:-300}"
  local discovery_interval="${TRAIN_SMART_CI_DISCOVERY_POLL_SECONDS:-10}"
  local poll_timeout="${TRAIN_SMART_CI_POLL_TIMEOUT_SECONDS:-1800}"
  local poll_interval="${TRAIN_SMART_CI_POLL_SECONDS:-30}"
  local pre_dispatch_runs now timeout_at

  # Prefer the newest run generated after the dispatch. If no NEW run appears on
  # this branch, CI did not start (or is severely throttled). This is exactly
  # the failure mode we must fail closed for in live mode (missing PAT or
  # workflow dispatch auth issues).
  pre_dispatch_runs="$(
    gh run list --workflow ci.yml --branch "${batch}" \
      --json databaseId,headBranch \
      --jq '.[] | select(.headBranch=="'"${batch}"'") | .databaseId' \
      2>/dev/null || true
  )"

  # Find the dispatched run id (most recent ci.yml run on this ref).
  local run_id=""
  timeout_at=$(( $(train_now) + discovery_timeout ))
  while :; do
    run_id="$(gh run list --workflow ci.yml --branch "${batch}" \
      --json databaseId,headBranch,event \
      --jq '[.[] | select(.headBranch=="'"${batch}"'")][0].databaseId' 2>/dev/null || echo "")"
    if [[ -n "${run_id}" && "${run_id}" != "null" ]]; then
      if ! grep -qx "${run_id}" <<<"${pre_dispatch_runs}" ; then
        break
      fi
    fi
    now="$(train_now)"
    [[ "${now}" -ge "${timeout_at}" ]] && break
    sleep "${discovery_interval}"
  done

  if [[ -z "${run_id}" || "${run_id}" == "null" ]]; then
    train_err "could not locate a newly dispatched ci.yml run for ${batch} within ${discovery_timeout}s; live mode requires MERGE_TRAIN_TOKEN for batch-branch CI dispatch"
    echo "FAILURE"; return 0
  fi
  train_log "smart-ci run id: ${run_id}"
  echo "${run_id}" >"${TRAIN_RUN_ID_FILE:-/dev/null}"

  # Poll until the CI Gate job completes.
  timeout_at=$(( $(train_now) + poll_timeout ))
  while :; do
    local status conclusion
    status="$(gh run view "${run_id}" --json status --jq '.status' 2>/dev/null || echo "")"
    if [[ "${status}" == "completed" ]]; then break; fi
    now="$(train_now)"
    if [[ "${now}" -ge "${timeout_at}" ]]; then
      train_err "CI run ${run_id} for ${batch} did not finish within ${poll_timeout}s"
      echo "FAILURE"; return 0
    fi
    sleep "${poll_interval}"
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

# train_ci_jobs <run-id>: emit every job as "<conclusion>\t<name>". Tests may
# override the reader so cancellation/incomplete-run safety is exercised
# offline without GitHub access.
train_ci_jobs() {
  local run_id="$1"
  if [[ -n "${TRAIN_CI_JOBS_READER:-}" ]]; then
    "${TRAIN_CI_JOBS_READER}" "${run_id}"
    return
  fi
  gh run view "${run_id}" --json jobs \
    --jq '.jobs[] | [.conclusion, .name] | @tsv'
}

# train_ci_jobs_are_terminal <run-id>: success/failure/skipped are the only
# conclusions safe to classify. Any cancellation or incomplete conclusion
# makes the entire run unusable as merge evidence.
train_ci_jobs_are_terminal() {
  local run_id="$1" rows conclusion name saw_gate=0
  rows="$(train_ci_jobs "${run_id}")" || return 1
  [[ -n "${rows//[$'\n'$'\t' ]/}" ]] || return 1

  while IFS=$'\t' read -r conclusion name; do
    [[ -n "${name}" ]] || return 1
    conclusion="$(tr '[:upper:]' '[:lower:]' <<<"${conclusion}")"
    case "${conclusion}" in
      success|failure|skipped) ;;
      *) return 1 ;;
    esac
    [[ "${name}" == "CI Gate" ]] && saw_gate=1
  done <<<"${rows}"

  [[ "${saw_gate}" == "1" ]]
}

# train_expected_shards_are_classifiable <run-id> <shard-descriptor>
# Every shard selected by the router must exist exactly once and conclude
# SUCCESS or FAILURE. A missing or skipped selected shard is not evidence.
train_expected_shards_are_classifiable() {
  local run_id="$1" descriptor="$2" rows shard expected matches conclusion
  jq -e '.shards | type == "array" and length > 0' <<<"${descriptor}" >/dev/null 2>&1 || return 1
  rows="$(train_ci_jobs "${run_id}")" || return 1

  while IFS= read -r shard; do
    [[ -n "${shard}" ]] || return 1
    expected="Server Tests (${shard})"
    matches="$(awk -F '\t' -v expected="${expected}" '$2 == expected { print $1 }' <<<"${rows}")"
    [[ "$(sed '/^$/d' <<<"${matches}" | wc -l | tr -d ' ')" == "1" ]] || return 1
    conclusion="$(tr '[:upper:]' '[:lower:]' <<<"${matches}")"
    [[ "${conclusion}" == "success" || "${conclusion}" == "failure" ]] || return 1
  done < <(jq -r '.shards[]' <<<"${descriptor}")
}

# train_nonblocking_failures_are_safe <run-id> <shard-descriptor>
#
# The optimistic non-blocking bypass is valid only when GitHub reports the
# failed CI Gate, every non-blocking exception is an ordinary FAILURE, and at
# least one blocking job explicitly succeeded. CANCELLED, missing, pending,
# timed-out, neutral, or otherwise incomplete jobs always fail closed.
train_nonblocking_failures_are_safe() {
  local run_id="$1" descriptor="$2" rows conclusion name
  train_ci_jobs_are_terminal "${run_id}" || return 1
  train_expected_shards_are_classifiable "${run_id}" "${descriptor}" || return 1
  rows="$(train_ci_jobs "${run_id}")" || return 1
  [[ -n "${rows//[$'\n'$'\t' ]/}" ]] || return 1

  local saw_failed_gate=0 saw_blocking_success=0
  while IFS=$'\t' read -r conclusion name; do
    [[ -n "${name}" ]] || return 1
    conclusion="$(tr '[:upper:]' '[:lower:]' <<<"${conclusion}")"
    case "${conclusion}" in
      success)
        if ! grep -Fxiq -- "${name}" <<<"${TRAIN_NONBLOCKING_JOBS}"; then
          saw_blocking_success=1
        fi
        ;;
      skipped)
        ;;
      failure)
        grep -Fxiq -- "${name}" <<<"${TRAIN_NONBLOCKING_JOBS}" || return 1
        [[ "${name}" == "CI Gate" ]] && saw_failed_gate=1
        ;;
      *)
        return 1
        ;;
    esac
  done <<<"${rows}"

  [[ "${saw_failed_gate}" == "1" && "${saw_blocking_success}" == "1" ]]
}
