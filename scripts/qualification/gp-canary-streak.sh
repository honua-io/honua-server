#!/usr/bin/env bash
set -euo pipefail
repository="${GITHUB_REPOSITORY:?set GITHUB_REPOSITORY}"
server_url="${GITHUB_SERVER_URL:-https://github.com}"
output="${HONUA_GP_CANARY_STREAK_RECEIPT:-artifacts/gp-canary/streak.json}"
mkdir -p "$(dirname "${output}")"
limit=7
current='[]'
if [[ "${GITHUB_EVENT_NAME:-}" == schedule ]]; then
  limit=6
  receipt="${HONUA_GP_CANARY_RECEIPT:-artifacts/gp-canary/receipt.json}"
  if [[ -f "${receipt}" ]]; then
    current="$(jq --arg url "${server_url}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}" '[{databaseId:(.github.run_id|tonumber),conclusion:(if .outcome=="pass" then "success" else "failure" end),createdAt:.started_at,headSha:(.server_revision // ""),url:$url}]' "${receipt}")"
  else
    current="$(jq -n --arg url "${server_url}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}" --arg sha "${GITHUB_SHA:-}" --arg created "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '[{databaseId:($id|tonumber),conclusion:"failure",createdAt:$created,headSha:$sha,url:$url,missing_receipt:true}]' --arg id "${GITHUB_RUN_ID}")"
  fi
fi
history="$(gh run list --repo "${repository}" --workflow gp-buffer-canary.yml --event schedule --status completed --limit "${limit}" --json databaseId,conclusion,createdAt,headSha,url)"
runs="$(jq -cn --argjson current "${current}" --argjson history "${history}" '$current + $history')"
jq --arg generated_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '
  [ .[] | select(.conclusion != "skipped") ] as $runs |
  ($runs | reduce .[] as $run ({count:0,broken:false}; if (.broken or $run.conclusion != "success") then .broken=true else .count += 1 end) | .count) as $consecutive |
  {schema:"honua.gp-buffer-canary-streak.v1",required_consecutive_green:7,
   observed_runs:($runs|length),consecutive_green:$consecutive,
   ready:(($runs|length) == 7 and $consecutive == 7),runs:$runs,generated_at:$generated_at}' <<<"${runs}" > "${output}"
