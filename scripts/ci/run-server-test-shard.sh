#!/usr/bin/env bash
# Run one Honua.Server.Tests shard with heartbeat, timeout, and timing output.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

shard_name="${HONUA_SERVER_TEST_SHARD_NAME:-}"
filter_expression="${HONUA_SERVER_TEST_FILTER:-}"
log_name="${HONUA_SERVER_TEST_LOG_NAME:-}"

if [[ -z "${shard_name}" || -z "${filter_expression}" || -z "${log_name}" ]]; then
  echo "HONUA_SERVER_TEST_SHARD_NAME, HONUA_SERVER_TEST_FILTER, and HONUA_SERVER_TEST_LOG_NAME are required." >&2
  exit 2
fi

configuration="${HONUA_SERVER_TEST_CONFIGURATION:-Release}"
results_dir="${HONUA_SERVER_TEST_RESULTS_DIR:-./tests/TestResults}"
# Phase 2 / ADR-0042: shards may target a per-protocol test project; default
# remains the Honua.Server.Tests monolith when the env var is unset so legacy
# shards keep working unchanged.
test_csproj="${HONUA_SERVER_TEST_CSPROJ:-tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj}"
max_cpu_count="${HONUA_SERVER_TEST_MAX_CPU_COUNT:-}"
timeout_minutes="${HONUA_SERVER_TEST_TIMEOUT_MINUTES:-}"
heartbeat_seconds="${HONUA_SERVER_TEST_HEARTBEAT_SECONDS:-30}"
heartbeat_tail_lines="${HONUA_SERVER_TEST_HEARTBEAT_TAIL_LINES:-40}"
console_verbosity="${HONUA_SERVER_TEST_CONSOLE_VERBOSITY:-normal}"
exclude_slow="${HONUA_SERVER_TEST_EXCLUDE_SLOW:-true}"
exclude_fast="${HONUA_SERVER_TEST_EXCLUDE_FAST:-true}"
# #3054 headroom monitoring. `poll_seconds` is how often the supervisor samples
# the child and the log; it is deliberately much smaller than the heartbeat
# interval so `duration_seconds` is the child's real runtime instead of being
# rounded up to the next heartbeat (the old loop slept a full heartbeat before
# noticing the child had exited, which inflated every measurement by up to
# `heartbeat_seconds` and made the headroom ratio unusable). 5s keeps the
# measurement error under half a percent of the smallest configured budget while
# waking the supervisor 6x less often than a 1s poll would on a shared runner.
poll_seconds="${HONUA_SERVER_TEST_POLL_SECONDS:-5}"
# Warn when a completed shard consumed at least this fraction of its inner
# timeout. A shard above the line still passed, but it has no room left for the
# next test the capability gate forces into it.
headroom_warn_ratio="${HONUA_SERVER_TEST_HEADROOM_WARN_RATIO:-0.80}"
# A timed-out shard whose log had not grown for at least this many seconds is
# reported as a suspected hang; one that was still emitting output when the cap
# fired is reported as capacity exhaustion. Honua integration shards stream host
# logs continuously, so a multi-minute silence is a genuine stall signal.
stall_seconds="${HONUA_SERVER_TEST_STALL_SECONDS:-300}"

mkdir -p "${results_dir}"

log_file="${results_dir%/}/${log_name}.log"
trx_file="${log_name}.trx"
timing_file="${results_dir%/}/${log_name}.timing.json"

filter="${filter_expression}"
if [[ "${exclude_slow}" == "true" ]]; then
  filter="(${filter_expression})&Tier!=Slow"
fi
if [[ "${exclude_fast}" == "true" ]]; then
  # #2943: Fast tests run once per CI run in dotnet-foundation-tests, which
  # (as of #2943) covers Honua.Server.Tests AND the 8 protocol-split projects
  # (Honua.Protocols.*.Tests, Honua.Ai.Tests) via dedicated foundation-job
  # steps — so this exclusion is safe for every shard's csproj, not only the
  # legacy Honua.Server.Tests.csproj default.
  filter="(${filter})&Tier!=Fast"
fi

extra_args=()
if [[ -n "${max_cpu_count}" ]]; then
  extra_args+=(-- "RunConfiguration.MaxCpuCount=${max_cpu_count}")
fi

test_command=(
  dotnet test "${test_csproj}"
  --no-build
  --no-restore
  --configuration "${configuration}"
  --filter "${filter}"
  --logger "trx;LogFileName=${trx_file}"
  --logger "console;verbosity=${console_verbosity}"
  --results-directory "${results_dir}"
  "${extra_args[@]}"
)

run_command=("${test_command[@]}")
timeout_command=""
if [[ -n "${timeout_minutes}" ]]; then
  if [[ -n "${HONUA_SERVER_TEST_TIMEOUT_COMMAND:-}" ]]; then
    timeout_command="${HONUA_SERVER_TEST_TIMEOUT_COMMAND}"
  elif command -v timeout >/dev/null 2>&1; then
    timeout_command="timeout"
  elif command -v gtimeout >/dev/null 2>&1; then
    timeout_command="gtimeout"
  else
    echo "::warning::No GNU timeout command found; running shard '${shard_name}' without an inner timeout." >&2
  fi

  if [[ -n "${timeout_command}" ]]; then
    run_command=("${timeout_command}" --kill-after=30s "${timeout_minutes}m" "${test_command[@]}")
  fi
fi

started_at="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
start_epoch="$(date +%s)"

{
  echo "Shard: ${shard_name}"
  echo "Log name: ${log_name}"
  echo "Started: ${started_at}"
  echo "Configuration: ${configuration}"
  echo "Console verbosity: ${console_verbosity}"
  echo "Timeout minutes: ${timeout_minutes:-none}"
  echo "Timeout command: ${timeout_command:-none}"
  echo "Max CPU count: ${max_cpu_count:-default}"
  echo "Filter: ${filter}"
  echo ""
} > "${log_file}"

log_size() {
  local size=""
  size="$(wc -c < "${log_file}" 2>/dev/null | tr -d '[:space:]')" || size=""
  printf '%s' "${size:-0}"
}

"${run_command[@]}" >> "${log_file}" 2>&1 &
test_pid=$!
heartbeat_count=0
next_heartbeat_epoch="${start_epoch}"
last_progress_epoch="${start_epoch}"
last_log_size="$(log_size)"

while kill -0 "${test_pid}" 2>/dev/null; do
  now_epoch="$(date +%s)"

  # Progress = the shard's own stdout growing. Honua shards stream host/test
  # output continuously, so this is what separates "still working, ran out of
  # budget" from "wedged" when the inner timeout fires.
  current_log_size="$(log_size)"
  if [[ "${current_log_size}" != "${last_log_size}" ]]; then
    last_log_size="${current_log_size}"
    last_progress_epoch="${now_epoch}"
  fi

  if (( now_epoch >= next_heartbeat_epoch )); then
    elapsed_seconds=$((now_epoch - start_epoch))
    echo "[${log_name}-heartbeat] $(date -u +'%Y-%m-%dT%H:%M:%SZ') shard=\"${shard_name}\" elapsed=${elapsed_seconds}s timeout=${timeout_minutes:-none}m idle=$((now_epoch - last_progress_epoch))s"
    next_heartbeat_epoch=$((now_epoch + heartbeat_seconds))

    heartbeat_count=$((heartbeat_count + 1))
    if (( heartbeat_count % 4 == 0 )); then
      echo "[${log_name}-tail] last ${heartbeat_tail_lines} log lines"
      tail -n "${heartbeat_tail_lines}" "${log_file}" || true
      echo "[${log_name}-tail-end]"
    fi
  fi

  sleep "${poll_seconds}"
done

set +e
wait "${test_pid}"
test_exit_code=$?
set -e

completed_at="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
completed_epoch="$(date +%s)"
duration_seconds=$((completed_epoch - start_epoch))
timed_out="false"
status="failed"

if [[ "${test_exit_code}" -eq 0 ]]; then
  status="passed"
elif [[ -n "${timeout_command}" && "${test_exit_code}" -eq 124 ]]; then
  status="timed_out"
  timed_out="true"
fi

# ---------------------------------------------------------------------------
# #3054 headroom classification.
#
# `test_timeout_minutes` exists to bound a genuine hang, not to bound normal
# growth — but nothing used to watch how much of that budget a healthy shard
# was already consuming, so a shard could sit at ~100% for weeks and then fail
# whichever PR happened to add the test that tipped it over. The capability
# completeness gate keeps pushing proving tests INTO shards, so the pressure is
# structural rather than accidental.
#
# capacity_status is the distinct, actionable signal:
#   unbounded          - no inner timeout configured; nothing to measure.
#   not_assessed       - ran under a budget but neither passed nor timed out; a
#                        failing run may abort early or run long on retries, so
#                        its duration is not a valid capacity sample.
#   ok                 - PASSED under the warn ratio.
#   low_headroom       - PASSED, but consumed >= warn ratio of its budget.
#   capacity_exhausted - hit the cap while still producing output.
#   hang_suspected     - hit the cap after going silent for >= stall_seconds.
# ---------------------------------------------------------------------------
idle_seconds_at_exit=$((completed_epoch - last_progress_epoch))
timeout_seconds=""
headroom_ratio=""
headroom_percent=""
capacity_status="unbounded"

if [[ -n "${timeout_minutes}" && -n "${timeout_command}" ]]; then
  timeout_seconds="$(awk -v m="${timeout_minutes}" 'BEGIN { printf "%.0f", m * 60 }')"
  if [[ "${timeout_seconds}" -gt 0 ]]; then
    headroom_ratio="$(awk -v d="${duration_seconds}" -v t="${timeout_seconds}" 'BEGIN { printf "%.4f", d / t }')"
    headroom_percent="$(awk -v r="${headroom_ratio}" 'BEGIN { printf "%.1f", r * 100 }')"
    if [[ "${timed_out}" == "true" ]]; then
      if (( idle_seconds_at_exit >= stall_seconds )); then
        capacity_status="hang_suspected"
      else
        capacity_status="capacity_exhausted"
      fi
    elif [[ "${status}" != "passed" ]]; then
      # A shard that failed on a test/infrastructure error did not necessarily
      # run its full workload, so claiming anything about its headroom (in
      # either direction) would be false. Record the ratio, assert nothing.
      capacity_status="not_assessed"
    elif awk -v r="${headroom_ratio}" -v w="${headroom_warn_ratio}" 'BEGIN { exit !(r >= w) }'; then
      capacity_status="low_headroom"
    else
      capacity_status="ok"
    fi
  fi
fi

if command -v jq >/dev/null 2>&1; then
  jq -nc \
    --arg shard "${shard_name}" \
    --arg log_name "${log_name}" \
    --arg status "${status}" \
    --arg exit_code "${test_exit_code}" \
    --arg started_at "${started_at}" \
    --arg completed_at "${completed_at}" \
    --arg duration_seconds "${duration_seconds}" \
    --arg timeout_minutes "${timeout_minutes}" \
    --arg timed_out "${timed_out}" \
    --arg filter "${filter}" \
    --arg timeout_seconds "${timeout_seconds}" \
    --arg headroom_ratio "${headroom_ratio}" \
    --arg headroom_warn_ratio "${headroom_warn_ratio}" \
    --arg capacity_status "${capacity_status}" \
    --arg idle_seconds_at_exit "${idle_seconds_at_exit}" \
    --arg stall_seconds "${stall_seconds}" \
    '{
      shard: $shard,
      log_name: $log_name,
      status: $status,
      exit_code: ($exit_code | tonumber),
      started_at: $started_at,
      completed_at: $completed_at,
      duration_seconds: ($duration_seconds | tonumber),
      timeout_minutes: (if $timeout_minutes == "" then null else ($timeout_minutes | tonumber) end),
      timeout_seconds: (if $timeout_seconds == "" then null else ($timeout_seconds | tonumber) end),
      timed_out: ($timed_out == "true"),
      headroom_ratio: (if $headroom_ratio == "" then null else ($headroom_ratio | tonumber) end),
      headroom_warn_ratio: ($headroom_warn_ratio | tonumber),
      capacity_status: $capacity_status,
      idle_seconds_at_exit: ($idle_seconds_at_exit | tonumber),
      stall_seconds: ($stall_seconds | tonumber),
      filter: $filter
    }' > "${timing_file}"
else
  printf '{"shard":"%s","log_name":"%s","status":"%s","exit_code":%s,"started_at":"%s","completed_at":"%s","duration_seconds":%s,"timeout_minutes":%s,"timed_out":%s,"headroom_ratio":%s,"capacity_status":"%s","idle_seconds_at_exit":%s}\n' \
    "${shard_name}" "${log_name}" "${status}" "${test_exit_code}" "${started_at}" "${completed_at}" "${duration_seconds}" \
    "${timeout_minutes:-null}" "${timed_out}" "${headroom_ratio:-null}" "${capacity_status}" "${idle_seconds_at_exit}" > "${timing_file}"
fi

{
  echo ""
  echo "Completed: ${completed_at}"
  echo "Duration seconds: ${duration_seconds}"
  echo "Exit code: ${test_exit_code}"
  echo "Status: ${status}"
  echo "Capacity status: ${capacity_status}"
  echo "Budget used: ${headroom_percent:-n/a}% of ${timeout_minutes:-none}m (warn at $(awk -v w="${headroom_warn_ratio}" 'BEGIN { printf "%.0f", w * 100 }')%)"
  echo "Idle seconds at exit: ${idle_seconds_at_exit}"
  echo "Timing artifact: ${timing_file}"
} >> "${log_file}"

cat "${log_file}"

# The HONUA_SHARD_* tokens are the stable, greppable contract consumed by the
# merge train's timeout classifier (scripts/ci/merge-train/classify-timeout.sh).
# Keep them literal and on the same line as the annotation.
case "${capacity_status}" in
  low_headroom)
    echo "::warning::HONUA_SHARD_LOW_HEADROOM shard='${shard_name}' used ${duration_seconds}s of its ${timeout_minutes}m test budget (${headroom_percent}%, warn at $(awk -v w="${headroom_warn_ratio}" 'BEGIN { printf "%.0f", w * 100 }')%). This shard passed but has almost no room for the next test routed into it — raise test_timeout_minutes/timeout_minutes or rebalance its filter in .github/ci-shards.json."
    ;;
  capacity_exhausted)
    echo "::error::HONUA_SHARD_CAPACITY_EXHAUSTED shard='${shard_name}' hit its ${timeout_minutes}m test budget while still producing output ${idle_seconds_at_exit}s ago. This is shard capacity exhaustion, not a hang, and it is not attributable to any single change: raise test_timeout_minutes/timeout_minutes or split the shard in .github/ci-shards.json."
    ;;
  hang_suspected)
    echo "::error::HONUA_SHARD_HANG_SUSPECTED shard='${shard_name}' hit its ${timeout_minutes}m test budget after producing no output for ${idle_seconds_at_exit}s (stall threshold ${stall_seconds}s). Treat this as a genuine hang and investigate the last test in the log tail above."
    ;;
esac

if [[ "${timed_out}" == "true" ]]; then
  echo "::error::Server test shard '${shard_name}' timed out after ${timeout_minutes} minute(s). Filter: ${filter}"
fi

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo "### Server test shard: ${shard_name}"
    echo
    echo "- Status: \`${status}\` (exit ${test_exit_code})"
    echo "- Duration: \`${duration_seconds}s\` of \`${timeout_minutes:-none}m\` budget"
    echo "- Headroom: \`${capacity_status}\` (\`${headroom_percent:-n/a}%\` used)"
  } >> "${GITHUB_STEP_SUMMARY}"
fi

exit "${test_exit_code}"
