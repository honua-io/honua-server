#!/usr/bin/env bash
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
compose_file="${repo_root}/docker/gp-reliability/compose.yml"
receipt_root="${HONUA_GP_RECEIPT_DIR:-${repo_root}/artifacts/gp-lifecycle}"
base_url="http://127.0.0.1:${HONUA_GP_PORT:-18080}"
api_key="gp-reliability-admin"
project_name="honua-gp-reliability-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-$$}"
lane="${HONUA_GP_LANE:-lifecycle}"
run_url="${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-local}/actions/runs/${GITHUB_RUN_ID:-local}"
candidate_source_sha="${HONUA_GP_SOURCE_SHA:-}"
candidate_source_sha="${candidate_source_sha,,}"
payload='{"inputs":{"wkb":"AQEAAABQ/Bhz15pewNDVVuwv40JA","srid":4326,"distance":500}}'
native_source="$(jq -cn '{type:"FeatureCollection",features:[range(0;500) as $id|{type:"Feature",properties:{id:$id},geometry:{type:"Point",coordinates:[(-157.8583 + ($id % 100) / 10000), (21.3069 + ($id % 50) / 10000)]}}]}' | base64 -w0)"
native_payload="$(jq -cn --arg source "${native_source}" '{inputs:{source:$source,targetFormat:"GeoJSON",sourceFormat:"GeoJSON"}}')"
mkdir -p "${receipt_root}"

case "${lane}" in
  lifecycle)
    declared_scenarios=(topology sync async cancel-claimed cancel-native-process-started \
      cancel-output-bytes-written-unpublished cancel-artifact-reference-published-terminal-cas-pending \
      idempotency retry timeout-cooperative timeout-ignoring \
      restart-worker-accepted restart-worker-running restart-worker-terminal restart-worker-results-read \
      restart-server-accepted restart-server-running restart-server-terminal restart-server-results-read \
      restart-redis-accepted restart-redis-running restart-redis-terminal restart-redis-results-read \
      restart-postgres-accepted restart-postgres-running restart-postgres-terminal restart-postgres-results-read \
      duplicate-delivery cleanup)
    ;;
  resilience)
    declared_scenarios=(topology poison-job stale-lease output-write-failure queue-backlog ttl-cleanup \
      retry-exhaustion output-size-cap tenant-quotas-backpressure-nondisclosure sustained-soak cleanup)
    ;;
  self-test)
    declared_scenarios=(assertion-failure follow-up cleanup)
    ;;
  *)
    echo "HONUA_GP_LANE must be lifecycle, resilience, or self-test" >&2
    exit 2
    ;;
esac

declare -A receipt_written=()
scenario_name=""
peer_url="http://127.0.0.1:${HONUA_GP_PEER_PORT:-18081}"
barrier_root="${HONUA_GP_BARRIER_ROOT-}"
[[ -n "$barrier_root" ]] || barrier_root="$receipt_root/barriers"
object_root="${HONUA_GP_OBJECT_ROOT-}"
[[ -n "$object_root" ]] || object_root="$receipt_root/objects"
export HONUA_GP_BARRIER_ROOT="$barrier_root" HONUA_GP_OBJECT_ROOT="$object_root"
mkdir -p "$barrier_root" "$object_root"
chmod 777 "$barrier_root" "$object_root"
scenario_started_at=""
scenario_state_file=""
scenario_transition_file=""
scenario_disruption_file=""
scenario_attempt_file=""
scenario_evidence_file=""
scenario_finding=""
scenario_cleanup_failure=""
preflight_failure=""
failures=0
finished=0
observed_candidate_file="${receipt_root}/.observed-candidate.json"

now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

scenario_fail() {
  scenario_finding="$1"
  return 1
}

scenario_state_reset() {
  scenario_name="$1"
  scenario_evidence_file="$receipt_root/.$scenario_name.evidence.json"
  jq -n '{}' > "$scenario_evidence_file"
  scenario_started_at="$(now)"
  scenario_state_file="${receipt_root}/.${scenario_name}.state.json"
  scenario_transition_file="${receipt_root}/.${scenario_name}.transitions.ndjson"
  scenario_disruption_file="${receipt_root}/.${scenario_name}.disruptions.ndjson"
  scenario_attempt_file="${receipt_root}/.${scenario_name}.attempts"
  scenario_finding=""
  : > "${scenario_transition_file}"
  : > "${scenario_disruption_file}"
  printf '1\n' > "${scenario_attempt_file}"
  jq -n '{sha256:null,bytes:0}' > "${scenario_state_file}"
}

record_transition() {
  local state="$1"
  [[ -n "${scenario_transition_file}" ]] || return 0
  jq -cn --arg observed_at "$(now)" --arg state "${state}" \
    '{observed_at:$observed_at,state:$state}' >> "${scenario_transition_file}"
}

record_disruption() {
  local component="$1" boundary="$2" action="$3"
  [[ -n "${scenario_disruption_file}" ]] || return 0
  jq -cn --arg observed_at "$(now)" --arg component "${component}" \
    --arg boundary "${boundary}" --arg action "${action}" \
    '{observed_at:$observed_at,component:$component,boundary:$boundary,action:$action}' \
    >> "${scenario_disruption_file}"
}

record_attempt() {
  local attempts="$1" current=1
  [[ -f "${scenario_attempt_file}" ]] && current="$(<"${scenario_attempt_file}")"
  (( attempts > current )) && printf '%s\n' "${attempts}" > "${scenario_attempt_file}"
}

set_scenario_evidence() {
  local value="$1"
  jq -e . <<<"$value" > "$scenario_evidence_file"
}

require_digest() {
  local name="$1" value="${!1:-}"
  if [[ ! "${value}" =~ ^[^[:space:]@]+@sha256:[0-9a-f]{64}$ ]]; then
    echo "${name} must be an exact image@sha256 digest" >&2
    return 1
  fi
}

compose() { docker compose --project-name "${project_name}" -f "${compose_file}" "$@"; }
auth_curl() { curl --fail-with-body --silent --show-error -H "X-API-Key: ${api_key}" "$@"; }
tenant_curl() { local token="$1"; shift; curl --silent --show-error -H "Authorization: Bearer ${token}" "$@"; }

image_id() {
  docker image inspect --format '{{index .RepoDigests 0}}' "$1"
}

verify_image_revision() {
  local name="$1" image="$2" revision
  revision="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "${image}")"
  if [[ "${revision,,}" != "${candidate_source_sha}" ]]; then
    echo "${name} image revision '${revision}' does not match candidate ${candidate_source_sha}" >&2
    return 1
  fi
}

write_receipt() {
  local evidence='{}'
  if [[ -f "$scenario_evidence_file" ]]; then
    evidence="$(<"$scenario_evidence_file")"
  fi
  local scenario="$1" outcome="$2" finding="${3:-}" job_id="${4:-}" terminal="${5:-}" output_sha="${6:-}"
  local path="${receipt_root}/${scenario}.json" completed_at attempts transitions disruptions state candidate
  if [[ -e "${path}" ]]; then
    receipt_written["${scenario}"]=$(( ${receipt_written["${scenario}"]:-0} + 1 ))
    return 1
  fi
  [[ -n "${output_sha}" ]] && jq --arg sha "${output_sha}" '.sha256=$sha' "${scenario_state_file}" > "${scenario_state_file}.tmp" && mv "${scenario_state_file}.tmp" "${scenario_state_file}"
  completed_at="$(now)"
  attempts=1; [[ -f "${scenario_attempt_file}" ]] && attempts="$(<"${scenario_attempt_file}")"
  transitions='[]'; [[ -f "${scenario_transition_file}" ]] && transitions="$(jq -s '.' "${scenario_transition_file}")"
  disruptions='[]'; [[ -f "${scenario_disruption_file}" ]] && disruptions="$(jq -s '.' "${scenario_disruption_file}")"
  state='{"sha256":null,"bytes":0}'; [[ -f "${scenario_state_file}" ]] && state="$(<"${scenario_state_file}")"
  candidate='{"requested":{"server_image":"","worker_image":"","source_sha":""},"observed":null}'
  [[ -f "${observed_candidate_file}" ]] && candidate="$(<"${observed_candidate_file}")"
  jq -n \
    --argjson evidence "$evidence" \
    --arg schema "honua.gp-lifecycle-receipt.v2" \
    --arg lane "${lane}" --arg scenario "${scenario}" --arg outcome "${outcome}" --arg finding "${finding}" \
    --arg job_id "${job_id}" --arg terminal "${terminal}" --arg source_sha "${candidate_source_sha}" \
    --arg started_at "${scenario_started_at:-${completed_at}}" --arg completed_at "${completed_at}" \
    --arg run_url "${run_url}" --argjson attempts "${attempts}" --argjson transitions "${transitions}" \
    --argjson disruptions "${disruptions}" --argjson output "${state}" --argjson candidate "${candidate}" \
    '{schema:$schema,lane:$lane,scenario:$scenario,outcome:$outcome,finding:(if $finding=="" then null else $finding end),started_at:$started_at,completed_at:$completed_at,attempt_count:$attempts,state_transitions:$transitions,disruptions:$disruptions,output:{bytes:$output.bytes,sha256:(if $output.sha256==null then null else $output.sha256 end)},job:{id:(if $job_id=="" then null else $job_id end),terminal_state:(if $terminal=="" then null else $terminal end)},evidence:$evidence,candidate:$candidate,source_sha:$source_sha,github:{run_url:$run_url,run_id:(env.GITHUB_RUN_ID // "local"),run_attempt:(env.GITHUB_RUN_ATTEMPT // "1")}}' \
    > "${path}"
  receipt_written["${scenario}"]=1
}

wait_ready() {
  local deadline=$((SECONDS + 180))
  until curl --fail --silent "${base_url}/healthz/ready" >/dev/null; do
    (( SECONDS < deadline )) || return 1
    sleep 2
  done
}

wait_peer_ready() {
  local deadline=$((SECONDS + 180))
  until curl --fail --silent "$peer_url/healthz/ready" >/dev/null; do
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

status_json() {
  local job_id="$1" body status attempts
  body="$(auth_curl "${base_url}/ogc/processes/jobs/${job_id}")" || return 1
  status="$(jq -r '.status // "unknown"' <<<"${body}")"
  attempts="$(jq -r '.attemptCount // 1' <<<"${body}")"
  record_transition "${status}"
  record_attempt "${attempts}"
  printf '%s' "${body}"
}

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

barrier_directory() {
  printf '%s/%s' "$barrier_root" "$1"
}

wait_barrier() {
  local job="$1" barrier="$2" deadline=$((SECONDS + 60)) ready
  ready="$(barrier_directory "$job")/$barrier.ready.json"
  while (( SECONDS < deadline )); do
    [[ -s "$ready" ]] && { jq -e . "$ready" >/dev/null || return 1; return 0; }
    sleep 0.05
  done
  return 1
}

release_barrier() {
  local job="$1" barrier="$2"
  : > "$(barrier_directory "$job")/$barrier.release"
}

barrier_record() {
  local job="$1" barrier="$2" suffix="${3:-}" path
  [[ -n "$suffix" ]] || suffix=ready
  path="$(barrier_directory "$job")/$barrier.$suffix.json"
  [[ -s "$path" ]] && jq -c . "$path" || printf 'null'
}

object_file_count() {
  local job="$1"
  find "$object_root/gp/outputs/$job" -type f ! -name '*.hold' ! -name '*.readlease' ! -name '*.pending' -print 2>/dev/null | wc -l | tr -d ' '
}

result_status_code() {
  local job="$1" url="$2" body
  body="$(mktemp)"
  curl --silent --show-error -H "X-API-Key: $api_key" -o "$body" -w '%{http_code}' \
    "$url/ogc/processes/jobs/$job/results"
  rm -f "$body"
}

delete_capture() {
  local url="$1" job="$2" slot="$3" headers body code
  headers="$(mktemp)"; body="$(mktemp)"
  code="$(curl --silent --show-error -H "X-API-Key: $api_key" -X DELETE \
    -D "$headers" -o "$body" -w '%{http_code}' "$url/ogc/processes/jobs/$job")"
  printf '%s' "$code" > "$receipt_root/.$scenario_name.$slot.cancel-code"
  jq -c . "$body" > "$receipt_root/.$scenario_name.$slot.cancel.json" 2>/dev/null || printf '{}' > "$receipt_root/.$scenario_name.$slot.cancel.json"
  rm -f "$headers" "$body"
  printf '%s' "$code"
}

cancel_barrier_job() {
  local target="$1" job cancel_url="$base_url" request_at
  local process_ready output_ready terminal_ready signal_observed result_code_after result_code_late
  local child_pid child_alive worker_container
  request_at="$(now)"
  job="$(submit_async gdal.ogr2ogr "$native_payload")" || return 1

  wait_barrier "$job" claimed || return 1
  if [[ "$target" != claimed ]]; then
    release_barrier "$job" claimed
    wait_barrier "$job" native-process-started || return 1
    process_ready="$(barrier_record "$job" native-process-started)"
    child_pid="$(jq -r '.childProcessId // empty' <<<"$process_ready")"
    worker_container="$(compose ps -q worker)"
    if [[ "$target" == native-process-started ]]; then
      [[ -n "$child_pid" && -n "$worker_container" ]] || {
        scenario_fail "native-process-started barrier did not report a child PID"
        return 1
      }
      docker exec "$worker_container" kill -0 "$child_pid" >/dev/null 2>&1 || {
        scenario_fail "native child was not alive at the cancellation barrier"
        return 1
      }
    fi
  fi
  if [[ "$target" == output-bytes-written-unpublished || "$target" == artifact-reference-published-terminal-cas-pending ]]; then
    release_barrier "$job" native-process-started
    wait_barrier "$job" output-bytes-written-unpublished || return 1
  fi
  if [[ "$target" == artifact-reference-published-terminal-cas-pending ]]; then
    release_barrier "$job" output-bytes-written-unpublished
    wait_barrier "$job" artifact-reference-published-terminal-cas-pending || return 1
  fi

  if [[ "$target" == artifact-reference-published-terminal-cas-pending ]]; then
    compose restart server >/dev/null || return 1
    wait_ready || return 1
    cancel_url="$peer_url"
  elif [[ "$target" == claimed || "$target" == output-bytes-written-unpublished ]]; then
    cancel_url="$peer_url"
  fi

  response_one="$(delete_capture "$cancel_url" "$job" first)"
  response_two="$(delete_capture "$cancel_url" "$job" second)"
  [[ "$response_one" =~ ^2[0-9][0-9]$ && "$response_two" == "$response_one" ]] || {
    scenario_fail "repeat cancellation did not preserve HTTP idempotency semantics"
    return 1
  }
  first_semantics="$(jq -c '{status,jobID,jobId}' "$receipt_root/.$scenario_name.first.cancel.json")"
  second_semantics="$(jq -c '{status,jobID,jobId}' "$receipt_root/.$scenario_name.second.cancel.json")"
  [[ "$first_semantics" == "$second_semantics" && "$first_semantics" != '{"status":null,"jobID":null,"jobId":null}' ]] || {
    scenario_fail "repeat cancellation changed the OGC response semantics"
    return 1
  }

  terminal="$(wait_terminal "$job")" || return 1
  state="$(jq -r '.status' <<<"$terminal")"
  [[ "$state" == dismissed ]] || {
    scenario_fail "in-flight cancellation did not converge to Dismissed"
    return 1
  }
  jq -e 'all(.[]; .state != "successful")' <(jq -s '.' "$scenario_transition_file") >/dev/null || {
    scenario_fail "cancelled job was observed as Success after cancellation"
    return 1
  }
  result_code_after="$(result_status_code "$job" "$cancel_url")"
  [[ "$result_code_after" == 410 || "$result_code_after" == 404 ]] || {
    scenario_fail "cancelled job retained a readable result"
    return 1
  }
  sleep 2
  result_code_late="$(result_status_code "$job" "$cancel_url")"
  [[ "$result_code_late" == "$result_code_after" ]] || {
    scenario_fail "cancelled job later changed result visibility"
    return 1
  }

  process_ready="$(barrier_record "$job" native-process-started)"
  output_ready="$(barrier_record "$job" output-bytes-written-unpublished)"
  terminal_ready="$(barrier_record "$job" artifact-reference-published-terminal-cas-pending)"
  signal_observed='null'
  for barrier in claimed native-process-started output-bytes-written-unpublished artifact-reference-published-terminal-cas-pending; do
    candidate_signal="$(barrier_record "$job" "$barrier" signal-observed)"
    if [[ "$candidate_signal" != null ]]; then
      signal_observed="$candidate_signal"
      break
    fi
  done
  child_pid="$(jq -r '.childProcessId // empty' <<<"$process_ready")"
  child_alive=false
  if [[ -n "$child_pid" ]]; then
    worker_container="$(compose ps -q worker)"
    if docker exec "$worker_container" kill -0 "$child_pid" >/dev/null 2>&1; then
      child_alive=true
    fi
    [[ "$child_alive" == false ]] || {
      scenario_fail "native child process remained alive after cancellation"
      return 1
    }
  fi
  record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:$job")"
  pending="$(compose exec -T redis redis-cli --raw ZSCORE controlplane:jobqueue:pending "$job" || true)"
  claimed_score="$(compose exec -T redis redis-cli --raw ZSCORE controlplane:jobqueue:claimed "$job" || true)"
  after_objects="$(object_file_count "$job")"
  [[ "$after_objects" == 0 ]] || {
    scenario_fail "cancelled job retained staged objects after retention cleanup"
    return 1
  }
  set_scenario_evidence "$(jq -n \
    --arg request_at "$request_at" \
    --arg cancel_source "OGC DELETE via $cancel_url" \
    --arg response_one "$response_one" --arg response_two "$response_two" \
    --arg result_code_after "$result_code_after" --arg result_code_late "$result_code_late" \
    --argjson process "$process_ready" --argjson output "$output_ready" --argjson terminal "$terminal_ready" \
    --argjson signal "$signal_observed" \
    --argjson child_alive "$child_alive" \
    --argjson record "$record" --arg pending "$pending" --arg claimed "$claimed_score" \
    --argjson objects_after "$after_objects" \
    --argjson transitions "$(jq -s '.' "$scenario_transition_file")" \
    '{request_at:$request_at,claim_at:$record.claimedAt,worker_id:$record.claimedBy,process:$process,output:$output,terminal_fence:$terminal,artifact_references:($record.artifactReferences // []),timeout_source:null,cancellation_source:$cancel_source,cancellation_responses:{first_http:$response_one,second_http:$response_two},signal_observed_at:($signal.observedAt // null),child_process:{pid:($process.childProcessId // null),exit_observed:($child_alive|not)},terminal_history:$transitions,attempt_count:($record.attemptCount // null),queue_membership:{pending_score:(if $pending=="" then null else $pending end),claimed_score:(if $claimed=="" then null else $claimed end)},result_visibility:{after_terminal:$result_code_after,late:$result_code_late},object_inventory:{after_retention_cleanup:$objects_after}}')"
  write_receipt "$scenario_name" pass "" "$job" "$state"
}

result_digest() {
  local tmp digest bytes
  tmp="$(mktemp)"
  if ! auth_curl "${base_url}/ogc/processes/jobs/$1/results" > "${tmp}"; then
    rm -f "${tmp}"
    return 1
  fi
  digest="$(sha256sum "${tmp}" | cut -d' ' -f1)"
  bytes="$(wc -c < "${tmp}")"
  jq -n --arg sha "${digest}" --argjson bytes "${bytes}" '{sha256:$sha,bytes:$bytes}' > "${scenario_state_file}"
  rm -f "${tmp}"
  printf '%s' "${digest}"
}

run_sync() {
  local scenario=sync response
  if response="$(auth_curl -H 'Content-Type: application/json' -d "${payload}" "${base_url}/ogc/processes/processes/geometry.buffer/execution")" && jq -e 'type == "object" and length > 0 and (.status? == null)' <<<"${response}" >/dev/null; then
    local output_sha output_bytes
    output_sha="$(sha256sum <<<"${response}" | cut -d' ' -f1)"; output_bytes="$(wc -c <<<"${response}")"
    jq -n --arg sha "${output_sha}" --argjson bytes "${output_bytes}" '{sha256:$sha,bytes:$bytes}' > "${scenario_state_file}"
    write_receipt "${scenario}" pass "" "" successful "${output_sha}"
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
  local scenario=duplicate-delivery job score terminal state digest record attempts deadline
  # Two consumers are required: re-deliver after the first has claimed and begun executing.
  compose up -d --scale worker=2 worker >/dev/null
  job="$(submit_async gdal.ogr2ogr "${native_payload}")" || { write_receipt "${scenario}" fail "submission failed"; return 1; }
  wait_running "${job}" || { write_receipt "${scenario}" fail "job did not reach running before redelivery" "${job}"; return 1; }
  score="$(date +%s%3N)"
  compose exec -T redis redis-cli ZADD controlplane:jobqueue:pending "${score}" "${job}" >/dev/null
  deadline=$((SECONDS + 30)); attempts=0
  while (( SECONDS < deadline )); do
    record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:${job}")"
    attempts="$(jq -r '.attemptCount // 0' <<<"${record}")"
    (( attempts >= 2 )) && break
    sleep 0.1
  done
  (( attempts >= 2 )) || { write_receipt "${scenario}" fail "FINDING: redelivery was not claimed" "${job}"; return 1; }
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: duplicate delivery lost job" "${job}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  digest="$(result_digest "${job}" 2>/dev/null || true)"
  [[ "${state}" == successful && -n "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: duplicate delivery produced invalid terminal/output" "${job}" "${state}" "${digest}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" "${state}" "${digest}"
}

run_cancel_barrier() {
  local target="$1"
  scenario_name="cancel-$target"
  cancel_barrier_job "$target"
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

run_timeout_live() {
  local mode="$1" job record terminal state retry_code result_code failed_terminal_count
  local request_at signal_file object_count_before object_count_after process_ready child_pid worker_container child_alive signal_deadline
  local behavior="native production executor"
  export HONUA_GP_QUALIFICATION_BARRIER_ROOT=/var/run/honua/qualification
  export HONUA_GP_TIMEOUT_SECONDS=2
  if [[ "$mode" == ignore-cancellation ]]; then
    export HONUA_GP_QUALIFICATION_EXECUTOR_MODE=ignore-cancellation
    behavior="native production executor ignores operator cancellation; timeout remains authoritative"
  else
    unset HONUA_GP_QUALIFICATION_EXECUTOR_MODE
  fi
  compose up -d --force-recreate server server-peer worker >/dev/null || {
    scenario_fail "timeout qualification topology could not be recreated"
    return 1
  }
  wait_ready && wait_peer_ready || return 1
  request_at="$(now)"
  job="$(submit_async gdal.ogr2ogr "$native_payload")" || return 1
  object_count_before="$(object_file_count "$job")"
  record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:$job")"
  jq -e '.timeoutPolicy.maxDuration != null' <<<"$record" >/dev/null || {
    scenario_fail "supported workload timeout was not persisted on the submitted job"
    return 1
  }
  wait_barrier "$job" claimed || return 1
  release_barrier "$job" claimed
  wait_barrier "$job" native-process-started || return 1
  process_ready="$(barrier_record "$job" native-process-started)"
  child_pid="$(jq -r '.childProcessId // empty' <<<"$process_ready")"
  worker_container="$(compose ps -q worker)"
  [[ -n "$child_pid" && -n "$worker_container" ]] || {
    scenario_fail "native-process-started barrier did not report a child PID"
    return 1
  }
  docker exec "$worker_container" kill -0 "$child_pid" >/dev/null 2>&1 || {
    scenario_fail "native child was not alive at the execution barrier"
    return 1
  }
  signal_file="$(barrier_directory "$job")/native-process-started.signal-observed.json"
  signal_deadline=$((SECONDS + 60))

  # Retry is not accepted while execution is live. In ignore mode the durable
  # operator cancellation is also sent while the real native child is alive;
  # timeout must still win and terminate the child.
  retry_code="$(curl --silent --show-error -H "X-API-Key: $api_key" -H 'Content-Type: application/json' \
    -o /dev/null -w '%{http_code}' -X POST -d '{}' "$base_url/api/v1/admin/jobs/$job/retry")"
  if [[ "$mode" == ignore-cancellation ]]; then
    delete_capture "$peer_url" "$job" first >/dev/null || return 1
    delete_capture "$peer_url" "$job" second >/dev/null || return 1
  fi
  while [[ ! -s "$signal_file" ]]; do
    (( SECONDS < signal_deadline )) || return 1
    sleep 0.05
  done
  compose kill -s TERM worker >/dev/null || true
  compose up -d worker >/dev/null || return 1
  child_alive=false
  docker exec "$worker_container" kill -0 "$child_pid" >/dev/null 2>&1 && child_alive=true
  [[ "$child_alive" == false ]] || {
    scenario_fail "native child process remained alive after timeout"
    return 1
  }

  terminal="$(wait_terminal "$job")" || return 1
  state="$(jq -r '.status' <<<"$terminal")"
  [[ "$state" == failed ]] || {
    scenario_fail "supported timeout did not produce Failed"
    return 1
  }
  jq -e 'all(.[]; .state != "successful")' <(jq -s '.' "$scenario_transition_file") >/dev/null || {
    scenario_fail "timed-out job was observed as Success"
    return 1
  }
  failed_terminal_count="$(jq -s '[.[] | select(.state == "failed")] | length' "$scenario_transition_file")"
  [[ "$failed_terminal_count" == 1 ]] || {
    scenario_fail "supported timeout produced more than one Failed terminal observation"
    return 1
  }
  [[ ! "$retry_code" =~ ^2[0-9][0-9]$ ]] || {
    scenario_fail "retry was accepted while the timed executor was live"
    return 1
  }
  result_code="$(result_status_code "$job" "$base_url")"
  [[ "$result_code" == 500 ]] || {
    scenario_fail "timed-out job exposed a readable result"
    return 1
  }
  sleep 2
  object_count_after="$(object_file_count "$job")"
  [[ "$object_count_after" == 0 ]] || {
    scenario_fail "timed-out job retained staged objects after retention cleanup"
    return 1
  }
  record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:$job")"
  pending="$(compose exec -T redis redis-cli --raw ZSCORE controlplane:jobqueue:pending "$job" || true)"
  claimed_score="$(compose exec -T redis redis-cli --raw ZSCORE controlplane:jobqueue:claimed "$job" || true)"
  set_scenario_evidence "$(jq -n \
    --arg request_at "$request_at" --arg behavior "$behavior" \
    --arg retry_code "$retry_code" --arg result_code "$result_code" \
    --argjson record "$record" --argjson signal "$(barrier_record "$job" native-process-started signal-observed)" \
    --argjson process "$(barrier_record "$job" native-process-started)" \
    --argjson transitions "$(jq -s '.' "$scenario_transition_file")" \
    --arg pending "$pending" --arg claimed "$claimed_score" \
    --argjson before "$object_count_before" --argjson after "$object_count_after" \
    --argjson child_alive "$child_alive" --argjson failed_terminal_count "$failed_terminal_count" \
    '{request_at:$request_at,claim_at:$record.claimedAt,worker_id:$record.claimedBy,process:$process,artifact_references:($record.artifactReferences // []),timeout_source:"supported workload policy batch.timeout_seconds",cancellation_source:(if $behavior|startswith("native production executor ignores") then "OGC DELETE via peer" else null end),signal_observed_at:($signal.observedAt // null),child_process:{pid:($process.childProcessId // null),exit_observed:($child_alive|not)},terminal_history:$transitions,terminal_failure_count:$failed_terminal_count,attempt_count:($record.attemptCount // null),queue_membership:{pending_score:(if $pending=="" then null else $pending end),claimed_score:(if $claimed=="" then null else $claimed end)},retry_race_http:$retry_code,result_visibility:{after_terminal:$result_code},object_inventory:{before:$before,after_retention_cleanup:$after}}')"
  unset HONUA_GP_QUALIFICATION_EXECUTOR_MODE
  unset HONUA_GP_QUALIFICATION_BARRIER_ROOT
  export HONUA_GP_TIMEOUT_SECONDS=3600
  compose up -d --force-recreate server server-peer worker >/dev/null || return 1
  wait_ready || return 1
  write_receipt "$scenario_name" pass "" "$job" "$state"
}

run_timeout_cooperative() {
  run_timeout_live cooperative
}

run_timeout_ignoring() {
  run_timeout_live ignore-cancellation
}

run_disruption() {
  local component="$1" boundary="$2" scenario="restart-${component}-${boundary}" job terminal state before after process=geometry.buffer body="${payload}"
  if [[ "${component}" == worker || "${boundary}" == running ]]; then process=gdal.ogr2ogr; body="${native_payload}"; fi
  job="$(submit_async "${process}" "${body}")" || { write_receipt "${scenario}" fail "submission failed"; return 1; }
  if [[ "${boundary}" == running ]] && ! wait_running "${job}"; then
    write_receipt "${scenario}" fail "FINDING: job reached terminal state before running boundary could be disrupted" "${job}"
    return 1
  fi
  if [[ "${boundary}" == terminal || "${boundary}" == results-read ]]; then
    terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: pre-restart terminal timeout" "${job}"; return 1; }
    [[ "${boundary}" == results-read ]] && before="$(result_digest "${job}" 2>/dev/null || true)"
  fi
  record_disruption "${component}" "${boundary}" before-kill
  compose kill -s KILL "${component}" >/dev/null
  record_disruption "${component}" "${boundary}" after-kill
  compose start "${component}" >/dev/null
  record_disruption "${component}" "${boundary}" after-restart
  [[ "${component}" != server ]] || wait_ready
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: job lost across kill/restart" "${job}"; return 1; }
  state="$(jq -r '.status' <<<"${terminal}")"
  after="$(result_digest "${job}" 2>/dev/null || true)"
  if [[ "${state}" != successful || -z "${after}" || ( "${boundary}" == results-read && "${before}" != "${after}" ) ]]; then
    write_receipt "${scenario}" fail "FINDING: duplicate terminal state or orphaned/changed output" "${job}" "${state}" "${after}"; return 1
  fi
  write_receipt "${scenario}" pass "" "${job}" "${state}" "${after}"
}

run_poison_job() {
  local scenario=poison-job poison="poison-$(date +%s%N)" job terminal digest
  compose exec -T redis redis-cli ZADD controlplane:jobqueue:pending 0 "${poison}" >/dev/null
  job="$(submit_async)" || { write_receipt "${scenario}" fail "valid submission failed"; return 1; }
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: poison blocked queue progress" "${job}"; return 1; }
  digest="$(result_digest "${job}" 2>/dev/null || true)"
  [[ "$(jq -r .status <<<"${terminal}")" == successful && -n "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: poison affected valid job" "${job}"; return 1; }
  [[ -z "$(compose exec -T redis redis-cli --raw ZSCORE controlplane:jobqueue:pending "${poison}")" ]] || { write_receipt "${scenario}" fail "FINDING: poison was not removed" "${job}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" successful "${digest}"
}

run_stale_lease() {
  local scenario=stale-lease job old terminal digest
  compose stop worker >/dev/null
  job="$(submit_async gdal.ogr2ogr "${native_payload}")" || { compose start worker >/dev/null; return 1; }
  old=$(( $(date +%s%3N) - 3600000 ))
  compose exec -T redis redis-cli ZREM controlplane:jobqueue:pending "${job}" >/dev/null
  compose exec -T redis redis-cli ZADD controlplane:jobqueue:claimed "${old}" "${job}" >/dev/null
  compose exec -T redis redis-cli HSET "controlplane:jobqueue:meta:${job}" claimedBy dead-worker claimedAt "${old}" >/dev/null
  compose start worker >/dev/null
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: stale lease was not recovered" "${job}"; return 1; }
  digest="$(result_digest "${job}" 2>/dev/null || true)"
  [[ "$(jq -r .status <<<"${terminal}")" == successful && -n "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: recovered lease lost output" "${job}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" successful "${digest}"
}

run_output_write_failure() {
  local scenario=output-write-failure job terminal state digest
  job="$(submit_async gdal.ogr2ogr "${native_payload}")" || return 1
  wait_running "${job}" || { write_receipt "${scenario}" fail "job never ran" "${job}"; return 1; }
  compose pause worker >/dev/null
  if result_digest "${job}" >/dev/null 2>&1; then
    compose unpause worker >/dev/null
    write_receipt "${scenario}" fail "FINDING: output was published before the outage barrier" "${job}" running
    return 1
  fi
  compose stop redis >/dev/null; compose unpause worker >/dev/null; sleep 2; compose start redis >/dev/null
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: output-store outage lost terminal state" "${job}"; return 1; }
  state="$(jq -r .status <<<"${terminal}")"; digest="$(result_digest "${job}" 2>/dev/null || true)"
  if [[ "${state}" == successful && -n "${digest}" ]]; then write_receipt "${scenario}" pass "recovered after output-store outage" "${job}" "${state}" "${digest}"; return; fi
  [[ "${state}" == failed && -z "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: partial/orphaned output after store outage" "${job}" "${state}" "${digest}"; return 1; }
  write_receipt "${scenario}" pass "bounded failure without exposed output" "${job}" "${state}"
}

run_backlog() {
  local scenario=queue-backlog count="${HONUA_GP_BACKLOG_JOBS:-20}" depth job terminal digest; local -a jobs=()
  compose stop worker >/dev/null
  for ((i=0;i<count;i++)); do jobs+=("$(submit_async gdal.ogr2ogr "${native_payload}")") || { compose start worker >/dev/null; return 1; }; done
  depth="$(compose exec -T redis redis-cli --raw ZCARD controlplane:jobqueue:pending)"; compose start worker >/dev/null
  (( depth >= count )) || { write_receipt "${scenario}" fail "FINDING: expected backlog ${count}, saw ${depth}"; return 1; }
  for job in "${jobs[@]}"; do terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: backlog did not drain" "${job}"; return 1; }; digest="$(result_digest "${job}" 2>/dev/null || true)"; [[ "$(jq -r .status <<<"${terminal}")" == successful && -n "${digest}" ]] || { write_receipt "${scenario}" fail "FINDING: backlog output missing" "${job}"; return 1; }; done
  write_receipt "${scenario}" pass "peak_depth=${depth};submitted=${count}" "${job}" successful "${digest}"
}

run_ttl_cleanup() {
  local scenario=ttl-cleanup job
  job="$(submit_async)"; wait_terminal "${job}" >/dev/null || return 1; result_digest "${job}" >/dev/null || return 1
  compose exec -T redis redis-cli PEXPIRE "controlplane:job:${job}" 250 >/dev/null; sleep 1
  [[ "$(compose exec -T redis redis-cli --raw EXISTS "controlplane:job:${job}")" == 0 ]] || { write_receipt "${scenario}" fail "FINDING: expired record remains" "${job}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" expired
}

run_retry_exhaustion() {
  local scenario=retry-exhaustion job record terminal state
  compose stop worker >/dev/null; job="$(submit_async gdal.ogr2ogr "${native_payload}")" || { compose start worker >/dev/null; return 1; }
  record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:${job}")"
  record="$(jq -c '.retryPolicy.maxAttempts=1 | .attemptCount=1 | .status="running" | .claimedBy="dead-worker" | .claimedAt="2000-01-01T00:00:00+00:00" | .lastHeartbeatAt="2000-01-01T00:00:00+00:00"' <<<"${record}")"
  printf '%s' "${record}" | compose exec -T redis redis-cli -x SET "controlplane:job:${job}" KEEPTTL >/dev/null
  compose exec -T redis redis-cli ZREM controlplane:jobqueue:pending "${job}" >/dev/null
  compose exec -T redis redis-cli ZADD controlplane:jobqueue:claimed 0 "${job}" >/dev/null; compose start worker >/dev/null
  terminal="$(wait_terminal "${job}")" || { write_receipt "${scenario}" fail "FINDING: exhausted retry never terminated" "${job}"; return 1; }
  state="$(jq -r .status <<<"${terminal}")"; [[ "${state}" == failed ]] || { write_receipt "${scenario}" fail "FINDING: exhausted retry executed again" "${job}" "${state}"; return 1; }
  write_receipt "${scenario}" pass "" "${job}" "${state}"
}

run_output_size_cap() {
  local scenario=output-size-cap job terminal state
  export HONUA_GP_MAX_ARTIFACT_BYTES=1024
  compose up -d --force-recreate server >/dev/null; wait_ready || return 1
  job="$(submit_async)" || { unset HONUA_GP_MAX_ARTIFACT_BYTES; return 1; }
  terminal="$(wait_terminal "${job}")" || true; state="$(jq -r '.status // empty' <<<"${terminal:-{}}" 2>/dev/null || true)"
  unset HONUA_GP_MAX_ARTIFACT_BYTES; compose up -d --force-recreate server >/dev/null; wait_ready || return 1
  [[ "${state}" == failed ]] || { write_receipt "${scenario}" fail "FINDING: oversized artifact was not rejected" "${job}" "${state}"; return 1; }
  result_digest "${job}" >/dev/null 2>&1 && { write_receipt "${scenario}" fail "FINDING: rejected oversized artifact exposed output" "${job}" "${state}"; return 1; }
  write_receipt "${scenario}" pass "MaxArtifactBytes=1024" "${job}" "${state}"
}

run_tenant_limits() {
  local scenario=tenant-quotas-backpressure-nondisclosure a="${HONUA_GP_TENANT_A_TOKEN:-}" b="${HONUA_GP_TENANT_B_TOKEN:-}" tmp headers code job retry body
  [[ -n "${a}" && -n "${b}" ]] || { write_receipt "${scenario}" fail "two tenant bearer tokens are required"; return 1; }
  tmp="$(mktemp)"; headers="$(mktemp)"; export HONUA_GP_MAX_CONCURRENT_PARTITION=1 HONUA_GP_MAX_CONCURRENT_GLOBAL=2
  compose up -d --force-recreate server >/dev/null; wait_ready || return 1; compose stop worker >/dev/null
  code="$(tenant_curl "${a}" -o "${tmp}" -w '%{http_code}' -H 'Content-Type: application/json' -H 'Prefer: respond-async' -d "${native_payload}" "${base_url}/ogc/processes/processes/gdal.ogr2ogr/execution")"; job="$(jq -r '.jobID // .jobId // empty' "${tmp}")"
  [[ "${code}" == 201 && -n "${job}" ]] || { compose start worker >/dev/null; write_receipt "${scenario}" fail "tenant A seed failed"; return 1; }
  code="$(tenant_curl "${a}" -D "${headers}" -o "${tmp}" -w '%{http_code}' -H 'Content-Type: application/json' -H 'Prefer: respond-async' -d "${native_payload}" "${base_url}/ogc/processes/processes/gdal.ogr2ogr/execution")"; retry="$(awk 'tolower($1)=="retry-after:" {gsub("\r","");print $2}' "${headers}")"
  [[ "${code}" == 503 && -n "${retry}" ]] || { compose start worker >/dev/null; write_receipt "${scenario}" fail "FINDING: quota/concurrency lacked 503 Retry-After" "${job}"; return 1; }
  code="$(tenant_curl "${b}" -o "${tmp}" -w '%{http_code}' "${base_url}/ogc/processes/jobs/${job}")"; body="$(<"${tmp}")"; compose start worker >/dev/null
  unset HONUA_GP_MAX_CONCURRENT_PARTITION HONUA_GP_MAX_CONCURRENT_GLOBAL; compose up -d --force-recreate server >/dev/null; wait_ready || return 1
  [[ "${code}" == 404 && "${body}" != *"${job}"* ]] || { write_receipt "${scenario}" fail "FINDING: cross-tenant existence disclosed" "${job}"; return 1; }
  write_receipt "${scenario}" pass "quota_status=503;retry_after=${retry};cross_tenant_status=404" "${job}" queued
}

run_soak() {
  local scenario=sustained-soak duration="${HONUA_GP_SOAK_SECONDS:-900}" concurrency="${HONUA_GP_SOAK_CONCURRENCY:-8}" end=$((SECONDS+duration)) submitted=0 verified=0 failed=0 job terminal before after; local -a jobs
  while ((SECONDS<end)); do jobs=(); for ((i=0;i<concurrency;i++)); do job="$(submit_async)" && jobs+=("${job}") && submitted=$((submitted+1)) || failed=$((failed+1)); done; for job in "${jobs[@]}"; do terminal="$(wait_terminal "${job}")" || { failed=$((failed+1)); continue; }; before="$(result_digest "${job}" 2>/dev/null || true)"; compose restart server >/dev/null; wait_ready || true; after="$(result_digest "${job}" 2>/dev/null || true)"; [[ "$(jq -r .status <<<"${terminal}")" == successful && -n "${before}" && "${before}" == "${after}" ]] && verified=$((verified+1)) || failed=$((failed+1)); done; done
  [[ ${submitted} -gt 0 && ${failed} -eq 0 && ${verified} -eq ${submitted} ]] || { write_receipt "${scenario}" fail "FINDING: duration=${duration};concurrency=${concurrency};submitted=${submitted};verified=${verified};failed=${failed}"; return 1; }
  write_receipt "${scenario}" pass "duration=${duration};concurrency=${concurrency};submitted=${submitted};durable_outputs_verified=${verified}"
}

run_topology() {
  local name value backlog_jobs backlog_cap soak_seconds soak_concurrency
  for name in docker curl jq; do
    command -v "${name}" >/dev/null || { preflight_failure="missing required command: ${name}"; return 1; }
  done
  require_digest HONUA_SERVER_IMAGE || { preflight_failure="HONUA_SERVER_IMAGE is not an exact digest"; return 1; }
  require_digest HONUA_WORKER_IMAGE || { preflight_failure="HONUA_WORKER_IMAGE is not an exact digest"; return 1; }
  [[ "${candidate_source_sha}" =~ ^[0-9a-f]{40}$ ]] || { preflight_failure="HONUA_GP_SOURCE_SHA is not a full source SHA"; return 1; }
  if [[ "${lane}" == resilience ]]; then
    for name in HONUA_GP_TENANT_A_TOKEN HONUA_GP_TENANT_B_TOKEN HONUA_GP_OIDC_ISSUER HONUA_GP_OIDC_AUDIENCE HONUA_GP_OIDC_SIGNING_KEY; do
      [[ -n "${!name:-}" ]] || { preflight_failure="${name} is required for resilience qualification"; return 1; }
    done
    backlog_jobs="${HONUA_GP_BACKLOG_JOBS:-20}"; backlog_cap="${HONUA_GP_BACKLOG_COST_CAP:-20}"
    soak_seconds="${HONUA_GP_SOAK_SECONDS:-900}"; soak_concurrency="${HONUA_GP_SOAK_CONCURRENCY:-8}"
    [[ "${backlog_jobs}" =~ ^[0-9]+$ && "${backlog_cap}" =~ ^[0-9]+$ ]] || {
      preflight_failure="backlog and cost cap must be integers"; return 1;
    }
    (( backlog_jobs > 0 && backlog_jobs <= backlog_cap )) || {
      preflight_failure="backlog ${backlog_jobs} exceeds cost cap ${backlog_cap}"; return 1;
    }
    [[ "${soak_seconds}" =~ ^[1-9][0-9]*$ && "${soak_concurrency}" =~ ^[1-9][0-9]*$ ]] || {
      preflight_failure="soak duration and concurrency must be positive integers"; return 1;
    }
  fi
  jq -n --arg server "${HONUA_SERVER_IMAGE:-}" --arg worker "${HONUA_WORKER_IMAGE:-}" \
    --arg source_sha "${candidate_source_sha}" \
    '{requested:{server_image:$server,worker_image:$worker,source_sha:$source_sha},observed:null}' \
    > "${observed_candidate_file}"
  if [[ "${HONUA_GP_SKIP_PULL:-false}" != true ]]; then
    compose pull || { preflight_failure="candidate image pull failed"; return 1; }
  fi
  compose up -d || { preflight_failure="candidate topology failed to start"; return 1; }
  wait_ready && wait_peer_ready || { preflight_failure="topology did not become ready"; return 1; }
  read_running_identity || { preflight_failure="candidate identity could not be read from running containers"; return 1; }
  candidate_matches_request || { preflight_failure="running candidate identity does not match requested source/images"; return 1; }
}

read_running_identity() {
  local component container image_id config_image revision refs
  local json
  json="$(<"${observed_candidate_file}")"
  for component in server server-peer worker; do
    container="$(compose ps -q "${component}")" || return 1
    [[ -n "${container}" ]] || return 1
    image_id="$(docker inspect --format '{{.Image}}' "${container}")" || return 1
    config_image="$(docker inspect --format '{{.Config.Image}}' "${container}")" || return 1
    revision="$(docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "${container}")" || return 1
    refs="$(docker image inspect --format '{{json .RepoDigests}}' "${image_id}")" || return 1
    json="$(jq -c --arg component "${component}" --arg container "${container}" --arg image_id "${image_id}" \
      --arg config_image "${config_image}" --arg revision "${revision}" --argjson refs "${refs}" \
      '.observed=(.observed // {}) | .observed[$component]={container_id:$container,image_id:$image_id,image_ref:$config_image,repo_digests:$refs,revision:$revision}' \
      <<<"${json}")" || return 1
  done
  printf '%s\n' "${json}" | jq '.observed as $observed | .observed=$observed' > "${observed_candidate_file}.tmp"
  mv "${observed_candidate_file}.tmp" "${observed_candidate_file}"
}

candidate_matches_request() {
  local server worker
  server="$(jq -r '.observed.server.image_ref // empty' "${observed_candidate_file}")"
  worker="$(jq -r '.observed.worker.image_ref // empty' "${observed_candidate_file}")"
  [[ "${server}" == "${HONUA_SERVER_IMAGE}" && "${worker}" == "${HONUA_WORKER_IMAGE}" ]] || return 1
  [[ "$(jq -r '.observed.server.revision // empty' "${observed_candidate_file}")" == "${candidate_source_sha}" ]] || return 1
  [[ "$(jq -r '.observed.worker.revision // empty' "${observed_candidate_file}")" == "${candidate_source_sha}" ]] || return 1
}

run_scenario() {
  local name="$1" function="$2" result=0 outcome finding
  shift 2
  scenario_state_reset "${name}"
  "${function}" "$@" || result=$?
  outcome=pass; finding=""
  if (( result != 0 )); then
    outcome=fail
    finding="${scenario_finding:-${preflight_failure:-scenario execution failed}}"
  fi
  if [[ ! -e "${receipt_root}/${name}.json" ]]; then
    write_receipt "${name}" "${outcome}" "${finding}"
  fi
  (( result == 0 ))
}

self_test_assertion_failure() { scenario_fail "intentional assertion failure"; }
self_test_follow_up() { [[ -z "${scenario_finding}" ]] || return 1; return 0; }
self_test_cleanup() { return 0; }

write_summary() {
  local declared_json missing_json duplicates_json receipts_json scenario missing_count duplicate_count receipt_count
  declared_json="$(printf '%s\n' "${declared_scenarios[@]}" | jq -Rsc 'split("\n")|map(select(length>0))')"
  missing_json='[]'; duplicates_json='[]'; receipts_json='[]'
  for scenario in "${declared_scenarios[@]}"; do
    if [[ ! -f "${receipt_root}/${scenario}.json" ]]; then
      missing_json="$(jq --arg scenario "${scenario}" '. + [$scenario]' <<<"${missing_json}")"
    else
      receipts_json="$(jq --slurpfile receipt "${receipt_root}/${scenario}.json" '. + $receipt' <<<"${receipts_json}")"
    fi
    if (( ${receipt_written["${scenario}"]:-0} > 1 )); then
      duplicates_json="$(jq --arg scenario "${scenario}" --argjson count "${receipt_written["${scenario}"]}" '. + [{scenario:$scenario,attempts:$count}]' <<<"${duplicates_json}")"
    fi
  done
  missing_count="$(jq 'length' <<<"${missing_json}")"; duplicate_count="$(jq 'length' <<<"${duplicates_json}")"; receipt_count="$(jq 'length' <<<"${receipts_json}")"
  jq -n --arg schema "honua.gp-qualification-summary.v2" --arg lane "${lane}" \
    --arg generated_at "$(now)" --arg run_url "${run_url}" --argjson declared "${declared_json}" \
    --argjson receipts "${receipts_json}" --argjson missing "${missing_json}" --argjson duplicates "${duplicates_json}" \
    --argjson declared_count "${#declared_scenarios[@]}" --argjson receipt_count "${receipt_count}" \
    --argjson missing_count "${missing_count}" --argjson duplicate_count "${duplicate_count}" \
    '{schema:$schema,lane:$lane,generated_at:$generated_at,github_run_url:$run_url,declared_scenarios:$declared,declared_scenario_count:$declared_count,receipt_count:$receipt_count,missing_scenarios:$missing,duplicate_receipts:$duplicates,passed:($receipts|map(select(.outcome=="pass"))|length),failed:($receipts|map(select(.outcome=="fail"))|length),scenarios:$receipts}' \
    > "${receipt_root}/summary.json"
  (( missing_count == 0 && duplicate_count == 0 && receipt_count == ${#declared_scenarios[@]} ))
}

fill_missing_receipts() {
  local scenario
  for scenario in "${declared_scenarios[@]}"; do
    if [[ ! -f "${receipt_root}/${scenario}.json" ]]; then
      scenario_state_reset "${scenario}"
      write_receipt "${scenario}" fail "preflight failure: ${preflight_failure:-scenario was not executed}"
    fi
  done
}

cleanup_runtime() {
  [[ -f "${observed_candidate_file}" ]] || return 0
  compose down --volumes --remove-orphans >/dev/null 2>&1 || { scenario_cleanup_failure="compose cleanup failed"; return 1; }
}

finish() {
  local status=$?
  (( finished == 1 )) && return
  finished=1
  if [[ "${lane}" != self-test ]]; then
    run_scenario cleanup cleanup_runtime || failures=$((failures + 1))
  else
    run_scenario cleanup self_test_cleanup || failures=$((failures + 1))
  fi
  fill_missing_receipts
  write_summary || failures=$((failures + 1))
  trap - EXIT
  exit "${failures}"
}

trap finish EXIT

if [[ "${lane}" == self-test ]]; then
  run_scenario assertion-failure self_test_assertion_failure || failures=$((failures + 1))
  run_scenario follow-up self_test_follow_up || failures=$((failures + 1))
else
  run_scenario topology run_topology || {
    failures=$((failures + 1))
    fill_missing_receipts
  }
  if [[ -z "${preflight_failure}" ]]; then
    if [[ "${lane}" == lifecycle ]]; then
      run_scenario sync run_sync || failures=$((failures + 1))
      run_scenario async run_async_baseline || failures=$((failures + 1))
      export HONUA_GP_QUALIFICATION_BARRIER_ROOT=/var/run/honua/qualification
      compose up -d --force-recreate worker >/dev/null || failures=$((failures + 1))
      run_scenario cancel-claimed run_cancel_barrier claimed || failures=$((failures + 1))
      run_scenario cancel-native-process-started run_cancel_barrier native-process-started || failures=$((failures + 1))
      run_scenario cancel-output-bytes-written-unpublished run_cancel_barrier output-bytes-written-unpublished || failures=$((failures + 1))
      run_scenario cancel-artifact-reference-published-terminal-cas-pending run_cancel_barrier artifact-reference-published-terminal-cas-pending || failures=$((failures + 1))
      unset HONUA_GP_QUALIFICATION_BARRIER_ROOT
      compose up -d --force-recreate worker >/dev/null || failures=$((failures + 1))
      run_scenario idempotency run_idempotency || failures=$((failures + 1))
      run_scenario retry run_retry || failures=$((failures + 1))
      run_scenario timeout-cooperative run_timeout_cooperative || failures=$((failures + 1))
      run_scenario timeout-ignoring run_timeout_ignoring || failures=$((failures + 1))
      for component in worker server redis postgres; do
        for boundary in accepted running terminal results-read; do
          run_scenario "restart-${component}-${boundary}" run_disruption "${component}" "${boundary}" || failures=$((failures + 1))
        done
      done
      run_scenario duplicate-delivery run_duplicate_delivery || failures=$((failures + 1))
    else
      run_scenario poison-job run_poison_job || failures=$((failures + 1))
      run_scenario stale-lease run_stale_lease || failures=$((failures + 1))
      run_scenario output-write-failure run_output_write_failure || failures=$((failures + 1))
      run_scenario queue-backlog run_backlog || failures=$((failures + 1))
      run_scenario ttl-cleanup run_ttl_cleanup || failures=$((failures + 1))
      run_scenario retry-exhaustion run_retry_exhaustion || failures=$((failures + 1))
      run_scenario output-size-cap run_output_size_cap || failures=$((failures + 1))
      run_scenario tenant-quotas-backpressure-nondisclosure run_tenant_limits || failures=$((failures + 1))
      run_scenario sustained-soak run_soak || failures=$((failures + 1))
    fi
  fi
fi
exit 0
