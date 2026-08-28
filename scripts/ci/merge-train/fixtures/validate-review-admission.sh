#!/usr/bin/env bash
# Focused, offline regression tests for the inverted review-admission ladder in
# train_refresh_review_gate: objections block, clean exact-head evidence admits,
# a missing review admits with review trailing and marks the exact head for the
# parent controller to dispatch.

set -euo pipefail

FIXTURE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TRAIN_DIR="$(cd "${FIXTURE_DIR}/.." && pwd)"
export TRAIN_REVIEW_GATE_EVIDENCE_SCRIPT="${TRAIN_DIR}/../review-gate-evidence.js"
export GITHUB_REPOSITORY="honua-io/honua-server"

# shellcheck disable=SC1091
source "${TRAIN_DIR}/select.sh"

HEAD_SHA="$(printf 'a%.0s' {1..40})"
OLD_SHA="$(printf 'b%.0s' {1..40})"

# --- stubs (defined after sourcing, so they shadow the real implementations) ---
train_log() { :; }
train_warn() { :; }
train_err() { :; }
train_attesting_logins_json() { printf '%s' '["chatgpt-codex-connector","chatgpt-codex-connector[bot]","claude[bot]","claude"]'; }
train_resolve_clean_comment_commits() { printf '%s' "$1"; }
PUBLISHED_STATE=""; PUBLISHED_DESC=""
train_publish_review_gate_status() { PUBLISHED_STATE="$3"; PUBLISHED_DESC="$4"; }
train_side_effect() {
  fail "review admission must not dispatch from the selector subshell"
}
GH_COMMIT_DATE=""
gh() {
  # Only the head-age lookup reaches gh in these tests.
  printf '%s' "${GH_COMMIT_DATE}"
}

snapshot() {
  # $1: unresolved-thread commit oid or "" for none; $2: cleanComments json
  local thread="[]"
  if [[ -n "$1" ]]; then
    thread="$(jq -nc --arg oid "$1" \
      '[{isResolved:false, comments:{nodes:[{author:{login:"chatgpt-codex-connector"}, commit:{oid:$oid}}]}}]')"
  fi
  jq -nc --argjson t "${thread}" --argjson c "$2" \
    '{reviewThreads:$t, cleanComments:$c, reviews:[]}'
}

clean_comment() {
  jq -nc --arg head "${HEAD_SHA}" '[{
    author:{login:"chatgpt-codex-connector"},
    body:("Codex Review: Didn'\''t find any major issues.\n\n**Reviewed commit:** `"+$head+"`"),
    createdAt:"2026-01-02T00:00:00Z", updatedAt:"2026-01-02T00:00:00Z",
    includesCreatedEdit:false}]'
}

run_gate() { # $1 snapshot; returns gate rc without tripping errexit
  local rc=0
  TRAIN_APPLY=1 train_refresh_review_gate 999 "${HEAD_SHA}" "$1" || rc=$?
  return "${rc}"
}

fail() { printf 'FAIL: %s\n' "$1"; exit 1; }

# 1. An unresolved reviewer finding blocks, even when anchored to an OLD commit.
rc=0; run_gate "$(snapshot "${OLD_SHA}" '[]')" || rc=$?
[[ "${rc}" != 0 ]] || fail "old-commit unresolved finding did not block"
[[ "${PUBLISHED_DESC}" == *"unresolved reviewer finding"* ]] || fail "wrong refusal reason: ${PUBLISHED_DESC}"
printf 'PASS: %s\n' "old-commit unresolved finding blocks admission"

# 2. Clean exact-head evidence admits immediately.
rc=0; run_gate "$(snapshot "" "$(clean_comment)")" || rc=$?
[[ "${rc}" == 0 ]] || fail "clean exact-head evidence did not admit (${PUBLISHED_DESC})"
[[ "${PUBLISHED_DESC}" == *"evidence is clean"* ]] || fail "wrong admit reason: ${PUBLISHED_DESC}"
printf 'PASS: %s\n' "clean exact-head evidence admits"

# 3. No evidence, no objections: admits IMMEDIATELY (operator decision
#    2026-08-28, "yes permanently" -- no courtesy window), and the catch-up is
#    marked so the parent controller can dispatch the exact PR/head.
unset TRAIN_REVIEW_CATCHUP_NEEDED 2>/dev/null || true
rc=0; run_gate "$(snapshot "" '[]')" || rc=$?
[[ "${rc}" == 0 ]] || fail "finding-free head was not admitted immediately (${PUBLISHED_DESC})"
[[ "${PUBLISHED_DESC}" == *"review trails"* ]] || fail "wrong trailing reason: ${PUBLISHED_DESC}"
[[ "${TRAIN_REVIEW_CATCHUP_NEEDED}" == 1 ]] || fail "exact-head review was not marked for dispatch"
rc=0; run_gate "$(snapshot "" '[]')" || rc=$?
[[ "${TRAIN_REVIEW_CATCHUP_NEEDED}" == 1 ]] || fail "exact-head review dispatch marker was not stable"
printf 'PASS: %s\n' "finding-free head admits immediately; exact head marked for dispatch"

# 4. Readiness probes evaluate admission without dispatching another catch-up.
unset TRAIN_REVIEW_CATCHUP_NEEDED TRAIN_REVIEW_CATCHUP_DISPATCHED 2>/dev/null || true
TRAIN_REVIEW_CATCHUP_DISPATCH_DISABLED=1
DISPATCH_COUNT=0
rc=0; run_gate "$(snapshot "" '[]')" || rc=$?
unset TRAIN_REVIEW_CATCHUP_DISPATCH_DISABLED
[[ "${rc}" == 0 ]] || fail "side-effect-free readiness evaluation rejected finding-free head"
[[ "${DISPATCH_COUNT}" == 0 ]] || fail "readiness evaluation dispatched ${DISPATCH_COUNT} catch-up run(s)"
printf 'PASS: %s\n' "readiness evaluation suppresses catch-up dispatch side effects"
# 5. The trusted Review Gate publishes the same inverted-admission vocabulary
#    and counts unresolved findings at any commit. These static guards exercise
#    the inline github-script evaluator without weakening its trusted workflow.
REVIEW_GATE_WORKFLOW="${TRAIN_DIR}/../../../.github/workflows/review-gate.yml"
grep -Fq 'const { exactReview, exactCleanComment, hasOpenObjection }' "${REVIEW_GATE_WORKFLOW}" \
  || fail "Review Gate does not consume hasOpenObjection"
grep -Fq 'isAttestingReviewer(comment.author?.login, comment.author?.__typename)))' "${REVIEW_GATE_WORKFLOW}" \
  || fail "Review Gate does not count unresolved findings at any commit"
if grep -Fq 'comment.commit?.oid === head' "${REVIEW_GATE_WORKFLOW}"; then
  fail "Review Gate still head-scopes unresolved findings"
fi
for description in \
  'unresolved reviewer finding(s) block admission' \
  'Open reviewer objection (negative review not withdrawn by its own identity)' \
  'Current exact-head review evidence is clean' \
  'No reviewer objections; review trails this merge (fix-forward admission)'; do
  grep -Fq "${description}" "${REVIEW_GATE_WORKFLOW}" \
    || fail "Review Gate is missing status description: ${description}"
done
if grep -Fq 'no exact-head review evidence from an attesting reviewer' "${REVIEW_GATE_WORKFLOW}"; then
  fail "Review Gate still fails finding-free unreviewed heads"
fi
printf 'PASS: %s\n' "trusted Review Gate matches inverted admission vocabulary"

printf 'ALL PASS\n'
