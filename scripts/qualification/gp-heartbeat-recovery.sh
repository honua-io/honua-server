#!/usr/bin/env bash
# Sourced by gp-lifecycle-harness.sh. Redis access is observational only.
run_heartbeat_recovery() {
  local result=0 cleanup_result=0 job="" digest="" first_container="" second_container="" paused=0
  local scenario="${scenario_name:-stale-lease}"
  local first_root="$barrier_root/heartbeat-original" second_root="$barrier_root/heartbeat-recovery"
  run_heartbeat_recovery_case || result=$?
  local root role receipt
  for role in original recovery; do
    root="$first_root"; [[ "$role" == original ]] || root="$second_root"
    if [[ -n "$job" && -d "$root/$job" ]]; then
      for receipt in "$root/$job/"*.json; do
        [[ -f "$receipt" ]] || continue
        cp "$receipt" "$receipt_root/$scenario-$role-$(basename "$receipt")" || cleanup_result=$?
      done
    fi
  done
  # Resume before cleanup, including failed observations. Killing a paused worker
  # is never evidence that it observed the winning attempt's ownership.
  if (( paused )); then docker unpause "$first_container" >/dev/null || cleanup_result=$?; fi
  if [[ -n "$second_container" ]]; then
    docker logs "$second_container" > "$receipt_root/$scenario-recovery-worker.log" 2>&1 || cleanup_result=$?
    docker rm -f "$second_container" >/dev/null || cleanup_result=$?
  fi
  compose logs --no-color worker > "$receipt_root/$scenario-original-worker.log" 2>&1 || cleanup_result=$?
  compose up -d --force-recreate worker >/dev/null || cleanup_result=$?
  wait_ready && wait_peer_ready || cleanup_result=$?
  if (( cleanup_result )); then
    scenario_cleanup_failure="heartbeat recovery topology restoration failed"
    runtime_taint="$scenario_cleanup_failure"
    scenario_finding="${scenario_finding:+${scenario_finding}; }$scenario_cleanup_failure"
    return 1
  fi
  (( result == 0 )) || return "$result"
  write_receipt "$scenario" pass "" "$job" successful "$digest"
}

heartbeat_job_record() {
  compose exec -T redis redis-cli --raw GET "controlplane:job:$job"
}

heartbeat_wait_record() {
  local predicate="$1" output="$2" record
  while (( SECONDS < recovery_deadline )); do
    record="$(heartbeat_job_record)" || return 1
    if jq -e --argjson original "$original_record" "$predicate" <<<"$record" >/dev/null; then
      printf '%s\n' "$record" > "$output" || return 1
      record_attempt "$(jq -r .attemptCount <<<"$record")"
      return 0
    fi
    sleep 0.2
  done
  scenario_fail "natural heartbeat recovery did not reach the required durable state"
}

heartbeat_barrier() {
  local root="$1" action="$2" barrier="$3"
  local barrier_root="$root"
  if [[ "$action" == wait ]]; then wait_barrier "$job" "$barrier"
  else release_barrier "$job" "$barrier"; fi
}

heartbeat_winner_projection() {
  jq -Sc '{status,claimedBy,claimedAt,attemptCount,completedAt,artifactReferences,cancellationRequestedAt}'
}

heartbeat_inventory() {
  python3 - "$object_root/gp/outputs/$job" > "$1" <<'PY'
import datetime, hashlib, json, sys
from pathlib import Path
root = Path(sys.argv[1])
entries = []
for path in sorted(root.rglob("*")):
    if not path.is_file():
        continue
    try:
        data = path.read_bytes()
        entries.append({"path": str(path.relative_to(root)), "bytes": len(data),
                        "sha256": hashlib.sha256(data).hexdigest()})
    except FileNotFoundError:
        entries.append({"path": str(path.relative_to(root)), "removed_during_snapshot": True})
print(json.dumps({"observed_at": datetime.datetime.now(datetime.timezone.utc).isoformat(), "entries": entries}))
PY
}

run_heartbeat_recovery_case() {
  local -x HONUA_GP_QUALIFICATION_BARRIER_ROOT=/var/run/honua/qualification/heartbeat-original
  local -x HONUA_GP_WORKER_REDIS=redis:6379
  local recovery_deadline=$((SECONDS + ${HONUA_GP_SCENARIO_TIMEOUT_SECONDS:-900}))
  local original_record winner_record original_worker recovery_worker barrier before_bytes after_bytes
  local peer_name="${project_name}-heartbeat-peer" resume_at stable_until record original_log
  mkdir -p "$first_root" "$second_root" || return 1
  chmod 777 "$first_root" "$second_root" || return 1
  compose up -d --force-recreate worker >/dev/null || return 1
  first_container="$(compose ps -q worker)" || return 1
  job="$(submit_async gdal.ogr2ogr "$native_payload")" || return 1
  jq -n --arg job "$job" '{submitted_job:$job,scope:"natural heartbeat expiry; not exclusive partition lease"}' > "$scenario_evidence_file"
  heartbeat_barrier "$first_root" wait claimed || return 1
  docker exec --user 0 "$first_container" chmod 777 "/var/run/honua/qualification/heartbeat-original/$job" || return 1
  heartbeat_barrier "$first_root" release claimed || return 1
  heartbeat_barrier "$first_root" wait native-process-started || return 1
  original_record="$(heartbeat_job_record)" || return 1
  jq -e '.status == 2 and .attemptCount == 1 and (.artifactReferences|length) == 0
    and .lastHeartbeatAt != null and .cancellationRequestedAt == null' <<<"$original_record" >/dev/null || return 1
  printf '%s\n' "$original_record" > "$receipt_root/$scenario-original-record.json"
  original_worker="$(jq -r .claimedBy <<<"$original_record")"
  jq -e --arg worker "$original_worker" '.workerId == $worker and .childProcessId > 0' \
    "$first_root/$job/native-process-started.ready.json" >/dev/null || return 1
  docker pause "$first_container" >/dev/null || return 1
  paused=1
  record_disruption worker native-process-started pause-heartbeat
  second_container="$(compose run -d --no-deps --name "$peer_name" \
    -e HONUA_GP_QUALIFICATION_BARRIER_ROOT=/var/run/honua/qualification/heartbeat-recovery worker)" || return 1
  # Both live containers must resolve the same actual image, not just equal supplied tags.
  docker inspect "$first_container" "$second_container" > "$receipt_root/$scenario-worker-identities.json" || return 1
  jq -e --arg image "$HONUA_WORKER_IMAGE" --arg sha "$candidate_source_sha" '
    length == 2 and .[0].Id != .[1].Id and .[0].Image == .[1].Image
    and all(.[]; .Config.Image == $image and .Config.Labels["org.opencontainers.image.revision"] == $sha)
    ' "$receipt_root/$scenario-worker-identities.json" >/dev/null || return 1
  heartbeat_wait_record '.status == 0 and .attemptCount == $original.attemptCount
    and .claimedBy == null and .nextRetryAt != null and (.artifactReferences|length) == 0
    and (.currentPhase|startswith("Retrying (attempt"))' "$receipt_root/$scenario-retry-record.json" || return 1
  heartbeat_validate_retry "$receipt_root/$scenario-original-record.json" "$receipt_root/$scenario-retry-record.json" || return 1
  record_transition queued
  heartbeat_wait_record '.status == 2 and .attemptCount == $original.attemptCount + 1
    and .claimedBy != $original.claimedBy and (.artifactReferences|length) == 0' "$receipt_root/$scenario-reclaimed-record.json" || return 1
  recovery_worker="$(jq -r .claimedBy "$receipt_root/$scenario-reclaimed-record.json")"
  heartbeat_barrier "$second_root" wait claimed || return 1
  [[ "$(jq -r .workerId "$second_root/$job/claimed.ready.json")" == "$recovery_worker" ]] || return 1
  docker exec --user 0 "$second_container" chmod 777 "/var/run/honua/qualification/heartbeat-recovery/$job" || return 1
  for barrier in claimed native-process-started output-bytes-written-unpublished artifact-reference-published-terminal-cas-pending; do
    heartbeat_barrier "$second_root" wait "$barrier" || return 1
    heartbeat_barrier "$second_root" release "$barrier" || return 1
  done
  heartbeat_wait_record '.status == 3 and .attemptCount == $original.attemptCount + 1
    and .claimedBy != $original.claimedBy and (.artifactReferences|length) == 1' "$receipt_root/$scenario-winning-record.json" || return 1
  winner_record="$(cat "$receipt_root/$scenario-winning-record.json")"
  before_bytes="$receipt_root/$scenario-winner-before.geojson"
  # Terminal CAS precedes result registration. Wait within the same scenario
  # budget for the normal authenticated content route to become readable.
  until auth_curl "$peer_url/api/geoprocessing/jobs/$job/artifacts/0/content" > "$before_bytes"; do
    (( SECONDS < recovery_deadline )) || return 1
    sleep 0.2
  done
  python3 "$repo_root/scripts/qualification/verify-gp-store-artifact.py" "$before_bytes" || return 1
  (( $(wc -c < "$before_bytes") > $(compose_staging_value MaxInlineArtifactBytes) )) || {
    scenario_fail "heartbeat fixture did not exceed the configured inline ceiling"; return 1; }
  digest="$(sha256sum "$before_bytes" | cut -d' ' -f1)"
  heartbeat_inventory "$receipt_root/$scenario-inventory-winner-before.json" || return 1
  # The losing process must actually resume and handle stale ownership. No job,
  # claim, timestamp, TTL, or output-store edits make this recovery happen.
  resume_at="$(now)"
  docker unpause "$first_container" >/dev/null || return 1
  paused=0
  record_disruption worker native-process-started resumed
  original_log="$receipt_root/$scenario-original-resumed.log"
  heartbeat_barrier "$first_root" release native-process-started || return 1
  while (( SECONDS < recovery_deadline )); do
    docker logs --since "$resume_at" "$first_container" > "$original_log" 2>&1 || return 1
    [[ -s "$first_root/$job/output-bytes-written-unpublished.ready.json" ]] && break
    grep -Fq "Skipping state transition for job $job:" "$original_log" && break
    sleep 0.05
  done
  heartbeat_inventory "$receipt_root/$scenario-inventory-resumed-stale.json" || return 1
  for barrier in output-bytes-written-unpublished artifact-reference-published-terminal-cas-pending; do
    # Pre-release is safe only within this original worker's private fence root.
    heartbeat_barrier "$first_root" release "$barrier" || return 1
  done
  while (( SECONDS < recovery_deadline )); do
    docker logs --since "$resume_at" "$first_container" > "$original_log" 2>&1 || return 1
    if grep -Fq "Skipping state transition for job $job:" "$original_log"; then break; fi
    sleep 0.2
  done
  grep -Fq "Skipping state transition for job $job:" "$original_log" || {
    scenario_fail "resumed original worker did not observe stale ownership"; return 1; }
  [[ "$(docker inspect --format '{{.State.Running}} {{.State.Paused}}' "$first_container")" == 'true false' ]] || return 1
  # Two ordinary 30-second reconciliation windows after stale completion. Also
  # allow the configured production sweeper to remove any stale attempt bytes.
  stable_until=$((SECONDS + 65))
  (( stable_until < recovery_deadline )) || return 1
  while (( SECONDS < stable_until )); do
    record="$(heartbeat_job_record)" || return 1
    [[ "$(heartbeat_winner_projection <<<"$record")" == "$(heartbeat_winner_projection <<<"$winner_record")" ]] || {
      scenario_fail "stale worker changed the winning durable result"; return 1; }
    sleep 1
  done
  [[ "$(object_file_count "$job")" == 1 ]] || {
    scenario_fail "natural sweep did not leave exactly the winning output object"; return 1; }
  heartbeat_inventory "$receipt_root/$scenario-inventory-after-sweep.json" || return 1
  heartbeat_job_record > "$receipt_root/$scenario-final-record.json" || return 1
  [[ "$(heartbeat_winner_projection < "$receipt_root/$scenario-final-record.json")" == "$(heartbeat_winner_projection <<<"$winner_record")" ]] || return 1
  [[ "$(docker inspect --format '{{.State.Running}} {{.State.Paused}}' "$first_container")" == 'true false' ]] || return 1
  after_bytes="$receipt_root/$scenario-winner-after.geojson"
  auth_curl "$peer_url/api/geoprocessing/jobs/$job/artifacts/0/content" > "$after_bytes" || return 1
  cmp "$before_bytes" "$after_bytes" || { scenario_fail "winning output changed after stale worker resumed"; return 1; }
  python3 "$repo_root/scripts/qualification/verify-gp-store-artifact.py" "$after_bytes" || return 1
  docker logs "$second_container" > "$receipt_root/$scenario-recovery-worker.log" 2>&1 || return 1
  compose logs --no-color server server-peer > "$receipt_root/$scenario-server-reconciliation.log" || return 1
  grep -Fq "Heartbeat expired for job $job: retrying" "$receipt_root/$scenario-recovery-worker.log" \
    || grep -Fq "Heartbeat expired for job $job: retrying" "$receipt_root/$scenario-server-reconciliation.log" || return 1
  jq -n --arg job "$job" --arg sha "$digest" --arg before "${before_bytes##*/}" --arg after "${after_bytes##*/}" \
    --arg resumed "$resume_at" --argjson original "$original_record" --argjson winner "$winner_record" \
    --slurpfile retry "$receipt_root/$scenario-retry-record.json" \
    --slurpfile final "$receipt_root/$scenario-final-record.json" \
    --slurpfile inventory_before "$receipt_root/$scenario-inventory-winner-before.json" \
    --slurpfile inventory_stale "$receipt_root/$scenario-inventory-resumed-stale.json" \
    --slurpfile inventory_after "$receipt_root/$scenario-inventory-after-sweep.json" \
    '{submitted_job:$job,scope:"heartbeat-expiry recovery",original:$original,retry:$retry[0],winner:$winner,final:$final[0],
      inventories:{winner_before:$inventory_before[0],resumed_stale:$inventory_stale[0],after_sweep:$inventory_after[0]},
      stale_worker:{resumed_at:$resumed,terminal_skipped:true,alive_after_observation:true},
      stable_observation_seconds:65,remaining_output_objects:1,sha256:$sha,output_files:[$before,$after]}' > "$scenario_evidence_file" || return 1
  jq -n --arg sha "$digest" --argjson bytes "$(wc -c < "$after_bytes")" \
    '{sha256:$sha,bytes:$bytes}' > "$scenario_state_file" || return 1
  record_transition successful
}

heartbeat_validate_retry() {
  python3 - "$1" "$2" <<'PY'
import datetime, json, sys
with open(sys.argv[1], encoding="utf-8") as handle:
    original = json.load(handle)
with open(sys.argv[2], encoding="utf-8") as handle:
    retry = json.load(handle)
parse = lambda value: datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
# These are the unmodified production heartbeat/backoff defaults in the retained
# source. Shortening them via test-side record mutation cannot satisfy the proof.
assert (parse(retry["updatedAt"]) - parse(original["lastHeartbeatAt"])).total_seconds() > 90
assert (parse(retry["nextRetryAt"]) - parse(retry["updatedAt"])).total_seconds() >= 30
assert retry["attemptCount"] == original["attemptCount"]
assert not retry["artifactReferences"] and retry["claimedBy"] is None
PY
}
