-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 073: Durable persistence for migration-run checkpoints (#2459, ADR-0060).
-- Moves resumable migration-run checkpoint state off the compute node's local disk so a
-- run checkpointed on one node can resume on any node. Backs
-- PostgresMigrationRunCheckpointStore, which replaces the dev/test-only filesystem store.

CREATE TABLE IF NOT EXISTS honua.migration_run_checkpoints (
    run_id      TEXT        PRIMARY KEY,
    checkpoint  JSONB       NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE honua.migration_run_checkpoints IS
    'Durable, release-safe migration-run checkpoints (#2459). Persists resume state so a run can resume on any node.';
COMMENT ON COLUMN honua.migration_run_checkpoints.run_id IS
    'Stable migration-run identifier assigned by the migration harness (primary key).';
COMMENT ON COLUMN honua.migration_run_checkpoints.checkpoint IS
    'Sanitized MigrationRunCheckpoint snapshot (phase, resume marker, completed item count, attempt).';
