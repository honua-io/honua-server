#!/usr/bin/env bash
HONUA_TAB="$(printf '\tX')"; HONUA_TAB="${HONUA_TAB%X}"
# Step 5.5 (NEW): pre-existing-failure filter - deterministic, no AI.
#
# Before the train blocks/escalates a failed batch, fetch the LATEST trunk CI
# run's failing causes and SUBTRACT any equivalent batch failures. Those are
# pre-existing failures - NOT the batch's fault. If after subtraction there are
# ZERO batch-introduced failures, the batch is treated as PASS and lands.
#
# This is the deterministic floor of the roll-forward design: a batch is never
# blocked, escalated, or AI-patched for a failure that trunk already carries
# (e.g. an already-red STAC api-validator conformance test). No Bedrock, no
# heuristics - a pure set subtraction over CI's own reported failure causes.
#
# Cause signatures are job-scoped and built from the most stable evidence in CI
# logs, in priority order:
#   * a run-scoped shard-terminal signature when the WHOLE job log carries any
#     HONUA_SHARD_{CAPACITY_EXHAUSTED,KILLED,HANG_SUSPECTED} marker: the shard
#     never finished executing its tests, so it has no comparable cause and can
#     never be subtracted (#3213).
#   * failing test FQNs from the existing surgical retry parser.
#   * normalized compiler/error/assertion/exception/format-drift lines.
#   * an opaque run-scoped fallback when no cause can be extracted, which is
#     intentionally not shared across runs.
# A batch failure survives the filter if any of its job-scoped signatures is not
# already present on trunk. The orchestrator still receives surviving JOB names
# so classify/attribute/fix behavior remains unchanged.

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

# train_run_failing_job_records <run-id>: emit failing job records as
# "<job-id><TAB><job-name>", one per line. The job id is empty for fixture
# overrides that only provide names. Test override:
# TRAIN_FAILING_JOB_RECORDS_FOR_RUN <cmd> is invoked with the run id and must
# print the same tab-separated records.
train_run_failing_job_records() {
  local run_id="$1"
  if [[ -n "${TRAIN_FAILING_JOB_RECORDS_FOR_RUN:-}" ]]; then
    "${TRAIN_FAILING_JOB_RECORDS_FOR_RUN}" "${run_id}" | sed '/^$/d' | sort -u
    return 0
  fi
  if [[ -n "${TRAIN_FAILING_JOBS_FOR_RUN:-}" ]]; then
    train_run_failing_job_names "${run_id}" | awk '{ print "\t" $0 }'
    return 0
  fi
  [[ -z "${run_id}" ]] && return 0
  gh run view "${run_id}" --json jobs \
    --jq '.jobs[]
          | select(.conclusion=="failure")
          | ((.databaseId // "") | tostring) + "\t" + .name' 2>/dev/null \
    | sed '/^$/d' | sort -u || true
}

# train_run_job_log <run-id> <job-name> [job-id]: emit the log for one failed
# job. Live path prefers the bounded per-job log so the signature extractor sees
# real Build & Format / shard error lines even when whole-run logs are huge.
# Test override: TRAIN_JOB_LOG_FOR_RUN <cmd> is called with
# (run id, job name, job id).
train_run_job_log() {
  local run_id="$1" job="$2" job_id="${3:-}"
  if [[ -n "${TRAIN_JOB_LOG_FOR_RUN:-}" ]]; then
    "${TRAIN_JOB_LOG_FOR_RUN}" "${run_id}" "${job}" "${job_id}"
    return 0
  fi
  if [[ -n "${TRAIN_RUN_LOG_FOR:-}" ]]; then
    "${TRAIN_RUN_LOG_FOR}" "${run_id}"
    return 0
  fi
  [[ -z "${run_id}" ]] && return 0
  if [[ -n "${job_id}" ]]; then
    # Delegate to lib.sh's reader, which tries the exact job-log REST resource
    # before `gh run view --job --log`. GitHub can expose a terminal job and its
    # REST log while the parent run's aggregate archive is still unavailable
    # (lib.sh:118-121); reading CLI-only saw '' in that window and downgraded a
    # real failure cause to an opaque, never-subtractable signature (#3213).
    train_read_job_log "${job_id}" 2>/dev/null || true
  else
    gh run view "${run_id}" --log-failed 2>/dev/null || true
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

# train_extract_failure_signatures <job-name> <log-text>: emit stable cause
# signatures from one job log, one per line. These are compared only within the
# same job name by train_run_failure_signatures.
train_extract_failure_signatures() {
  local _job="$1" text="$2"
  {
    if declare -F train_parse_failed_test_names >/dev/null 2>&1; then
      train_parse_failed_test_names "${text}" | sed 's/^/test:/'
    fi
    printf '%s\n' "${text}" | awk '
      function trim(s) {
        sub(/^[[:space:]]+/, "", s)
        sub(/[[:space:]]+$/, "", s)
        return s
      }
      function normalize(line, n, parts) {
        gsub(/\r/, "", line)
        gsub(/\033\[[0-9;?]*[ -/]*[@-~]/, "", line)
        n = split(line, parts, "\t")
        if (n > 1) line = parts[n]
        sub(/^[0-9]{4}-[0-9]{2}-[0-9]{2}T[^[:space:]]+[[:space:]]+/, "", line)
        gsub(/##\[error\]/, "", line)
        gsub(/\\/, "/", line)
        gsub(/[A-Za-z]:\/[^[:space:]]*honua-server\//, "", line)
        gsub(/\/[^[:space:]]*honua-server\//, "", line)
        gsub(/\([0-9]+,[0-9]+\)/, "", line)
        gsub(/:[0-9]+:[0-9]+/, ":", line)
        gsub(/[0-9]+ ms/, "<duration>", line)
        gsub(/[[:space:]]+/, " ", line)
        return trim(line)
      }
      {
        line = normalize($0)
        lower = tolower(line)
        if (line == "") next
        # A PASSING or SKIPPED test line is not a failure cause, but its NAME
        # routinely collides with these heuristics: `...ReturnsFailed...`,
        # `...IsCaseAndWhitespaceInsensitive`, `...Expected: 200`. On a shard
        # log those lines vastly outnumber real ones, so they used to fill the
        # bounded window with green noise and starve it of the actual cause
        # (#3213 — verified on job 95149717187, whose sampled window was almost
        # entirely `Passed ...` lines).
        if (line ~ /^(Passed|Skipped)[[:space:]]/) next
        # Structured (Serilog/JSON) log records carry a per-run `@t` timestamp,
        # so they never match across runs and only consume the window.
        if (line ~ /"@t"[[:space:]]*:/) next
        prefix = ""
        if (line ~ /error[[:space:]]+(CS|CA|NETSDK|NU|MSB|BC|FS)[0-9]+[: ]/) {
          prefix = "compiler"
        } else if (lower ~ /formatted code file|format verification failed|format.*(failed|would)|error whitespace|whitespace formatting/) {
          prefix = "format"
        } else if (lower ~ /assert\.[a-z]+|assertion failed|expected:|actual:|xunit\.sdk\./) {
          prefix = "assert"
        } else if (line ~ /[A-Za-z0-9_.]+Exception(:|[[:space:]])/) {
          prefix = "exception"
        } else if (lower ~ /(^| )error(:| )|(^| )failed(:| )|(^| )fatal(:| )/) {
          prefix = "error"
        } else {
          next
        }
        signature = prefix ":" line
        # Dedupe BEFORE the cap. A repeated line (the same connection error
        # thousands of times) is one cause, and letting it consume 40 slots
        # hid every other cause in the job.
        if (signature in seen) next
        seen[signature] = 1
        # Bounded to the first 40 DISTINCT causes, but WITHOUT `exit`: exiting
        # mid-pipe SIGPIPEs the upstream printf, and under `set -o pipefail`
        # that turned a ~47k-line shard log into a nondeterministic 141 from the
        # whole filter (#3213). Keep draining stdin once the cap is reached.
        if (count < 40) {
          print signature
          count += 1
        }
      }
    '
  } | sed '/^$/d' | sort -u
}

# train_emit_job_failure_signatures <run-id> <job-name> [job-id]: emit
# "<job-name><TAB><signature>" records for one failed job. The opaque fallback
# is run-scoped so an unparseable failure never incorrectly matches a different
# run just because the job name is the same.
train_emit_job_failure_signatures() {
  local run_id="$1" job="$2" job_id="${3:-}" log sigs sig terminal_marker
  [[ -z "${job}" ]] && return 0
  log="$(train_run_job_log "${run_id}" "${job}" "${job_id}")"
  # #3213: a shard that could not finish executing its tests produced NO
  # comparable failure cause, so it must never cancel against trunk's latest
  # run no matter how similar the rest of its log looks. All three
  # shard-terminal markers are emitted as the shard's LAST error, far outside
  # the bounded window train_extract_failure_signatures samples, so scan the
  # WHOLE log for them and emit a single run-scoped signature instead. The
  # train classifies these before the filter ever runs (train.sh ordering
  # guard); this keeps the filter correct in isolation too.
  terminal_marker=""
  if train_log_is_capacity_exhaustion "${log}"; then
    terminal_marker=capacity-exhausted
  elif train_log_is_shard_killed "${log}"; then
    terminal_marker=shard-killed
  elif train_log_is_shard_hang "${log}"; then
    terminal_marker=shard-hang
  fi
  if [[ -n "${terminal_marker}" ]]; then
    printf '%s\t%s:%s:%s\n' "${job}" "${terminal_marker}" "${run_id:-unknown-run}" "${job}"
    return 0
  fi
  sigs="$(train_extract_failure_signatures "${job}" "${log}")"
  if [[ -z "$(printf '%s' "${sigs}" | sed '/^$/d')" ]]; then
    printf '%s\topaque:%s:%s\n' "${job}" "${run_id:-unknown-run}" "${job}"
    return 0
  fi
  while IFS= read -r sig; do
    [[ -z "${sig}" ]] && continue
    printf '%s\t%s\n' "${job}" "${sig}"
  done <<<"${sigs}"
}

# train_run_failure_signatures <run-id> [job-names-newline]: emit
# "<job-name><TAB><signature>" records for the supplied jobs. When job names are
# omitted, the failed jobs are discovered from the run.
train_run_failure_signatures() {
  local run_id="$1" job_names="${2:-}" job record job_id record_job records matched
  if [[ -n "$(printf '%s' "${job_names}" | sed '/^$/d')" ]]; then
    records="$(train_run_failing_job_records "${run_id}")"
    while IFS= read -r job; do
      [[ -z "${job}" ]] && continue
      matched=0
      while IFS= read -r record; do
        [[ -z "${record}" ]] && continue
        job_id="${record%%${HONUA_TAB}*}"
        record_job="${record#*${HONUA_TAB}}"
        [[ "${record_job}" == "${record}" ]] && { record_job="${record}"; job_id=""; }
        if [[ "${record_job}" == "${job}" ]]; then
          matched=1
          train_emit_job_failure_signatures "${run_id}" "${record_job}" "${job_id}"
        fi
      done <<<"${records}"
      if [[ "${matched}" -eq 0 ]]; then
        train_emit_job_failure_signatures "${run_id}" "${job}"
      fi
    done <<<"$(printf '%s\n' "${job_names}" | sed '/^$/d' | sort -u)"
    return 0
  fi

  while IFS= read -r record; do
    [[ -z "${record}" ]] && continue
    job_id="${record%%${HONUA_TAB}*}"
    job="${record#*${HONUA_TAB}}"
    [[ "${job}" == "${record}" ]] && { job="${record}"; job_id=""; }
    train_emit_job_failure_signatures "${run_id}" "${job}" "${job_id}"
  done <<<"$(train_run_failing_job_records "${run_id}")" | sort -u
}

# train_trunk_preexisting_signatures: the job-scoped failure signatures on
# trunk's latest CI run. READ-ONLY; emits "<job><TAB><signature>" records.
train_trunk_preexisting_signatures() {
  local trunk_run; trunk_run="$(train_trunk_latest_ci_run_id)"
  [[ -z "${trunk_run}" ]] && return 0
  train_run_failure_signatures "${trunk_run}"
}

# train_batch_introduced_jobs <batch-run-id> <batch-failing-jobs-newline>:
# subtract trunk's pre-existing job-scoped failure signatures from the batch's
# signatures. Emits the batch-INTRODUCED failing job names (one per line). Empty
# output => every batch failure has an equivalent failure cause on trunk.
train_batch_introduced_jobs() {
  local batch_run_id="$1" batch_failing="$2"
  local trunk_signatures batch_signatures introduced_signatures
  trunk_signatures="$(train_trunk_preexisting_signatures)"
  batch_signatures="$(train_run_failure_signatures "${batch_run_id}" "${batch_failing}")"
  introduced_signatures="$(train_subtract_lines "${trunk_signatures}" "${batch_signatures}")"
  printf '%s\n' "${introduced_signatures}" | sed '/^$/d' | awk -F '\t' '{ print $1 }' | sort -u
}

# train_preexisting_filter <run-id> <batch-failing-jobs-newline>:
# The orchestrator entrypoint. Given the batch run and failing JOB names, compute
# the batch-introduced subset by comparing job-scoped failure signatures against
# trunk. Emits the surviving batch-introduced job names on stdout (one per line).
# Return code:
#   0  => at least one batch-introduced failure survives (caller must act).
#   11 => ZERO batch-introduced failures (ALL pre-existing on trunk) => land.
# READ-ONLY (no side effects); safe in dry-run and live.
train_preexisting_filter() {
  local run_id="$1" batch_failing="$2"
  local introduced
  introduced="$(train_batch_introduced_jobs "${run_id}" "${batch_failing}")"
  if [[ -z "$(printf '%s' "${introduced}" | sed '/^$/d')" ]]; then
    train_decision "pre-existing filter: every batch failure cause also fails on trunk; batch introduced NO new failures (treat as PASS)"
    return 11
  fi
  printf '%s\n' "${introduced}" | sed '/^$/d'
  return 0
}
