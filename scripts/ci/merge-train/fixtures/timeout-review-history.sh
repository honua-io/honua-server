#!/usr/bin/env bash
# Review-history doubles and assertions for the timeout/recovery fixture.

review_history_mode=empty
review_history_operation="api --paginate repos/${GITHUB_REPOSITORY}/actions/workflows/claude-review.yml/runs?event=workflow_dispatch&per_page=100 --jq .workflow_runs[].display_title"
timeout_fixture_review_history() {
  case "${review_history_mode}" in
    empty) return 0 ;;
    exact) printf 'Claude catch-up #101 @ %s\n' "${fixture_member_sha}" ;;
    stale) printf 'Claude catch-up #101 @ aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n' ;;
    unavailable) return 1 ;;
    *) fail "unexpected review-history fixture mode: ${review_history_mode}" ;;
  esac
}

validate_timeout_review_history() {
  local review_target review_dispatch history_warning
  review_target="$(jq -cn --arg head "${fixture_member_sha}" \
    '[{number:101,headRefOid:$head,reviewCatchupNeeded:true}]')"
  review_dispatch="gh workflow run claude-review.yml --repo ${GITHUB_REPOSITORY} --ref ${TRAIN_BASE_BRANCH:-trunk} -f pr=101 -f head=${fixture_member_sha}"
  for review_history_mode in empty stale; do
    : >"${record}"
    train_dispatch_selected_reviews "${review_target}"
    [[ "$(cat "${record}")" == "${review_dispatch}" ]] \
      || fail "${review_history_mode} history did not dispatch exactly one exact-head review"
  done
  review_history_mode=exact
  : >"${record}"
  train_dispatch_selected_reviews "${review_target}"
  [[ ! -s "${record}" ]] || fail "exact-head history dispatched a duplicate review"
  review_history_mode=empty
  train_dispatch_selected_reviews '[]'
  [[ ! -s "${record}" ]] || fail "empty selection dispatched a review"
  train_dispatch_selected_reviews "$(jq '.[0].reviewCatchupNeeded = false' <<<"${review_target}")"
  [[ ! -s "${record}" ]] || fail "reviewed selection dispatched a review"
  review_history_mode=unavailable
  history_warning="$(train_dispatch_selected_reviews "${review_target}" 2>&1)" \
    || fail "unavailable review history failed recovery"
  [[ ! -s "${record}" ]] || fail "unavailable review history dispatched a review"
  [[ "${history_warning}" == *'exact-head review history unavailable; scheduled catch-up will retry safely'* ]] \
    || fail "unavailable review history did not report its safe retry warning"
  [[ "${history_warning}" != *'FAIL:'* ]] || fail "unavailable history emitted an unexpected mock failure"
  review_history_mode=empty
  pass "review catch-up dispatch and safe history failure are explicit"
}
