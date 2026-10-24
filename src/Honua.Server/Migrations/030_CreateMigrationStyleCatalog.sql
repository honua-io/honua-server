-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 030: Add migration_styles catalog
-- Tracks GeoServer-style style entries applied by the migration apply path.
-- Used by issue #1015 slice 3 to capture deterministic evidence that a
-- style (typically SLD) has been persisted into the Honua catalog along
-- with the original body, the converter format (e.g. MapLibre JSON when
-- conversion succeeded), and structured conversion diagnostics. The
-- diagnostics column lets operators audit "manual-review" outcomes for
-- styles where SLD-to-MapLibre conversion could not produce perfect
-- visual parity (see issue AC: "Do not claim perfect SLD visual parity
-- when conversion diagnostics report manual review").
-- Dependencies: Requires honua schema from 001_CreateHonuaSchema.sql.

CREATE TABLE IF NOT EXISTS honua.migration_styles (
    source_kind          VARCHAR(64)  NOT NULL,
    source_id            VARCHAR(256) NOT NULL,
    workspace_name       VARCHAR(128),
    style_name           VARCHAR(256) NOT NULL,
    source_format        VARCHAR(64)  NOT NULL DEFAULT 'sld',
    source_language_version VARCHAR(32),
    target_style_id      VARCHAR(256) NOT NULL,
    source_body          TEXT,
    converted_body       TEXT,
    converted_format     VARCHAR(64),
    diagnostics          JSONB NOT NULL DEFAULT '[]'::jsonb,
    review_disposition   VARCHAR(32)  NOT NULL DEFAULT 'applied',
    created_at           TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    PRIMARY KEY (source_kind, source_id)
);

CREATE INDEX IF NOT EXISTS idx_migration_styles_workspace
    ON honua.migration_styles (workspace_name);

CREATE INDEX IF NOT EXISTS idx_migration_styles_disposition
    ON honua.migration_styles (review_disposition);

COMMENT ON TABLE honua.migration_styles IS
    'Idempotent record of styles applied by the migration apply path (issue #1015 slice 3). The diagnostics column captures SLD-to-MapLibre conversion warnings/errors so operators can audit manual-review outcomes.';
