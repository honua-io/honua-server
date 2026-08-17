#!/usr/bin/env bash
# Offline validation of the #3213 ordering guarantee.
#
# The merge train's pre-existing-failure filter subtracts a batch failure whose
# job-scoped log signatures also appear on trunk's latest CI. Those signatures
# come from a BOUNDED window at the HEAD of each job log, while a shard that
# burned its whole configured budget emits HONUA_SHARD_CAPACITY_EXHAUSTED at the
# TAIL (job 95149717187 of run 31940825557 carried it on line 47296 of 47298).
# Trunk's latest run carried the same head-of-log noise, so filtering first
# could subtract the shard and LAND a batch on tests that never finished.
#
# This validator locks in three things:
#   1. the naive head-of-log comparison really would cancel on the production
#      shape (so the guarantee is load-bearing, not decorative);
#   2. capacity exhaustion and unavailable evidence survive the filter and are
#      classified by the guard, which the orchestrator runs FIRST; and
#   3. a genuine pre-existing failure is still subtracted exactly as before.
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

CAPACITY_BATCH_RUN=31940825557
CAPACITY_TRUNK_RUN=31931541793
CAPACITY_SHARD="Server Tests (Infra and Security)"
REAL_BATCH_RUN=900001
REAL_TRUNK_RUN=900002
REAL_SHARD="Server Tests (Core)"
BLIND_BATCH_RUN=900003
BLIND_TRUNK_RUN=900004
BLIND_SHARD="Server Tests (Migration)"

# --- production-shaped logs --------------------------------------------------
# The same unrelated postgres noise leads BOTH the batch shard's log and trunk's
# latest failing shard, which is exactly why a head-bounded signature comparison
# cancels. 4000 lines put the real marker far past the 40-signature cap.
noise() {
  local i
  for ((i = 1; i <= 4000; i++)); do
    printf '2026-08-16T10:%02d:%02dZ  FATAL:  role "root" does not exist (%s)\n' \
      $(( i % 60 )) $(( (i * 7) % 60 )) "${i}"
  done
}
{
  noise
  printf "::error::HONUA_SHARD_CAPACITY_EXHAUSTED shard='Infra and Security' hit its 39m test budget while still producing output 2s ago.\n"
  printf '::error::Process completed with exit code 124.\n'
} >"${work}/capacity-batch.log"
{
  noise
  printf '::error::Process completed with exit code 1.\n'
} >"${work}/capacity-trunk.log"

# A genuine, comparable regression: identical deterministic assertion output on
# both sides, no capacity marker, no timeout.
printf '%s\n' \
  '  Failed Honua.Server.Tests.StacConformanceTests.Catalog_IsValid [1 s]' \
  '  Error Message:' \
  '   Assert.Equal() Failure: Values differ' \
  'Expected: 200' \
  'Actual:   500' \
  >"${work}/real-failure.log"

cat >"${work}/records.sh" <<'RECORDS'
#!/usr/bin/env bash
case "$1" in
  31940825557) printf '95149717187\tServer Tests (Infra and Security)\n' ;;
  31931541793) printf '95100000001\tServer Tests (Infra and Security)\n' ;;
  900001) printf '900011\tServer Tests (Core)\n' ;;
  900002) printf '900012\tServer Tests (Core)\n' ;;
  900003) printf '900013\tServer Tests (Migration)\n' ;;
  900004) printf '900014\tServer Tests (Migration)\n' ;;
esac
RECORDS
cat >"${work}/job-log.sh" <<JOBLOG
#!/usr/bin/env bash
# TRAIN_JOB_LOG_FOR_RUN is called with (run id, job name, job id).
case "\$3" in
  95149717187) cat "${work}/capacity-batch.log" ;;
  95100000001) cat "${work}/capacity-trunk.log" ;;
  900011|900012) cat "${work}/real-failure.log" ;;
  900013|900014) : ;;  # neither log surface is readable
esac
JOBLOG
cat >"${work}/reader.sh" <<READER
#!/usr/bin/env bash
# TRAIN_JOB_LOG_READER is called with the job id alone and must FAIL when no
# log surface is readable.
case "\$1" in
  95149717187) cat "${work}/capacity-batch.log" ;;
  95100000001) cat "${work}/capacity-trunk.log" ;;
  900011|900012) cat "${work}/real-failure.log" ;;
  *) exit 1 ;;
esac
READER
cat >"${work}/no-annotations.sh" <<'ANN'
#!/usr/bin/env bash
exit 1
ANN
chmod +x "${work}/records.sh" "${work}/job-log.sh" "${work}/reader.sh" "${work}/no-annotations.sh"

export TRAIN_FAILING_JOB_RECORDS_FOR_RUN="${work}/records.sh"
export TRAIN_JOB_LOG_FOR_RUN="${work}/job-log.sh"
export TRAIN_JOB_LOG_READER="${work}/reader.sh"
export TRAIN_JOB_ANNOTATION_READER="${work}/no-annotations.sh"

gh() {
  case "$*" in
    *31940825557*) printf '95149717187\t%s\tfailure\n' "${CAPACITY_SHARD}" ;;
    *900001*) printf '900011\t%s\tfailure\n' "${REAL_SHARD}" ;;
    *900003*) printf '900013\t%s\tfailure\n' "${BLIND_SHARD}" ;;
    *) return 1 ;;
  esac
}

# --- 1. the trap is real: head-of-log signatures miss the marker and cancel ---
batch_sigs="$(train_extract_failure_signatures "${CAPACITY_SHARD}" "$(cat "${work}/capacity-batch.log")")"
trunk_sigs="$(train_extract_failure_signatures "${CAPACITY_SHARD}" "$(cat "${work}/capacity-trunk.log")")"
[[ -n "${batch_sigs}" ]] || fail "the production-shaped log produced no signatures at all"
if grep -Fq 'HONUA_SHARD_CAPACITY_EXHAUSTED' <<<"${batch_sigs}"; then
  fail "fixture no longer reproduces the head-of-log blind spot; move the marker further from the head"
fi
if [[ -n "$(train_subtract_lines "${trunk_sigs}" "${batch_sigs}")" ]]; then
  fail "fixture no longer reproduces the cancelling trunk baseline; the naive filter would not have subtracted"
fi
pass "production shape reproduced: head-of-log signatures miss the tail marker and cancel against trunk"

# The bounded signature window must never `exit` mid-pipe: on a log this size
# that SIGPIPEs the upstream writer and, under `set -o pipefail`, fails the
# whole filter with a nondeterministic 141. Repeat to catch the race.
for _ in 1 2 3 4 5; do
  repeat_rc=0
  train_extract_failure_signatures "${CAPACITY_SHARD}" "$(cat "${work}/capacity-batch.log")" >/dev/null || repeat_rc=$?
  [[ "${repeat_rc}" == "0" ]] \
    || fail "signature extraction over a large log is not pipe-safe (rc=${repeat_rc})"
done
pass "signature extraction over a large log is deterministic and pipe-safe"

# --- 2. the filter itself never subtracts a capacity-exhausted shard ---------
export TRAIN_TRUNK_RUN_ID="${CAPACITY_TRUNK_RUN}"
rc=0
introduced="$(train_preexisting_filter "${CAPACITY_BATCH_RUN}" "${CAPACITY_SHARD}")" || rc=$?
[[ "${rc}" != "11" ]] || fail "the pre-existing filter subtracted a shard that never finished executing its tests"
[[ "${rc}" == "0" ]] || fail "unexpected pre-existing filter result code (rc=${rc})"
[[ "${introduced}" == "${CAPACITY_SHARD}" ]] \
  || fail "capacity-exhausted shard did not survive the filter (got '${introduced}')"
pass "capacity-exhausted shard survives the pre-existing-failure filter"

# --- 3. the guard classifies it before anything can reinterpret it -----------
: >"${side_effects}"
rc=0
train_classify_capacity_guard "${CAPACITY_BATCH_RUN}" "${CAPACITY_SHARD}" || rc=$?
[[ "${rc}" == "7" ]] || fail "the ordering guard did not classify tail-of-log capacity exhaustion (rc=${rc})"
[[ "${TRAIN_TIMEOUT_KIND}" == "capacity" ]] || fail "timeout kind was '${TRAIN_TIMEOUT_KIND}', expected capacity"
[[ ! -s "${side_effects}" ]] || fail "the read-only ordering guard performed a side effect"
pass "ordering guard classifies capacity exhaustion, read-only and without a rerun"

# --- 4. unreadable evidence is never subtracted and never attributable -------
export TRAIN_TRUNK_RUN_ID="${BLIND_TRUNK_RUN}"
rc=0
introduced="$(train_preexisting_filter "${BLIND_BATCH_RUN}" "${BLIND_SHARD}")" || rc=$?
[[ "${rc}" == "0" && "${introduced}" == "${BLIND_SHARD}" ]] \
  || fail "a failing job with no readable log was subtracted as pre-existing (rc=${rc})"
: >"${side_effects}"
rc=0
train_classify_capacity_guard "${BLIND_BATCH_RUN}" "${BLIND_SHARD}" || rc=$?
[[ "${rc}" == "8" ]] || fail "the ordering guard did not fail closed on unavailable evidence (rc=${rc})"
[[ ! -s "${side_effects}" ]] || fail "the evidence-unavailable guard performed a side effect"
pass "unavailable failure evidence survives the filter and fails closed in the guard"

# --- 5. CONTROL: a genuine pre-existing failure is still subtracted ----------
export TRAIN_TRUNK_RUN_ID="${REAL_TRUNK_RUN}"
rc=0
train_classify_capacity_guard "${REAL_BATCH_RUN}" "${REAL_SHARD}" || rc=$?
[[ "${rc}" == "0" ]] || fail "the guard hijacked an ordinary comparable failure (rc=${rc})"
rc=0
train_preexisting_filter "${REAL_BATCH_RUN}" "${REAL_SHARD}" >/dev/null || rc=$?
[[ "${rc}" == "11" ]] \
  || fail "the guard broke the pre-existing filter's purpose: a failure trunk already has was not subtracted (rc=${rc})"
pass "control: a genuine pre-existing failure is still subtracted and merged through"

unset TRAIN_TRUNK_RUN_ID

# --- 6. the orchestrator really runs the guard FIRST -------------------------
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
  || fail "train.sh runs the pre-existing filter (line ${filter_line}) before the capacity guard (line ${guard_line})"
[[ "${guard_line}" -lt "${retry_line}" && "${guard_line}" -lt "${attribute_line}" ]] \
  || fail "train.sh runs the capacity guard after retry classification or attribution"
grep -q 'train_classify_capacity_guard "${run_id}" "${failing}"' "${train_sh}" \
  || fail "the capacity guard is not applied to the full set of failing jobs"
grep -q 'rc_guard.*==.*"7"' "${train_sh}" \
  || fail "train.sh has no branch for a guard-classified capacity exhaustion"
grep -q 'rc_guard.*==.*"8"' "${train_sh}" \
  || fail "train.sh has no branch for guard-classified unavailable evidence"
grep -q '_capacity_or_evidence_outcome capacity' "${train_sh}" \
  || fail "train.sh does not route capacity exhaustion to the shared terminal outcome"
grep -q '_capacity_or_evidence_outcome evidence-unavailable' "${train_sh}" \
  || fail "train.sh does not route unavailable evidence to the shared terminal outcome"
pass "train.sh classifies capacity/evidence before the pre-existing filter, retry, and attribution"

printf 'capacity-ordering fixtures passed\n'
