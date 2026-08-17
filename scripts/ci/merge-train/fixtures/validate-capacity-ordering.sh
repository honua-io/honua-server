#!/usr/bin/env bash
# Offline validation of the #3213 ordering guarantee.
#
# A server-test shard that could not FINISH executing its tests produced no
# comparable failure cause. scripts/ci/run-server-test-shard.sh ends such a
# shard with exactly one marker — HONUA_SHARD_CAPACITY_EXHAUSTED (over budget),
# HONUA_SHARD_HANG_SUSPECTED (stalled), or HONUA_SHARD_KILLED (host SIGKILLed) —
# emitted as the shard's LAST error, far outside the bounded window the
# pre-existing-failure filter samples from each job log. That window fills with
# per-run noise a red shard on trunk produces too, so subtracting first could
# cancel the shard against trunk and LAND a batch on tests that never ran.
#
# This validator locks in:
#   1. every shard-terminal marker is non-subtractable, whatever its position;
#   2. the guard classifies each marker into the right route (terminal vs the
#      bounded hang rerun) and is read-only;
#   3. marker detection is ANCHORED, so a job log that merely PRINTS the token
#      (every CI Router Validation log does) is still ordinary and subtractable;
#   4. transient evidence-read failures are retried before failing closed;
#   5. the signature extractor samples real causes, not passing-test noise;
#   6. a genuine pre-existing failure is still subtracted, exactly as before;
#   7. train.sh really runs the guard first.
set -euo pipefail

TRAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export TRAIN_APPLY=0
# shellcheck source=../lib.sh
. "${TRAIN_DIR}/lib.sh"
# shellcheck source=../classify-timeout.sh
. "${TRAIN_DIR}/classify-timeout.sh"
# shellcheck source=../preexisting.sh
. "${TRAIN_DIR}/preexisting.sh"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
pass() { printf 'PASS: %s\n' "$1"; }

work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT

side_effects="${work}/side-effects.log"
: >"${side_effects}"
train_side_effect() { printf '%s\n' "$*" >>"${side_effects}"; }

# Keep the guard's evidence-read backoff instant and deterministic offline.
export TRAIN_EVIDENCE_READ_BACKOFF_SECONDS=0

SHARD="Server Tests (Infra and Security)"
ROUTER='CI Router Validation'

# --- production-shaped logs --------------------------------------------------
# Reproduces job 95149717187 of run 31940825557: the marker is the LAST error of
# a very large log whose sampled head is dominated by PASSING test lines and
# per-run structured log records — which is why the naive extractor filled its
# whole window with green noise and never saw the real cause.
noise() {
  local i
  for ((i = 1; i <= 3000; i++)); do
    printf '2026-08-16T10:%02d:%02dZ  Passed Honua.Server.Tests.Suite%s.ReturnsFailed_WhenExpected: 200 [%s ms]\n' \
      $(( i % 60 )) $(( (i * 7) % 60 )) "${i}" "${i}"
    printf '{"@t":"2026-08-16T10:%02d:%02d.%04dZ","@mt":"Request handled","Code":200}\n' \
      $(( i % 60 )) $(( (i * 7) % 60 )) "${i}"
  done
}
marker_log() {  # <marker-line> <trailing-exit-line>
  noise
  printf '%s\n' "$1"
  printf '%s\n' "$2"
}
marker_log "::error::HONUA_SHARD_CAPACITY_EXHAUSTED shard='Infra and Security' hit its 39m test budget while still producing output 2s ago." \
  '::error::Process completed with exit code 124.' >"${work}/capacity.log"
marker_log "::error::HONUA_SHARD_HANG_SUSPECTED shard='Infra and Security' hit its 39m test budget after producing no output for 900s (stall threshold 600s)." \
  '::error::Process completed with exit code 124.' >"${work}/hang.log"
marker_log "::error::HONUA_SHARD_KILLED shard='Infra and Security' was SIGKILLed after 900s, before this runner's own SIGKILL deadline of 2400s, so this is not a timeout." \
  '::error::Process completed with exit code 137.' >"${work}/killed.log"
# Trunk's latest run: the same shard, equally red, same per-run noise shape and
# no shard-terminal marker. This is what the filter compares against.
{ noise; printf '::error::Process completed with exit code 1.\n'; } >"${work}/trunk-shard.log"

# A genuine, comparable regression: identical deterministic assertion output on
# both sides, no marker, no timeout.
printf '%s\n' \
  '  Failed Honua.Server.Tests.StacConformanceTests.Catalog_IsValid [1 s]' \
  '  Error Message:' \
  '   Assert.Equal() Failure: Values differ' \
  'Expected: 200' \
  'Actual:   500' \
  >"${work}/real-failure.log"
# The CI Router Validation job log: it PRINTS the capacity token (the merge
# train's own warning text names it) but is an ordinary, comparable failure.
printf '%s\n' \
  '[train][WARN] shard capacity exhausted (HONUA_SHARD_CAPACITY_EXHAUSTED): the test step used its whole configured budget while still running tests.' \
  'PASS: shard capacity exhaustion is terminal, not retried and not attributed' \
  '  Error: ci-shards.json coverage check failed for shard Core' \
  >"${work}/router.log"

# One stub serves both the filter (run, job name, job id) and the classifier
# (job id): lib.sh's REST-first reader is what preexisting.sh now delegates to,
# so a single job-id-keyed table is enough for every read path.
cat >"${work}/reader.sh" <<READER
#!/usr/bin/env bash
case "\$1" in
  9101) cat "${work}/capacity.log" ;;
  9102) cat "${work}/hang.log" ;;
  9103) cat "${work}/killed.log" ;;
  9104) cat "${work}/real-failure.log" ;;
  9105) cat "${work}/router.log" ;;
  9190) cat "${work}/trunk-shard.log" ;;
  9194) cat "${work}/real-failure.log" ;;
  9195) cat "${work}/router.log" ;;
  *) exit 1 ;;  # 9106/9196: neither log surface is readable
esac
READER
cat >"${work}/records.sh" <<'RECORDS'
#!/usr/bin/env bash
# <run-id> -> "<job-id>\t<job-name>" for every failing job of that run.
case "$1" in
  701) printf '9101\tServer Tests (Infra and Security)\n' ;;
  702) printf '9102\tServer Tests (Infra and Security)\n' ;;
  703) printf '9103\tServer Tests (Infra and Security)\n' ;;
  704) printf '9104\tServer Tests (Core)\n' ;;
  705) printf '9105\tCI Router Validation\n' ;;
  706) printf '9106\tServer Tests (Migration)\n' ;;
  790) printf '9190\tServer Tests (Infra and Security)\n' ;;
  794) printf '9194\tServer Tests (Core)\n' ;;
  795) printf '9195\tCI Router Validation\n' ;;
  796) printf '9196\tServer Tests (Migration)\n' ;;
esac
RECORDS
cat >"${work}/no-annotations.sh" <<'ANN'
#!/usr/bin/env bash
exit 1
ANN
chmod +x "${work}/reader.sh" "${work}/records.sh" "${work}/no-annotations.sh"

export TRAIN_FAILING_JOB_RECORDS_FOR_RUN="${work}/records.sh"
export TRAIN_JOB_LOG_READER="${work}/reader.sh"
export TRAIN_JOB_ANNOTATION_READER="${work}/no-annotations.sh"

gh() {  # only `gh run view <id> --json jobs` reaches here
  case "$*" in
    *701*) printf '9101\t%s\tfailure\n' "${SHARD}" ;;
    *702*) printf '9102\t%s\tfailure\n' "${SHARD}" ;;
    *703*) printf '9103\t%s\tfailure\n' "${SHARD}" ;;
    *704*) printf '9104\tServer Tests (Core)\tfailure\n' ;;
    *705*) printf '9105\t%s\tfailure\n' "${ROUTER}" ;;
    *706*) printf '9106\tServer Tests (Migration)\tfailure\n' ;;
    *) return 1 ;;
  esac
}

filter_rc() {  # <batch-run> <trunk-run> <job-name> -> prints rc, emits survivors
  local rc=0
  TRAIN_TRUNK_RUN_ID="$2" train_preexisting_filter "$1" "$3" >"${work}/survivors" 2>/dev/null || rc=$?
  printf '%s' "${rc}"
}
# Sets GUARD_RC and leaves TRAIN_GUARD_KIND readable. Deliberately NOT a
# command substitution: the guard communicates its kind through a variable, and
# a subshell would discard it.
GUARD_RC=0
run_guard() {  # <run> <job-name>
  GUARD_RC=0
  TRAIN_GUARD_KIND=""
  train_guard_scan_reset
  train_classify_capacity_guard "$1" "$2" || GUARD_RC=$?
}

# --- 1. the head-of-log window really does cancel against trunk --------------
# This is the trap the ordering guarantee exists for: sampled signatures are
# identical on both sides, so signature comparison alone says "pre-existing".
batch_sigs="$(train_extract_failure_signatures "${SHARD}" "$(cat "${work}/capacity.log")")"
trunk_sigs="$(train_extract_failure_signatures "${SHARD}" "$(cat "${work}/trunk-shard.log")")"
[[ -n "$(train_subtract_lines "${trunk_sigs}" "${batch_sigs}")" ]] \
  && fail "fixture no longer reproduces the cancelling trunk baseline"
# What actually protects the shard is the whole-log marker scan, which replaces
# those signatures with one run-scoped record that cannot exist on another run.
emitted="$(train_emit_job_failure_signatures 701 "${SHARD}" 9101)"
[[ "$(grep -c . <<<"${emitted}")" == "1" ]] \
  || fail "a shard-terminal job emitted comparable signatures instead of one run-scoped record"
grep -Fq "capacity-exhausted:701:${SHARD}" <<<"${emitted}" \
  || fail "the run-scoped capacity signature was not emitted (got '${emitted}')"
train_subtract_lines "$(train_emit_job_failure_signatures 790 "${SHARD}" 9190)" "${emitted}" \
  | grep -q . || fail "the run-scoped capacity signature still cancelled against trunk"
pass "sampled signatures cancel against trunk, but the whole-log marker scan makes the shard non-subtractable"

# The bounded window must never `exit` mid-pipe: on a log this size that
# SIGPIPEs the upstream writer and, under `set -o pipefail`, fails the whole
# filter with a nondeterministic 141. Repeat to catch the race.
for _ in 1 2 3 4 5; do
  repeat_rc=0
  train_extract_failure_signatures "${SHARD}" "$(cat "${work}/capacity.log")" >/dev/null || repeat_rc=$?
  [[ "${repeat_rc}" == "0" ]] \
    || fail "signature extraction over a large log is not pipe-safe (rc=${repeat_rc})"
done
pass "signature extraction over a large log is deterministic and pipe-safe"

# --- 2. every shard-terminal marker is non-subtractable, correctly routed ----
# capacity -> terminal, never rerun.
[[ "$(filter_rc 701 790 "${SHARD}")" != "11" ]] \
  || fail "the filter subtracted a capacity-exhausted shard"
: >"${side_effects}"
run_guard 701 "${SHARD}"
[[ "${GUARD_RC}" == "7" && "${TRAIN_GUARD_KIND}" == "capacity" ]] \
  || fail "capacity was not routed to the terminal outcome (rc=${GUARD_RC} kind=${TRAIN_GUARD_KIND})"
# hang -> bypasses subtraction but keeps the historical bounded rerun.
[[ "$(filter_rc 702 790 "${SHARD}")" != "11" ]] \
  || fail "the filter subtracted a stalled (HANG_SUSPECTED) shard that never finished its tests"
run_guard 702 "${SHARD}"
[[ "${GUARD_RC}" == "9" && "${TRAIN_GUARD_KIND}" == "shard-timeout" ]] \
  || fail "a stalled shard was not routed to the bounded rerun path (rc=${GUARD_RC} kind=${TRAIN_GUARD_KIND})"
# killed -> terminal; exit 137 carries no timeout wording at all.
[[ "$(filter_rc 703 790 "${SHARD}")" != "11" ]] \
  || fail "the filter subtracted a SIGKILLed shard"
run_guard 703 "${SHARD}"
[[ "${GUARD_RC}" == "7" && "${TRAIN_GUARD_KIND}" == "shard-killed" ]] \
  || fail "a SIGKILLed shard was not routed to the terminal outcome (rc=${GUARD_RC} kind=${TRAIN_GUARD_KIND})"
[[ ! -s "${side_effects}" ]] || fail "the read-only ordering guard performed a side effect"
pass "capacity, stalled and SIGKILLed shards are all non-subtractable and correctly routed"

# --- 3. marker detection is anchored: naming the token is not evidence -------
# The CI Router Validation job prints the capacity token in prose on every run.
# If that counted as capacity evidence, a router failure could never be
# subtracted and every batch carrying one would be escalated.
run_guard 705 "${ROUTER}"
[[ "${GUARD_RC}" == "0" ]] \
  || fail "a job that merely prints the capacity token was treated as shard-terminal (rc=${GUARD_RC})"
[[ "$(filter_rc 705 795 "${ROUTER}")" == "11" ]] \
  || fail "a router failure trunk already has was not subtracted because its log names the token"
train_log_is_capacity_exhaustion "$(cat "${work}/router.log")" \
  && fail "the anchored predicate still matches prose that only names the marker"
pass "anchored markers: printing the token is not capacity evidence"

# --- 4. transient evidence reads are retried before failing closed -----------
attempts="${work}/attempts"
printf '0' >"${attempts}"
flaky_reader() {
  local n; n=$(( $(cat "${attempts}") + 1 )); printf '%s' "${n}" >"${attempts}"
  [[ "${n}" -ge 2 ]] || return 1
  cat "${work}/real-failure.log"
}
export -f flaky_reader
TRAIN_JOB_LOG_READER=flaky_reader TRAIN_EVIDENCE_READ_RETRIES=3 run_guard 704 'Server Tests (Core)'
[[ "${GUARD_RC}" == "0" ]] \
  || fail "a transient evidence read was escalated instead of retried (rc=${GUARD_RC}, attempts=$(cat "${attempts}"))"
[[ "$(cat "${attempts}")" -ge 2 ]] || fail "the guard did not retry the failed evidence read"
# Persistently unreadable evidence still fails closed.
TRAIN_EVIDENCE_READ_RETRIES=2 run_guard 706 'Server Tests (Migration)'
[[ "${GUARD_RC}" == "8" && "${TRAIN_GUARD_KIND}" == "evidence-unavailable" ]] \
  || fail "persistently unreadable evidence did not fail closed (rc=${GUARD_RC})"
[[ "$(filter_rc 706 796 'Server Tests (Migration)')" != "11" ]] \
  || fail "a failing job with no readable log was subtracted as pre-existing"
pass "evidence reads are retried with backoff, and only persistent failure fails closed"

# --- 5. the extractor samples real causes, not passing-test noise ------------
noisy="$(printf '%s\n' \
  '  Passed Honua.Server.Tests.OData.FilterTests.Filter_ReturnsFailed_WhenInvalid [12 ms]' \
  '  Passed Honua.Server.Tests.Text.CaseTests.IsCaseAndWhitespaceInsensitive [3 ms]' \
  '  Passed Honua.Server.Tests.Query.Tests.Expected: 200 [1 ms]' \
  '  Skipped Honua.Server.Tests.Slow.BigTest' \
  '{"@t":"2026-08-16T10:00:00.1234567Z","@mt":"Request failed","Code":500}' \
  '   Assert.Equal() Failure: Values differ')"
sigs="$(train_extract_failure_signatures "${SHARD}" "${noisy}")"
[[ "$(grep -c . <<<"${sigs}")" == "1" ]] \
  || fail "passing-test / structured-log noise still produces signatures: ${sigs}"
grep -Fq 'assert:Assert.Equal() Failure: Values differ' <<<"${sigs}" \
  || fail "the real assertion cause was dropped from the sampled window"
# Repeats are one cause, not forty.
repeated="$(for _ in $(seq 1 100); do printf '  Error: connection refused\n'; done; printf '   Assert.Equal() Failure: Values differ\n')"
[[ "$(train_extract_failure_signatures "${SHARD}" "${repeated}" | grep -c .)" == "2" ]] \
  || fail "a repeated line consumed the whole bounded window instead of deduping"
pass "the extractor samples distinct real causes and ignores passing-test noise"

# --- 5b. the armed memo is read once, reused once, and never stale ----------
# train.sh arms the memo per ci-gate iteration so train_classify_timeout reuses
# the guard's scan instead of re-downloading the same logs. It must be consumed
# on first reuse, and unarmed callers must always re-read.
reads="${work}/reads"
printf '0' >"${reads}"
counting_reader() {
  local n; n=$(( $(cat "${reads}") + 1 )); printf '%s' "${n}" >"${reads}"
  cat "${work}/capacity.log"
}
export -f counting_reader
export TRAIN_JOB_LOG_READER=counting_reader

train_guard_scan_arm
GUARD_RC=0; train_classify_capacity_guard 701 "${SHARD}" || GUARD_RC=$?
[[ "${GUARD_RC}" == "7" ]] || fail "armed guard lost capacity classification (rc=${GUARD_RC})"
first_reads="$(cat "${reads}")"
[[ "${first_reads}" -ge 1 ]] || fail "the armed guard never read the job log"
rc=0; train_classify_timeout 701 0 "${SHARD}" || rc=$?
[[ "${rc}" == "7" ]] || fail "the delegated classifier lost capacity classification (rc=${rc})"
[[ "$(cat "${reads}")" == "${first_reads}" ]] \
  || fail "the armed memo was not reused; the same job log was downloaded twice"
# Consumed: a second delegation re-reads rather than serving a stale scan.
rc=0; train_classify_timeout 701 0 "${SHARD}" || rc=$?
[[ "$(cat "${reads}")" -gt "${first_reads}" ]] \
  || fail "the memo was reused twice; a later attempt could be served a stale scan"
# Unarmed callers always re-read.
TRAIN_GUARD_SCAN_ARMED=0
train_guard_scan_reset
before="$(cat "${reads}")"
GUARD_RC=0; train_classify_capacity_guard 701 "${SHARD}" || GUARD_RC=$?
rc=0; train_classify_timeout 701 0 "${SHARD}" || rc=$?
[[ "$(cat "${reads}")" -gt $(( before + 1 )) ]] \
  || fail "an unarmed caller reused a memoized scan"
unset TRAIN_JOB_LOG_READER
export TRAIN_JOB_LOG_READER="${work}/reader.sh"
TRAIN_GUARD_SCAN_ARMED=0
train_guard_scan_reset
pass "the armed evidence memo is read once, reused exactly once, and never used unarmed"

# --- 6. CONTROL: a genuine pre-existing failure is still subtracted ----------
run_guard 704 'Server Tests (Core)'
[[ "${GUARD_RC}" == "0" ]] || fail "the guard hijacked an ordinary comparable failure (rc=${GUARD_RC})"
[[ "$(filter_rc 704 794 'Server Tests (Core)')" == "11" ]] \
  || fail "the guard broke the filter's purpose: a failure trunk already has was not subtracted"
pass "control: a genuine pre-existing failure is still subtracted and merged through"

# --- 7. the orchestrator really runs the guard FIRST -------------------------
# A correct classifier that runs too late is the whole bug, so assert the order
# structurally in train.sh rather than trusting the comment.
train_sh="${TRAIN_DIR}/train.sh"
line_of() {
  local pattern="$1" line
  line="$(grep -n -- "${pattern}" "${train_sh}" | head -1 | cut -d: -f1)"
  [[ -n "${line}" ]] || fail "train.sh no longer contains '${pattern}'"
  printf '%s' "${line}"
}
guard_line="$(line_of 'train_classify_capacity_guard "${run_id}"')"
filter_line="$(line_of 'train_preexisting_filter "${run_id}"')"
retry_line="$(line_of 'train_classify_retry_candidate "${run_id}"')"
attribute_line="$(line_of 'train_attribute "${failing}"')"
[[ "${guard_line}" -lt "${filter_line}" ]] \
  || fail "train.sh runs the pre-existing filter (line ${filter_line}) before the guard (line ${guard_line})"
[[ "${guard_line}" -lt "${retry_line}" && "${guard_line}" -lt "${attribute_line}" ]] \
  || fail "train.sh runs the guard after retry classification or attribution"
grep -q 'train_classify_capacity_guard "${run_id}" "${failing}"' "${train_sh}" \
  || fail "the guard is not applied to the full set of failing jobs"
grep -q 'train_guard_scan_arm' "${train_sh}" \
  || fail "train.sh does not arm the evidence memo per ci-gate iteration"
grep -q 'shard_terminal}" == "1"' "${train_sh}" \
  || fail "train.sh does not bypass pre-existing subtraction for a shard-terminal job"
grep -q '_capacity_or_evidence_outcome "${TRAIN_GUARD_KIND}"' "${train_sh}" \
  || fail "train.sh does not route the guard's kind to the shared terminal outcome"
# --- 8. the escalation actually names what failed ----------------------------
# An escalation that cannot tell the author which shard blew its budget is not
# actionable, and the runbook promises the name is there.
[[ "$(train_join_job_names "$(printf 'Server Tests (Infra and Security)\nServer Tests (Core)\n')")" \
  == "Server Tests (Infra and Security), Server Tests (Core)" ]] \
  || fail "failing job names are not joined readably"
[[ "$(train_join_job_names '')" == "the selected server-test shard" ]] \
  || fail "an unnamed escalation has no neutral fallback"
outcome_body="$(sed -n '/^_capacity_or_evidence_outcome()/,/^}/p' "${train_sh}")"
[[ -n "${outcome_body}" ]] || fail "train.sh no longer defines _capacity_or_evidence_outcome"
[[ "$(grep -c 'reason=.*\${named}' <<<"${outcome_body}")" == "3" ]] \
  || fail "not every terminal escalation reason names the offending jobs"
grep -q 'train_join_job_names "\${failing}"' <<<"${outcome_body}" \
  || fail "the terminal handler does not derive names from the failing job set"
for kind in capacity shard-killed evidence-unavailable; do
  grep -Fq "${kind}" <<<"${outcome_body}" \
    || fail "the terminal handler has no branch for the '${kind}' guard kind"
done
pass "every terminal escalation names the offending jobs and covers all three kinds"

pass "train.sh resets the memo, guards before the filter/retry/attribution, and bypasses subtraction on rc 9"

printf 'capacity-ordering fixtures passed\n'
