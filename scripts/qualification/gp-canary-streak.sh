#!/usr/bin/env bash
set -euo pipefail
repository="${GITHUB_REPOSITORY:?set GITHUB_REPOSITORY}"
output="${HONUA_GP_CANARY_STREAK_RECEIPT:-artifacts/gp-canary/streak.json}"
mkdir -p "$(dirname "${output}")"
limit=7
current='[]'
if [[ "${GITHUB_EVENT_NAME:-}" == schedule && -f "${HONUA_GP_CANARY_RECEIPT:-artifacts/gp-canary/receipt.json}" ]]; then
  limit=6
  current="$(jq '[{databaseId:(.github.run_id|tonumber),conclusion:(if .outcome=="pass" then "success" else "failure" end),createdAt:.started_at,headSha:(.server_revision // ""),url:""}]' "${HONUA_GP_CANARY_RECEIPT:-artifacts/gp-canary/receipt.json}")"
fi
history="$(gh run list --repo "${repository}" --workflow gp-buffer-canary.yml --event schedule --status completed --limit "${limit}" --json databaseId,conclusion,createdAt,headSha,url)"
runs="$(jq -cn --argjson current "${current}" --argjson history "${history}" '$current + $history')"
jq --arg generated_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '
  [ .[] | select(.conclusion != "skipped") ] as $runs |
  ($runs | map(select(.conclusion == "success")) | length) as $green |
  {schema:"honua.gp-buffer-canary-streak.v1",required_consecutive_green:7,
   observed_runs:($runs|length),consecutive_green:(if $green == ($runs|length) then $green else ([range(0; $runs|length) as $i | select($runs[$i].conclusion != "success")][0] // ($runs|length)) end),
   ready:(($runs|length) == 7 and $green == 7),runs:$runs,generated_at:$generated_at}' <<<"${runs}" > "${output}"
