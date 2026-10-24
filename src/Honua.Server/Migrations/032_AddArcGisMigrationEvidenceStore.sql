-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- ArcGIS migration evidence store (#1025 slice 6).
-- Persists slice 2-4 manifest artifacts and slice 5 parity artifacts so that admin
-- UIs and SDKs can fetch them via the new
-- GET /api/v1/admin/import/arcgis/migrations[/{runId}/{manifest|parity}] endpoints.
-- Artifacts are stored as JSONB so artifact schema evolution remains a Core concern
-- (artifact kind/version travel inside the payload).

CREATE TABLE IF NOT EXISTS honua.arcgis_migration_runs (
    run_id                  VARCHAR(128) PRIMARY KEY,
    source_url              TEXT NOT NULL,
    source_display_name     VARCHAR(256),
    source_version          VARCHAR(64),
    actor                   VARCHAR(256),
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    target_resource_count   INTEGER NOT NULL DEFAULT 0,
    manifest_json           JSONB NOT NULL,
    parity_json             JSONB,
    parity_classification   VARCHAR(32)
);

CREATE INDEX IF NOT EXISTS idx_arcgis_migration_runs_created_at
    ON honua.arcgis_migration_runs (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_arcgis_migration_runs_source_url
    ON honua.arcgis_migration_runs (source_url);

CREATE INDEX IF NOT EXISTS idx_arcgis_migration_runs_parity_classification
    ON honua.arcgis_migration_runs (parity_classification);

COMMENT ON TABLE  honua.arcgis_migration_runs IS
    'Persisted ArcGIS migration evidence (#1025 slice 6) — manifest + parity artifacts surfaced by /api/v1/admin/import/arcgis/migrations.';
COMMENT ON COLUMN honua.arcgis_migration_runs.run_id IS
    'Stable run identifier (typically a GUID string emitted by the apply pipeline).';
COMMENT ON COLUMN honua.arcgis_migration_runs.manifest_json IS
    'Slice 2-4 MigrationManifestArtifact payload (identity remap, attachments, relationships, renderer/label diagnostics).';
COMMENT ON COLUMN honua.arcgis_migration_runs.parity_json IS
    'Slice 5 ArcGisMigrationParityArtifact payload. NULL until parity probes are executed.';
COMMENT ON COLUMN honua.arcgis_migration_runs.parity_classification IS
    'Denormalized parity Classification (pass/warn/fail) to back the admin list filter without scanning JSONB.';
