#!/usr/bin/env bash
# Sourced by gp-lifecycle-harness.sh; uses its compose, scenario_fail and runtime_taint.

# A job and its queue position are one row of honua.execution_jobs (ADR-0081); the cache
# container holds no job state. The database-stamped attempt columns override their JSON copies, as the
# store does when it reads a row.
job_sql() {
  compose exec -T postgres psql -U honua -d honua -v ON_ERROR_STOP=1 -qAtX -c "$1"
}

# ExecutionJobStatus in declaration order; the stored record carries the ordinal.
job_statuses="ARRAY['Queued', 'Provisioning', 'Running', 'Succeeded', 'Failed', 'Cancelled']"

job_id_is_safe() { [[ "$1" =~ ^[A-Za-z0-9._:-]+$ ]]; }

job_record() {
  local job="$1" record
  job_id_is_safe "${job}" || return 1
  record="$(job_sql "SELECT (record::jsonb || jsonb_build_object(
      'status', array_position(${job_statuses}, status) - 1, 'version', version,
      'attemptCount', attempt_count, 'claimedBy', claimed_by, 'claimedAt', claimed_at,
      'lastHeartbeatAt', last_heartbeat_at, 'nextRetryAt', next_retry_at, 'completedAt', completed_at,
      'percentComplete', percent_complete, 'currentPhase', current_phase, 'updatedAt', updated_at))::text
    FROM honua.execution_jobs WHERE operation_id = '${job}'")" || return 1
  [[ -n "${record}" ]] || return 1
  printf '%s' "${record}"
}

# pending: the time the row becomes claimable while it waits in the queue.
# claimed: the claim time while a worker holds it. Empty when the job is in neither.
job_queue_position() {
  local job="$1" slot="$2" predicate column
  job_id_is_safe "${job}" || return 1
  case "${slot}" in
    pending) column=queue_visible_at; predicate="status = 'Queued' AND queue_visible_at IS NOT NULL" ;;
    claimed) column=claimed_at; predicate="status IN ('Provisioning', 'Running')" ;;
    *) return 1 ;;
  esac
  job_sql "SELECT ${column} FROM honua.execution_jobs WHERE operation_id = '${job}' AND ${predicate}"
}

# What the reconciler does to a claim whose lease is lost: the row returns to the queue
# while the first worker may still be executing, and the version moves so that worker's
# later writes are fenced.
requeue_claimed_job() {
  local job="$1"
  job_id_is_safe "${job}" || return 1
  [[ "$(job_sql "UPDATE honua.execution_jobs
      SET status = 'Queued', version = version + 1, claimed_by = NULL, claimed_at = NULL,
          last_heartbeat_at = NULL, current_phase = NULL, queue_priority = priority,
          queue_visible_at = now(), updated_at = now()
      WHERE operation_id = '${job}' AND backend = 'local' AND status IN ('Provisioning', 'Running')
      RETURNING operation_id")" == "${job}" ]]
}

# Local jobs waiting to be claimed.
pending_queue_depth() {
  job_sql "SELECT count(*) FROM honua.execution_jobs WHERE status = 'Queued' AND queue_visible_at IS NOT NULL"
}

# The stored result package, empty when none is registered.
result_package_record() {
  job_id_is_safe "$1" || return 1
  job_sql "SELECT package::text FROM honua.geoprocessing_result_packages WHERE job_id = '$1'"
}

# 1 when a result package is registered for the job, else 0.
result_package_exists() {
  job_id_is_safe "$1" || return 1
  job_sql "SELECT count(*) FROM honua.geoprocessing_result_packages WHERE job_id = '$1'"
}

# A terminal job past its retention; the server's retention sweep deletes the row.
expire_terminal_job() {
  job_id_is_safe "$1" || return 1
  [[ "$(job_sql "UPDATE honua.execution_jobs SET expires_at = now() - interval '1 second'
      WHERE operation_id = '$1' AND status IN ('Succeeded', 'Failed', 'Cancelled')
      RETURNING operation_id")" == "$1" ]]
}

job_row_exists() {
  job_id_is_safe "$1" || return 1
  [[ "$(job_sql "SELECT count(*) FROM honua.execution_jobs WHERE operation_id = '$1'")" == 1 ]]
}

# A queued job with its only attempt spent, left claimed by a worker that died long ago:
# the reconciler must fail it instead of running it again.
strand_exhausted_claim() {
  job_id_is_safe "$1" || return 1
  [[ "$(job_sql "UPDATE honua.execution_jobs
      SET record = (record::jsonb || jsonb_build_object('retryPolicy',
                    coalesce(record::jsonb->'retryPolicy', '{}'::jsonb) || jsonb_build_object('maxAttempts', 1)))::json,
          status = 'Running', version = version + 1, attempt_count = 1, claimed_by = 'dead-worker',
          claimed_at = '2000-01-01T00:00:00Z', last_heartbeat_at = '2000-01-01T00:00:00Z',
          queue_visible_at = NULL, queue_priority = NULL, updated_at = now()
      WHERE operation_id = '$1' AND status = 'Queued' AND backend = 'local'
      RETURNING operation_id")" == "$1" ]]
}

# A queued row whose record no reader can deserialize, placed ahead of every other job.
# It is a copy of a real queued row so every other column is valid.
insert_poison_job() {
  local poison="$1" template="$2"
  job_id_is_safe "${poison}" && job_id_is_safe "${template}" || return 1
  [[ "$(job_sql "INSERT INTO honua.execution_jobs (operation_id, version, status, attempt_count,
          updated_at, queue_priority, queue_visible_at, expires_at, record, kind, runtime_profile,
          backend, priority, created_at, tenant_id, cancellation_requested, heartbeat_timeout,
          max_duration, lookup_keys)
      SELECT '${poison}', 1, 'Queued', 0, now(), queue_priority, queue_visible_at - interval '1 minute',
             expires_at, json_build_object('poison', true), kind, runtime_profile, backend, priority,
             now(), tenant_id, false, heartbeat_timeout, max_duration, '{}'
      FROM honua.execution_jobs WHERE operation_id = '${template}' AND queue_visible_at IS NOT NULL
      RETURNING operation_id")" == "${poison}" ]]
}

# Recreating the worker discards the held worker's logs, so a failed scenario keeps them first.
keep_held_worker_logs() {
  compose logs --no-color --tail 1000 worker > "${receipt_root}/${scenario_name}.held-worker.log" 2>&1 || true
}

native_entry_hold_active() { [[ "${HONUA_GP_QUALIFICATION_PAUSE_AT:-}" == native-process-started ]]; }

# Without a pause at its entry the native child runs free, and a short conversion has
# exited before the barrier reports its PID. Pausing there alone makes the worker hold
# the child before main until the barrier releases or cancellation kills it.
with_native_entry_hold() {
  local result=0
  export HONUA_GP_QUALIFICATION_PAUSE_AT=native-process-started
  if compose up -d --force-recreate worker >/dev/null; then
    "$@" || result=$?
    (( result == 0 )) || keep_held_worker_logs
  else
    scenario_fail "worker could not be recreated with the native entry hold"
    result=1
  fi
  unset HONUA_GP_QUALIFICATION_PAUSE_AT
  compose up -d --force-recreate worker >/dev/null || {
    runtime_taint="worker could not be restored after the native entry hold"
    return 1
  }
  return "$result"
}
