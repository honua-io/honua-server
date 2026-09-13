#!/usr/bin/env bash
# Sourced by gp-lifecycle-harness.sh; every assertion uses the published images.
run_store_crash_boundary() {
  local target="$1" disruption="$2" job barrier state terminal before_record after_record
  local before_inventory after_inventory before_sha after_sha content ready outage_root
  local scenario="crash-${target}-${disruption}"
  export HONUA_GP_QUALIFICATION_BARRIER_ROOT=/var/run/honua/qualification
  export HONUA_GP_WORKER_REDIS=terminal-proxy:6379
  compose --profile crash-boundaries up -d terminal-proxy >/dev/null || return 1
  compose up -d --force-recreate worker >/dev/null || return 1
  job="$(submit_async gdal.ogr2ogr "${native_payload}")" || return 1
  wait_barrier "$job" claimed || { scenario_fail "worker did not reach claimed fence"; return 1; }
  if [[ "$target" == terminal-committed-registration-pending ]]; then
    : > "$(barrier_directory "$job")/$target.arm"
  fi
  for barrier in claimed native-process-started output-bytes-written-unpublished artifact-reference-published-terminal-cas-pending; do
    [[ "$barrier" == "$target" ]] && break
    release_barrier "$job" "$barrier"
    case "$barrier" in
      claimed) wait_barrier "$job" native-process-started || return 1;;
      native-process-started) wait_barrier "$job" output-bytes-written-unpublished || return 1;;
      output-bytes-written-unpublished) wait_barrier "$job" artifact-reference-published-terminal-cas-pending || return 1;;
      artifact-reference-published-terminal-cas-pending) wait_barrier "$job" "$target" || return 1;;
    esac
  done
  ready="$(barrier_record "$job" "$target")"
  [[ "$ready" != null ]] || { scenario_fail "requested crash fence was never observed"; return 1; }
  before_record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:$job")"
  before_inventory="$(find "$object_root/gp/outputs/$job" -type f -exec sha256sum {} \; | LC_ALL=C sort)"
  content="$(find "$object_root/gp/outputs/$job" -type f ! -name '*.pending' ! -name '*.hold' ! -name '*.readlease' | head -1)"
  [[ -n "$content" ]] || { scenario_fail "crash fixture did not force output staging"; return 1; }
  python3 "${repo_root}/scripts/qualification/verify-gp-store-artifact.py" "$content" || return 1
  before_sha="$(sha256sum "$content" | cut -d' ' -f1)"
  if [[ "$target" == terminal-committed-registration-pending ]]; then
    [[ "$(jq -r .status <<<"$before_record")" == succeeded ]] || {
      scenario_fail "proxy fence did not follow a durable successful terminal CAS"; return 1; }
  else
    [[ "$(jq -r .status <<<"$before_record")" == running ]] || {
      scenario_fail "worker escaped the pre-terminal crash fence"; return 1; }
  fi
  record_disruption "$disruption" "$target" inject
  if [[ "$disruption" == worker ]]; then
    compose kill -s SIGKILL worker >/dev/null || return 1
  else
    # Hide both the marker and the actual bytes; no synthetic Redis outage.
    outage_root="$receipt_root/.outage-$job"
    mkdir "$outage_root" || return 1
    mv "$object_root/.honua-gp-store.json" "$object_root/gp" "$outage_root/" || return 1
    local readiness
    readiness="$(curl --silent -o /dev/null -w '%{http_code}' "$peer_url/healthz/ready")"
    [[ "$readiness" == 503 ]] || { scenario_fail "output-store loss did not fail readiness closed"; return 1; }
    # The normal result route must not return readable bytes while the store is absent.
    local code
    code="$(curl --silent -H "X-API-Key: $api_key" -o /dev/null -w '%{http_code}' "$peer_url/api/geoprocessing/jobs/$job/artifacts/0/content")"
    [[ "$code" != 200 ]] || { scenario_fail "missing store exposed a successful artifact"; return 1; }
    mv "$outage_root/.honua-gp-store.json" "$outage_root/gp" "$object_root/" || return 1
    rmdir "$outage_root"
  fi
  for barrier in claimed native-process-started output-bytes-written-unpublished artifact-reference-published-terminal-cas-pending terminal-committed-registration-pending; do
    release_barrier "$job" "$barrier"
  done
  unset HONUA_GP_QUALIFICATION_BARRIER_ROOT HONUA_GP_WORKER_REDIS
  compose up -d --force-recreate worker >/dev/null || return 1
  compose restart server server-peer redis postgres >/dev/null || return 1
  wait_ready && wait_peer_ready || return 1
  record_disruption "$disruption" "$target" recovered
  terminal="$(wait_terminal "$job")" || { scenario_fail "job did not converge after the crash"; return 1; }
  state="$(jq -r .status <<<"$terminal")"
  [[ "$state" == successful ]] || { scenario_fail "crash recovery did not produce a successful job"; return 1; }
  content="$receipt_root/.$scenario.geojson"
  auth_curl "$peer_url/api/geoprocessing/jobs/$job/artifacts/0/content" > "$content" || return 1
  python3 "${repo_root}/scripts/qualification/verify-gp-store-artifact.py" "$content" || return 1
  after_sha="$(sha256sum "$content" | cut -d' ' -f1)"
  [[ "$before_sha" == "$after_sha" ]] || { scenario_fail "recovered bytes differ from durable pre-crash bytes"; return 1; }
  after_record="$(compose exec -T redis redis-cli --raw GET "controlplane:job:$job")"
  after_inventory="$(find "$object_root/gp/outputs/$job" -type f -exec sha256sum {} \; | LC_ALL=C sort)"
  auth_curl "$peer_url/ogc/processes/jobs/$job/results" > "$receipt_root/.$scenario.descriptor.json" || return 1
  jq -n --argjson fence "$ready" --argjson before "$before_record" --argjson after "$after_record" \
    --arg before_inventory "$before_inventory" --arg after_inventory "$after_inventory" \
    --arg before_sha "$before_sha" --arg after_sha "$after_sha" \
    --slurpfile descriptor "$receipt_root/.$scenario.descriptor.json" \
    '{fence:$fence,job_before:$before,job_after:$after,inventory_before:$before_inventory,inventory_after:$after_inventory,sha256_before:$before_sha,sha256_after:$after_sha,descriptor:$descriptor[0]}' > "$scenario_evidence_file" || return 1
  record_attempt "$(jq -r .attemptCount <<<"$after_record")"
  write_receipt "$scenario" pass "" "$job" "$state" "$after_sha"
}
