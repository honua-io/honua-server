#!/usr/bin/env bash
# Offline behavioural fixture for scripts/ci/run-server-test-shard.sh headroom
# monitoring (#3054). No .NET, no Docker, no network: `dotnet` is stubbed on
# PATH so the supervisor's timing, classification and annotation contract can be
# exercised deterministically in seconds.
#
# Run: scripts/ci/fixtures/validate-shard-headroom.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../../.." && pwd)"
RUNNER="${REPO_ROOT}/scripts/ci/run-server-test-shard.sh"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
pass() { printf 'PASS: %s\n' "$1"; }

if ! command -v timeout >/dev/null 2>&1 && ! command -v gtimeout >/dev/null 2>&1; then
  echo "SKIP: no GNU timeout available; shard headroom fixture needs one."
  exit 0
fi

workdir="$(mktemp -d)"
trap 'rm -rf "${workdir}"' EXIT

stub_dir="${workdir}/bin"
mkdir -p "${stub_dir}"

# Stub `dotnet`. HONUA_FIXTURE_MODE selects the shape of the run:
#   chatty  - emit output continuously for HONUA_FIXTURE_SECONDS, then exit 0.
#   failing - same, but exit 1 as a normal test failure would.
#   silent  - emit one line, then go quiet forever (until the cap kills it).
cat > "${stub_dir}/dotnet" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
mode="${HONUA_FIXTURE_MODE:-chatty}"
seconds="${HONUA_FIXTURE_SECONDS:-2}"
echo "stub dotnet test starting (mode=${mode})"
case "${mode}" in
  chatty|failing)
    end=$(( $(date +%s) + seconds ))
    while [[ "$(date +%s)" -lt "${end}" ]]; do
      echo "stub progress $(date -u +'%H:%M:%S')"
      sleep 0.2
    done
    if [[ "${mode}" == "failing" ]]; then
      echo "Failed!  - Failed: 1, Passed: 0"
      exit 1
    fi
    echo "Passed!  - Failed: 0, Passed: 1"
    ;;
  silent)
    sleep 600
    ;;
esac
STUB
chmod +x "${stub_dir}/dotnet"

# run_case <name> <mode> <seconds> <timeout-minutes> <warn-ratio> <stall-seconds>
# Emits the runner's stdout to ${out_file} and its timing JSON to ${timing_file};
# sets ${rc} to the runner's exit code.
run_case() {
  local name="$1" mode="$2" seconds="$3" tmo="$4" warn="$5" stall="$6"
  results_dir="${workdir}/${name}"
  out_file="${workdir}/${name}.out"
  timing_file="${results_dir}/fixture-${name}.timing.json"
  rc=0
  PATH="${stub_dir}:${PATH}" \
  HONUA_FIXTURE_MODE="${mode}" \
  HONUA_FIXTURE_SECONDS="${seconds}" \
  HONUA_SERVER_TEST_SHARD_NAME="Fixture ${name}" \
  HONUA_SERVER_TEST_FILTER="FullyQualifiedName~Fixture" \
  HONUA_SERVER_TEST_LOG_NAME="fixture-${name}" \
  HONUA_SERVER_TEST_RESULTS_DIR="${results_dir}" \
  HONUA_SERVER_TEST_TIMEOUT_MINUTES="${tmo}" \
  HONUA_SERVER_TEST_HEADROOM_WARN_RATIO="${warn}" \
  HONUA_SERVER_TEST_STALL_SECONDS="${stall}" \
  HONUA_SERVER_TEST_HEARTBEAT_SECONDS=1 \
  HONUA_SERVER_TEST_POLL_SECONDS=1 \
  GITHUB_STEP_SUMMARY="${workdir}/${name}.summary" \
    "${RUNNER}" > "${out_file}" 2>&1 || rc=$?
}

timing_field() { jq -r "$1" "${timing_file}"; }

# --- 1. Healthy shard: well under its budget, no capacity annotation. --------
run_case ok chatty 2 1 0.90 60
[[ "${rc}" == "0" ]] || fail "healthy shard did not exit 0 (rc=${rc})"
[[ "$(timing_field .capacity_status)" == "ok" ]] \
  || fail "healthy shard capacity_status was $(timing_field .capacity_status)"
if grep -q 'HONUA_SHARD_' "${out_file}"; then fail "healthy shard emitted a capacity annotation"; fi
pass "healthy shard reports capacity_status=ok with no annotation"

# The supervisor must not round the duration up to the heartbeat interval: a
# ~2s run under a 60s cap has to measure as a few seconds, not as 60.
duration="$(timing_field .duration_seconds)"
[[ "${duration}" -lt 15 ]] || fail "duration_seconds=${duration} is not the child's real runtime"
ratio="$(timing_field .headroom_ratio)"
awk -v r="${ratio}" 'BEGIN { exit !(r > 0 && r < 0.30) }' \
  || fail "headroom_ratio=${ratio} is not a plausible fraction of the budget"
pass "duration and headroom_ratio reflect the child's real runtime"

# --- 2. Passing shard over the warn line: warning, still exit 0. -------------
run_case warn chatty 2 1 0.01 60
[[ "${rc}" == "0" ]] || fail "low-headroom shard must still pass (rc=${rc})"
[[ "$(timing_field .capacity_status)" == "low_headroom" ]] \
  || fail "low-headroom capacity_status was $(timing_field .capacity_status)"
grep -q '^::warning::HONUA_SHARD_LOW_HEADROOM' "${out_file}" \
  || fail "low-headroom shard did not emit the ::warning:: annotation"
if grep -q 'HONUA_SHARD_CAPACITY_EXHAUSTED\|HONUA_SHARD_HANG_SUSPECTED' "${out_file}"; then
  fail "a passing shard must not be reported as exhausted or hung"
fi
pass "shard over the warn ratio warns without failing"

# --- 3. Capacity exhaustion: hit the cap while still producing output. -------
run_case capacity chatty 600 0.05 0.80 60
[[ "${rc}" == "124" ]] || fail "capacity case did not time out (rc=${rc})"
[[ "$(timing_field .capacity_status)" == "capacity_exhausted" ]] \
  || fail "capacity_status was $(timing_field .capacity_status)"
[[ "$(timing_field .timed_out)" == "true" ]] || fail "timed_out flag not set"
grep -q '^::error::HONUA_SHARD_CAPACITY_EXHAUSTED' "${out_file}" \
  || fail "capacity exhaustion did not emit its distinct ::error:: annotation"
if grep -q 'HONUA_SHARD_HANG_SUSPECTED' "${out_file}"; then
  fail "an actively-progressing shard must not be reported as a hang"
fi
# The generic timeout line must survive so the merge train still recognises the
# failure as a timeout at all.
grep -q 'timed out after' "${out_file}" || fail "generic timeout annotation was lost"
pass "budget exhaustion while progressing is reported as capacity, not a hang"

# --- 4. Hang: hit the cap after going silent. --------------------------------
run_case hang silent 0 0.05 0.80 1
[[ "${rc}" == "124" ]] || fail "hang case did not time out (rc=${rc})"
[[ "$(timing_field .capacity_status)" == "hang_suspected" ]] \
  || fail "capacity_status was $(timing_field .capacity_status)"
grep -q '^::error::HONUA_SHARD_HANG_SUSPECTED' "${out_file}" \
  || fail "hang did not emit its distinct ::error:: annotation"
if grep -q 'HONUA_SHARD_CAPACITY_EXHAUSTED' "${out_file}"; then
  fail "a stalled shard must not be reported as capacity exhaustion"
fi
pass "budget exhaustion after a stall is reported as a suspected hang"

# --- 5. No inner timeout configured: nothing to measure, nothing to claim. ---
run_case unbounded chatty 2 "" 0.80 60
[[ "${rc}" == "0" ]] || fail "unbounded case did not exit 0 (rc=${rc})"
[[ "$(timing_field .capacity_status)" == "unbounded" ]] \
  || fail "capacity_status was $(timing_field .capacity_status)"
[[ "$(timing_field .headroom_ratio)" == "null" ]] \
  || fail "headroom_ratio must be null with no configured budget"
pass "unconfigured budget reports unbounded instead of inventing a ratio"

# --- 5b. A FAILING shard never claims anything about its headroom. -----------
# A test/infrastructure failure can abort early or run long on retries, so its
# duration is not a capacity sample. Claiming "this shard passed but has no
# headroom" for a run that did not pass would be a false signal.
run_case failed failing 2 1 0.01 60
[[ "${rc}" == "1" ]] || fail "failing shard did not propagate its exit code (rc=${rc})"
[[ "$(timing_field .status)" == "failed" ]] || fail "status was $(timing_field .status)"
[[ "$(timing_field .capacity_status)" == "not_assessed" ]] \
  || fail "failing shard capacity_status was $(timing_field .capacity_status), expected not_assessed"
if grep -q 'HONUA_SHARD_' "${out_file}"; then
  fail "a failing shard must not emit a capacity annotation"
fi
pass "failing shard is not assessed for headroom"

# --- 6. Fleet audit over collected timing artifacts. -------------------------
# scripts/ci/audit-shard-headroom.py is the tool used to re-base budgets, so its
# classification has to agree with the per-run classification above.
python_bin=""
for candidate in python3 python py; do
  if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" -c 'import sys' >/dev/null 2>&1; then
    python_bin="${candidate}"
    break
  fi
done

if [[ -z "${python_bin}" ]]; then
  echo "SKIP: no working Python 3; shard headroom audit checks not run."
else
  audit_dir="${workdir}/audit"
  mkdir -p "${audit_dir}"
  audit_shard="Migration"
  audit_cap="$(jq -r --arg s "${audit_shard}" \
    '.shards[] | select(.shard_name == $s) | .test_timeout_minutes' "${REPO_ROOT}/.github/ci-shards.json")"
  [[ -n "${audit_cap}" && "${audit_cap}" != "null" ]] || fail "could not read ${audit_shard} cap from ci-shards.json"

  write_timing() {
    jq -nc --arg shard "${audit_shard}" --argjson duration "$1" --argjson timed_out "$2" \
      --arg capacity_status "${4:-ok}" \
      '{shard: $shard, duration_seconds: $duration, timed_out: $timed_out, capacity_status: $capacity_status}' \
      > "${audit_dir}/$3.timing.json"
  }

  # Comfortably inside the budget: no finding, clean exit even with --fail-on-warn.
  comfortable_seconds="$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 * 0.5 }')"
  write_timing "${comfortable_seconds}" false a
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" \
    --timings-dir "${audit_dir}" --fail-on-warn)" \
    || fail "audit failed a shard that is only half way through its budget"
  [[ "$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")" == "ok" ]] \
    || fail "half-budget shard was not classified ok"
  pass "fleet audit passes a shard with real headroom"

  # Over the warn line: flagged, recommended cap strictly larger, and the gate
  # option turns the finding into a non-zero exit.
  tight_seconds="$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 * 0.95 }')"
  write_timing "${tight_seconds}" false a
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")" \
    || fail "audit without --fail-on-warn must still exit 0"
  status="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")"
  [[ "${status}" == "low_headroom" ]] || fail "95%-of-budget shard classified '${status}'"
  recommended="$(jq -r --arg s "${audit_shard}" \
    '.[] | select(.shard == $s) | .recommended_test_timeout_minutes' <<<"${audit_json}")"
  (( recommended > audit_cap )) || fail "recommended cap ${recommended} does not exceed current cap ${audit_cap}"
  rc=0
  "${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" \
    --timings-dir "${audit_dir}" --fail-on-warn >/dev/null 2>&1 || rc=$?
  [[ "${rc}" == "1" ]] || fail "--fail-on-warn did not gate a low-headroom shard (rc=${rc})"
  pass "fleet audit flags and gates a shard over the warn line"

  # A recorded timeout is censored data: the recommendation must be based on at
  # least the cap, never on the (necessarily smaller) truncated duration.
  write_timing "$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 }')" true a capacity_exhausted
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")"
  [[ "$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")" == "over_capacity" ]] \
    || fail "a recorded timeout was not classified over_capacity"
  pass "fleet audit treats a recorded timeout as over capacity"

  # not_assessed runs are excluded from the sample entirely, so a failing run
  # that happened to be slow cannot inflate a shard's measured p90.
  rm -f "${audit_dir}"/*.timing.json
  write_timing "${comfortable_seconds}" false a
  write_timing "$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 * 0.99 }')" false b not_assessed
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")"
  runs="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .runs' <<<"${audit_json}")"
  [[ "${runs}" == "1" ]] || fail "not_assessed run was counted as a capacity sample (runs=${runs})"
  [[ "$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")" == "ok" ]] \
    || fail "not_assessed run skewed the shard's classification"
  pass "fleet audit excludes not_assessed runs from the sample"

  # Legacy artifacts predate capacity_status. A failed run must still be
  # filtered out via the long-standing `status` field, or re-basing from an
  # older CI run mixes aborted/retried failures back into the sample.
  rm -f "${audit_dir}"/*.timing.json
  jq -nc --arg shard "${audit_shard}" --argjson d "${comfortable_seconds}" \
    '{shard: $shard, duration_seconds: $d, timed_out: false, status: "passed"}' \
    > "${audit_dir}/legacy-pass.timing.json"
  jq -nc --arg shard "${audit_shard}" \
    --argjson d "$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 * 0.99 }')" \
    '{shard: $shard, duration_seconds: $d, timed_out: false, status: "failed"}' \
    > "${audit_dir}/legacy-fail.timing.json"
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")"
  runs="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .runs' <<<"${audit_json}")"
  [[ "${runs}" == "1" ]] || fail "legacy failed record was counted as a capacity sample (runs=${runs})"
  pass "fleet audit filters legacy failed records without capacity_status"

  # Nearest rank must not round DOWN past the slowest observation: with six
  # runs, p90 is the sixth value, and dropping it would classify an undersized
  # shard as ok and suppress the budget recommendation.
  rm -f "${audit_dir}"/*.timing.json
  slow_seconds="$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 * 0.95 }')"
  for i in 1 2 3 4 5; do write_timing "${comfortable_seconds}" false "p90-${i}"; done
  write_timing "${slow_seconds}" false p90-slow
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")"
  status="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")"
  [[ "${status}" == "low_headroom" ]] \
    || fail "p90 over 6 runs dropped the slowest observation (status=${status})"
  pass "p90 uses nearest rank and keeps the slowest observation"
fi

echo "✅ shard headroom instrumentation fixture passed"
