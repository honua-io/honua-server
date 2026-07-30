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
#   chatty       - emit output continuously for HONUA_FIXTURE_SECONDS, then exit 0.
#   failing      - same, but exit 1 as a normal test failure would.
#   silent       - emit one line, then go quiet forever (until the cap kills it).
#   sigterm-proof- emit continuously AND ignore SIGTERM, so `timeout` has to
#                  escalate to SIGKILL and reports 137 instead of 124.
#   selfkill     - emit one line, then SIGKILL itself almost immediately. Stands
#                  in for an OOM kill: also 137, but nowhere near the budget.
#   ppidkill     - ignore SIGTERM like sigterm-proof, then SIGKILL the `timeout`
#                  wrapper itself HONUA_FIXTURE_SECONDS in. Stands in for a host
#                  OOM kill reaping the test-host tree inside `timeout`'s
#                  --kill-after grace window: the runner observes 137 past the
#                  cap, yet at an instant `timeout` could not have produced.
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
  sigterm-proof)
    trap '' TERM
    while :; do
      echo "stub progress $(date -u +'%H:%M:%S')"
      sleep 0.2
    done
    ;;
  stall-then-cleanup)
    # Wedged: silent for the whole budget, then noisy while it is torn down.
    # Real shards do this -- cancellation and disposal messages land after
    # `timeout` sends SIGTERM. That teardown output must not count as progress.
    trap 'echo "stub cancelling outstanding work"; echo "stub disposing fixtures"; echo "stub teardown complete"; exit 143' TERM
    sleep 600 &
    wait $!
    ;;
  selfkill)
    sleep 0.3
    kill -9 $$
    sleep 600
    ;;
  ppidkill)
    trap '' TERM
    kill_at=$(( $(date +%s) + seconds ))
    while :; do
      echo "stub progress $(date -u +'%H:%M:%S')"
      if [[ "$(date +%s)" -ge "${kill_at}" ]]; then
        kill -9 "${PPID}" 2>/dev/null || true
        exit 0
      fi
      sleep 0.2
    done
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
  HONUA_SERVER_TEST_KILL_AFTER_SECONDS="${HONUA_FIXTURE_KILL_AFTER:-2}" \
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

# --- 4a. Hang whose teardown is chatty: still a hang. ------------------------
# A wedged shard that stays silent through its whole budget and then emits
# cancellation/disposal output while `timeout` kills it. Crediting that teardown
# output as progress made idle_seconds_at_exit collapse to ~0 and reported
# capacity_exhausted, so the train escalated the whole batch instead of running
# its hang retry. Only pre-deadline output may count as progress.
run_case hangchattyteardown stall-then-cleanup 0 0.1 0.80 3
[[ "${rc}" == "124" ]] || fail "chatty-teardown hang did not time out (rc=${rc}); fixture premise is wrong"
grep -q 'stub teardown complete' "${results_dir}/fixture-hangchattyteardown.log" \
  || fail "fixture premise is wrong: no teardown output was produced after SIGTERM"
[[ "$(timing_field .capacity_status)" == "hang_suspected" ]] \
  || fail "teardown output after the deadline was credited as progress (capacity_status=$(timing_field .capacity_status))"
grep -q '^::error::HONUA_SHARD_HANG_SUSPECTED' "${out_file}" \
  || fail "chatty-teardown hang did not emit the hang annotation"
if grep -q 'HONUA_SHARD_CAPACITY_EXHAUSTED' "${out_file}"; then
  fail "a shard silent until its deadline must not be reported as capacity exhaustion"
fi
pass "output produced after the deadline does not mask a hang"

# --- 4e. The warn threshold comes from shard policy, not a second default. ----
# `.github/ci-shards.json` -> shard_budget_policy.warn_utilization is what the
# fleet audit reads. The runner must read the same key, or a shard sitting between
# the old and new thresholds gets one status in CI and a different one in the
# audit. Deliberately does NOT set HONUA_SERVER_TEST_HEADROOM_WARN_RATIO, so a
# regression to a hardcoded 0.80 makes this case fail: a ~2s run under a 60s cap
# is ~3% utilisation, far under 0.80, but over a policy warn line of 0.01.
policy_file="${workdir}/policy-shards.json"
printf '{"shard_budget_policy":{"warn_utilization":0.01},"shards":[]}\n' > "${policy_file}"
results_dir="${workdir}/policywarn"
out_file="${workdir}/policywarn.out"
timing_file="${results_dir}/fixture-policywarn.timing.json"
rc=0
PATH="${stub_dir}:${PATH}" \
HONUA_FIXTURE_MODE=chatty \
HONUA_FIXTURE_SECONDS=2 \
HONUA_SERVER_TEST_SHARD_NAME="Fixture policywarn" \
HONUA_SERVER_TEST_FILTER="FullyQualifiedName~Fixture" \
HONUA_SERVER_TEST_LOG_NAME="fixture-policywarn" \
HONUA_SERVER_TEST_RESULTS_DIR="${results_dir}" \
HONUA_SERVER_TEST_TIMEOUT_MINUTES=1 \
HONUA_SERVER_TEST_SHARD_POLICY_FILE="${policy_file}" \
HONUA_SERVER_TEST_STALL_SECONDS=60 \
HONUA_SERVER_TEST_HEARTBEAT_SECONDS=1 \
HONUA_SERVER_TEST_POLL_SECONDS=1 \
GITHUB_STEP_SUMMARY="${workdir}/policywarn.summary" \
  "${RUNNER}" > "${out_file}" 2>&1 || rc=$?
[[ "${rc}" == "0" ]] || fail "policy-warn case did not exit 0 (rc=${rc})"
[[ "$(timing_field .capacity_status)" == "low_headroom" ]] \
  || fail "runner ignored shard_budget_policy.warn_utilization (capacity_status=$(timing_field .capacity_status))"
grep -q '^::warning::HONUA_SHARD_LOW_HEADROOM' "${out_file}" \
  || fail "policy-driven warn threshold did not emit the low-headroom warning"
pass "warn threshold is read from shard policy rather than a private default"

# --- 5. No inner timeout configured: nothing to measure, nothing to claim. ---
run_case unbounded chatty 2 "" 0.80 60
[[ "${rc}" == "0" ]] || fail "unbounded case did not exit 0 (rc=${rc})"
[[ "$(timing_field .capacity_status)" == "unbounded" ]] \
  || fail "capacity_status was $(timing_field .capacity_status)"
[[ "$(timing_field .headroom_ratio)" == "null" ]] \
  || fail "headroom_ratio must be null with no configured budget"
pass "unconfigured budget reports unbounded instead of inventing a ratio"

# --- 4b. Kill-escalated timeout: SIGTERM ignored, so `timeout` reports 137. ---
# This is the worst case the instrumentation exists for — a shard wedged hard
# enough to ignore SIGTERM. `timeout --kill-after` escalates to SIGKILL and
# exits 128+9 = 137, NOT 124, so recognising only 124 would attribute the most
# stuck shard in the fleet as an ordinary real failure.
#
# The grace is widened to 6s (CI uses 30s) so the escalation deadline is a
# distinct instant from the cap: `timeout` TERMs at ~3s and only KILLs at ~9s.
# That separation is what case 4d below exercises from the other side.
HONUA_FIXTURE_KILL_AFTER=6
run_case killescalated sigterm-proof 0 0.05 0.80 60
[[ "${rc}" == "137" ]] || fail "kill-escalated case did not exit 137 (rc=${rc}); fixture premise is wrong"
[[ "$(timing_field .status)" == "timed_out" ]] \
  || fail "exit 137 at the cap was not classified as a timeout (status=$(timing_field .status))"
[[ "$(timing_field .timed_out)" == "true" ]] || fail "timed_out flag not set for a kill-escalated timeout"
[[ "$(timing_field .kill_escalated)" == "true" ]] || fail "kill_escalated flag not recorded"
[[ "$(timing_field .capacity_status)" == "capacity_exhausted" ]] \
  || fail "kill-escalated capacity_status was $(timing_field .capacity_status)"
grep -q '^::error::HONUA_SHARD_CAPACITY_EXHAUSTED' "${out_file}" \
  || fail "kill-escalated timeout did not emit the capacity annotation"
# The merge train matches on this phrase, so it must survive the 137 path.
grep -q 'timed out after' "${out_file}" || fail "generic timeout annotation was lost on the 137 path"
grep -q 'SIGKILLed' "${out_file}" || fail "kill escalation was not called out in the annotation"
if grep -q 'HONUA_SHARD_KILLED' "${out_file}"; then
  fail "a kill-escalated timeout must not be reported as an external kill"
fi
pass "SIGTERM-ignoring shard exits 137 and is still classified as a timeout"

# --- 4c. Negative control: 137 well INSIDE the budget is not a timeout. -------
# An OOM kill produces the same 137. Blanket-mapping 137 to "timed out" would
# launder a genuine external kill into a capacity signal and hide the real
# cause, so the clock is the discriminator.
run_case externalkill selfkill 0 1 0.80 60
[[ "${rc}" == "137" ]] || fail "external-kill case did not exit 137 (rc=${rc}); fixture premise is wrong"
[[ "$(timing_field .status)" == "killed" ]] \
  || fail "early SIGKILL was classified as $(timing_field .status), expected killed"
[[ "$(timing_field .timed_out)" == "false" ]] || fail "an external SIGKILL must not set timed_out"
[[ "$(timing_field .kill_escalated)" == "false" ]] || fail "an external SIGKILL is not a kill escalation"
[[ "$(timing_field .capacity_status)" == "not_assessed" ]] \
  || fail "external kill capacity_status was $(timing_field .capacity_status)"
grep -q '^::error::HONUA_SHARD_KILLED' "${out_file}" || fail "external kill did not emit its own annotation"
if grep -q 'timed out after\|HONUA_SHARD_CAPACITY_EXHAUSTED\|HONUA_SHARD_HANG_SUSPECTED' "${out_file}"; then
  fail "an external SIGKILL must not be reported as a timeout or capacity problem"
fi
pass "SIGKILL well inside the budget is reported as a kill, not a timeout"

# --- 4d. External SIGKILL inside the --kill-after GRACE window. ---------------
# The dangerous middle ground: `timeout` sends SIGTERM at the cap but cannot
# send SIGKILL until the full --kill-after grace has also elapsed. A host OOM
# kill landing in that window produces 137 PAST the cap, so a `duration >= cap`
# discriminator calls it a kill-escalated timeout and emits
# HONUA_SHARD_CAPACITY_EXHAUSTED — sending the merge train into batch-wide
# capacity escalation for what is actually an out-of-memory kill. The deadline
# must therefore be cap + grace (less measurement tolerance), not cap.
#
# Cap 3s, grace 12s, wrapper SIGKILLed at 5s: past the cap, nowhere near the 13s
# instant at which `timeout` could first have done it itself.
HONUA_FIXTURE_KILL_AFTER=12
run_case gracekill ppidkill 5 0.05 0.80 60
unset HONUA_FIXTURE_KILL_AFTER
[[ "${rc}" == "137" ]] || fail "grace-window kill case did not exit 137 (rc=${rc}); fixture premise is wrong"
grace_duration="$(timing_field .duration_seconds)"
grace_cap_seconds=3
(( grace_duration >= grace_cap_seconds )) \
  || fail "grace-window kill landed before the cap (${grace_duration}s); fixture proves nothing"
[[ "$(timing_field .status)" == "killed" ]] \
  || fail "SIGKILL inside the grace window was classified as $(timing_field .status), expected killed"
[[ "$(timing_field .timed_out)" == "false" ]] \
  || fail "a kill inside the grace window must not set timed_out"
[[ "$(timing_field .kill_escalated)" == "false" ]] \
  || fail "a kill inside the grace window is not a kill escalation"
[[ "$(timing_field .capacity_status)" == "not_assessed" ]] \
  || fail "grace-window kill capacity_status was $(timing_field .capacity_status)"
grep -q '^::error::HONUA_SHARD_KILLED' "${out_file}" \
  || fail "grace-window kill did not emit the external-kill annotation"
if grep -q 'timed out after\|HONUA_SHARD_CAPACITY_EXHAUSTED\|HONUA_SHARD_HANG_SUSPECTED' "${out_file}"; then
  fail "an OOM kill inside the grace window must not be reported as a timeout or capacity problem"
fi
pass "SIGKILL at the cap but inside the kill-after grace is a kill, not a timeout"

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

  # A suspected hang is not evidence that the normal workload needs more time.
  # Counting it as censored capacity data would recommend a bigger budget for a
  # stall, delaying detection of the hang.
  rm -f "${audit_dir}"/*.timing.json
  write_timing "${comfortable_seconds}" false a
  jq -nc --arg shard "${audit_shard}" \
    --argjson d "$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 }')" \
    --argjson cap "${audit_cap}" \
    '{shard: $shard, duration_seconds: $d, timed_out: true, timeout_minutes: $cap, capacity_status: "hang_suspected"}' \
    > "${audit_dir}/hang.timing.json"
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")"
  runs="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .runs' <<<"${audit_json}")"
  [[ "${runs}" == "1" ]] || fail "hang_suspected run was counted as a capacity sample (runs=${runs})"
  status="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")"
  [[ "${status}" == "ok" ]] || fail "a suspected hang drove the shard to '${status}'"
  recommended="$(jq -r --arg s "${audit_shard}" \
    '.[] | select(.shard == $s) | .recommended_test_timeout_minutes' <<<"${audit_json}")"
  [[ "${recommended}" == "${audit_cap}" ]] \
    || fail "a suspected hang recommended a budget raise to ${recommended}"
  pass "fleet audit excludes suspected hangs from capacity samples"

  # A timeout is censored at the cap THAT RECORD ran under. Auditing an old
  # artifact against a since-raised cap would invent evidence: a timeout at a
  # 10-minute cap must not be read as proof that 29 minutes were needed.
  rm -f "${audit_dir}"/*.timing.json
  jq -nc --arg shard "${audit_shard}" \
    '{shard: $shard, duration_seconds: 600, timed_out: true, timeout_minutes: 10, capacity_status: "capacity_exhausted"}' \
    > "${audit_dir}/old-cap-timeout.timing.json"
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")"
  recommended="$(jq -r --arg s "${audit_shard}" \
    '.[] | select(.shard == $s) | .recommended_test_timeout_minutes' <<<"${audit_json}")"
  inflated="$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", int((c / 0.7) + 0.999999) }')"
  [[ "${recommended}" != "${inflated}" ]] \
    || fail "old-cap timeout was audited against the current cap (recommended ${recommended})"
  [[ "${recommended}" == "${audit_cap}" ]] \
    || fail "old-cap timeout should leave the current cap alone, got ${recommended}"
  pass "fleet audit bounds a censored sample by its own recorded budget"

  # ...and the finding itself must clear with the recommendation. Keying
  # over_capacity off "this shard has ever timed out" made the verdict
  # permanent: a history containing one timeout under the old cap could never
  # pass --fail-on-warn again, not even after the cap raise this audit
  # prescribed, so the gate had no clean state to return to.
  status="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")"
  [[ "${status}" == "ok" ]] \
    || fail "a timeout the current cap already clears still reports '${status}'"
  rc=0
  "${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" \
    --timings-dir "${audit_dir}" --fail-on-warn >/dev/null 2>&1 || rc=$?
  [[ "${rc}" == "0" ]] \
    || fail "--fail-on-warn still gates on a stale timeout the current cap covers (rc=${rc})"
  pass "a rebased budget clears a stale timeout finding"

  # The other direction: a timeout recorded under TODAY's cap is live evidence
  # and must still be over capacity and still gate.
  rm -f "${audit_dir}"/*.timing.json
  jq -nc --arg shard "${audit_shard}" --argjson cap "${audit_cap}" \
    --argjson d "$(awk -v c="${audit_cap}" 'BEGIN { printf "%d", c * 60 }')" \
    '{shard: $shard, duration_seconds: $d, timed_out: true, timeout_minutes: $cap, capacity_status: "capacity_exhausted"}' \
    > "${audit_dir}/current-cap-timeout.timing.json"
  audit_json="$("${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" --timings-dir "${audit_dir}")"
  status="$(jq -r --arg s "${audit_shard}" '.[] | select(.shard == $s) | .status' <<<"${audit_json}")"
  [[ "${status}" == "over_capacity" ]] \
    || fail "a timeout at the current cap reported '${status}' instead of over_capacity"
  recommended="$(jq -r --arg s "${audit_shard}" \
    '.[] | select(.shard == $s) | .recommended_test_timeout_minutes' <<<"${audit_json}")"
  (( recommended > audit_cap )) \
    || fail "a timeout at the current cap did not raise the recommendation (${recommended})"
  rc=0
  "${python_bin}" "${REPO_ROOT}/scripts/ci/audit-shard-headroom.py" \
    --timings-dir "${audit_dir}" --fail-on-warn >/dev/null 2>&1 || rc=$?
  [[ "${rc}" == "1" ]] || fail "--fail-on-warn did not gate a timeout at the current cap (rc=${rc})"
  pass "a timeout recorded at the current cap still gates"
fi

echo "✅ shard headroom instrumentation fixture passed"
