-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Durable job substrate (ADR-0081). The queue is a query over execution_jobs: queue_visible_at
-- is the queue position and NULL means the job is not in the local queue.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.execution_jobs (
    operation_id           TEXT COLLATE "C" PRIMARY KEY CHECK (length(operation_id) BETWEEN 1 AND 512),
    version                BIGINT NOT NULL CHECK (version > 0),
    status                 TEXT NOT NULL CHECK (status IN
                               ('Queued', 'Provisioning', 'Running', 'Succeeded', 'Failed', 'Cancelled')),
    attempt_count          INTEGER NOT NULL CHECK (attempt_count >= 0),
    claimed_by             TEXT,
    claimed_at             TIMESTAMPTZ,
    last_heartbeat_at      TIMESTAMPTZ,
    next_retry_at          TIMESTAMPTZ,
    completed_at           TIMESTAMPTZ,
    percent_complete       DOUBLE PRECISION,
    current_phase          TEXT,
    updated_at             TIMESTAMPTZ NOT NULL,
    queue_priority         SMALLINT,
    queue_visible_at       TIMESTAMPTZ,
    expires_at             TIMESTAMPTZ NOT NULL,
    record                 JSON NOT NULL,
    kind                   TEXT NOT NULL,
    runtime_profile        TEXT NOT NULL,
    backend                TEXT NOT NULL,
    priority               SMALLINT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL,
    tenant_id              TEXT,
    cancellation_requested BOOLEAN NOT NULL,
    heartbeat_timeout      INTERVAL NOT NULL,
    max_duration           INTERVAL NOT NULL,
    lookup_keys            TEXT[] NOT NULL,
    CHECK ((queue_visible_at IS NULL) = (queue_priority IS NULL)),
    CHECK (queue_visible_at IS NULL OR backend = 'local')
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_threshold = 200,
        autovacuum_analyze_scale_factor = 0.05, autovacuum_analyze_threshold = 200);

CREATE INDEX IF NOT EXISTS idx_execution_jobs_created
    ON $HonuaSchema$.execution_jobs (created_at DESC, operation_id DESC);
CREATE INDEX IF NOT EXISTS idx_execution_jobs_status_created
    ON $HonuaSchema$.execution_jobs (status, created_at DESC, operation_id DESC);
CREATE INDEX IF NOT EXISTS idx_execution_jobs_kind_created
    ON $HonuaSchema$.execution_jobs (kind, created_at DESC, operation_id DESC);
CREATE INDEX IF NOT EXISTS idx_execution_jobs_lookup_keys
    ON $HonuaSchema$.execution_jobs USING GIN (lookup_keys);
CREATE INDEX IF NOT EXISTS idx_execution_jobs_claimable
    ON $HonuaSchema$.execution_jobs (runtime_profile, queue_priority, queue_visible_at, operation_id)
    WHERE status = 'Queued' AND queue_visible_at IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_execution_jobs_undispatched
    ON $HonuaSchema$.execution_jobs (backend)
    WHERE status = 'Queued' AND queue_visible_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_execution_jobs_reconcilable
    ON $HonuaSchema$.execution_jobs (status)
    WHERE status IN ('Queued', 'Provisioning', 'Running')
      AND NOT (status = 'Queued' AND backend = 'local' AND attempt_count = 0 AND NOT cancellation_requested);
CREATE INDEX IF NOT EXISTS idx_execution_jobs_expired
    ON $HonuaSchema$.execution_jobs (expires_at)
    WHERE status IN ('Succeeded', 'Failed', 'Cancelled');

CREATE TABLE IF NOT EXISTS $HonuaSchema$.leases (
    lease_key  TEXT COLLATE "C" PRIMARY KEY,
    owner_id   TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_leases_expires ON $HonuaSchema$.leases (expires_at);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.execution_job_log_streams (
    operation_id         TEXT COLLATE "C" PRIMARY KEY,
    appended_count       BIGINT NOT NULL DEFAULT 0,
    dropped_count        BIGINT NOT NULL DEFAULT 0,
    event_identity_count INTEGER NOT NULL DEFAULT 0,
    expires_at           TIMESTAMPTZ NOT NULL,
    CONSTRAINT execution_job_log_streams_event_identity_limit CHECK (event_identity_count <= 8192)
);
CREATE INDEX IF NOT EXISTS idx_execution_job_log_streams_expires
    ON $HonuaSchema$.execution_job_log_streams (expires_at);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.execution_job_log_entries (
    operation_id TEXT COLLATE "C" NOT NULL
        REFERENCES $HonuaSchema$.execution_job_log_streams (operation_id) ON DELETE CASCADE,
    sequence     BIGINT NOT NULL,
    entry        JSON NOT NULL CHECK (octet_length(entry::text) <= 4096),
    PRIMARY KEY (operation_id, sequence)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.execution_job_log_event_identities (
    operation_id TEXT COLLATE "C" NOT NULL
        REFERENCES $HonuaSchema$.execution_job_log_streams (operation_id) ON DELETE CASCADE,
    event_id     TEXT COLLATE "C" NOT NULL CHECK (length(event_id) BETWEEN 1 AND 160),
    PRIMARY KEY (operation_id, event_id)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.geoprocessing_result_packages (
    job_id     TEXT COLLATE "C" PRIMARY KEY,
    package    JSON NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_geoprocessing_result_packages_expires
    ON $HonuaSchema$.geoprocessing_result_packages (expires_at);

COMMENT ON TABLE $HonuaSchema$.execution_jobs IS 'Execution jobs and their local queue position; the attempt-state columns override their copies in record.';
COMMENT ON TABLE $HonuaSchema$.leases IS 'Expiring ownership rows compared against the database clock.';
COMMENT ON TABLE $HonuaSchema$.execution_job_log_streams IS 'Per-job log counters: entries with sequence in [dropped_count, appended_count) are retained.';
COMMENT ON TABLE $HonuaSchema$.execution_job_log_event_identities IS 'Producer event identities already appended, kept after their entry is trimmed.';
