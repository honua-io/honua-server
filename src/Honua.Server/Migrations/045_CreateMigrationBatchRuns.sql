-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 045: Footprint-driven batch import orchestration (issue #1253).
-- A batch run aggregates an ordered set of per-layer Geoservices import jobs
-- (the "footprint") into a single resumable run with rolled-up progress. The
-- migration_batch_runs row is the parent aggregate; migration_batch_children
-- rows hold the deterministic child ordering, per-child status, dependency
-- edges, and the resolved Honua layer id used by relationship-apply (#1256).
-- Dependencies: Requires the honua schema from 001_CreateHonuaSchema.sql. The
-- batch surface is independent of honua.migration_runs (031) so a batch can be
-- audited on its own.

CREATE TABLE IF NOT EXISTS honua.migration_batch_runs (
    batch_id                VARCHAR(64)  PRIMARY KEY,
    source_kind             VARCHAR(64)  NOT NULL,
    source_url              TEXT         NOT NULL DEFAULT '',
    source_display_name     TEXT,
    status                  VARCHAR(32)  NOT NULL DEFAULT 'running',
    started_at              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    completed_at            TIMESTAMPTZ,
    total_children          INTEGER      NOT NULL DEFAULT 0,
    succeeded_children      INTEGER      NOT NULL DEFAULT 0,
    failed_children         INTEGER      NOT NULL DEFAULT 0,
    cancelled_children      INTEGER      NOT NULL DEFAULT 0,
    apply_relationships     BOOLEAN      NOT NULL DEFAULT FALSE,
    relationships_applied   BOOLEAN      NOT NULL DEFAULT FALSE,
    manifest_body           JSONB,
    status_note             TEXT,
    CONSTRAINT chk_migration_batch_runs_status
        CHECK (status IN ('running','succeeded','failed','cancelled','needs-review'))
);

CREATE INDEX IF NOT EXISTS idx_migration_batch_runs_started_at
    ON honua.migration_batch_runs (started_at DESC);

CREATE INDEX IF NOT EXISTS idx_migration_batch_runs_status
    ON honua.migration_batch_runs (status);

CREATE TABLE IF NOT EXISTS honua.migration_batch_children (
    batch_id            VARCHAR(64)  NOT NULL
        REFERENCES honua.migration_batch_runs (batch_id) ON DELETE CASCADE,
    ordinal             INTEGER      NOT NULL,
    source_resource_id  TEXT         NOT NULL,
    service_url         TEXT         NOT NULL,
    source_layer_id     INTEGER      NOT NULL,
    table_name          TEXT         NOT NULL,
    target_schema       TEXT,
    service_name        TEXT,
    depends_on          JSONB        NOT NULL DEFAULT '[]'::jsonb,
    status              VARCHAR(32)  NOT NULL DEFAULT 'pending',
    job_id              VARCHAR(64),
    published_layer_id  INTEGER,
    status_note         TEXT,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    PRIMARY KEY (batch_id, ordinal),
    CONSTRAINT chk_migration_batch_children_status
        CHECK (status IN ('pending','running','succeeded','failed','needs-review','cancelled'))
);

CREATE INDEX IF NOT EXISTS idx_migration_batch_children_batch
    ON honua.migration_batch_children (batch_id, ordinal);

COMMENT ON TABLE honua.migration_batch_runs IS
    'Footprint-driven batch import run aggregate (issue #1253). Aggregates ordered per-layer Geoservices import jobs into one resumable run with rolled-up progress and optional post-publish relationship application (#1256). manifest_body is jsonb so reviewers can introspect the batch manifest directly with SQL.';

COMMENT ON TABLE honua.migration_batch_children IS
    'Ordered child layer imports within a migration_batch_runs row (issue #1253). ordinal is the deterministic execution sequence; depends_on lists the source_resource_ids that must succeed first (relationship origin layers); published_layer_id is the resolved Honua layer id used to build the relationship-apply published-layer map.';
