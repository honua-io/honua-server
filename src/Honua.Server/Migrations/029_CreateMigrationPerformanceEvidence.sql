-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 029: Persistence table for migration performance evidence artifacts (#1033 slice 5).
-- Backs the admin / SDK endpoints that surface the latest passing publish plus per-fixture history.

CREATE TABLE IF NOT EXISTS honua.migration_performance_evidence (
    evidence_id      TEXT        PRIMARY KEY,
    source_family    TEXT        NOT NULL,
    fixture_size     TEXT        NOT NULL,
    status           TEXT        NOT NULL,
    generated_at     TIMESTAMPTZ NOT NULL,
    fingerprint      TEXT        NOT NULL,
    artifact_json    JSONB       NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE honua.migration_performance_evidence IS
    'Published migration performance evidence artifacts (#1033 slice 5). Latest-passing and per-fixture history are served from this table.';
COMMENT ON COLUMN honua.migration_performance_evidence.evidence_id IS
    'Stable evidence identifier derived from the artifact fingerprint.';
COMMENT ON COLUMN honua.migration_performance_evidence.status IS
    'Aggregate run status: Pass, Warn, or Fail.';

CREATE INDEX IF NOT EXISTS ix_migration_performance_evidence_generated_at
    ON honua.migration_performance_evidence (generated_at DESC);

CREATE INDEX IF NOT EXISTS ix_migration_performance_evidence_fixture
    ON honua.migration_performance_evidence (source_family, fixture_size, generated_at DESC);

CREATE INDEX IF NOT EXISTS ix_migration_performance_evidence_status
    ON honua.migration_performance_evidence (status, generated_at DESC);
