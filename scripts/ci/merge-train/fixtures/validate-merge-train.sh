#!/usr/bin/env bash
# Fixture harness for the merge train (Phase 1). Builds a throwaway local git
# repo with synthetic "PR" branches and asserts the train's git/decision logic
# offline — NO network, NO gh, NO dotnet. Each case targets one decision path:
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
  git log -1 --pretty='%an <%ae>%n%b' | grep -Eqi 'co-authored-by|generated with|🤖' && bad "fwdfix: bot attribution present" || ok "fwdfix: no bot attribution"
else
  bad "fwdfix: should have applied a change"
fi
# Cap: at cap, returns non-zero.
__fake_fmt_noop() { :; }
export TRAIN_FORMAT_CMD=__fake_fmt_noop; export -f __fake_fmt_noop
train_forward_fix fwdfix-batch 2 && bad "fwdfix: cap not enforced" || ok "fwdfix: cap (2) enforced"
unset TRAIN_FORMAT_CMD

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

echo
echo "== Case 8: smart-CI shard computation on a real batch diff =="
# pr101 touched a FeatureServer path => the descriptor must target FeatureServer
# shards (not run_all) — proving smart-CI uses production routing.
git fetch -q origin trunk
git checkout -q -B smartci-batch origin/trunk
mkdir -p src/Honua.Protocols.GeoServices/FeatureServer
echo "// change" >> src/Honua.Protocols.GeoServices/FeatureServer/fs.cs
git add -A; git commit -qm "featureserver change"
desc="$(train_smart_ci_shards smartci-batch)"
assert_contains "smart-ci: descriptor is valid JSON with shards" "$(jq -r 'has("shards")' <<<"${desc}")" "true"
assert_contains "smart-ci: FeatureServer-only diff targets FeatureServer shard" "${desc}" "FeatureServer"
assert_eq "smart-ci: not run_all for a targeted feature diff" "$(jq -r '.run_all' <<<"${desc}")" "false"

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
echo "== select: CI-Gate classification + ordering + hold/draft filters =="
export TRAIN_PR_LIST_JSON='[
  {"number":10,"headRefOid":"aaa","isDraft":false,"mergeable":"MERGEABLE","mergeStateStatus":"BLOCKED","labels":[],"createdAt":"2026-01-02T00:00:00Z","gate":"SUCCESS"},
  {"number":11,"headRefOid":"bbb","isDraft":false,"mergeable":"MERGEABLE","mergeStateStatus":"BLOCKED","labels":[],"createdAt":"2026-01-01T00:00:00Z","gate":"SUCCESS"},
  {"number":12,"headRefOid":"ccc","isDraft":true,"mergeable":"MERGEABLE","mergeStateStatus":"BLOCKED","labels":[],"createdAt":"2026-01-03T00:00:00Z","gate":"SUCCESS"},
  {"number":13,"headRefOid":"ddd","isDraft":false,"mergeable":"MERGEABLE","mergeStateStatus":"BLOCKED","labels":[{"name":"hold"}],"createdAt":"2026-01-04T00:00:00Z","gate":"SUCCESS"},
  {"number":14,"headRefOid":"eee","isDraft":false,"mergeable":"CONFLICTING","mergeStateStatus":"DIRTY","labels":[],"createdAt":"2026-01-05T00:00:00Z","gate":"SUCCESS"},
  {"number":15,"headRefOid":"fff","isDraft":false,"mergeable":"MERGEABLE","mergeStateStatus":"BLOCKED","labels":[],"createdAt":"2026-01-06T00:00:00Z","gate":"FAIL"}
]'
MAX_BATCH=3 sel="$(MAX_BATCH=3 train_select | jq -s -c '[.[].number]')"
# Optimistic batch-and-fix: draft/hold/conflict are still excluded, but a CI
# FAILURE no longer excludes a PR — it is batched and judged by the ONE batch CI
# (autofix/attribute handle real breaks). #15 (gate=FAIL) is now INCLUDED.
assert_eq "select: oldest-first; draft/hold/conflict excluded; CI-fail INCLUDED" "${sel}" "[11,10,15]"
unset TRAIN_PR_LIST_JSON
# CI Gate state mapping.
assert_eq "select: COMPLETED+SUCCESS => SUCCESS" "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"COMPLETED","conclusion":"SUCCESS"}]')" "SUCCESS"
# A bare FAILURE rollup with no detailsUrl/jobs => conservative FAIL (unchanged).
assert_eq "select: COMPLETED+FAILURE => FAIL" "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"COMPLETED","conclusion":"FAILURE"}]')" "FAIL"
assert_eq "select: QUEUED => PENDING" "$(train_select_ci_gate_state '[{"name":"CI Gate","status":"QUEUED","conclusion":""}]')" "PENDING"
assert_eq "select: absent => MISSING" "$(train_select_ci_gate_state '[]')" "MISSING"

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
    none_pre) : ;;
    *) : ;;
  esac
}
export -f __trunk_jobs
export TRAIN_FAILING_JOBS_FOR_RUN=__trunk_jobs

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
# subtraction primitive
assert_eq "preexisting: subtract removes baseline lines" \
  "$(train_subtract_lines $'a\nb' $'a\nb\nc' | tr '\n' ' ' | xargs)" "c"
unset TRAIN_TRUNK_RUN_ID TRAIN_FAILING_JOBS_FOR_RUN

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
__recover_prs() {
  case "$1" in
    train/batch/deadbee/123) printf '1944\n1961\n1969\n' ;;
    *) : ;;
  esac
}
__recover_info() {
  case "$1" in
    1944) printf 'sha1944\tOPEN\ttrain:escalated,train:landing\n' ;;
    1961) printf 'sha1961\tOPEN\ttrain:landing\n' ;;
    1969) printf 'sha1969\tCLOSED\ttrain:escalated\n' ;;
    *) return 1 ;;
  esac
}
export -f __recover_prs __recover_info
export TRAIN_RECOVERY_PRS_FOR_BRANCH=__recover_prs
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
unset TRAIN_RECOVERY_PRS_FOR_BRANCH TRAIN_RECOVERY_PR_INFO_FOR

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
git -C "${WORK}" log -1 --pretty='%an <%ae>%n%b' autofix-batch | grep -Eqi 'co-authored-by|generated with|🤖' \
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
printf 'RESULT: %d passed, %d failed\n' "${PASS}" "${FAIL}"
[[ "${FAIL}" -eq 0 ]]
