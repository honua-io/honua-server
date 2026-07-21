#!/usr/bin/env bash
# Fixture harness for the merge train (Phase 1). Builds a throwaway local git
# repo with synthetic "PR" branches and asserts the train's git/decision logic
# offline â€” NO network, NO gh, NO dotnet. Each case targets one decision path:
#
#   1. clean-merge       -> two non-conflicting PRs both INCLUDED.
#   2. trunk-conflict    -> a PR conflicting with trunk is SKIPPED (zero residue).
#   3. inter-PR-conflict -> two PRs touching the same line; 2nd SKIPPED.
#   4. format-drift      -> forward-fix path (format-only failure detection +
#                           fake formatter commit; non-format failure escalates).
#   5. real-test-fail    -> attribute maps failing shard -> paths -> culprit PR,
#                           dropped; 0-suspect failure escalates whole batch.
#   6. flake             -> flake regex match triggers ONE rerun (no bisection);
#                           reproduce-twice => real.
#   7. ff-cas-race       -> concurrent trunk advance => FF push rejected +
#                           re-assemble signaled (rc=10).
#   8. fail-closed CI    -> cancelled/incomplete jobs cannot use the
#                           non-blocking failure bypass.
#
# Roll-forward auto-fix loop (offline-mocked; NO real Bedrock/AI calls):
#   Cap.1 pre-existing filter -> all-pre-existing => rc11 (land); some
#                                batch-introduced => survives (rc0); subtraction.
#   Cap.3 surgical retry      -> FQN parse (VSTest + xUnit) => dotnet-test
#                                --filter "FullyQualifiedName=..." (+ JS/Py); no
#                                FQNs => rc2 (fall back, never a full shard rerun).
#   Cap.2 escalation          -> labels every culprit train:escalated, removes
#                                train:landing, clears active_batch (phase=select).
#   Cap.2b green rerun recovery -> clears stale train:escalated + stamps CI Gate
#                                  only for still-open escalated batch members.
#   Cap.4 autofix gate        -> TRAIN_AUTOFIX=0 inert (escalate like today);
#                                TRAIN_AUTOFIX=1 fix path (mock agent commits,
#                                no bot attribution; cap enforced; decline=>escalate).
#
# Run: scripts/ci/merge-train/fixtures/validate-merge-train.sh
# Exit 0 = all pass.

set -euo pipefail

HARNESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TRAIN_DIR="$(cd "${HARNESS_DIR}/.." && pwd)"

PASS=0; FAIL=0
ok()   { printf '  PASS: %s\n' "$1"; PASS=$((PASS+1)); }
bad()  { printf '  FAIL: %s\n' "$1"; FAIL=$((FAIL+1)); }
assert_eq() { [[ "$2" == "$3" ]] && ok "$1" || { bad "$1 (got [$2] want [$3])"; }; }
assert_contains() { grep -Fq -- "$3" <<<"$2" && ok "$1" || bad "$1 (missing [$3] in [$2])"; }
assert_not_contains() { ! grep -Fq -- "$3" <<<"$2" && ok "$1" || bad "$1 (unexpected [$3])"; }

# --- build a throwaway repo with a fake remote -------------------------------
SCRATCH="$(mktemp -d)"
trap 'rm -rf "${SCRATCH}"' EXIT
export TRAIN_APPLY=0
export TRAIN_BASE_BRANCH=trunk
export TRAIN_REMOTE=origin

UPSTREAM="${SCRATCH}/upstream.git"
WORK="${SCRATCH}/work"
git init -q --bare "${UPSTREAM}"
git init -q "${WORK}"
cd "${WORK}"
git config user.email "test@honua.io"; git config user.name "Test"
git remote add origin "${UPSTREAM}"

# Seed trunk with a couple of files mirroring real shard paths so attribution
# can be exercised against ci-shards.json prefixes.
mkdir -p src/Honua.Protocols.GeoServices/FeatureServer src/Honua.Protocols.OgcApi/Features
printf 'line1\nline2\nline3\n' > shared.txt
printf 'a\n' > src/Honua.Protocols.GeoServices/FeatureServer/fs.cs
printf 'b\n' > src/Honua.Protocols.OgcApi/Features/ogc.cs
git add -A; git commit -qm "seed trunk"
git push -q origin HEAD:trunk
TRUNK_SHA="$(git rev-parse HEAD)"

# Helper to create a PR branch off trunk with a given mutation, pushed to a
# local ref the train can fetch via FETCH_HEAD substitution.
make_pr() {  # make_pr <name> <file> <content-append-or-cmd>
  local name="$1" file="$2"; shift 2
  git checkout -q -B "${name}" origin/trunk
  "$@"
  git add -A; git commit -qm "pr ${name}"
  git push -q origin "HEAD:refs/heads/${name}"
  git checkout -q origin/trunk 2>/dev/null || git checkout -q trunk
}

# The assemble step fetches pull/<n>/head; in fixtures we override the ref
# resolver to map a PR "number" to its local branch ref.
declare -A PR_REF
fake_fetch_pr_ref() { git rev-parse "origin/${PR_REF[$1]}"; }
export -f fake_fetch_pr_ref
# Bash can't export assoc arrays; resolve via a file map instead.
PRMAP="${SCRATCH}/prmap"; : >"${PRMAP}"
resolve_pr_ref() { awk -v n="$1" '$1==n{print $2}' "${PRMAP}"; }
export TRAIN_REPO_ROOT="${WORK}"

# shellcheck source=../lib.sh
. "${TRAIN_DIR}/lib.sh"
. "${TRAIN_DIR}/bedrock-invoke.sh"
. "${TRAIN_DIR}/assemble.sh"
. "${TRAIN_DIR}/smart-ci.sh"
. "${TRAIN_DIR}/forward-fix.sh"
. "${TRAIN_DIR}/classify-flake.sh"
. "${TRAIN_DIR}/surgical.sh"
. "${TRAIN_DIR}/preexisting.sh"
. "${TRAIN_DIR}/autofix.sh"
. "${TRAIN_DIR}/attribute.sh"
. "${TRAIN_DIR}/land.sh"
. "${TRAIN_DIR}/select.sh"
. "${TRAIN_DIR}/state.sh"
. "${TRAIN_DIR}/recovery.sh"

# Exact-head admission seam shared by selection and pre-land fixtures.
__fixture_admission() {
  local pr="$1" head state=OPEN draft=false labels='[]' gate=SUCCESS review=SUCCESS threads='[]' reviews
  head="$(awk -v n="${pr}" '$1==n{print $2}' "${TRAIN_INCLUDED_FILE}" 2>/dev/null || true)"
  if [[ -z "${head}" && -n "${TRAIN_PR_LIST_JSON:-}" ]]; then
    head="$(jq -r --argjson n "${pr}" '.[] | select(.number==$n) | .headRefOid' <<<"${TRAIN_PR_LIST_JSON}")"
  fi
  reviews="[{\"author\":{\"login\":\"chatgpt-codex-connector\"},\"body\":\"Codex Review\",\"submittedAt\":\"2026-01-02T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\",\"state\":\"COMMENTED\",\"commit\":{\"oid\":\"${head}\"}}]"
  case "${ADMISSION_CASE:-ok}" in
    gate-fail) gate=FAILURE ;;
    review-fail) review=FAILURE ;;
    unresolved) threads="[{\"isResolved\":false,\"comments\":{\"nodes\":[{\"author\":{\"login\":\"chatgpt-codex-connector[bot]\"},\"commit\":{\"oid\":\"${head}\"}}]}}]" ;;
    negative-review) reviews="[{\"author\":{\"login\":\"chatgpt-codex-connector\"},\"body\":\"Codex Review\",\"submittedAt\":\"2026-01-02T00:00:00Z\",\"updatedAt\":\"2026-01-02T00:00:00Z\",\"state\":\"CHANGES_REQUESTED\",\"commit\":{\"oid\":\"${head}\"}}]" ;;
    held) labels='[{"name":"train:hold"}]' ;;
    escalated) labels='[{"name":"train:escalated"}]' ;;
    draft) draft=true ;;
    closed) state=CLOSED ;;
    advanced) head=advanced ;;
  esac
  jq -nc --argjson n "${pr}" --arg head "${head}" --arg state "${state}" \
    --argjson draft "${draft}" --argjson labels "${labels}" --arg gate "${gate}" \
    --arg review "${review}" --argjson threads "${threads}" --argjson reviews "${reviews}" \
    '{number:$n,state:$state,isDraft:$draft,headRefOid:$head,labels:$labels,labelsTruncated:false,reviews:$reviews,reviewsTruncated:false,reviewThreads:$threads,reviewThreadsTruncated:false,checksTruncated:false,statusCheckRollup:[{__typename:"CheckRun",name:"PR Gate",status:"COMPLETED",conclusion:$gate},{__typename:"StatusContext",context:"Review Gate",state:$review}]}'
}
export -f __fixture_admission
export TRAIN_ADMISSION_JSON_FOR_PR=__fixture_admission
__fixture_reactions() { printf '[]\n'; }
__fixture_observed_at() { printf 'null\n'; }
__fixture_publish_review_gate() {
  [[ -n "${FIXTURE_REVIEW_STATUS_RECORD:-}" ]] && printf '%s\t%s\t%s\t%s\n' "$@" >>"${FIXTURE_REVIEW_STATUS_RECORD}"
  return 0
}
export -f __fixture_reactions __fixture_observed_at __fixture_publish_review_gate
export TRAIN_ADMISSION_REACTIONS_FOR_PR=__fixture_reactions
export TRAIN_ADMISSION_OBSERVED_AT_FOR_HEAD=__fixture_observed_at
export TRAIN_REVIEW_GATE_STATUS_PUBLISHER=__fixture_publish_review_gate

# Point shard config + targeted script at the REAL repo files so attribution and
# smart-CI exercise production routing.
REAL_ROOT="$(cd "${TRAIN_DIR}/../../.." && pwd)"
export TRAIN_SHARDS_CONFIG="${REAL_ROOT}/.github/ci-shards.json"
export TRAIN_TARGETED_SCRIPT="${REAL_ROOT}/scripts/ci/honua-server-targeted-tests.sh"

# The train fetches "FETCH_HEAD" by default; override fetch to resolve our map.
export TRAIN_FETCH_PR_REF=__pr_ref
__pr_ref() { resolve_pr_ref "$1"; }
export -f __pr_ref resolve_pr_ref 2>/dev/null || true

INC="${SCRATCH}/inc.tsv"; SKP="${SCRATCH}/skp.tsv"
export TRAIN_INCLUDED_FILE="${INC}" TRAIN_SKIPPED_FILE="${SKP}"

echo "== Case 1: clean-merge (two non-conflicting PRs both INCLUDED) =="
make_pr pr101 src/Honua.Protocols.GeoServices/FeatureServer/fs.cs \
  bash -c 'echo newfs >> src/Honua.Protocols.GeoServices/FeatureServer/fs.cs'
echo "101 pr101" >>"${PRMAP}"
make_pr pr102 src/Honua.Protocols.OgcApi/Features/ogc.cs \
  bash -c 'echo newogc >> src/Honua.Protocols.OgcApi/Features/ogc.cs'
echo "102 pr102" >>"${PRMAP}"
git fetch -q origin trunk
unset TRAIN_BATCH_BRANCH
BATCH="$(train_assemble "${TRUNK_SHA:0:7}" 101 102)"
inc_list="$(cut -f1 "${INC}" | tr '\n' ' ')"
assert_contains "clean: #101 included" "${inc_list}" "101"
assert_contains "clean: #102 included" "${inc_list}" "102"
assert_eq "clean: no skips" "$(wc -l <"${SKP}" | tr -d ' ')" "0"

echo "== Case 2: trunk-conflict (PR conflicts with trunk -> SKIPPED, zero residue) =="
# Advance trunk so a PR built on the OLD trunk conflicts on the same line.
git checkout -q -B trunk-adv origin/trunk
printf 'line1\nTRUNK-CHANGED\nline3\n' > shared.txt
git add -A; git commit -qm "advance trunk shared"
git push -q origin HEAD:trunk
git fetch -q origin trunk
NEW_TRUNK="$(git rev-parse origin/trunk)"
# PR built off OLD trunk that edits the same line differently.
git checkout -q -B pr201 "${TRUNK_SHA}"
printf 'line1\nPR-CHANGED\nline3\n' > shared.txt
git add -A; git commit -qm "pr201 conflict"
git push -q origin HEAD:refs/heads/pr201
echo "201 pr201" >>"${PRMAP}"
git checkout -q origin/trunk
BATCH2="$(train_assemble "${NEW_TRUNK:0:7}" 201)"
skp_list="$(cut -f1 "${SKP}" | tr '\n' ' ')"
assert_contains "trunk-conflict: #201 skipped" "${skp_list}" "201"
# Zero residue: working tree clean, no MERGE_HEAD.
git diff --quiet && git diff --cached --quiet && ok "trunk-conflict: zero residue (clean tree)" || bad "trunk-conflict: residue left"
[[ ! -f "${WORK}/.git/MERGE_HEAD" ]] && ok "trunk-conflict: no MERGE_HEAD" || bad "trunk-conflict: MERGE_HEAD remains"

echo "== Case 3: inter-PR-conflict (2nd PR conflicts with 1st -> SKIPPED) =="
git fetch -q origin trunk
BASE3="$(git rev-parse origin/trunk)"
git checkout -q -B pr301 origin/trunk
printf 'X1\nX2\nX3\n' > conflictzone.txt
git add -A; git commit -qm pr301; git push -q origin HEAD:refs/heads/pr301
echo "301 pr301" >>"${PRMAP}"
git checkout -q -B pr302 origin/trunk
printf 'Y1\nY2\nY3\n' > conflictzone.txt
git add -A; git commit -qm pr302; git push -q origin HEAD:refs/heads/pr302
echo "302 pr302" >>"${PRMAP}"
git checkout -q origin/trunk
BATCH3="$(train_assemble "${BASE3:0:7}" 301 302)"
inc3="$(cut -f1 "${INC}" | tr '\n' ' ')"; skp3="$(cut -f1 "${SKP}" | tr '\n' ' ')"
assert_contains "inter-PR: #301 included" "${inc3}" "301"
assert_contains "inter-PR: #302 skipped" "${skp3}" "302"
git diff --quiet && ok "inter-PR: zero residue after 2nd abort" || bad "inter-PR: residue"

echo "== Case 4: format-drift (forward-fix path) =="
# Only-format-failure detection.
train_is_format_only_failure "build / Format Verification" && ok "fwdfix: single format job => format-only" || bad "fwdfix: should be format-only"
train_is_format_only_failure $'build / Format Verification\nserver-tests (Core)' && bad "fwdfix: multi-failure must NOT be format-only" || ok "fwdfix: multi-failure not format-only"
train_is_format_only_failure "server-tests (Core)" && bad "fwdfix: non-format must NOT be format-only" || ok "fwdfix: non-format not format-only"
# Real-world: the format job ALWAYS co-fails with the CI Gate (and often Test Suite
# Summary) aggregators. After stripping non-blocking/aggregator jobs the sole real
# failure is the format job => format-only (this is what was broken: it escalated).
train_is_format_only_failure $'Build & Format Check\nCI Gate' && ok "fwdfix: format+CI Gate aggregator => format-only" || bad "fwdfix: format+aggregator must be format-only"
train_is_format_only_failure $'Build & Format Check\nCI Gate\nTest Suite Summary' && ok "fwdfix: format+2 aggregators => format-only" || bad "fwdfix: format+aggregators must be format-only"
train_is_format_only_failure $'Build & Format Check\nCI Gate\nServer Tests (OData Core)' && bad "fwdfix: format+REAL shard must NOT be format-only" || ok "fwdfix: format+real shard not format-only"
train_is_format_only_failure $'CI Gate\nTest Suite Summary' && bad "fwdfix: aggregators-only must NOT be format-only" || ok "fwdfix: aggregators-only not format-only"
# Fake formatter that produces a change -> forward-fix commits it.
git fetch -q origin trunk; git checkout -q -B fwdfix-batch origin/trunk
export TRAIN_FORMAT_CMD=__fake_fmt
__fake_fmt() { echo "reformatted" >> shared.txt; }
export -f __fake_fmt
before="$(git rev-parse HEAD)"
if train_forward_fix fwdfix-batch 0; then
  after="$(git rev-parse HEAD)"
  [[ "${before}" != "${after}" ]] && ok "fwdfix: produced a commit" || bad "fwdfix: no commit"
  git log -1 --pretty=%s | grep -Fq "style: dotnet format (train forward-fix)" && ok "fwdfix: correct commit subject" || bad "fwdfix: wrong subject"
  git log -1 --pretty='%an <%ae>%n%b' | grep -Eqi 'co-authored-by|generated with|ðŸ¤–' && bad "fwdfix: bot attribution present" || ok "fwdfix: no bot attribution"
else
  bad "fwdfix: should have applied a change"
fi
# Cap: at cap, returns non-zero.
__fake_fmt_noop() { :; }
export TRAIN_FORMAT_CMD=__fake_fmt_noop; export -f __fake_fmt_noop
train_forward_fix fwdfix-batch 2 && bad "fwdfix: cap not enforced" || ok "fwdfix: cap (2) enforced"
unset TRAIN_FORMAT_CMD

echo "== Case 4b: non-blocking bypass fails closed on incomplete CI =="
export TRAIN_CI_JOBS_READER=__ci_jobs
__ci_jobs() {
  case "${CI_JOBS_CASE}" in
    safe)
      printf 'success\tBuild & Format Check\nsuccess\tServer Tests (Core)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n'
      ;;
    cancelled)
      printf 'success\tBuild & Format Check\ncancelled\tServer Tests (Core)\nfailure\tTest Suite Summary\ncancelled\tCI Gate\n'
      ;;
    missing-shards)
      printf 'failure\tTest Suite Summary\nfailure\tCI Gate\n'
      ;;
    partial-missing)
      printf 'success\tBuild & Format Check\nsuccess\tServer Tests (Core)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n'
      ;;
    blocking-skipped)
      printf 'success\tBuild & Format Check\nsuccess\tServer Tests (Core)\nskipped\tServer Tests (Workflow Packages)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n'
      ;;
    timed-out)
      printf 'success\tBuild & Format Check\ntimed_out\tServer Tests (Core)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n'
      ;;
  esac
}
export -f __ci_jobs
SAFE_DESCRIPTOR='{"run_all":false,"shards":["Core"]}'
TWO_SHARD_DESCRIPTOR='{"run_all":false,"shards":["Core","Workflow Packages"]}'
CI_JOBS_CASE=safe train_nonblocking_failures_are_safe 1 "${SAFE_DESCRIPTOR}" \
  && ok "ci-safe: optional failures with successful blocking jobs may land" \
  || bad "ci-safe: valid optional-only failure was rejected"
CI_JOBS_CASE=safe train_ci_jobs_are_terminal 1 \
  && ok "ci-safe: success/failure jobs are terminal evidence" \
  || bad "ci-safe: terminal job set was rejected"
CI_JOBS_CASE=cancelled train_nonblocking_failures_are_safe 2 "${SAFE_DESCRIPTOR}" \
  && bad "ci-safe: cancelled gate/shard must fail closed" \
  || ok "ci-safe: cancelled gate/shard fails closed"
CI_JOBS_CASE=cancelled train_ci_jobs_are_terminal 2 \
  && bad "ci-safe: cancelled jobs must make the run unusable" \
  || ok "ci-safe: cancelled jobs make the run unusable"
CI_JOBS_CASE=missing-shards train_nonblocking_failures_are_safe 3 "${SAFE_DESCRIPTOR}" \
  && bad "ci-safe: missing blocking jobs must fail closed" \
  || ok "ci-safe: missing blocking jobs fail closed"
CI_JOBS_CASE=timed-out train_nonblocking_failures_are_safe 4 "${SAFE_DESCRIPTOR}" \
  && bad "ci-safe: timed-out shard must fail closed" \
  || ok "ci-safe: timed-out shard fails closed"
CI_JOBS_CASE=partial-missing train_expected_shards_are_classifiable 5 "${TWO_SHARD_DESCRIPTOR}" \
  && bad "ci-safe: partially missing selected shard must fail closed" \
  || ok "ci-safe: partially missing selected shard fails closed"
CI_JOBS_CASE=blocking-skipped train_expected_shards_are_classifiable 6 "${TWO_SHARD_DESCRIPTOR}" \
  && bad "ci-safe: skipped selected shard must fail closed" \
  || ok "ci-safe: skipped selected shard fails closed"
unset TRAIN_CI_JOBS_READER CI_JOBS_CASE SAFE_DESCRIPTOR TWO_SHARD_DESCRIPTOR

echo "== Case 5: real-test-fail (attribute + drop; 0-suspect escalates) =="
# Build a 2-PR batch where pr401 touches a FeatureServer path and pr402 an OGC
# path; a failing "FeatureServer Endpoints" shard must attribute to pr401 only.
: >"${INC}"
printf '401\t%s\n' "$(git rev-parse origin/trunk)" >>"${INC}"   # head placeholder (overridden below)
printf '402\t%s\n' "$(git rev-parse origin/trunk)" >>"${INC}"
# Override per-PR diffs to deterministic file lists.
export TRAIN_DIFF_FOR_PR=__diff_for_pr
__diff_for_pr() {
  case "$1" in
    401) printf 'src/Honua.Protocols.GeoServices/FeatureServer/fs.cs\n' ;;
    402) printf 'src/Honua.Protocols.OgcApi/Features/ogc.cs\n' ;;
  esac
}
export -f __diff_for_pr
culprits="$(train_attribute "server-tests (FeatureServer Endpoints)" "${INC}")"
assert_eq "attribute: single suspect => #401" "$(tr '\n' ' ' <<<"${culprits}" | xargs)" "401"
# 0-suspect: a failing shard whose paths no PR touches => ESCALATE_BATCH.
__diff_for_pr_none() { printf 'docs/readme.md\n'; }
export TRAIN_DIFF_FOR_PR=__diff_for_pr_none; export -f __diff_for_pr_none
esc="$(train_attribute "server-tests (FeatureServer Endpoints)" "${INC}")"
assert_eq "attribute: 0 suspects => ESCALATE_BATCH" "${esc}" "ESCALATE_BATCH"
# >=2 suspects: both PRs touch FeatureServer paths => both dropped.
__diff_for_pr_both() { printf 'src/Honua.Protocols.GeoServices/FeatureServer/x.cs\n'; }
export TRAIN_DIFF_FOR_PR=__diff_for_pr_both; export -f __diff_for_pr_both
both="$(train_attribute "server-tests (FeatureServer Endpoints)" "${INC}" | sort | tr '\n' ' ' | xargs)"
assert_eq "attribute: >=2 suspects => drop all" "${both}" "401 402"
unset TRAIN_DIFF_FOR_PR

echo "== Case 6: flake (single rerun, no bisection; reproduce-twice => real) =="
export TRAIN_RUN_LOG_TEXT="... ERROR 40P01: deadlock detected ..."
train_run_logs_match_flake 999 && ok "flake: 40P01 recognized" || bad "flake: 40P01 missed"
export TRAIN_RUN_LOG_TEXT="Testcontainers timed out waiting for ryuk"
train_run_logs_match_flake 999 && ok "flake: testcontainers/ryuk recognized" || bad "flake: missed"
export TRAIN_RUN_LOG_TEXT="Assert.Equal() Failure: expected 3 actual 4"
train_run_logs_match_flake 999 && bad "flake: real assertion misclassified" || ok "flake: real assertion not a flake"
# classify_flake: under cap with a flake => returns 0 (rerun once, dry-run logs).
export TRAIN_RUN_LOG_TEXT="40P01 deadlock detected"
train_classify_flake 999 0 && ok "flake: first occurrence => rerun (rc0)" || bad "flake: should rerun once"
# At cap (reproduced) => returns 1 (treat as real). No bisection ever invoked.
train_classify_flake 999 1 && bad "flake: reproduced should be real" || ok "flake: reproduced => real (rc1)"
unset TRAIN_RUN_LOG_TEXT

echo "== Case 7: ff-cas-race (concurrent trunk advance => FF push rejected + re-assemble) =="
LABEL_CLEAR_RECORD="${SCRATCH}/cleared-landing-labels"; : >"${LABEL_CLEAR_RECORD}"
LABEL_CLEAR_FAIL=0
__land_clear_label() {
  [[ "${LABEL_CLEAR_FAIL}" == "1" ]] && return 42
  printf '%s\n' "$1" >>"${LABEL_CLEAR_RECORD}"
}
export LABEL_CLEAR_RECORD LABEL_CLEAR_FAIL
export -f __land_clear_label
export TRAIN_LAND_CLEAR_LABEL_CMD=__land_clear_label
git fetch -q origin trunk
RACE_BASE="$(git rev-parse origin/trunk)"
git checkout -q -B race-batch origin/trunk
echo "batchwork" >> shared.txt; git add -A; git commit -qm "batch work"
git push -q origin HEAD:refs/heads/race-batch
# Now SOMEONE ELSE advances trunk after we captured RACE_BASE.
git checkout -q -B race-interloper origin/trunk
echo "interloper" >> other.txt; git add -A; git commit -qm "interloper advances trunk"
git push -q origin HEAD:trunk
git checkout -q race-batch
: >"${INC}"; printf '701\t%s\n' "$(git rev-parse HEAD)" >>"${INC}"
# Land with the STALE base => CAS detects trunk moved => rc 10 (re-assemble).
set +e
TRAIN_APPLY=1 train_land race-batch "${RACE_BASE}" "${INC}"; rc=$?
set -e
assert_eq "ff-cas: stale-base land => rc10 (re-assemble, no land)" "${rc}" "10"
race_batch_sha="$(git rev-parse race-batch)"
git push -q origin race-batch:refs/heads/train/batch/race/1
export TRAIN_STATE_ISSUE_OVERRIDE=1
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n'"{\"active_batch\":{\"branch\":\"train/batch/race/1\",\"trunk_base\":\"${RACE_BASE}\",\"included\":[701],\"included_heads\":[{\"number\":701,\"head\":\"${race_batch_sha}\"}],\"batch_sha\":\"${race_batch_sha}\",\"phase\":\"pre-land-cleanup\"}}"$'\n```'
set +e; train_restore_post_land; rc_restart_moved=$?; set -e
assert_eq "pre-CAS restart: trunk-moved cleanup completes" "${rc_restart_moved}" "4"
assert_contains "pre-CAS restart: trunk-moved member label cleared" "$(cat "${LABEL_CLEAR_RECORD}")" "701"
# And a genuine FF push (base current) with TRAIN_APPLY=1 against a non-FF branch
# is server-rejected => rc10 too. Simulate by making race-batch NOT a descendant.
git fetch -q origin trunk
CUR_TRUNK="$(git rev-parse origin/trunk)"
git checkout -q -B nonff-batch "${RACE_BASE}"   # branched off the OLD trunk
echo "diverged" >> shared.txt; git add -A; git commit -qm "diverged work"
: >"${INC}"; printf '702\t%s\n' "$(git rev-parse HEAD)" >>"${INC}"
set +e
TRAIN_APPLY=1 train_land nonff-batch "${CUR_TRUNK}" "${INC}"; rc2=$?
set -e
assert_eq "ff-cas: non-FF push server-rejected => rc10" "${rc2}" "10"

# Admission failure before push uses the same durable cleanup phase and restart.
git checkout -q -B train/batch/admission/1 origin/trunk
echo "admission defer" >> admission-defer.txt; git add -A; git commit -qm "admission defer"
admission_batch_sha="$(git rev-parse HEAD)"
git push -q origin HEAD:refs/heads/train/batch/admission/1
: >"${INC}"; printf '706\t%s\n' "${admission_batch_sha}" >>"${INC}"
export ADMISSION_CASE=advanced
set +e; TRAIN_APPLY=1 train_land train/batch/admission/1 "${CUR_TRUNK}" "${INC}"; rc_admission=$?; set -e
assert_eq "pre-CAS admission: failure defers without push" "${rc_admission}" "10"
unset ADMISSION_CASE
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n'"{\"active_batch\":{\"branch\":\"train/batch/admission/1\",\"trunk_base\":\"${CUR_TRUNK}\",\"included\":[706],\"included_heads\":[{\"number\":706,\"head\":\"${admission_batch_sha}\"}],\"batch_sha\":\"${admission_batch_sha}\",\"phase\":\"pre-land-cleanup\"}}"$'\n```'
set +e; train_restore_post_land; rc_restart_admission=$?; set -e
assert_eq "pre-CAS restart: admission-failure cleanup completes" "${rc_restart_admission}" "4"
assert_contains "pre-CAS restart: admission-failure label cleared" "$(cat "${LABEL_CLEAR_RECORD}")" "706"
assert_contains "pre-CAS orchestrator: cleanup returns to select at observed trunk" "$(cat "${TRAIN_DIR}/train.sh")" \
  '_write_state "" "${TRAIN_POST_LAND_OBSERVED_TRUNK}" "" "select" "" 0 0 null'
assert_contains "post-land orchestrator: observed descendant remains current trunk" "$(cat "${TRAIN_DIR}/train.sh")" \
  '_write_state "" "${TRAIN_POST_LAND_OBSERVED_TRUNK}" "" "done" "" 0 0 "${TRAIN_POST_LAND_BATCH_SHA}"'

# The server may accept a push even when the client reports nonzero. Refetching
# origin must recognize the exact batch and continue without pre-CAS cleanup.
git fetch -q origin trunk
AMBIG_BASE="$(git rev-parse origin/trunk)"
git checkout -q -B push-ambiguous origin/trunk
echo "accepted despite client error" >> push-ambiguous.txt; git add -A; git commit -qm "ambiguous accepted batch"
ambig_sha="$(git rev-parse HEAD)"
: >"${INC}"; printf '707\t%s\n' "${ambig_sha}" >>"${INC}"
__push_accept_then_error() {
  git -C "${WORK}" push origin "$1:$2" >/dev/null
  return 42
}
__land_info_ambiguous_merged() { printf '%s\tMERGED\n' "${ambig_sha}"; }
export WORK ambig_sha
export -f __push_accept_then_error __land_info_ambiguous_merged
export TRAIN_LAND_PUSH_CMD=__push_accept_then_error
export TRAIN_LAND_PR_INFO_FOR=__land_info_ambiguous_merged
TRAIN_LAND_FINALIZED_FILE="${SCRATCH}/ambig-finalized" TRAIN_LAND_ADVANCED_FILE="${SCRATCH}/ambig-advanced" TRAIN_LAND_PENDING_FILE="${SCRATCH}/ambig-pending" \
  TRAIN_APPLY=1 train_land push-ambiguous "${AMBIG_BASE}" "${INC}"; rc_ambig=$?
assert_eq "push ambiguity: server-accepted/client-nonzero lands" "${rc_ambig}" "0"
git fetch -q origin trunk
assert_eq "push ambiguity: origin confirms exact accepted batch" "$(git rev-parse origin/trunk)" "${ambig_sha}"
assert_contains "push ambiguity: member finalizes only after origin proof" "$(cat "${SCRATCH}/ambig-finalized")" "707"
unset TRAIN_LAND_PUSH_CMD TRAIN_LAND_PR_INFO_FOR

# Client nonzero plus an unavailable verification read is irreducibly ambiguous:
# rc11 retains phase=land and performs no label rollback.
git checkout -q -B push-fetch-fail origin/trunk
echo "ambiguous unavailable" >> push-fetch-fail.txt; git add -A; git commit -qm "ambiguous unavailable"
fetch_fail_sha="$(git rev-parse HEAD)"
: >"${INC}"; printf '709\t%s\n' "${fetch_fail_sha}" >>"${INC}"
__push_error_only() { return 42; }
__observe_trunk_fail() { return 1; }
export -f __push_error_only __observe_trunk_fail
export TRAIN_LAND_PUSH_CMD=__push_error_only TRAIN_LAND_REMOTE_TRUNK_OBSERVER=__observe_trunk_fail
labels_before_fetch_fail="$(wc -l <"${LABEL_CLEAR_RECORD}" | tr -d ' ')"
set +e; TRAIN_APPLY=1 train_land push-fetch-fail "${ambig_sha}" "${INC}"; rc_fetch_fail=$?; set -e
assert_eq "push ambiguity unavailable: returns rc11" "${rc_fetch_fail}" "11"
assert_eq "push ambiguity unavailable: does not clear landing labels" "$(wc -l <"${LABEL_CLEAR_RECORD}" | tr -d ' ')" "${labels_before_fetch_fail}"
assert_contains "push ambiguity unavailable: orchestrator retains land phase" "$(cat "${TRAIN_DIR}/train.sh")" "retaining durable phase=land"
unset TRAIN_LAND_PUSH_CMD TRAIN_LAND_REMOTE_TRUNK_OBSERVER

# Client nonzero can also race a later trunk advance. A descendant proves the
# batch landed, but exact-trunk post-land work is deferred to durable recovery.
git checkout -q -B push-descendant origin/trunk
echo "ambiguous descendant" >> push-descendant.txt; git add -A; git commit -qm "ambiguous descendant"
push_desc_sha="$(git rev-parse HEAD)"
: >"${INC}"; printf '710\t%s\n' "${push_desc_sha}" >>"${INC}"
__push_accept_then_descend() {
  git -C "${WORK}" push origin "$1:$2" >/dev/null
  local tree child
  tree="$(git -C "${WORK}" rev-parse "$1^{tree}")"
  child="$(printf 'post-accept descendant\n' | git -C "${WORK}" commit-tree "${tree}" -p "$(git -C "${WORK}" rev-parse "$1")")"
  git -C "${WORK}" push origin "${child}:$2" >/dev/null
  return 42
}
export -f __push_accept_then_descend
export TRAIN_LAND_PUSH_CMD=__push_accept_then_descend
set +e; TRAIN_APPLY=1 train_land push-descendant "${ambig_sha}" "${INC}"; rc_push_desc=$?; set -e
assert_eq "push ambiguity descendant: retains durable land intent" "${rc_push_desc}" "11"
git fetch -q origin trunk
git merge-base --is-ancestor "${push_desc_sha}" origin/trunk \
  && ok "push ambiguity descendant: origin proves batch ancestry" \
  || bad "push ambiguity descendant: batch ancestry missing"
unset TRAIN_LAND_PUSH_CMD

# Crash-state `land` plus trunk descendant means the batch did land and another
# authority advanced later; recover observation-only rather than pre-CAS cleanup.
git push -q origin push-ambiguous:refs/heads/train/batch/descendant/1
git checkout -q -B descendant-after-batch origin/trunk
echo "later trunk commit" >> later-trunk.txt; git add -A; git commit -qm "later trunk advance"
descendant_sha="$(git rev-parse HEAD)"
git push -q origin HEAD:trunk
admitted_descendant=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n'"{\"active_batch\":{\"branch\":\"train/batch/descendant/1\",\"trunk_base\":\"${AMBIG_BASE}\",\"included\":[708],\"included_heads\":[{\"number\":708,\"head\":\"${admitted_descendant}\"}],\"batch_sha\":\"${ambig_sha}\",\"phase\":\"land\"}}"$'\n```'
__land_info_descendant_closed() { printf '%s\tCLOSED\n' "${admitted_descendant}"; }
export admitted_descendant
export -f __land_info_descendant_closed
export TRAIN_LAND_PR_INFO_FOR=__land_info_descendant_closed
TRAIN_LAND_FINALIZED_FILE="${SCRATCH}/desc-finalized" TRAIN_LAND_ADVANCED_FILE="${SCRATCH}/desc-advanced" TRAIN_LAND_PENDING_FILE="${SCRATCH}/desc-pending"
export TRAIN_LAND_FINALIZED_FILE TRAIN_LAND_ADVANCED_FILE TRAIN_LAND_PENDING_FILE
train_restore_post_land; rc_descendant=$?
assert_eq "land restart descendant: recognized as already landed" "${rc_descendant}" "0"
git fetch -q origin trunk
assert_eq "land restart descendant: observation does not move trunk" "$(git rev-parse origin/trunk)" "${descendant_sha}"
assert_contains "land restart descendant: member bookkeeping reconciled" "$(cat "${TRAIN_LAND_FINALIZED_FILE}")" "708"
unset TRAIN_LAND_PR_INFO_FOR TRAIN_STATE_BODY_OVERRIDE

# A PR can advance after admission while the immutable batch is being pushed.
# The exact batch SHA still lands, but the advanced PR must remain unfinalized.
git fetch -q origin trunk
CLOSE_BASE="$(git rev-parse origin/trunk)"
git checkout -q -B close-race origin/trunk
echo "close race" >> close-race.txt; git add -A; git commit -qm "close race batch"
close_sha="$(git rev-parse HEAD)"
: >"${INC}"; printf '703\t%s\n' "${close_sha}" >>"${INC}"
MERGE_ARGS="${SCRATCH}/merge-args"
__land_info_advanced_before_finalize() { printf 'new-head-703\tOPEN\n'; }
__merge_must_not_run() { printf 'called\n' >"${MERGE_ARGS}"; return 1; }
export -f __land_info_advanced_before_finalize __merge_must_not_run
export TRAIN_LAND_PR_INFO_FOR=__land_info_advanced_before_finalize
export TRAIN_LAND_PR_MERGE_CMD=__merge_must_not_run
export TRAIN_LAND_CLEAR_LABEL_CMD=__land_clear_label
: >"${MERGE_ARGS}"
export LABEL_CLEAR_FAIL=1
TRAIN_LAND_FINALIZED_FILE="${SCRATCH}/finalized-before" TRAIN_LAND_ADVANCED_FILE="${SCRATCH}/advanced-before" TRAIN_LAND_PENDING_FILE="${SCRATCH}/pending-before" \
  TRAIN_APPLY=1 train_land close-race "${CLOSE_BASE}" "${INC}"; rc3=$?
export LABEL_CLEAR_FAIL=0
assert_eq "snapshot race: immutable batch lands successfully" "${rc3}" "0"
git fetch -q origin trunk
assert_eq "snapshot race: trunk is exact tested batch SHA" "$(git rev-parse origin/trunk)" "${close_sha}"
assert_eq "snapshot race: advanced member is not finalized" "$([[ -s "${SCRATCH}/finalized-before" ]] && echo yes || echo no)" "no"
assert_contains "snapshot race: advanced member remains recorded for delta" "$(cat "${SCRATCH}/advanced-before")" $'703\t'"${close_sha}"$'\tnew-head-703'
assert_eq "snapshot race: merge command never runs for advanced member" "$([[ -s "${MERGE_ARGS}" ]] && echo yes || echo no)" "no"
assert_contains "snapshot race: failed advanced-label cleanup is durable" "$(cat "${SCRATCH}/pending-before")" "advanced-label-cleanup"
git push -q origin close-race:refs/heads/train/batch/close/1
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n'"{\"active_batch\":{\"branch\":\"train/batch/close/1\",\"trunk_base\":\"${CLOSE_BASE}\",\"included\":[703],\"included_heads\":[{\"number\":703,\"head\":\"${close_sha}\"}],\"batch_sha\":\"${close_sha}\",\"phase\":\"post-land-finalize\"}}"$'\n```'
set +e; train_restore_post_land; rc_label_restart=$?; set -e
assert_eq "snapshot race restart: advanced-label cleanup retries" "${rc_label_restart}" "0"
assert_contains "snapshot race restart: advanced member label eventually clears" "$(cat "${LABEL_CLEAR_RECORD}")" "703"

# No post-CAS endpoint may be capable of writing trunk.
assert_not_contains "post-CAS: PR merge endpoint removed" "$(cat "${TRAIN_DIR}/land.sh")" "gh pr merge"
finalize_surface="$(awk '/^train_finalize_landed_members\(\)/{f=1} f{print} f && /^}/{exit}' "${TRAIN_DIR}/land.sh")"
assert_not_contains "post-CAS: finalizer has no git push" "${finalize_surface}" "git push"
assert_not_contains "post-CAS: finalizer has no gh api" "${finalize_surface}" "gh api"
assert_not_contains "post-CAS: finalizer has no GraphQL" "${finalize_surface}" "graphql"

# Kill/restart: the durable land record exists, trunk was pushed, and the first
# controller dies before finalization. A fresh controller rebuilds journals from
# the state issue and performs observation-only reconciliation.
git checkout -q -B train/batch/restart/1 origin/trunk
echo "restart batch" >> restart-batch.txt; git add -A; git commit -qm "restart batch"
restart_sha="$(git rev-parse HEAD)"
restart_base="${close_sha}"
git push -q origin HEAD:refs/heads/train/batch/restart/1
git push -q origin HEAD:trunk
admitted_restart=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export TRAIN_STATE_ISSUE_OVERRIDE=1
export TRAIN_STATE_BODY_OVERRIDE=$'```json\n'"{\"active_batch\":{\"branch\":\"train/batch/restart/1\",\"trunk_base\":\"${restart_base}\",\"included\":[705],\"included_heads\":[{\"number\":705,\"head\":\"${admitted_restart}\"}],\"batch_sha\":\"${restart_sha}\",\"phase\":\"land\"}}"$'\n```'
__land_info_recovered_merged() { printf '%s\tCLOSED\n' "${admitted_restart}"; }
export admitted_restart
export -f __land_info_recovered_merged
export TRAIN_LAND_PR_INFO_FOR=__land_info_recovered_merged
export TRAIN_LAND_CLEAR_LABEL_CMD=__land_clear_label
TRAIN_LAND_FINALIZED_FILE="${SCRATCH}/restart-finalized" TRAIN_LAND_ADVANCED_FILE="${SCRATCH}/restart-advanced" TRAIN_LAND_PENDING_FILE="${SCRATCH}/restart-pending"
export TRAIN_LAND_FINALIZED_FILE TRAIN_LAND_ADVANCED_FILE TRAIN_LAND_PENDING_FILE
train_restore_post_land; restart_rc=$?
assert_eq "post-land restart: durable land intent recovers" "${restart_rc}" "0"
assert_contains "post-land restart: exact member finalized from durable SHA" "$(cat "${TRAIN_LAND_FINALIZED_FILE}")" $'705\t'"${admitted_restart}"
assert_contains "post-land restart: exact CLOSED member is terminal" "$(cat "${TRAIN_LAND_FINALIZED_FILE}")" "CLOSED"
git fetch -q origin trunk
assert_eq "post-land restart: no post-CAS trunk movement" "$(git rev-parse origin/trunk)" "${restart_sha}"

# If GitHub has not exposed merged state yet, recovery remains durable/pending
# and must not start a new selection or truncate the record.
__land_info_recovered_pending() { printf '%s\tOPEN\n' "${admitted_restart}"; }
export -f __land_info_recovered_pending
export TRAIN_LAND_PR_INFO_FOR=__land_info_recovered_pending
export TRAIN_STATE_BODY_OVERRIDE="${TRAIN_STATE_BODY_OVERRIDE/\"phase\":\"land\"/\"phase\":\"post-land-finalize\"}"
set +e
train_restore_post_land; pending_rc=$?
set -e
assert_eq "post-land restart: unresolved native finalization stays pending" "${pending_rc}" "3"
assert_contains "post-land restart: pending journal retains admitted SHA" "$(cat "${TRAIN_LAND_PENDING_FILE}")" "${admitted_restart}"
unset TRAIN_LAND_PR_INFO_FOR TRAIN_LAND_CLEAR_LABEL_CMD TRAIN_STATE_BODY_OVERRIDE TRAIN_STATE_ISSUE_OVERRIDE LABEL_CLEAR_FAIL

echo
echo "== Case 8: smart-CI shard computation on a real batch diff =="
# pr101 touched a FeatureServer path => the descriptor must target FeatureServer
# shards (not run_all) â€” proving smart-CI uses production routing.
git fetch -q origin trunk
git checkout -q -B smartci-batch origin/trunk
mkdir -p src/Honua.Protocols.GeoServices/FeatureServer
echo "// change" >> src/Honua.Protocols.GeoServices/FeatureServer/fs.cs
git add -A; git commit -qm "featureserver change"
desc="$(train_smart_ci_shards smartci-batch)"
assert_contains "smart-ci: descriptor is valid JSON with shards" "$(jq -r 'has("shards")' <<<"${desc}")" "true"
assert_contains "smart-ci: FeatureServer-only diff targets FeatureServer shard" "${desc}" "FeatureServer"
assert_eq "smart-ci: not run_all for a targeted feature diff" "$(jq -r '.run_all' <<<"${desc}")" "false"
assert_contains "derived artifacts: shell generators do not require executable bits" \
  "$(cat "${TRAIN_DIR}/train.sh")" \
  'bash "${repo_root}/scripts/generate-geoservices-parity.sh"'

# A dispatched run may become visible on the first post-dispatch query. The
# baseline must be captured before dispatch or that run is rejected as stale.
smart_ci_dispatched=0
smart_ci_mode=immediate
smart_ci_dispatch_marker="${SCRATCH}/smart-ci-dispatched"
smart_ci_baseline_calls="${SCRATCH}/smart-ci-baseline-calls"
gh() {
  if [[ "$1 $2" == "workflow run" ]]; then
    smart_ci_dispatched=1
    : >"${smart_ci_dispatch_marker}"
    return 0
  fi
  if [[ "$1 $2" == "run list" ]]; then
    if [[ "${smart_ci_mode}" == "baseline-failure" ]]; then
      local baseline_calls=0
      [[ -f "${smart_ci_baseline_calls}" ]] && baseline_calls="$(cat "${smart_ci_baseline_calls}")"
      baseline_calls=$((baseline_calls + 1))
      printf '%s\n' "${baseline_calls}" >"${smart_ci_baseline_calls}"
      if [[ "${baseline_calls}" == "1" ]]; then return 1; fi
      echo "444"
      return 0
    fi
    if [[ "${smart_ci_mode}" == "stale" ]]; then
      echo "333"
      return 0
    fi
    if [[ "${smart_ci_dispatched}" == "1" ]]; then echo "222"; else echo "111"; fi
    return 0
  fi
  if [[ "$1 $2" == "run view" && "$*" == *"--json status"* ]]; then
    echo "completed"
    return 0
  fi
  if [[ "$1 $2" == "run view" && "$*" == *"--json jobs"* ]]; then
    echo "success"
    return 0
  fi
  return 1
}
immediate_gate="$(TRAIN_APPLY=1 \
  TRAIN_SMART_CI_DISCOVERY_TIMEOUT_SECONDS=0 \
  TRAIN_SMART_CI_POLL_TIMEOUT_SECONDS=0 \
  train_smart_ci_run smartci-batch)"
assert_eq "smart-ci: immediately visible dispatched run is not in baseline" "${immediate_gate}" "SUCCESS"

smart_ci_mode=stale
stale_gate="$(TRAIN_APPLY=1 \
  TRAIN_SMART_CI_DISCOVERY_TIMEOUT_SECONDS=0 \
  TRAIN_SMART_CI_POLL_TIMEOUT_SECONDS=0 \
  train_smart_ci_run smartci-batch)"
assert_eq "smart-ci: stale green run fails closed when no new run appears" "${stale_gate}" "FAILURE"

rm -f "${smart_ci_dispatch_marker}" "${smart_ci_baseline_calls}"
smart_ci_mode=baseline-failure
baseline_failure_gate="$(TRAIN_APPLY=1 \
  TRAIN_SMART_CI_DISCOVERY_TIMEOUT_SECONDS=0 \
  TRAIN_SMART_CI_POLL_TIMEOUT_SECONDS=0 \
  train_smart_ci_run smartci-batch)"
unset -f gh
assert_eq "smart-ci: baseline query failure fails closed" "${baseline_failure_gate}" "FAILURE"
assert_eq "smart-ci: baseline query failure prevents dispatch" "$([[ -e "${smart_ci_dispatch_marker}" ]] && echo yes || echo no)" "no"

echo
echo "== State JSON rendering (crash-resume contract) =="
body="$(train_state_render train/batch/abc/1 deadbeef "101,102" smart-ci 555 1 0 cafef00d)"
assert_contains "state: fenced json block" "${body}" '```json'
sj="$(printf '%s\n' "${body}" | awk '/^```json/{f=1;next}/^```/{f=0}f')"
assert_eq "state: phase persisted" "$(jq -r '.active_batch.phase' <<<"${sj}")" "smart-ci"
assert_eq "state: included persisted" "$(jq -rc '.active_batch.included' <<<"${sj}")" "[101,102]"
assert_eq "state: max_batch in config" "$(jq -r '.config.max_batch' <<<"${sj}")" "${MAX_BATCH:-3}"
assert_eq "state: last_landed_trunk" "$(jq -r '.last_landed_trunk' <<<"${sj}")" "cafef00d"

echo
echo "== select: exact-head PR/Review gates + ordering + fail-closed filters =="
__queue_pages() {
  jq -nc '{data:{repository:{pullRequests:{nodes:[range(0;100)|{number:(1000+.),headRefOid:"page1",isDraft:false,mergeable:"MERGEABLE",labels:{nodes:[]},createdAt:"2026-01-01T00:00:00Z",author:{login:"a"}}],pageInfo:{hasNextPage:true,endCursor:"p1"}}}}}'
  jq -nc '{data:{repository:{pullRequests:{nodes:[{number:1100,headRefOid:"page2",isDraft:false,mergeable:"MERGEABLE",labels:{nodes:[]},createdAt:"2026-01-02T00:00:00Z",author:{login:"b"}}],pageInfo:{hasNextPage:false,endCursor:null}}}}}'
}
export -f __queue_pages
export TRAIN_PR_QUEUE_PAGES_CMD=__queue_pages
assert_eq "select: paginated queue includes entries beyond first 100" "$(train_open_pr_queue | jq length)" "101"
unset TRAIN_PR_QUEUE_PAGES_CMD
export TRAIN_PR_LIST_JSON='[
  {"number":10,"headRefOid":"aaa","isDraft":false,"mergeable":"MERGEABLE","labels":[],"createdAt":"2026-01-02T00:00:00Z"},
  {"number":11,"headRefOid":"bbb","isDraft":false,"mergeable":"MERGEABLE","labels":[],"createdAt":"2026-01-01T00:00:00Z"},
  {"number":12,"headRefOid":"ccc","isDraft":true,"mergeable":"MERGEABLE","labels":[],"createdAt":"2026-01-03T00:00:00Z"},
  {"number":13,"headRefOid":"ddd","isDraft":false,"mergeable":"MERGEABLE","labels":[{"name":"hold"}],"createdAt":"2026-01-04T00:00:00Z"},
  {"number":14,"headRefOid":"eee","isDraft":false,"mergeable":"CONFLICTING","labels":[],"createdAt":"2026-01-05T00:00:00Z"}
]'
MAX_BATCH=3 sel="$(MAX_BATCH=3 train_select | jq -s -c '[.[].number]')"
assert_eq "select: oldest-first; draft/hold/conflict excluded" "${sel}" "[11,10]"
selected_evidence="$(MAX_BATCH=1 train_select | jq -s -r '.[0].gate')"
assert_eq "select: PR+Review admission does not synthesize CI Gate" "${selected_evidence}" "MISSING"
assert_not_contains "batch: direct all-green bypass removed" "$(cat "${TRAIN_DIR}/train.sh")" "direct-merge-all-green"
assert_contains "batch: admitted PRs dispatch batch CI" "$(cat "${TRAIN_DIR}/train.sh")" 'gate="$(train_run_batch_ci "${batch}")"'

for admission_case in gate-fail review-fail unresolved negative-review held escalated draft closed advanced; do
  export ADMISSION_CASE="${admission_case}"
  train_pr_admission 10 aaa \
    && bad "admission: ${admission_case} must fail closed" \
    || ok "admission: ${admission_case} fails closed"
done
status_record="${SCRATCH}/review-gate-status"; : >"${status_record}"
export FIXTURE_REVIEW_STATUS_RECORD="${status_record}"
export ADMISSION_CASE=review-fail TRAIN_APPLY=1
train_pr_admission 10 aaa \
  && ok "admission: resolved thread recovers stale failed Review Gate in live mode" \
  || bad "admission: resolved thread did not recover stale failed Review Gate"
assert_contains "admission: recovery refreshes exact-head Review Gate success" "$(cat "${status_record}")" $'10\taaa\tsuccess'
export TRAIN_APPLY=0
unset ADMISSION_CASE FIXTURE_REVIEW_STATUS_RECORD

( train_select() { :; }; train_has_selectable_pr ) \
  && bad "self-chain: all-inadmissible queue must not redispatch" \
  || ok "self-chain: all-inadmissible queue does not redispatch"
( train_select() { printf '{\"number\":10}\n'; }; train_has_selectable_pr ) \
  && ok "self-chain: demonstrated selectable progress redispatches" \
  || bad "self-chain: selectable progress did not redispatch"
unset ADMISSION_CASE TRAIN_PR_LIST_JSON
# CI Gate state mapping.
assert_eq "select: COMPLETED+SUCCESS => SUCCESS" "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"COMPLETED","conclusion":"SUCCESS"}]')" "SUCCESS"
# A bare FAILURE rollup with no detailsUrl/jobs => conservative FAIL (unchanged).
assert_eq "select: COMPLETED+FAILURE => FAIL" "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"COMPLETED","conclusion":"FAILURE"}]')" "FAIL"
assert_eq "select: QUEUED => PENDING" "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"QUEUED","conclusion":""}]')" "PENDING"
assert_eq "select: absent => MISSING" "$(train_select_ci_gate_state '[]')" "MISSING"
assert_eq "select: recovered StatusContext SUCCESS => SUCCESS" \
  "$(train_select_ci_gate_state '[{"__typename":"StatusContext","context":"CI Gate","state":"SUCCESS","startedAt":"2026-01-02T00:00:00Z"}]')" "SUCCESS"
assert_eq "select: StatusContext PENDING => PENDING" \
  "$(train_select_ci_gate_state '[{"__typename":"StatusContext","context":"CI Gate","state":"PENDING"}]')" "PENDING"
assert_eq "select: StatusContext FAILURE => FAIL" \
  "$(train_select_ci_gate_state '[{"__typename":"StatusContext","context":"CI Gate","state":"FAILURE"}]')" "FAIL"
assert_eq "select: recovery status supersedes failed CheckRun" \
  "$(train_select_ci_gate_state '[{"__typename":"CheckRun","name":"CI Gate","status":"COMPLETED","conclusion":"FAILURE"},{"__typename":"StatusContext","context":"CI Gate","state":"SUCCESS","startedAt":"2026-01-02T00:00:00Z"}]')" "SUCCESS"

echo
echo "== select: merge-through-flakes (FAILURE => FLAKE only when flake-only) =="
# Mock the run-id + per-run failing-job + per-job-log lookups (no network).
# A CI Gate FAILURE rollup carries a detailsUrl with the run id; the classifier
# fetches that run's failed leaf jobs, then (for shard jobs) their logs.
GATE_FAIL='[{"name":"CI Gate","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://github.com/honua-io/honua-server/actions/runs/424242/job/9"}]'
export TRAIN_SELECT_FAILED_JOBS_FOR_RUN=__sel_failed_jobs
export TRAIN_SELECT_JOB_LOG_FOR=__sel_job_log
# Per-fixture job/log fixtures keyed off SEL_CASE. Each failing-job line is
# "<conclusion>\t<name>" (matching the live `gh run view --json jobs` shape).
__sel_failed_jobs() {  # <run-id>
  case "${SEL_CASE:-}" in
    aggregator)  printf 'failure\tCI Gate\nfailure\tTest Suite Summary\n' ;;
    cancelshard) printf 'cancelled\tServer Tests (Server Features Misc)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n' ;;
    shard_flake) printf 'failure\tServer Tests (STAC and API Governance)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n' ;;
    foundation)  printf 'failure\t.NET Foundation Tests\nfailure\tServer Tests (STAC and API Governance)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n' ;;
    buildfmt)    printf 'failure\tBuild & Format\nfailure\tTest Suite Summary\nfailure\tCI Gate\n' ;;
    shard_real)  printf 'failure\tServer Tests (STAC and API Governance)\nfailure\tTest Suite Summary\nfailure\tCI Gate\n' ;;
    nojobs)      : ;;
    *) : ;;
  esac
}
__sel_job_log() {  # <run-id> <job-name>
  case "${SEL_CASE:-}" in
    shard_flake) printf 'ERROR 40P01: deadlock detected during seed\n' ;;
    shard_real)  printf 'Assert.Equal() Failure: expected 3 actual 4\n' ;;
    *) : ;;
  esac
}
export -f __sel_failed_jobs __sel_job_log

# (a) aggregator-only failure (CI Gate + Test Suite Summary) => FLAKE (ready).
assert_eq "select(flake): aggregator-only FAILURE => FLAKE" \
  "$(SEL_CASE=aggregator train_select_ci_gate_state "${GATE_FAIL}")" "FLAKE"
# (b) shard FAILURE whose log matches 40P01 flake regex => FLAKE.
assert_eq "select(flake): 40P01 shard log => FLAKE" \
  "$(SEL_CASE=shard_flake train_select_ci_gate_state "${GATE_FAIL}")" "FLAKE"
# (b2) a CANCELLED shard alongside aggregators (cancel-cascade) => FLAKE.
assert_eq "select(flake): cancelled shard => FLAKE" \
  "$(SEL_CASE=cancelshard train_select_ci_gate_state "${GATE_FAIL}")" "FLAKE"
# (c) real-gate job (.NET Foundation Tests) failed => FAIL (skip).
assert_eq "select(flake): .NET Foundation Tests failed => FAIL" \
  "$(SEL_CASE=foundation train_select_ci_gate_state "${GATE_FAIL}")" "FAIL"
# (d) real-gate job (Build & Format) failed => FAIL.
assert_eq "select(flake): Build & Format failed => FAIL" \
  "$(SEL_CASE=buildfmt train_select_ci_gate_state "${GATE_FAIL}")" "FAIL"
# (extra) shard FAILURE with a real assertion log (no flake regex) => FAIL.
assert_eq "select(flake): real shard assertion => FAIL" \
  "$(SEL_CASE=shard_real train_select_ci_gate_state "${GATE_FAIL}")" "FAIL"
# (extra) no failing jobs fetchable => conservative FAIL.
assert_eq "select(flake): unfetchable jobs => FAIL (conservative)" \
  "$(SEL_CASE=nojobs train_select_ci_gate_state "${GATE_FAIL}")" "FAIL"
# (e) SUCCESS unchanged (and PENDING/MISSING never touch the run lookups).
assert_eq "select(flake): SUCCESS unchanged" \
  "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"COMPLETED","conclusion":"SUCCESS"}]')" "SUCCESS"
assert_eq "select(flake): PENDING unchanged" \
  "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"IN_PROGRESS","conclusion":""}]')" "PENDING"
assert_eq "select(flake): MISSING unchanged" \
  "$(train_select_ci_gate_state '[]')" "MISSING"
unset TRAIN_SELECT_FAILED_JOBS_FOR_RUN TRAIN_SELECT_JOB_LOG_FOR

echo
echo "== Phase 2: gated Bedrock LLM judgments (mock; no real Bedrock) =="
# A fake bedrock_ask, wired via TRAIN_BEDROCK_ASK_CMD. It records that it was
# called (so we can assert the gates NEVER touch it when TRAIN_LLM=0) and returns
# whatever canned answer the test stuffs into FAKE_BEDROCK_ANSWER. Setting the
# answer to the error sentinel simulates a Bedrock outage/timeout.
BEDROCK_CALLS="${SCRATCH}/bedrock_calls"; : >"${BEDROCK_CALLS}"
__fake_bedrock() {  # __fake_bedrock <system> <user>
  echo "called" >>"${BEDROCK_CALLS}"
  printf '%s\n' "${FAKE_BEDROCK_ANSWER:-}"
}
export -f __fake_bedrock
export TRAIN_BEDROCK_ASK_CMD=__fake_bedrock
reset_calls() { : >"${BEDROCK_CALLS}"; }
calls() { wc -l <"${BEDROCK_CALLS}" | tr -d ' '; }

# Two PR file sets with HEAVY overlap (B's files all appear in A) => ambiguous.
FILES_A=$'src/Honua.Core/Query/Filter.cs\nsrc/Honua.Core/Query/Paging.cs\nsrc/Honua.Core/Query/Crs.cs'
FILES_B=$'src/Honua.Core/Query/Filter.cs\nsrc/Honua.Core/Query/Paging.cs'  # 100% of B in A
# A non-overlapping pair (deterministic-only, never ambiguous).
FILES_C=$'docs/readme.md'

# Pure overlap ratio is testable on its own.
assert_eq "p2 overlap: B fully in A => 100" "$(train_pr_overlap_ratio "${FILES_A}" "${FILES_B}")" "100"
assert_eq "p2 overlap: disjoint => 0" "$(train_pr_overlap_ratio "${FILES_A}" "${FILES_C}")" "0"

echo "-- (a) TRAIN_LLM=0: gates NEVER call Bedrock; behavior == Phase-1 deterministic --"
export TRAIN_LLM=0
reset_calls
# select.overlap: deterministic fallback is PROCEED (rc1), even on heavy overlap.
train_select_should_wait 10 "${FILES_A}" 11 "${FILES_B}" && bad "p2 select(llm0): heavy overlap must still PROCEED" || ok "p2 select(llm0): PROCEED (Phase-1 ordering)"
assert_eq "p2 select(llm0): no bedrock call" "$(calls)" "0"
# classify-flake unknown: deterministic fallback is REAL (rc1).
reset_calls
export TRAIN_RUN_LOG_TEXT="Assert.Equal() Failure: expected 3 actual 4"
train_classify_flake_unknown 999 && bad "p2 flake(llm0): unknown must be REAL" || ok "p2 flake(llm0): unknown => REAL (conservative)"
assert_eq "p2 flake(llm0): no bedrock call" "$(calls)" "0"
# forward-fix heal: deterministic fallback is ESCALATE (rc1).
reset_calls
train_forward_fix_heal_safe "server-tests (OpenAPI drift)" "openapi.json out of date" && bad "p2 heal(llm0): must ESCALATE" || ok "p2 heal(llm0): ESCALATE (never auto-patch)"
assert_eq "p2 heal(llm0): no bedrock call" "$(calls)" "0"
unset TRAIN_RUN_LOG_TEXT

echo "-- (b) TRAIN_LLM=1: each gate fires ONLY in its ambiguous condition, routes on yes/no --"
export TRAIN_LLM=1
# select.overlap: BELOW threshold (disjoint) must NOT call the LLM at all.
reset_calls
FAKE_BEDROCK_ANSWER="YES" train_select_should_wait 10 "${FILES_A}" 11 "${FILES_C}" && bad "p2 select(llm1,low-overlap): disjoint must PROCEED" || ok "p2 select(llm1): disjoint => PROCEED without LLM"
assert_eq "p2 select(llm1): low-overlap skips bedrock" "$(calls)" "0"
# select.overlap: heavy overlap + LLM says YES => WAIT (rc0).
reset_calls
FAKE_BEDROCK_ANSWER="YES, B depends on A" train_select_should_wait 10 "${FILES_A}" 11 "${FILES_B}" && ok "p2 select(llm1): heavy overlap + YES => WAIT" || bad "p2 select(llm1): YES should WAIT"
assert_eq "p2 select(llm1,yes): bedrock consulted once" "$(calls)" "1"
# select.overlap: heavy overlap + LLM says NO => PROCEED (rc1).
reset_calls
FAKE_BEDROCK_ANSWER="NO, independent" train_select_should_wait 10 "${FILES_A}" 11 "${FILES_B}" && bad "p2 select(llm1): NO should PROCEED" || ok "p2 select(llm1): heavy overlap + NO => PROCEED"
assert_eq "p2 select(llm1,no): bedrock consulted once" "$(calls)" "1"

# classify-flake unknown: a KNOWN signature must NOT reach the LLM (still flake).
reset_calls
export TRAIN_RUN_LOG_TEXT="40P01 deadlock detected"
train_run_logs_match_flake 999 && ok "p2 flake(llm1): known signature handled deterministically" || bad "p2 flake(llm1): 40P01 should match"
assert_eq "p2 flake(llm1): known signature skips bedrock" "$(calls)" "0"
# Unknown signature + LLM says TRANSIENT => rc0 (rerunnable), learns regex.
reset_calls
export TRAIN_RUN_LOG_TEXT="ECONNRESET talking to package registry mid-restore"
LEARN="${SCRATCH}/learned_regex"; : >"${LEARN}"
export TRAIN_FLAKE_REGEX_LEARN_FILE="${LEARN}" TRAIN_APPLY=1
FAKE_BEDROCK_ANSWER=$'TRANSIENT\nECONNRESET.*package registry' train_classify_flake_unknown 999 && ok "p2 flake(llm1): unknown + TRANSIENT => rerunnable" || bad "p2 flake(llm1): TRANSIENT should be rc0"
assert_eq "p2 flake(llm1,transient): bedrock consulted once" "$(calls)" "1"
grep -Fq 'ECONNRESET.*package registry' "${LEARN}" && ok "p2 flake(llm1): learned regex appended (apply mode)" || bad "p2 flake(llm1): learned regex not recorded"
# Unknown signature + LLM says REAL => rc1.
reset_calls
FAKE_BEDROCK_ANSWER="REAL" train_classify_flake_unknown 999 && bad "p2 flake(llm1): REAL should be rc1" || ok "p2 flake(llm1): unknown + REAL => real failure"
assert_eq "p2 flake(llm1,real): bedrock consulted once" "$(calls)" "1"
unset TRAIN_RUN_LOG_TEXT TRAIN_FLAKE_REGEX_LEARN_FILE; export TRAIN_APPLY=0

# forward-fix heal: LLM says HEAL with an allowlisted generator => rc0 + name.
reset_calls
FAKE_BEDROCK_ANSWER=$'HEAL\nupdate-openapi-snapshot'
gen="$(train_forward_fix_heal_safe "server-tests (OpenAPI drift)" "openapi.json out of date")" && \
  assert_eq "p2 heal(llm1): HEAL => allowlisted generator" "${gen}" "update-openapi-snapshot" || bad "p2 heal(llm1): HEAL should return generator"
assert_eq "p2 heal(llm1,heal): bedrock consulted once" "$(calls)" "1"
# forward-fix heal: LLM says HEAL but a NON-allowlisted generator => ESCALATE (safety).
reset_calls
FAKE_BEDROCK_ANSWER=$'HEAL\nrm -rf /' train_forward_fix_heal_safe "server-tests (drift)" "x" && bad "p2 heal(llm1): non-allowlisted must ESCALATE" || ok "p2 heal(llm1): HEAL + bad generator => ESCALATE (safety)"
# forward-fix heal: LLM says ESCALATE => rc1 (the expected usual answer).
reset_calls
FAKE_BEDROCK_ANSWER="ESCALATE" train_forward_fix_heal_safe "server-tests (drift)" "x" && bad "p2 heal(llm1): ESCALATE should be rc1" || ok "p2 heal(llm1): ESCALATE => human fix"

echo "-- (c) Bedrock error => deterministic fallback; train never blocks --"
# select.overlap: heavy overlap but Bedrock errors => PROCEED (Phase-1 ordering).
reset_calls
FAKE_BEDROCK_ANSWER="${BEDROCK_ERROR_SENTINEL}" train_select_should_wait 10 "${FILES_A}" 11 "${FILES_B}" && bad "p2 select(err): must fall back to PROCEED" || ok "p2 select(err): bedrock error => PROCEED (no block)"
# classify-flake unknown: Bedrock errors => REAL (conservative fallback).
reset_calls
export TRAIN_RUN_LOG_TEXT="some novel failure"
FAKE_BEDROCK_ANSWER="${BEDROCK_ERROR_SENTINEL}" train_classify_flake_unknown 999 && bad "p2 flake(err): must fall back to REAL" || ok "p2 flake(err): bedrock error => REAL"
unset TRAIN_RUN_LOG_TEXT
# forward-fix heal: Bedrock errors => ESCALATE.
reset_calls
FAKE_BEDROCK_ANSWER="${BEDROCK_ERROR_SENTINEL}" train_forward_fix_heal_safe "server-tests (drift)" "x" && bad "p2 heal(err): must fall back to ESCALATE" || ok "p2 heal(err): bedrock error => ESCALATE"
# Sanity: bedrock_ask itself returns the sentinel (never crashes) when disabled.
TRAIN_LLM=0 ans_disabled="$(bedrock_ask sys usr)"; assert_eq "p2 bedrock_ask: disabled => sentinel" "${ans_disabled}" "${BEDROCK_ERROR_SENTINEL}"

unset TRAIN_BEDROCK_ASK_CMD FAKE_BEDROCK_ANSWER TRAIN_LLM

echo
echo "== Roll-forward Cap. 1: pre-existing-failure filter (deterministic, no AI) =="
# Mock trunk's latest CI run id + its failing jobs/tests. The batch's failing
# jobs are subtracted against trunk's; only batch-INTRODUCED ones survive.
export TRAIN_TRUNK_RUN_ID=trunk-run-1
__trunk_jobs() {  # <run-id>
  case "${PE_CASE:-}" in
    all_pre)  printf 'Server Tests (STAC and API Governance)\n' ;;   # trunk already red here
    some_new) printf 'Server Tests (STAC and API Governance)\n' ;;
    buildfmt_20260705) printf 'Build & Format Check\n' ;;
    none_pre) : ;;
    *) : ;;
  esac
}
export -f __trunk_jobs
export TRAIN_FAILING_JOBS_FOR_RUN=__trunk_jobs
__preexisting_job_records() {  # <run-id>
  case "${PE_CASE:-}:$1" in
    jobid_join:trunk-run-1)
      printf '101\tServer Tests (Shared Failure)\n'
      ;;
    jobid_join:batch-run-jobids)
      printf '201\tServer Tests (Shared Failure)\n'
      printf '202\tServer Tests (New Failure)\n'
      ;;
    *) : ;;
  esac
}
export -f __preexisting_job_records
__preexisting_job_log() {  # <run-id> <job-name> [job-id]
  local run_id="$1" job="$2" job_id="${3:-}"
  if [[ "${PE_CASE:-}" == "jobid_join" ]]; then
    case "${run_id}:${job_id}" in
      trunk-run-1:101|batch-run-jobids:201)
        printf 'Failed Honua.Server.Tests.Shared.AlreadyFails [12 ms]\n'
        ;;
      batch-run-jobids:202)
        printf 'Failed Honua.Server.Tests.New.IntroducedFailure [12 ms]\n'
        ;;
      batch-run-jobids:)
        printf 'Failed Honua.Server.Tests.Shared.AlreadyFails [12 ms]\n'
        printf 'Failed Honua.Server.Tests.New.IntroducedFailure [12 ms]\n'
        ;;
      *) : ;;
    esac
    return 0
  fi
  case "${PE_CASE:-}:${run_id}:${job}" in
    all_pre:*:"Server Tests (STAC and API Governance)"|some_new:*:"Server Tests (STAC and API Governance)")
      printf '[xUnit.net 00:00:00.42]    Honua.Server.Tests.Stac.ItemTests.Returns200 [FAIL]\n'
      ;;
    some_new:batch-run-9:"Server Tests (FeatureServer Endpoints)")
      printf 'Failed Honua.Server.Tests.GeoServices.FeatureServer.QueryEndpointTests.Query_Returns200 [12 ms]\n'
      ;;
    buildfmt_20260705:trunk-run-1:"Build & Format Check")
      printf "Build & Format Check\tFormat Verification\tFormatted code file '/home/runner/work/honua-server/src/Honua.Server/Program.cs'.\n"
      ;;
    buildfmt_20260705:batch-run-20260705:"Build & Format Check")
      printf "/home/runner/work/honua-server/src/Honua.Server/Features/BrokenHandler.cs(42,14): error CS0535: 'BrokenHandler' does not implement interface member 'IHandler.HandleAsync(CancellationToken)'\n"
      ;;
    *) : ;;
  esac
}
export -f __preexisting_job_log
export TRAIN_JOB_LOG_FOR_RUN=__preexisting_job_log

# (a) ALL batch failures are also on trunk => filter returns rc11 (treat as PASS).
set +e
PE_CASE=all_pre train_preexisting_filter batch-run-9 "Server Tests (STAC and API Governance)" >/dev/null
rc_pe=$?
set -e
assert_eq "preexisting: all-pre-existing => rc11 (land)" "${rc_pe}" "11"

# (b) The batch introduced a NEW failing job not on trunk => it survives (rc0)
# and only the introduced job is emitted (the pre-existing STAC one is stripped).
set +e
survivors="$(PE_CASE=some_new train_preexisting_filter batch-run-9 \
  $'Server Tests (STAC and API Governance)\nServer Tests (FeatureServer Endpoints)')"
rc_pe2=$?
set -e
assert_eq "preexisting: some-introduced => rc0 (act)" "${rc_pe2}" "0"
assert_contains "preexisting: introduced job survives" "${survivors}" "FeatureServer Endpoints"
assert_not_contains "preexisting: pre-existing job stripped" "${survivors}" "STAC and API Governance"

# (c) trunk has NO failures => every batch failure is batch-introduced (rc0).
set +e
survivors2="$(PE_CASE=none_pre train_preexisting_filter batch-run-9 'Server Tests (FeatureServer Endpoints)')"
rc_pe3=$?
set -e
assert_eq "preexisting: clean-trunk => all introduced (rc0)" "${rc_pe3}" "0"
assert_contains "preexisting: introduced survives on clean trunk" "${survivors2}" "FeatureServer Endpoints"
# (d) Regression for 2026-07-05: same job name, different cause. Trunk has
# format drift, but the batch has a C# compile error, so this is NOT pre-existing.
set +e
survivors3="$(PE_CASE=buildfmt_20260705 train_preexisting_filter batch-run-20260705 'Build & Format Check')"
rc_pe4=$?
set -e
assert_eq "preexisting: same job different cause => rc0 (act)" "${rc_pe4}" "0"
assert_contains "preexisting: Build & Format Check CS0535 survives format drift" "${survivors3}" "Build & Format Check"
# (e) Live train supplies failing job names to the filter, but signatures must
# still come from per-job logs. This guards against assigning every failed
# run-log signature to every supplied job name.
export TRAIN_FAILING_JOB_RECORDS_FOR_RUN=__preexisting_job_records
set +e
survivors4="$(PE_CASE=jobid_join train_preexisting_filter batch-run-jobids \
  $'Server Tests (Shared Failure)\nServer Tests (New Failure)')"
rc_pe5=$?
set -e
assert_eq "preexisting: supplied names use per-job ids => rc0 (act)" "${rc_pe5}" "0"
assert_contains "preexisting: per-job id new failure survives" "${survivors4}" "New Failure"
assert_not_contains "preexisting: per-job id shared failure stripped" "${survivors4}" "Shared Failure"
# subtraction primitive
assert_eq "preexisting: subtract removes baseline lines" \
  "$(train_subtract_lines $'a\nb' $'a\nb\nc' | tr '\n' ' ' | xargs)" "c"
unset TRAIN_TRUNK_RUN_ID TRAIN_FAILING_JOBS_FOR_RUN TRAIN_FAILING_JOB_RECORDS_FOR_RUN TRAIN_JOB_LOG_FOR_RUN

echo
echo "== Roll-forward Cap. 3: surgical retry (filter built from failed FQNs) =="
# Parse failed FQNs from VSTest + xUnit reporter lines, then build the
# dotnet-test --filter and the JS/Python equivalents.
LOG=$'Failed Honua.Core.Tests.Query.FilterTests.Parses_Nested [12 ms]\n[xUnit.net 00:00:00.42]    Honua.Server.Tests.Stac.ItemTests.Returns200 [FAIL]\n  Passed Honua.Core.Tests.Query.FilterTests.Other [1 ms]'
fqns="$(train_parse_failed_test_names "${LOG}")"
assert_contains "surgical: parsed VSTest FQN" "${fqns}" "Honua.Core.Tests.Query.FilterTests.Parses_Nested"
assert_contains "surgical: parsed xUnit [FAIL] FQN" "${fqns}" "Honua.Server.Tests.Stac.ItemTests.Returns200"
assert_not_contains "surgical: passed test not collected" "${fqns}" "FilterTests.Other"
filter="$(train_build_test_filter "${fqns}")"
assert_contains "surgical: --filter has FullyQualifiedName= for each FQN" "${filter}" "FullyQualifiedName=Honua.Core.Tests.Query.FilterTests.Parses_Nested"
assert_contains "surgical: --filter OR-joins FQNs" "${filter}" "|FullyQualifiedName=Honua.Server.Tests.Stac.ItemTests.Returns200"
# Exactly two FQNs => exactly one pipe separator.
assert_eq "surgical: two FQNs => single pipe" "$(awk -F'|' '{print NF-1}' <<<"${filter}")" "1"
# JS + Python equivalents (leaf-name based).
js="$(train_build_js_test_pattern "${fqns}")"
assert_contains "surgical(js): jest -t pattern uses leaf names" "${js}" "Parses_Nested"
py="$(train_build_py_test_pattern "${fqns}")"
assert_contains "surgical(py): pytest -k uses ' or ' join" "${py}" " or "
# Surgical rerun honors the test-runner seam and reports pass/fail per project.
export TRAIN_TEST_PROJECT_FOR=__test_proj
__test_proj() { printf 'tests/dotnet/Fake.Tests/Fake.Tests.csproj\n'; }
export -f __test_proj
GOT_FILTER="${SCRATCH}/got_filter"; : >"${GOT_FILTER}"
__runner_ok() { printf '%s\n' "$2" >"${GOT_FILTER}"; return 0; }
__runner_fail() { return 1; }
export -f __runner_ok __runner_fail
export TRAIN_SURGICAL_RUNNER=__runner_ok
train_surgical_rerun some-run "${fqns}" && ok "surgical: green rerun => rc0" || bad "surgical: should pass"
assert_contains "surgical: runner received the FullyQualifiedName filter" "$(cat "${GOT_FILTER}")" "FullyQualifiedName="
export TRAIN_SURGICAL_RUNNER=__runner_fail
train_surgical_rerun some-run "${fqns}" && bad "surgical: failing rerun should be rc!=0" || ok "surgical: failing rerun => rc1"
# No FQNs => rc2 (caller must fall back, never a full rerun).
set +e; train_surgical_rerun some-run ""; rc_sr=$?; set -e
assert_eq "surgical: no FQNs => rc2 (fall back, no full shard rerun)" "${rc_sr}" "2"
unset TRAIN_SURGICAL_RUNNER TRAIN_TEST_PROJECT_FOR

echo
echo "== Roll-forward Cap. 2: escalation labels culprits + clears active_batch =="
# train_escalate_batch (dry-run logs the side effects): assert it would add
# train:escalated to EVERY member, remove train:landing, and that the state-issue
# write that clears active_batch renders included:[] phase:select.
ESC_LOG="$(TRAIN_APPLY=0 train_escalate_batch "1944,1961,1969,1971,1972" "not attributable" 2>&1)"
for n in 1944 1961 1969 1971 1972; do
  assert_contains "escalate: #${n} gets train:escalated" "${ESC_LOG}" "gh pr edit ${n} --add-label train:escalated"
  assert_contains "escalate: #${n} loses train:landing" "${ESC_LOG}" "gh pr edit ${n} --remove-label train:landing"
done
# Clearing active_batch: the state body the train writes on escalation.
cleared="$(train_state_render "" deadbeef "" select "" 0 0 null)"
csj="$(printf '%s\n' "${cleared}" | awk '/^```json/{f=1;next}/^```/{f=0}f')"
assert_eq "escalate: active_batch.branch cleared" "$(jq -r '.active_batch.branch' <<<"${csj}")" ""
assert_eq "escalate: active_batch.included cleared" "$(jq -rc '.active_batch.included' <<<"${csj}")" "[]"
assert_eq "escalate: phase reset to select" "$(jq -r '.active_batch.phase' <<<"${csj}")" "select"

echo
echo "== Roll-forward Cap. 2b: green rerun recovery clears stale escalation =="
__recover_records() {
  case "$1" in
    train/batch/deadbee/123)
      printf '1944\tsha1944\n'
      printf '1961\tsha1961\n'
      printf '1969\tsha1969\n'
      printf '1972\toldsha1972\n'
      ;;
    *) : ;;
  esac
}
__recover_info() {
  case "$1" in
    1944) printf 'sha1944\tOPEN\ttrain:escalated,train:landing\n' ;;
    1961) printf 'sha1961\tOPEN\ttrain:landing\n' ;;
    1969) printf 'sha1969\tCLOSED\ttrain:escalated\n' ;;
    1972) printf 'newsha1972\tOPEN\ttrain:escalated\n' ;;
    *) return 1 ;;
  esac
}
export -f __recover_records __recover_info
export TRAIN_RECOVERY_PR_RECORDS_FOR_BRANCH=__recover_records
export TRAIN_RECOVERY_PR_INFO_FOR=__recover_info
RECOVERY_LOG="$(
  GITHUB_REPOSITORY=honua-io/honua-server \
  TRAIN_APPLY=0 \
  train_recover_green_batch_rerun 123 train/batch/deadbee/123 https://github.example/runs/123 2>&1
)"
assert_contains "recovery: escalated PR gets CI Gate status" "${RECOVERY_LOG}" "gh api repos/honua-io/honua-server/statuses/sha1944"
assert_contains "recovery: CI Gate context stamped" "${RECOVERY_LOG}" "-f context=CI Gate"
assert_contains "recovery: clears train:escalated" "${RECOVERY_LOG}" "gh pr edit 1944 --remove-label train:escalated"
assert_contains "recovery: clears train:landing" "${RECOVERY_LOG}" "gh pr edit 1944 --remove-label train:landing"
assert_not_contains "recovery: non-escalated PR is not stamped" "${RECOVERY_LOG}" "statuses/sha1961"
assert_not_contains "recovery: closed PR is not stamped" "${RECOVERY_LOG}" "statuses/sha1969"
assert_not_contains "recovery: advanced PR head is not stamped" "${RECOVERY_LOG}" "statuses/newsha1972"
assert_not_contains "recovery: stale batch head is not stamped" "${RECOVERY_LOG}" "statuses/oldsha1972"
assert_not_contains "recovery: advanced PR keeps escalation label" "${RECOVERY_LOG}" "gh pr edit 1972 --remove-label train:escalated"
assert_contains "recovery: advanced PR explains skipped SHA mismatch" "${RECOVERY_LOG}" "current head newsha1972 differs from validated batch head oldsha1972"
unset TRAIN_RECOVERY_PR_RECORDS_FOR_BRANCH TRAIN_RECOVERY_PR_INFO_FOR

echo
echo "== Roll-forward Cap. 4: autofix disabled (TRAIN_AUTOFIX=0) => behaves like today =="
export TRAIN_AUTOFIX=0
# autofix gate is inert: no attempt, returns rc1 (caller escalates), no commit.
set +e
TRAIN_AUTOFIX=0 train_autofix_attempt some-batch "Server Tests (X)" "Pkg.Tests.T1" "boom" 0
rc_af=$?
set -e
assert_eq "autofix(off): disabled => rc1 (escalate path, like Phase 1)" "${rc_af}" "1"
assert_eq "autofix(off): autofix_enabled is false" "$(autofix_enabled && echo on || echo off)" "off"

echo "== Roll-forward Cap. 4: autofix enabled => fix path (mocked, no real Bedrock) =="
export TRAIN_AUTOFIX=1 TRAIN_APPLY=1
export TRAIN_WORK="${SCRATCH}"
export TRAIN_AUTOFIX_REQUEST_FILE="${SCRATCH}/autofix-req.md"
export TRAIN_AUTOFIX_PREHEAD_FILE="${SCRATCH}/autofix-prehead"
# A fake fix-agent that, like a real one, commits a change on the batch branch.
git fetch -q origin trunk; git checkout -q -B autofix-batch origin/trunk
export TRAIN_AUTOFIX_DIFF_CMD=__af_diff
__af_diff() { printf 'diff --git a/x b/x\n+changed\n'; }
export -f __af_diff
__fake_fixagent() {  # <batch> <request-file>
  # The agent edits + commits on the batch branch (NO bot attribution).
  echo "fixed" >> shared.txt
  git add -A
  git commit -q -m "fix(merge-train): correct brittle test assertion"
}
export -f __fake_fixagent
export TRAIN_AUTOFIX_STEP_CMD=__fake_fixagent
set +e
train_autofix_attempt autofix-batch "Server Tests (X)" $'Pkg.Tests.T1\nPkg.Tests.T2' "Assert.Equal failure" 0
rc_af2=$?
set -e
assert_eq "autofix(on): agent committed a fix => rc0 (verify path)" "${rc_af2}" "0"
# The request prompt embeds the fix-forward contract + the failing FQNs + diff.
req_body="$(cat "${TRAIN_AUTOFIX_REQUEST_FILE}")"
assert_contains "autofix(on): request embeds failing FQN" "${req_body}" "Pkg.Tests.T1"
assert_contains "autofix(on): request says fix FORWARD" "${req_body}" "Fix forward"
assert_contains "autofix(on): request forbids bot attribution" "${req_body}" "Add NO bot attribution"
assert_contains "autofix(on): request authors as Mike McDougall" "${req_body}" "Mike McDougall"
# The fix commit carries NO bot attribution.
git -C "${WORK}" log -1 --pretty='%an <%ae>%n%b' autofix-batch | grep -Eqi 'co-authored-by|generated with|ðŸ¤–' \
  && bad "autofix(on): bot attribution present" || ok "autofix(on): fix commit has no bot attribution"
# Cap: at the cap, no attempt is made (rc1 => escalate).
set +e
TRAIN_AUTOFIX_CAP=2 train_autofix_attempt autofix-batch "Server Tests (X)" "Pkg.Tests.T1" "boom" 2
rc_cap=$?
set -e
assert_eq "autofix(on): at cap => rc1 (escalate genuinely-hard)" "${rc_cap}" "1"
# Agent declines (makes no commit) => rc1 (escalate).
__noop_agent() { :; }
export -f __noop_agent
export TRAIN_AUTOFIX_STEP_CMD=__noop_agent
set +e
train_autofix_attempt autofix-batch "Server Tests (X)" "Pkg.Tests.T1" "boom" 0
rc_noc=$?
set -e
assert_eq "autofix(on): agent declined (no commit) => rc1 (escalate)" "${rc_noc}" "1"
unset TRAIN_AUTOFIX TRAIN_AUTOFIX_STEP_CMD TRAIN_AUTOFIX_DIFF_CMD TRAIN_AUTOFIX_CAP
export TRAIN_APPLY=0
git checkout -q origin/trunk 2>/dev/null || true

echo
echo "== Single merge authority static guard =="
node --test "${REAL_ROOT}/scripts/ci/review-gate-evidence.test.js" \
  && ok "review gate: active/dismissed/unresolved evidence fixtures" \
  || bad "review gate: evidence fixtures failed"
assert_contains "review gate: reaction API has least-privilege issues read" \
  "$(cat "${REAL_ROOT}/.github/workflows/review-gate.yml")" \
  "  issues: read"
bash "${REAL_ROOT}/scripts/ci/validate-single-merge-authority.sh" --self-test \
  && ok "authority: positive/negative fixtures" \
  || bad "authority: fixture coverage failed"
bash "${REAL_ROOT}/scripts/ci/validate-single-merge-authority.sh" \
  && ok "authority: only merge-train.yml can land" \
  || bad "authority: multiple merge mechanisms detected"

echo
printf 'RESULT: %d passed, %d failed\n' "${PASS}" "${FAIL}"
[[ "${FAIL}" -eq 0 ]]
