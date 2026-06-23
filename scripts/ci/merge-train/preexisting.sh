#!/usr/bin/env bash
# Step 5.5 (NEW): pre-existing-failure filter — deterministic, no AI.
#
# Before the train blocks/escalates a failed batch, fetch the LATEST trunk CI
# run's failing jobs (and, where available, failing test FQNs) and SUBTRACT any
# that ALSO fail on trunk. Those are pre-existing failures — NOT the batch's
# fault. If after subtraction there are ZERO batch-introduced failures, the batch
# is treated as PASS and lands.
#
# This is the deterministic floor of the roll-forward design: a batch is never
# blocked, escalated, or AI-patched for a failure that trunk already carries
# (e.g. an already-red STAC api-validator conformance test). No Bedrock, no
# heuristics — a pure set subtraction over CI's own reported failures.
#
# Two granularities, both subtractive, both READ-ONLY (run in dry-run and live):
#   * job-level:  failing JOB names present on trunk are pre-existing.
#   * test-level: failing test FQNs present on trunk are pre-existing.
# A batch failure survives the filter only if it is a job/test NOT already
# failing on trunk. The orchestrator uses the surviving set to decide land vs
# classify/attribute/fix.

# train_trunk_latest_ci_run_id: the databaseId of the most recent COMPLETED CI
# run on the base branch (trunk). Empty if none/ungettable. READ-ONLY.
# Test override: TRAIN_TRUNK_RUN_ID forces a fixed id (offline fixtures).
train_trunk_latest_ci_run_id() {
  if [[ -n "${TRAIN_TRUNK_RUN_ID:-}" ]]; then
    printf '%s' "${TRAIN_TRUNK_RUN_ID}"
    return 0
  fi
  gh run list --workflow ci.yml --branch "${TRAIN_BASE_BRANCH}" \
    --status completed --limit 1 \
    --json databaseId --jq '.[0].databaseId // empty' 2>/dev/null || echo ""
}

# train_run_failing_job_names <run-id>: emit the failing JOB names of a run, one
# per line (sorted-unique). Live path uses `gh run view --json jobs`. Test
# override: TRAIN_FAILING_JOBS_FOR_RUN <cmd> is invoked with the run id and must
# print the same newline-separated job names.
train_run_failing_job_names() {
  local run_id="$1"
  if [[ -n "${TRAIN_FAILING_JOBS_FOR_RUN:-}" ]]; then
    "${TRAIN_FAILING_JOBS_FOR_RUN}" "${run_id}" | sed '/^$/d' | sort -u
    return 0
  fi
  [[ -z "${run_id}" ]] && return 0
  gh run view "${run_id}" --json jobs \
    --jq '.jobs[] | select(.conclusion=="failure") | .name' 2>/dev/null \
    | sed '/^$/d' | sort -u || true
}

# train_trunk_preexisting_jobs: the set of failing JOB names on trunk's latest
# CI run (the pre-existing job-level failures). READ-ONLY; emits one per line.
train_trunk_preexisting_jobs() {
  local trunk_run; trunk_run="$(train_trunk_latest_ci_run_id)"
  [[ -z "${trunk_run}" ]] && return 0
  train_run_failing_job_names "${trunk_run}"
}

# train_trunk_preexisting_tests: the set of failing test FQNs on trunk's latest
# CI run (the pre-existing test-level failures). READ-ONLY; emits one per line.
# Depends on train_failed_test_names (surgical.sh) for FQN extraction.
train_trunk_preexisting_tests() {
  local trunk_run; trunk_run="$(train_trunk_latest_ci_run_id)"
  [[ -z "${trunk_run}" ]] && return 0
  if declare -F train_failed_test_names >/dev/null 2>&1; then
    train_failed_test_names "${trunk_run}"
  fi
}

# train_subtract_lines <baseline-stdin-via-$1> <candidate-stdin-via-$2>:
# emit the lines in CANDIDATE that are NOT in BASELINE (set difference
# candidate - baseline), sorted-unique. Both args are multi-line strings. Used to
# strip pre-existing failures from the batch's failures.
train_subtract_lines() {
  local baseline="$1" candidate="$2"
  local base_sorted cand_sorted
  base_sorted="$(printf '%s\n' "${baseline}" | sed '/^$/d' | sort -u)"
  cand_sorted="$(printf '%s\n' "${candidate}" | sed '/^$/d' | sort -u)"
  # comm -23: lines only in candidate (file1) not in baseline (file2).
  comm -23 <(printf '%s\n' "${cand_sorted}") <(printf '%s\n' "${base_sorted}")
}

# train_batch_introduced_jobs <batch-failing-jobs-newline>: subtract trunk's
# pre-existing failing jobs from the batch's failing jobs. Emits the
# batch-INTRODUCED failing job names (one per line). Empty output => every batch
# failure is pre-existing on trunk (the batch introduced no new job failure).
train_batch_introduced_jobs() {
  local batch_failing="$1"
  local trunk_jobs; trunk_jobs="$(train_trunk_preexisting_jobs)"
  train_subtract_lines "${trunk_jobs}" "${batch_failing}"
}

# train_batch_introduced_tests <batch-failing-tests-newline>: subtract trunk's
# pre-existing failing test FQNs from the batch's failing test FQNs. Emits the
# batch-INTRODUCED failing test FQNs (one per line).
train_batch_introduced_tests() {
  local batch_failing="$1"
  local trunk_tests; trunk_tests="$(train_trunk_preexisting_tests)"
  train_subtract_lines "${trunk_tests}" "${batch_failing}"
}

# train_preexisting_filter <run-id> <batch-failing-jobs-newline>:
# The orchestrator entrypoint. Given the batch's failing JOB names, compute the
# batch-introduced subset (job-level minus trunk). Emits the surviving
# batch-introduced job names on stdout (one per line). Return code:
#   0  => at least one batch-introduced failure survives (caller must act).
#   11 => ZERO batch-introduced failures (ALL pre-existing on trunk) => land.
# READ-ONLY (no side effects); safe in dry-run and live.
train_preexisting_filter() {
  local _run_id="$1" batch_failing="$2"
  local introduced
  introduced="$(train_batch_introduced_jobs "${batch_failing}")"
  if [[ -z "$(printf '%s' "${introduced}" | sed '/^$/d')" ]]; then
    train_decision "pre-existing filter: every batch failure also fails on trunk; batch introduced NO new failures (treat as PASS)"
    return 11
  fi
  printf '%s\n' "${introduced}" | sed '/^$/d'
  return 0
}
