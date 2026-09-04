#!/usr/bin/env bash
set -euo pipefail
base_url="${HONUA_GP_CANARY_URL:?set HONUA_GP_CANARY_URL}"
receipt="${HONUA_GP_CANARY_RECEIPT:-artifacts/gp-canary/receipt.json}"
payload='{"inputs":{"wkb":"AQEAAABQ/Bhz15pewNDVVuwv40JA","srid":4326,"distance":500}}'
mkdir -p "$(dirname "${receipt}")"
started="$(date -u +%Y-%m-%dT%H:%M:%SZ)" response="$(mktemp)" headers="$(mktemp)"
transport_status=0
code="$(curl --silent --show-error -D "${headers}" -o "${response}" -w '%{http_code}' -H "Authorization: Bearer ${HONUA_GP_CANARY_TOKEN:?set HONUA_GP_CANARY_TOKEN}" -H 'Content-Type: application/json' -d "${payload}" "${base_url%/}/ogc/processes/processes/geometry.buffer/execution")" || transport_status=$?
digest="$(sha256sum "${response}" | cut -d' ' -f1)"; bytes="$(wc -c < "${response}")"; revision="$(awk 'tolower($1)=="x-honua-revision:" {gsub("\r",""); print $2}' "${headers}" | head -1)"
outcome=fail; finding="HTTP ${code}"; (( transport_status == 0 )) || finding="curl transport failure (${transport_status})"
[[ "${code}" == 200 ]] && jq -e 'type=="object" and length>0' "${response}" >/dev/null && outcome=pass && finding=""
jq -n --arg outcome "${outcome}" --arg finding "${finding}" --arg started_at "${started}" --arg completed_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg endpoint "${base_url%/}" --arg revision "${revision}" --arg output_sha256 "${digest}" --argjson output_bytes "${bytes}" --arg run_id "${GITHUB_RUN_ID:-local}" --arg run_attempt "${GITHUB_RUN_ATTEMPT:-1}" --arg run_url "${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-local}/actions/runs/${GITHUB_RUN_ID:-local}" '{schema:"honua.gp-buffer-canary.v1",canary:"geometry.buffer",outcome:$outcome,finding:(if $finding=="" then null else $finding end),endpoint:$endpoint,server_revision:(if $revision=="" then null else $revision end),output:{bytes:$output_bytes,sha256:$output_sha256},started_at:$started_at,completed_at:$completed_at,github:{run_id:$run_id,run_attempt:$run_attempt,run_url:$run_url}}' > "${receipt}"
[[ "${outcome}" == pass ]]
