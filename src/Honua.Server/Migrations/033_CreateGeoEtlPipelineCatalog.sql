-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- GeoETL durable pipeline catalog (#361 Child Ticket A — durable persistence).
-- Replaces the in-memory baseline stores with PostgreSQL-backed definition and
-- execution stores. The stage chain (Source -> []Transform -> Sink) is persisted
-- as a JSONB document so the ConnectorConfig / TransformConfig discriminated
-- unions can evolve without a schema migration; the definition root carries a
-- schema_version column denormalized from the document for forward-compat reads.

CREATE TABLE IF NOT EXISTS honua.pipeline_definitions (
    id              VARCHAR(128) PRIMARY KEY,
    name            VARCHAR(256) NOT NULL,
    description     TEXT,
    schema_version  INTEGER NOT NULL DEFAULT 1,
    version         INTEGER NOT NULL DEFAULT 1,
    stages_json     JSONB NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_pipeline_definitions_name
    ON honua.pipeline_definitions (name);

CREATE INDEX IF NOT EXISTS idx_pipeline_definitions_updated_at
    ON honua.pipeline_definitions (updated_at DESC);

COMMENT ON TABLE  honua.pipeline_definitions IS
    'GeoETL pipeline definitions (#361). Declarative Source -> []Transform -> Sink chains executed as ExtractTransformLoad jobs on the #681 substrate.';
COMMENT ON COLUMN honua.pipeline_definitions.schema_version IS
    'Definition document schema version; lives on the root so stored definitions stay readable across discriminated-union evolutions.';
COMMENT ON COLUMN honua.pipeline_definitions.version IS
    'Definition version; incremented on every successful update. Older versions remain readable for audit and rollback.';
COMMENT ON COLUMN honua.pipeline_definitions.stages_json IS
    'Ordered stage chain (PipelineStage[]) persisted as JSONB so ConnectorConfig / TransformConfig unions evolve without a schema migration.';

CREATE TABLE IF NOT EXISTS honua.pipeline_executions (
    id                      VARCHAR(128) PRIMARY KEY,
    pipeline_id             VARCHAR(128) NOT NULL,
    pipeline_version        INTEGER NOT NULL DEFAULT 1,
    execution_job_id        VARCHAR(128) NOT NULL,
    status                  VARCHAR(32) NOT NULL,
    is_dry_run              BOOLEAN NOT NULL DEFAULT FALSE,
    features_read           BIGINT NOT NULL DEFAULT 0,
    features_written        BIGINT NOT NULL DEFAULT 0,
    features_quarantined    BIGINT NOT NULL DEFAULT 0,
    batch_id                VARCHAR(128),
    error_message           TEXT,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at            TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_pipeline_executions_pipeline_id
    ON honua.pipeline_executions (pipeline_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_pipeline_executions_status
    ON honua.pipeline_executions (status);

CREATE INDEX IF NOT EXISTS idx_pipeline_executions_job_id
    ON honua.pipeline_executions (execution_job_id);

COMMENT ON TABLE  honua.pipeline_executions IS
    'GeoETL pipeline execution records (#361). Correlated to the substrate ExecutionJobRecord by execution_job_id; status mirrors the job lifecycle.';
COMMENT ON COLUMN honua.pipeline_executions.execution_job_id IS
    'Substrate execution job id (ExecutionJobRecord.OperationId) backing this run.';
COMMENT ON COLUMN honua.pipeline_executions.batch_id IS
    'Batch identifier tagged on every sink write so a failed run can soft-delete its own rows (ADR-0038).';
