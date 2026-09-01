#!/usr/bin/env bash
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
compose_file="${repo_root}/docker/gp-reliability/compose.yml"
receipt_root="${HONUA_GP_RECEIPT_DIR:-${repo_root}/artifacts/gp-lifecycle}"
base_url="http://127.0.0.1:${HONUA_GP_PORT:-18080}"
api_key="gp-reliability-admin"
project_name="honua-gp-reliability-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-$$}"
payload='{"inputs":{"wkb":"AQEAAABQ/Bhz15pewNDVVuwv40JA","srid":4326,"distance":500}}'
native_source="$(jq -cn '{type:"FeatureCollection",features:[range(0;500) as $id|{type:"Feature",properties:{id:$id},geometry:{type:"Point",coordinates:[(-157.8583 + ($id % 100) / 10000), (21.3069 + ($id % 50) / 10000)]}}]}' | base64 -w0)"
native_payload="$(jq -cn --arg source "${native_source}" '{inputs:{source:$source,targetFormat:"GeoJSON",sourceFormat:"GeoJSON"}}')"
mkdir -p "${receipt_root}"

require_digest() {
  local name="$1" value="${!1:-}"
  if [[ ! "${value}" =~ ^[^[:space:]@]+@sha256:[0-9a-f]{64}$ ]]; then
    echo "${name} must be an exact image@sha256 digest" >&2
    exit 2
  fi
}

require_digest HONUA_SERVER_IMAGE
require_digest HONUA_WORKER_IMAGE
command -v docker >/dev/null
command -v curl >/dev/null
command -v jq >/dev/null

compose() { docker compose --project-name "${project_name}" -f "${compose_file}" "$@"; }
auth_curl() { curl --fail-with-body --silent --show-error -H "X-API-Key: ${api_key}" "$@"; }
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

image_id() {
  docker image inspect --format '{{index .RepoDigests 0}}' "$1"
}

write_receipt() {
  local scenario="$1" outcome="$2" finding="$3" job_id="${4:-}" terminal="${5:-}" output_sha="${6:-}"
  jq -n \
    --arg schema "honua.gp-lifecycle-receipt.v1" \
    --arg scenario "${scenario}" --arg outcome "${outcome}" --arg finding "${finding}" \
    --arg job_id "${job_id}" --arg terminal "${terminal}" --arg output_sha256 "${output_sha}" \
    --arg server_image "${HONUA_SERVER_IMAGE}" --arg worker_image "${HONUA_WORKER_IMAGE}" \
    --arg postgres_image "$(compose images -q postgres | xargs docker image inspect --format '{{index .RepoDigests 0}}')" \
    --arg redis_image "$(compose images -q redis | xargs docker image inspect --format '{{index .RepoDigests 0}}')" \
    --arg source_sha "$(git -C "${repo_root}" rev-parse HEAD)" --arg completed_at "$(now)" \
    '{schema:$schema,scenario:$scenario,outcome:$outcome,finding:(if $finding == "" then null else $finding end),candidate:{source_sha:$source_sha,server_image:$server_image,worker_image:$worker_image,postgres_image:$postgres_image,redis_image:$redis_image},job:{id:$job_id,terminal_state:$terminal,output_sha256:$output_sha256},completed_at:$completed_at}' \
    > "${receipt_root}/${scenario}.json"
}

wait_ready() {
  local deadline=$((SECONDS + 180))
  until curl --fail --silent "${base_url}/healthz/ready" >/dev/null; do
    (( SECONDS < deadline )) || return 1
    sleep 2
  done
}

submit_async() {
  local process="${1:-geometry.buffer}" body="${2:-${payload}}" response
  response="$(auth_curl -H 'Content-Type: application/json' -H 'Prefer: respond-async' \
    -d "${body}" "${base_url}/ogc/processes/processes/${process}/execution")"
  jq -er '.jobID // .jobId' <<<"${response}"
}

status_json() { auth_curl "${base_url}/ogc/processes/jobs/$1"; }

wait_terminal() {
  local job_id="$1" deadline=$((SECONDS + ${HONUA_GP_SCENARIO_TIMEOUT_SECONDS:-180})) body status
  while (( SECONDS < deadline )); do
    if body="$(status_json "${job_id}" 2>/dev/null)"; then
      status="$(jq -r '.status' <<<"${body}")"
      case "${status}" in successful|failed|dismissed) printf '%s' "${body}"; return 0;; esac
    fi
    sleep 1
  done
  return 1
}

wait_running() {
  local job_id="$1" deadline=$((SECONDS + 30)) body status
  while (( SECONDS < deadline )); do
    body="$(status_json "${job_id}" 2>/dev/null || true)"
    status="$(jq -r '.status // empty' <<<"${body}" 2>/dev/null || true)"
    [[ "${status}" == running ]] && return 0
    case "${status}" in successful|failed|dismissed) return 1;; esac
    sleep 0.01
  done
  return 1
}

result_digest() {
  auth_curl "${base_url}/ogc/processes/jobs/$1/results" | sha256sum | cut -d' ' -f1
}

run_sync() {
  local scenario=sync response
  if response="$(auth_curl -H 'Content-Type: application/json' -d "${payload}" "${base_url}/ogc/processes/processes/geometry.buffer/execution")" && jq -e 'type == "object" and length > 0 and (.status? == null)' <<<"${response}" >/dev/null; then
    write_receipt "${scenario}" pass "" "" successful "$(sha256sum <<<"${response}" | cut -d' ' -f1)"
  else
    write_receipt "${scenario}" fail "synchronous execution failed"; return 1
  fi
}

run_async_baseline() {
  local scenario=async job terminal state progress digest
  job="$(submit_async)" || { write_receipt "${scenario}" fail "async submission failed"; return 1; }
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: lost job or terminal timeout" "${job}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  [[ "${state}" == successful ]] || { write_receipt "${scenario}" fail "unexpected terminal state" "${job}" "${state}"; return 1; }
  progress="$(jq -r '.progress // empty' <<<"${terminal}")"
  [[ "${progress}" == 100 ]] || { write_receipt "${scenario}" fail "FINDING: terminal success did not retain 100 percent progress" "${job}" "${state}"; return 1; }
  digest="$(result_digest "${job}")" || { write_receipt "${scenario}" fail "FINDING: successful job has orphaned output" "${job}" "${state}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" "${state}" "${digest}"
}

run_duplicate_delivery() {
  local scenario=duplicate-delivery job score terminal state digest
  # Redis uses a sorted set, so adding the same delivery twice must remain one member.
  job="$(submit_async)" || { write_receipt "${scenario}" fail "submission failed"; compose start worker >/dev/null; return 1; }
  score="$(date +%s%3N)"
  compose exec -T redis redis-cli ZADD controlplane:jobqueue:pending "${score}" "${job}" >/dev/null
  compose exec -T redis redis-cli ZADD controlplane:jobqueue:pending "${score}" "${job}" >/dev/null
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: duplicate delivery lost job" "${job}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  digest="$(result_digest "${job}" 2>/dev/null || true)"
  [[ "${state}" == successful && -n "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: duplicate delivery produced invalid terminal/output" "${job}" "${state}" "${digest}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" "${state}" "${digest}"
}

run_cancel() {
  local scenario=cancel job terminal state
  job="$(submit_async)" || { write_receipt "${scenario}" fail "submission failed"; return 1; }
  auth_curl -X DELETE "${base_url}/ogc/processes/jobs/${job}" >/dev/null || {
    write_receipt "${scenario}" fail "cancel request failed" "${job}"; return 1;
  }
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: cancelled job lost" "${job}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  case "${state}" in
    dismissed) write_receipt "${scenario}" pass "" "${job}" "${state}";;
    successful)
      # A bounded job may win the race with cancellation. Its output must still be durable.
      local digest
      digest="$(result_digest "${job}" 2>/dev/null || true)"
      [[ -n "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: cancel race orphaned successful output" "${job}" "${state}"; return 1; }
      write_receipt "${scenario}" pass "cancel raced with terminal success" "${job}" "${state}" "${digest}";;
    *) write_receipt "${scenario}" fail "unexpected cancel terminal state" "${job}" "${state}"; return 1;;
  esac
}

run_idempotency() {
  local scenario=idempotency key="gp-qualification-$(date +%s%N)" first second
  first="$(auth_curl -H 'Content-Type: application/json' -H 'Prefer: respond-async' -H "Idempotency-Key: ${key}" \
    -d "${payload}" "${base_url}/ogc/processes/processes/geometry.buffer/execution" | jq -er '.jobID // .jobId')" || {
      write_receipt "${scenario}" fail "first idempotent submission failed"; return 1;
    }
  second="$(auth_curl -H 'Content-Type: application/json' -H 'Prefer: respond-async' -H "Idempotency-Key: ${key}" \
    -d "${payload}" "${base_url}/ogc/processes/processes/geometry.buffer/execution" | jq -er '.jobID // .jobId')" || {
      write_receipt "${scenario}" fail "second idempotent submission failed" "${first}"; return 1;
    }
  if [[ "${first}" != "${second}" ]]; then
    write_receipt "${scenario}" fail "FINDING: identical Idempotency-Key created two jobs" "${first}"
    return 1
  fi
  local terminal state digest
  terminal="$(wait_terminal "${first}")" || { write_receipt "${scenario}" fail "FINDING: idempotent job lost" "${first}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"; digest="$(result_digest "${first}" 2>/dev/null || true)"
  [[ "${state}" == successful && -n "${digest}" ]] || { write_receipt "${scenario}" fail "idempotent job terminal/output invalid" "${first}" "${state}" "${digest}"; return 1; }
  write_receipt "${scenario}" pass "" "${first}" "${state}" "${digest}"
}

run_retry() {
  local scenario=retry job terminal state digest
  # Stop the native worker so cancellation deterministically wins before execution.
  compose stop worker >/dev/null
  job="$(submit_async gdal.ogr2ogr "${native_payload}")" || { write_receipt "${scenario}" fail "retry seed submission failed"; compose start worker >/dev/null; return 1; }
  auth_curl -X DELETE "${base_url}/ogc/processes/jobs/${job}" >/dev/null || true
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "cancelled retry seed was lost" "${job}"; compose start worker >/dev/null; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  if [[ "${state}" != dismissed ]]; then
    write_receipt "${scenario}" fail "retry seed did not reach dismissed" "${job}" "${state}"; compose start worker >/dev/null; return 1
  fi
  auth_curl -H 'Content-Type: application/json' -X POST -d '{}' "${base_url}/api/v1/admin/jobs/${job}/retry" >/dev/null || {
    write_receipt "${scenario}" fail "manual retry request failed" "${job}" "${state}"; compose start worker >/dev/null; return 1;
  }
  compose start worker >/dev/null
  local deadline=$((SECONDS + ${HONUA_GP_SCENARIO_TIMEOUT_SECONDS:-180}))
  terminal=""
  while (( SECONDS < deadline )); do
    terminal="$(status_json "${job}" 2>/dev/null || true)"
    state="$(jq -r '.status // empty' <<<"${terminal}" 2>/dev/null || true)"
    case "${state}" in successful|failed) break;; esac
    sleep 1
  done
  [[ "${state}" == successful || "${state}" == failed ]] || { write_receipt "${scenario}" fail "FINDING: retried job was lost" "${job}" "${state}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"; digest="$(result_digest "${job}" 2>/dev/null || true)"
  [[ "${state}" == successful && -n "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: retry terminal/output invalid" "${job}" "${state}" "${digest}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" "${state}" "${digest}"
}

run_timeout() {
  local scenario=timeout job record terminal state
  compose stop worker >/dev/null
  job="$(submit_async gdal.ogr2ogr "${native_payload}")" || { write_receipt "${scenario}" fail "timeout seed submission failed"; compose start worker >/dev/null; return 1; }
  record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:${job}")"
  record="$(jq -c '.timeoutPolicy={maxDuration:"00:00:00.0010000"}' <<<"${record}")" || {
    write_receipt "${scenario}" fail "could not install bounded timeout policy" "${job}"; compose start worker >/dev/null; return 1;
  }
  printf '%s' "${record}" | compose exec -T redis redis-cli -x SET "controlplane:job:${job}" KEEPTTL >/dev/null
  compose start worker >/dev/null
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: timed job was lost" "${job}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  if [[ "${state}" != failed ]]; then
    write_receipt "${scenario}" fail "FINDING: timeout did not produce one failed terminal state" "${job}" "${state}"; return 1
  fi
  if result_digest "${job}" >/dev/null 2>&1; then
    write_receipt "${scenario}" fail "FINDING: timed-out job exposed orphaned output" "${job}" "${state}"; return 1
  fi
  write_receipt "${scenario}" pass "" "${job}" "${state}"
}

run_disruption() {
  local component="$1" boundary="$2" scenario="restart-${component}-${boundary}" job terminal state before after process=geometry.buffer body="${payload}"
  if [[ "${component}" == worker ]]; then process=gdal.ogr2ogr; body="${native_payload}"; fi
  job="$(submit_async "${process}" "${body}")" || { write_receipt "${scenario}" fail "submission failed"; return 1; }
  if [[ "${boundary}" == running ]] && ! wait_running "${job}"; then
    write_receipt "${scenario}" fail "FINDING: job reached terminal state before running boundary could be disrupted" "${job}"
    return 1
  fi
  if [[ "${boundary}" == terminal || "${boundary}" == results-read ]]; then
    terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: pre-restart terminal timeout" "${job}"; return 1; }
    [[ "${boundary}" == results-read ]] && before="$(result_digest "${job}" 2>/dev/null || true)"
  fi
  compose kill -s KILL "${component}" >/dev/null
  compose start "${component}" >/dev/null
  [[ "${component}" != server ]] || wait_ready
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: job lost across kill/restart" "${job}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  after="$(result_digest "${job}" 2>/dev/null || true)"
  if [[ "${state}" != successful || -z "${after}" || ( "${boundary}" == results-read && "${before}" != "${after}" ) ]]; then
    write_receipt "${scenario}" fail "FINDING: duplicate terminal state or orphaned/changed output" "${job}" "${state}" "${after}"; return 1
  fi
  write_receipt "${scenario}" pass "" "${job}" "${state}" "${after}"
}

failures=0
cleanup() { compose down --volumes --remove-orphans >/dev/null 2>&1 || true; }
trap cleanup EXIT
if [[ "${HONUA_GP_SKIP_PULL:-false}" != true ]]; then compose pull || exit 1; fi
compose up -d || exit 1
wait_ready || { write_receipt topology fail "topology did not become ready"; exit 1; }
run_sync || failures=$((failures + 1))
run_async_baseline || failures=$((failures + 1))
run_cancel || failures=$((failures + 1))
run_idempotency || failures=$((failures + 1))
run_retry || failures=$((failures + 1))
run_timeout || failures=$((failures + 1))
for component in worker server redis postgres; do
  for boundary in accepted running terminal results-read; do
    run_disruption "${component}" "${boundary}" || failures=$((failures + 1))
  done
done
run_duplicate_delivery || failures=$((failures + 1))
jq -s '{schema:"honua.gp-lifecycle-summary.v1",scenarios:.,passed:(map(select(.outcome=="pass"))|length),failed:(map(select(.outcome=="fail"))|length)}' \
  "${receipt_root}"/*.json > "${receipt_root}/summary.json.tmp"
mv "${receipt_root}/summary.json.tmp" "${receipt_root}/summary.json"
exit "${failures}"
