-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 029: Add migration_data_sources catalog
-- Tracks GeoServer-style data stores applied by the migration apply path.
-- Used by issue #1015 slice 2 to capture deterministic evidence that a
-- data source (PostGIS, GeoPackage, shapefile, etc.) has been wired up
-- in the Honua catalog. Connection material is intentionally summarized
-- without secrets.
-- Dependencies: Requires honua schema from 001_CreateHonuaSchema.sql.

CREATE TABLE IF NOT EXISTS honua.migration_data_sources (
    source_kind     VARCHAR(64)  NOT NULL,
    source_id       VARCHAR(256) NOT NULL,
    data_source_type VARCHAR(64) NOT NULL,
    workspace_name  VARCHAR(128),
    display_name    TEXT NOT NULL DEFAULT '',
    connection_summary TEXT NOT NULL DEFAULT '',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (source_kind, source_id)
);

CREATE INDEX IF NOT EXISTS idx_migration_data_sources_workspace
    ON honua.migration_data_sources (workspace_name);

COMMENT ON TABLE honua.migration_data_sources IS
    'Idempotent record of data sources applied by the migration apply path (issue #1015 slice 2).';
