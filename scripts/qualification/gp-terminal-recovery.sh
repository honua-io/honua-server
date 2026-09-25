#!/usr/bin/env bash
# Sourced at the actual terminal-CAS acknowledgement fence by gp-store-crash.sh.
# The caller retains failure isolation and restores its topology after this case.
terminal_assert_fence() {
  jq -e --arg job "$job" --argjson before "$before_record" '
    .operationId == $job and .barrier == "terminal-committed-registration-pending"
    and .redis_reply == ":1" and .reply_forwarded == false
    and .workerId == $before.claimedBy and .terminal_record == $before
    and ($before.status == 3 or $before.status == "succeeded")
    and ($before.artifactReferences | length) == 1' <<<"$ready" >/dev/null
}

terminal_assert_killed() {
  jq -e 'length == 1 and .[0].State.Running == false
    and .[0].State.ExitCode == 137 and .[0].State.OOMKilled == false' "$1" >/dev/null
}

terminal_assert_same_record() {
  local first second
  local single='if length == 1 and .[0] != null then .[0] else error("expected one nonnull JSON document") end'
  first="$(jq -Sce -s "$single" "$1")" && second="$(jq -Sce -s "$single" "$2")" || return 1
  [[ "$first" == "$second" ]]
}

terminal_capture_services() {
  local service container
  for service in server server-peer redis postgres; do
    container="$(compose ps -q "$service")" || return 1
    docker inspect "$container" | jq -c --arg service "$service" \
      '.[0] | {service:$service,id:.Id,image:.Image,started_at:.State.StartedAt,running:.State.Running}' || return 1
  done | jq -se 'sort_by(.service) | select(length == 4 and all(.[]; .running == true))' > "$1"
}

terminal_assert_package() {
  jq -e --argjson terminal "$before_record" '
    ($terminal.artifactReferences[0]|fromjson) as $descriptor |
    ("/api/geoprocessing/jobs/" + $terminal.operationId + "/artifacts/0/content") as $route |
    .resultPackageId == ($terminal.operationId + ":v" + ($terminal.version|tostring))
    and .status == 5 and (.artifacts|length) == 1
    and $descriptor.outputType == "staged-object" and $descriptor.jobId == $terminal.operationId
    and $descriptor.attemptNumber == $terminal.attemptCount
    and .artifacts[0].artifactId == ($terminal.operationId + ":artifact:1")
    and .artifacts[0].uri == $route and .artifacts[0].contentType == $descriptor.content.mediaType
    and .artifacts[0].metadata["raster.output.contentRoute"] == $route
    and .artifacts[0].metadata["raster.output.staged"] == "true"
    and .artifacts[0].metadata["raster.output.sizeBytes"] == ($descriptor.content.sizeBytes|tostring)
    and .artifacts[0].metadata["raster.output.checksum"] ==
      ($descriptor.content.checksum.algorithm + ":" + $descriptor.content.checksum.value)' "$1" >/dev/null
}

terminal_assert_public_result() {
  # Advertised native OGC processes support value transmission only. The wire
  # document is the output dictionary itself, not an {outputs: ...} envelope.
  # Bind its actual value to the independently checked committed bytes; staged
  # reference retrieval below uses the recovered canonical package's URI.
  jq -e --slurpfile package "$2" --slurpfile committed "$3" '
    keys == ["outputFeatureLayer"]
    and (.outputFeatureLayer|keys) == ["mediaType", "value"]
    and .outputFeatureLayer.mediaType == $package[0].artifacts[0].contentType
    and .outputFeatureLayer.value == $committed[0]' "$1" >/dev/null
}

terminal_job_record() {
  compose exec -T redis redis-cli --raw GET "controlplane:job:$job"
}

terminal_package_record() {
  compose exec -T redis redis-cli --raw GET "controlplane:job:gp-result:$job"
}

run_terminal_result_recovery() {
  local original_container replacement_container baseline killed replacement services_before services_after
  local package_before_read package_first package_final descriptor_first descriptor_final final_record
  local before_bytes after_bytes stable_until record_file inventory_before inventory_after artifact_uri
  baseline="$receipt_root/$scenario-terminal-before.json"
  killed="$receipt_root/$scenario-killed-worker.json"
  replacement="$receipt_root/$scenario-replacement-worker.json"
  services_before="$receipt_root/$scenario-services-before.json"
  services_after="$receipt_root/$scenario-services-after.json"
  printf '%s\n' "$before_record" > "$baseline" || return 1
  printf '%s\n' "$ready" > "$receipt_root/$scenario-terminal-fence.json" || return 1
  terminal_assert_fence || { scenario_fail "terminal fence did not prove exact committed CAS with withheld acknowledgement"; return 1; }
  before_bytes="$receipt_root/$scenario-before.geojson"
  cp "$content" "$before_bytes" || return 1
  (( $(wc -c < "$before_bytes") > $(compose_staging_value MaxInlineArtifactBytes) )) || {
    scenario_fail "terminal fixture did not exceed the configured inline ceiling"; return 1; }
  inventory_before="$receipt_root/$scenario-inventory-before.json"
  jq -n --arg inventory "$before_inventory" '{files:$inventory}' > "$inventory_before" || return 1
  terminal_capture_services "$services_before" || return 1
  original_container="$(compose ps -q worker)" || return 1
  record_disruption worker terminal-committed-registration-pending SIGKILL
  compose kill -s SIGKILL worker >/dev/null || return 1
  docker inspect "$original_container" > "$killed" || return 1
  docker logs "$original_container" > "$receipt_root/$scenario-killed-worker.log" 2>&1 || return 1
  terminal_assert_killed "$killed" || { scenario_fail "worker did not actually exit from SIGKILL (137)"; return 1; }
  # The old process is dead before the withheld response can be released.
  release_barrier "$job" terminal-committed-registration-pending || return 1
  HONUA_GP_QUALIFICATION_BARRIER_ROOT="" HONUA_GP_WORKER_REDIS=redis:6379
  # Deliberately do not restart serving hosts, Redis or PostgreSQL in this cell.
  compose up -d --no-deps --force-recreate worker >/dev/null || return 1
  replacement_container="$(compose ps -q worker)" || return 1
  docker inspect "$replacement_container" > "$replacement" || return 1
  jq -e --slurpfile original "$killed" --arg image "$HONUA_WORKER_IMAGE" --arg sha "$candidate_source_sha" '
    length == 1 and .[0].Id != $original[0][0].Id and .[0].State.Running
    and .[0].Image == $original[0][0].Image
    and all((.[0], $original[0][0]); .Config.Image == $image
      and .Config.Labels["org.opencontainers.image.revision"] == $sha)' "$replacement" >/dev/null || return 1
  record_disruption worker terminal-committed-registration-pending recovered
  package_before_read="$(compose exec -T redis redis-cli --raw EXISTS "controlplane:job:gp-result:$job")" || return 1
  jq -n --argjson before "$package_before" --argjson before_read "$package_before_read" \
    '{at_crash:$before,after_worker_replacement_before_read:$before_read}' \
    > "$receipt_root/$scenario-package-absence.json" || return 1
  [[ "$package_before_read" == 0 ]] || {
    scenario_fail "result package was not absent before the ordinary read"; return 1; }
  record_file="$receipt_root/$scenario-after-replacement-record.json"
  terminal_job_record > "$record_file" || return 1
  terminal_assert_same_record "$baseline" "$record_file" || return 1
  descriptor_first="$receipt_root/$scenario-first-results.json"
  descriptor_final="$receipt_root/$scenario-final-results.json"
  package_first="$receipt_root/$scenario-first-package.json"
  package_final="$receipt_root/$scenario-final-package.json"
  # This public read is the sole recovery trigger. The harness never writes a
  # Redis job, index, queue, result package, timestamp or retry record.
  auth_curl "$peer_url/ogc/processes/jobs/$job/results" > "$descriptor_first" || return 1
  terminal_package_record > "$package_first" || return 1
  terminal_assert_package "$package_first" || {
    scenario_fail "ordinary result read did not persist a result package"; return 1; }
  terminal_assert_public_result "$descriptor_first" "$package_first" "$before_bytes" || {
    scenario_fail "OGC value result did not match the committed package artifact"; return 1; }
  # The validated package route is exactly relative, same job/index, with no
  # network authority, query, fragment or provider URL accepted by the validator.
  artifact_uri="$(jq -r '.artifacts[0].uri' "$package_first")" || return 1
  # Cover two normal 30-second reconciliation intervals. Full terminal metadata
  # is immutable; no selected-field projection can hide a stale write.
  stable_until=$((SECONDS + 65))
  while (( SECONDS < stable_until )); do
    terminal_job_record > "$record_file" || return 1
    terminal_assert_same_record "$baseline" "$record_file" || {
      scenario_fail "durable terminal record changed after worker replacement"; return 1; }
    sleep 1
  done
  auth_curl "$peer_url/ogc/processes/jobs/$job/results" > "$descriptor_final" || return 1
  terminal_package_record > "$package_final" || return 1
  terminal_assert_same_record "$package_first" "$package_final" || {
    scenario_fail "repeated result read changed the persisted result package"; return 1; }
  terminal_assert_same_record "$descriptor_first" "$descriptor_final" || {
    scenario_fail "repeated result read changed the public result descriptor"; return 1; }
  after_bytes="$receipt_root/$scenario-after.geojson"
  auth_curl "$peer_url$artifact_uri" > "$after_bytes" || return 1
  cmp "$before_bytes" "$after_bytes" || { scenario_fail "result read changed committed output bytes"; return 1; }
  python3 "$repo_root/scripts/qualification/verify-gp-store-artifact.py" "$after_bytes" || return 1
  after_sha="$(sha256sum "$after_bytes" | cut -d' ' -f1)"
  jq -e --arg sha "sha256:$after_sha" --arg bytes "$(wc -c < "$after_bytes")" '
    .artifacts[0].metadata["raster.output.checksum"] == $sha
    and .artifacts[0].metadata["raster.output.sizeBytes"] == $bytes' "$package_final" >/dev/null || {
      scenario_fail "recovered bytes did not match committed descriptor metadata"; return 1; }
  final_record="$receipt_root/$scenario-terminal-final.json"
  terminal_job_record > "$final_record" || return 1
  terminal_assert_same_record "$baseline" "$final_record" || return 1
  [[ "$(object_file_count "$job")" == 1 ]] || { scenario_fail "terminal recovery left duplicate output objects"; return 1; }
  inventory_after="$receipt_root/$scenario-inventory-after.json"
  find "$object_root/gp/outputs/$job" -type f -exec sha256sum {} \; | LC_ALL=C sort | \
    jq -Rs '{files:.}' > "$inventory_after" || return 1
  terminal_capture_services "$services_after" || return 1
  terminal_assert_same_record "$services_before" "$services_after" || {
    scenario_fail "terminal recovery restarted a non-worker service"; return 1; }
  docker logs "$replacement_container" > "$receipt_root/$scenario-replacement-worker.log" 2>&1 || return 1
  state=successful
  jq -n --arg job "$job" --arg before_file "${before_bytes##*/}" --arg after_file "${after_bytes##*/}" \
    --arg sha "$after_sha" --argjson bytes "$(wc -c < "$after_bytes")" \
    --slurpfile fence "$receipt_root/$scenario-terminal-fence.json" --slurpfile before "$baseline" \
    --slurpfile final "$final_record" --slurpfile package "$package_final" \
    '{submitted_job:$job,scope:"terminal-CAS worker death and ordinary-read result-package recovery",
      fence:$fence[0],terminal_before:$before[0],terminal_final:$final[0],
      worker_exit_code:137,worker_only_recovery:true,stable_seconds:65,
      result_package_before:0,result_package_before_read:0,result_package_after:1,result_package:$package[0],
      output_files:[$before_file,$after_file],sha256:$sha,bytes:$bytes}' > "$scenario_evidence_file" || return 1
  record_attempt "$(jq -r .attemptCount "$final_record")"
  jq -n --arg sha "$after_sha" --argjson bytes "$(wc -c < "$after_bytes")" \
    '{sha256:$sha,bytes:$bytes}' > "$scenario_state_file" || return 1
}
