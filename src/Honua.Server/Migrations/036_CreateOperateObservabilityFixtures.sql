-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 036_CreateOperateObservabilityFixtures.sql
-- Description: Adds a small PostgreSQL-backed execution job/log substrate used
--              only by explicit Development/Test Operate observability fixtures
--              for Console Testcontainers (#1209).
-- Dependencies: honua schema and Console Operate observability tables (#1168).

CREATE TABLE IF NOT EXISTS honua.operate_fixture_execution_jobs (
    operation_id     TEXT        PRIMARY KEY,
    fixture_profile  TEXT        NOT NULL,
    record_json      JSONB       NOT NULL,
    version          BIGINT      NOT NULL DEFAULT 1,
    status           TEXT        NOT NULL,
    kind             TEXT        NOT NULL,
    backend          TEXT        NOT NULL,
    queue            TEXT        NULL,
    requested_by     TEXT        NULL,
    correlation_id   TEXT        NULL,
    trace_id         TEXT        NULL,
    definition_id    TEXT        NULL,
    resource_refs    TEXT[]      NOT NULL DEFAULT '{}'::text[],
    environment      TEXT        NULL,
    server           TEXT        NULL,
    release_id       TEXT        NULL,
    change_set_id    TEXT        NULL,
    alert_id         TEXT        NULL,
    created_at       TIMESTAMPTZ NOT NULL,
    updated_at       TIMESTAMPTZ NOT NULL,
    completed_at     TIMESTAMPTZ NULL,
    CONSTRAINT operate_fixture_execution_jobs_profile_not_empty CHECK (length(fixture_profile) > 0),
    CONSTRAINT operate_fixture_execution_jobs_record_object CHECK (jsonb_typeof(record_json) = 'object')
);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_profile_created
    ON honua.operate_fixture_execution_jobs (fixture_profile, created_at DESC, operation_id DESC);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_created
    ON honua.operate_fixture_execution_jobs (created_at DESC, operation_id DESC);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_status_created
    ON honua.operate_fixture_execution_jobs (status, created_at DESC, operation_id DESC);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_kind_created
    ON honua.operate_fixture_execution_jobs (kind, created_at DESC, operation_id DESC);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_backend
    ON honua.operate_fixture_execution_jobs (backend, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_queue
    ON honua.operate_fixture_execution_jobs (queue, created_at DESC) WHERE queue IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_requested_by
    ON honua.operate_fixture_execution_jobs (requested_by, created_at DESC) WHERE requested_by IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_correlation
    ON honua.operate_fixture_execution_jobs (correlation_id, created_at DESC) WHERE correlation_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_trace
    ON honua.operate_fixture_execution_jobs (trace_id, created_at DESC) WHERE trace_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_definition
    ON honua.operate_fixture_execution_jobs (definition_id, created_at DESC) WHERE definition_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_resource_refs
    ON honua.operate_fixture_execution_jobs USING GIN (resource_refs);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_release
    ON honua.operate_fixture_execution_jobs (release_id, created_at DESC) WHERE release_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_change_set
    ON honua.operate_fixture_execution_jobs (change_set_id, created_at DESC) WHERE change_set_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_operate_fixture_jobs_alert
    ON honua.operate_fixture_execution_jobs (alert_id, created_at DESC) WHERE alert_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS honua.operate_fixture_execution_logs (
    log_id           BIGSERIAL   PRIMARY KEY,
    operation_id     TEXT        NOT NULL REFERENCES honua.operate_fixture_execution_jobs(operation_id) ON DELETE CASCADE,
    fixture_profile  TEXT        NOT NULL,
    timestamp        TIMESTAMPTZ NOT NULL,
    level            TEXT        NOT NULL,
    payload_json     JSONB       NOT NULL,
    CONSTRAINT operate_fixture_execution_logs_profile_not_empty CHECK (length(fixture_profile) > 0),
    CONSTRAINT operate_fixture_execution_logs_payload_object CHECK (jsonb_typeof(payload_json) = 'object')
);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_logs_operation
    ON honua.operate_fixture_execution_logs (operation_id, log_id);

CREATE INDEX IF NOT EXISTS idx_operate_fixture_logs_profile
    ON honua.operate_fixture_execution_logs (fixture_profile, log_id);

COMMENT ON TABLE honua.operate_fixture_execution_jobs IS
    'Development/Test-only durable execution job fixture rows for Console Operate Testcontainers (#1209). Empty unless OperateObservabilityFixture is explicitly enabled.';
COMMENT ON TABLE honua.operate_fixture_execution_logs IS
    'Development/Test-only structured execution logs for seeded Console Operate fixture jobs (#1209).';
