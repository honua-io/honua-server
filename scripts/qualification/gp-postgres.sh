#!/usr/bin/env bash
# Sourced by the lifecycle harness. Read the same projected state as ExecutionJobRow.
gp_sql() {
  compose exec -T postgres psql -X -qAt -v ON_ERROR_STOP=1 -U honua -d honua "$@"
}

gp_job_record() {
  gp_sql --set=job="$1" <<'SQL'
SELECT record::jsonb || jsonb_build_object(
  'version',version,'status',CASE status
    WHEN 'Queued' THEN 0 WHEN 'Provisioning' THEN 1 WHEN 'Running' THEN 2
    WHEN 'Succeeded' THEN 3 WHEN 'Failed' THEN 4 WHEN 'Cancelled' THEN 5 END,
  'attemptCount',attempt_count,'claimedBy',claimed_by,'claimedAt',claimed_at,
  'lastHeartbeatAt',last_heartbeat_at,'nextRetryAt',next_retry_at,
  'completedAt',completed_at,'percentComplete',percent_complete,
  'currentPhase',current_phase,'updatedAt',updated_at)
FROM honua.execution_jobs WHERE operation_id=:'job' AND expires_at>clock_timestamp();
SQL
}

gp_package_record() {
  gp_sql --set=job="$1" <<'SQL'
SELECT package FROM honua.geoprocessing_result_packages
WHERE job_id=:'job' AND expires_at>clock_timestamp();
SQL
}

gp_package_exists() {
  gp_sql --set=job="$1" <<'SQL'
SELECT count(*) FROM honua.geoprocessing_result_packages
WHERE job_id=:'job' AND expires_at>clock_timestamp();
SQL
}
