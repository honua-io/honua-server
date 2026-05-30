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

mkdir -p "${results_dir}"

log_file="${results_dir%/}/${log_name}.log"
trx_file="${log_name}.trx"
timing_file="${results_dir%/}/${log_name}.timing.json"

filter="${filter_expression}"
if [[ "${exclude_slow}" == "true" ]]; then
  filter="(${filter_expression})&Tier!=Slow"
fi
if [[ "${exclude_fast}" == "true" ]]; then
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

"${run_command[@]}" >> "${log_file}" 2>&1 &
test_pid=$!
heartbeat_count=0

while kill -0 "${test_pid}" 2>/dev/null; do
  now_epoch="$(date +%s)"
  elapsed_seconds=$((now_epoch - start_epoch))
  echo "[${log_name}-heartbeat] $(date -u +'%Y-%m-%dT%H:%M:%SZ') shard=\"${shard_name}\" elapsed=${elapsed_seconds}s timeout=${timeout_minutes:-none}m"

  heartbeat_count=$((heartbeat_count + 1))
  if (( heartbeat_count % 4 == 0 )); then
    echo "[${log_name}-tail] last ${heartbeat_tail_lines} log lines"
    tail -n "${heartbeat_tail_lines}" "${log_file}" || true
    echo "[${log_name}-tail-end]"
  fi

  sleep "${heartbeat_seconds}"
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
    '{
      shard: $shard,
      log_name: $log_name,
      status: $status,
      exit_code: ($exit_code | tonumber),
      started_at: $started_at,
      completed_at: $completed_at,
      duration_seconds: ($duration_seconds | tonumber),
      timeout_minutes: (if $timeout_minutes == "" then null else ($timeout_minutes | tonumber) end),
      timed_out: ($timed_out == "true"),
      filter: $filter
    }' > "${timing_file}"
else
  printf '{"shard":"%s","log_name":"%s","status":"%s","exit_code":%s,"started_at":"%s","completed_at":"%s","duration_seconds":%s,"timeout_minutes":null,"timed_out":%s}\n' \
    "${shard_name}" "${log_name}" "${status}" "${test_exit_code}" "${started_at}" "${completed_at}" "${duration_seconds}" "${timed_out}" > "${timing_file}"
fi

{
  echo ""
  echo "Completed: ${completed_at}"
  echo "Duration seconds: ${duration_seconds}"
  echo "Exit code: ${test_exit_code}"
  echo "Status: ${status}"
  echo "Timing artifact: ${timing_file}"
} >> "${log_file}"

cat "${log_file}"

if [[ "${timed_out}" == "true" ]]; then
  echo "::error::Server test shard '${shard_name}' timed out after ${timeout_minutes} minute(s). Filter: ${filter}"
fi

exit "${test_exit_code}"
