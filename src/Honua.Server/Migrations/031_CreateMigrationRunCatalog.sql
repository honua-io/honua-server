-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 031: Add migration_runs catalog
-- Tracks GeoServer-style migration runs surfaced by the admin orchestration
-- endpoints introduced in issue #1015 slice 5. Each row records a single
-- apply run: identity, source kind, redacted source URL, lifecycle status,
-- start/completion timestamps, and a reference (with fingerprint and
-- optional inline body) to the slice-4 evidence pack so admin and SDK
-- callers can audit past runs without re-deriving state from per-stage
-- artifacts.
-- Dependencies: Requires honua schema from 001_CreateHonuaSchema.sql.
-- Builds on slice 1-4 catalog tables: honua.migration_data_sources (029)
-- and honua.migration_styles (030).

CREATE TABLE IF NOT EXISTS honua.migration_runs (
    run_id                      VARCHAR(64)  PRIMARY KEY,
    source_kind                 VARCHAR(64)  NOT NULL,
    source_url                  TEXT         NOT NULL DEFAULT '',
    source_display_name         TEXT,
    status                      VARCHAR(32)  NOT NULL DEFAULT 'running',
    started_at                  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    completed_at                TIMESTAMPTZ,
    evidence_pack_ref           TEXT,
    evidence_pack_fingerprint   VARCHAR(128),
    evidence_pack_body          JSONB,
    status_note                 TEXT,
    CONSTRAINT chk_migration_runs_status
        CHECK (status IN ('running','succeeded','failed','cancelled'))
);

-- The admin list endpoint returns runs ordered most-recent first; this
-- supporting index keeps that query cheap as the table grows.
CREATE INDEX IF NOT EXISTS idx_migration_runs_started_at
    ON honua.migration_runs (started_at DESC);

CREATE INDEX IF NOT EXISTS idx_migration_runs_status
    ON honua.migration_runs (status);

CREATE INDEX IF NOT EXISTS idx_migration_runs_source_kind
    ON honua.migration_runs (source_kind);

COMMENT ON TABLE honua.migration_runs IS
    'Operator-visible record of GeoServer migration apply runs (issue #1015 slice 5). The evidence_pack_body column carries the slice-4 evidence pack JSON for download via the admin orchestration endpoints; it is intentionally jsonb so reviewers can introspect it directly with SQL when triaging a failed cutover.';
