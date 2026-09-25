#!/usr/bin/env bash
# Sourced by gp-lifecycle-harness.sh; every assertion uses the published images.
run_store_crash_boundary() {
  local target="$1" disruption="$2" result=0 cleanup_result=0 barrier
  local job="" state="" after_sha="" outage_root="" store_hidden=0
  local scenario="${scenario_name:-crash-${target}-${disruption}}"
  run_store_crash_case "$target" "$disruption" || result=$?
  # A failed assertion can leave a live worker behind a fence or the actual
  # store hidden. Restore those resources before restoring caller topology.
  if (( store_hidden != 0 )); then
    restore_crash_output_store || cleanup_result=$?
  fi
  if [[ -n "$job" ]]; then
    for barrier in claimed native-process-started output-bytes-written-unpublished artifact-reference-published-terminal-cas-pending terminal-committed-registration-pending; do
      release_barrier "$job" "$barrier" || cleanup_result=$?
    done
  fi
  compose up -d --force-recreate server server-peer worker >/dev/null || cleanup_result=$?
  if (( cleanup_result == 0 )); then
    wait_ready && wait_peer_ready || cleanup_result=$?
  fi
  if (( cleanup_result != 0 )); then
    scenario_cleanup_failure="staged-store qualification topology restoration failed"
    runtime_taint="$scenario_cleanup_failure"
    scenario_finding="${scenario_finding:+${scenario_finding}; }${scenario_cleanup_failure}"
    return 1
  fi
  (( result == 0 )) || return "$result"
  write_receipt "$scenario" pass "" "$job" "$state" "$after_sha"
}

restore_crash_output_store() {
  # Either move can have failed after moving the other entry. Restore each
  # present entry independently and retain the directory on any failure.
  compose exec -T --user 0 worker sh -ec '
    root=/var/lib/honua/gp-outputs
    for entry in .honua-gp-store.json gp; do
      if [ -e "$root/.qualification-outage-$1/$entry" ]; then
        [ ! -e "$root/$entry" ] || exit 1
        mv "$root/.qualification-outage-$1/$entry" "$root/"
      fi
    done
  ' restore-store "$job" || return 1
  rmdir "$outage_root" || return 1
  store_hidden=0
}

run_store_crash_case() {
  local target="$1" disruption="$2" barrier terminal before_record after_record
  local before_inventory after_inventory before_sha content ready
  local package_before package_after outage_evidence='null'
  local -x HONUA_GP_QUALIFICATION_BARRIER_ROOT=/var/run/honua/qualification
  local -x HONUA_GP_WORKER_REDIS=terminal-proxy:6379
  compose --profile crash-boundaries up -d --wait terminal-proxy >/dev/null || return 1
  compose up -d --force-recreate worker >/dev/null || return 1
  job="$(submit_async gdal.ogr2ogr "${native_payload}")" || return 1
  jq -n --arg job "$job" '{submitted_job:$job}' > "$scenario_evidence_file" || return 1
  wait_barrier "$job" claimed || { scenario_fail "worker did not reach claimed fence"; return 1; }
  # The production worker creates this directory as its unprivileged UID.
  # Permit the host-side qualification controller to publish release files.
  compose exec -T --user 0 worker chmod 777 "/var/run/honua/qualification/$job" || return 1
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
  package_before="$(compose exec -T redis redis-cli --raw EXISTS "controlplane:job:gp-result:$job")"
  before_inventory="$(find "$object_root/gp/outputs/$job" -type f -exec sha256sum {} \; | LC_ALL=C sort)"
  content="$(find "$object_root/gp/outputs/$job" -type f ! -name '*.pending' ! -name '*.hold' ! -name '*.readlease' | head -1)"
  [[ -n "$content" ]] || { scenario_fail "crash fixture did not force output staging"; return 1; }
  python3 "${repo_root}/scripts/qualification/verify-gp-store-artifact.py" "$content" || return 1
  before_sha="$(sha256sum "$content" | cut -d' ' -f1)"
  if [[ "$target" == terminal-committed-registration-pending ]]; then
    jq -e '.status == 3 or .status == "succeeded"' <<<"$before_record" >/dev/null || {
      scenario_fail "proxy fence did not follow a durable successful terminal CAS"; return 1; }
    [[ "$package_before" == 0 ]] || {
      scenario_fail "result package was already registered before the requested crash boundary"; return 1; }
  else
    jq -e '.status == 2 or .status == "running"' <<<"$before_record" >/dev/null || {
      scenario_fail "worker escaped the pre-terminal crash fence"; return 1; }
  fi
  if [[ "$disruption" == worker ]]; then
    record_disruption worker "$target" SIGKILL
    compose kill -s SIGKILL worker >/dev/null || return 1
  else
    record_disruption store "$target" hide-marker-and-bytes
    # Hide both the marker and the actual bytes; no synthetic Redis outage.
    outage_root="$object_root/.qualification-outage-$job"
    mkdir "$outage_root" || return 1
    local mounted_root=/var/lib/honua/gp-outputs outage_failed=0
    store_hidden=1
    compose exec -T --user 0 worker mv "$mounted_root/.honua-gp-store.json" "$mounted_root/gp" \
      "$mounted_root/.qualification-outage-$job/" || return 1
    local readiness
    readiness="$(curl --silent -o /dev/null -w '%{http_code}' "$peer_url/healthz/ready")"
    [[ "$readiness" == 503 ]] || outage_failed=1
    # The normal result route must not return readable bytes while the store is absent.
    local code
    code="$(curl --silent -H "X-API-Key: $api_key" -o /dev/null -w '%{http_code}' "$peer_url/api/geoprocessing/jobs/$job/artifacts/0/content")"
    [[ "$code" != 200 ]] || outage_failed=1
    outage_evidence="$(jq -cn --arg readiness "$readiness" --arg content "$code" \
      '{readiness_http:$readiness,content_http:$content}')" || outage_failed=1
    jq -n --arg job "$job" --arg readiness "$readiness" --arg content "$code" \
      --argjson fence "$ready" --argjson before "$before_record" \
      '{submitted_job:$job,fence:$fence,job_before:$before,store_unavailable:{readiness_http:$readiness,content_http:$content}}' \
      > "$scenario_evidence_file" || outage_failed=1
    restore_crash_output_store || return 1
    [[ "$outage_failed" == 0 ]] || { scenario_fail "unavailable output store did not fail closed"; return 1; }
  fi
  for barrier in claimed native-process-started output-bytes-written-unpublished artifact-reference-published-terminal-cas-pending terminal-committed-registration-pending; do
    release_barrier "$job" "$barrier"
  done
  # Resume without qualification fences/proxy for natural recovery. These local
  # exports never alter the caller's original environment.
  HONUA_GP_QUALIFICATION_BARRIER_ROOT="" HONUA_GP_WORKER_REDIS=redis:6379
  compose restart server server-peer redis postgres >/dev/null || return 1
  wait_ready && wait_peer_ready || return 1
  compose up -d --force-recreate worker >/dev/null || return 1
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
  [[ "$(jq '.artifactReferences | length' <<<"$after_record")" == 1 ]] || {
    scenario_fail "recovery did not converge to exactly one externally visible artifact"; return 1; }
  after_inventory="$(find "$object_root/gp/outputs/$job" -type f -exec sha256sum {} \; | LC_ALL=C sort)"
  auth_curl "$peer_url/ogc/processes/jobs/$job/results" > "$receipt_root/.$scenario.descriptor.json" || return 1
  package_after="$(compose exec -T redis redis-cli --raw EXISTS "controlplane:job:gp-result:$job")"
  [[ "$package_after" == 1 ]] || { scenario_fail "normal read did not recover result-package registration"; return 1; }
  jq -n --argjson fence "$ready" --argjson before "$before_record" --argjson after "$after_record" \
    --arg before_inventory "$before_inventory" --arg after_inventory "$after_inventory" \
    --arg before_sha "$before_sha" --arg after_sha "$after_sha" \
    --arg target "$target" --arg disruption "$disruption" --argjson outage "$outage_evidence" \
    --argjson package_before "$package_before" --argjson package_after "$package_after" \
    --slurpfile descriptor "$receipt_root/.$scenario.descriptor.json" \
    '{boundary:$target,disruption:$disruption,store_unavailable:$outage,fence:$fence,job_before:$before,job_after:$after,inventory_before:$before_inventory,inventory_after:$after_inventory,sha256_before:$before_sha,sha256_after:$after_sha,result_package_before:$package_before,result_package_after:$package_after,descriptor:$descriptor[0]}' > "$scenario_evidence_file" || return 1
  record_attempt "$(jq -r .attemptCount <<<"$after_record")"
  jq -n --arg sha "$after_sha" --argjson bytes "$(wc -c < "$content")" \
    '{sha256:$sha,bytes:$bytes}' > "$scenario_state_file" || return 1

}
