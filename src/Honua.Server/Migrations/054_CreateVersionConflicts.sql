-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 054_CreateVersionConflicts.sql
-- Description: Durable persistence for branch-version reconcile conflicts and their manual
--              resolution state (#371). When a reconcile detects genuinely-overlapping edits between
--              a branch version and DEFAULT since the merge base, it persists one pending row per
--              conflicting (version_id, layer_id, objectid) here, carrying the three-way
--              base/DEFAULT/version images (JSON attributes + WKT geometry) so a manual-resolution UI
--              can render a before/after/base diff after the synchronous reconcile call returns. An
--              operator resolution (take version / take default / take base) clears the pending row;
--              post is blocked while any pending row remains for the version. This migration is
--              additive and completely inert for DEFAULT (no DEFAULT read/write path touches this
--              table), so existing replication/change-tracking and CITE paths stay byte-identical.
-- Dependencies: Requires honua.gdb_versions (047).

-- One pending conflict per (version_id, layer_id, objectid). The three-way images are opaque
-- pre-serialized JSON/WKT so the review surface stays decoupled from the physical feature schema and
-- the payload never carries SQL, connection details, or provider internals. conflict_type mirrors the
-- ReplicaConflictType ordinal taxonomy (0=Attribute,1=Geometry,2=DeleteUpdate,3=UpdateDelete,...).
CREATE TABLE IF NOT EXISTS honua.version_conflicts (
    conflict_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    version_id UUID NOT NULL REFERENCES honua.gdb_versions(version_id) ON DELETE CASCADE,
    layer_id INT NOT NULL,
    objectid BIGINT NOT NULL,
    -- Conflict classification (ReplicaConflictType ordinal).
    conflict_type SMALLINT NOT NULL,
    -- Lifecycle status: 0=pending, 1=resolved (mirrors ReplicaConflictStatus pending/resolved).
    status SMALLINT NOT NULL DEFAULT 0,
    -- Three-way images captured at reconcile time. attributes are JSONB; geometry is rendered to WKT
    -- text (display/diff only — post replays from the live overlay, not from these images).
    base_attributes JSONB,
    default_attributes JSONB,
    version_attributes JSONB,
    base_geometry_wkt TEXT,
    default_geometry_wkt TEXT,
    version_geometry_wkt TEXT,
    -- Per-field three-way diffs for the overlapping fields (JSON array of {name,base,default,version}).
    field_diffs JSONB,
    -- Resolution choice once resolved: 0=take version, 1=take default, 2=take base (NULL while pending).
    resolution_choice SMALLINT,
    resolved_by TEXT,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    resolved_at TIMESTAMPTZ,
    CONSTRAINT version_conflicts_valid_status CHECK(status IN (0, 1)),
    CONSTRAINT version_conflicts_valid_resolution CHECK(resolution_choice IS NULL OR resolution_choice IN (0, 1, 2))
);

-- At most one pending conflict per (version_id, layer_id, objectid): a re-reconcile refreshes the
-- existing pending row rather than accumulating duplicates. Resolved rows are retained as audit
-- evidence and are excluded from this partial unique index.
CREATE UNIQUE INDEX IF NOT EXISTS idx_version_conflicts_pending_feature
    ON honua.version_conflicts(version_id, layer_id, objectid)
    WHERE status = 0;

-- Pending-conflict lookups for inspect/resolve and the post block check.
CREATE INDEX IF NOT EXISTS idx_version_conflicts_version_status
    ON honua.version_conflicts(version_id, status);

COMMENT ON TABLE honua.version_conflicts IS
    'Durable branch-version reconcile conflicts + manual resolution state; inert for DEFAULT (#371)';
COMMENT ON COLUMN honua.version_conflicts.version_id IS 'Owning branch version (honua.gdb_versions)';
COMMENT ON COLUMN honua.version_conflicts.layer_id IS 'Storage layer of the conflicting feature';
COMMENT ON COLUMN honua.version_conflicts.objectid IS 'Object id of the conflicting feature';
COMMENT ON COLUMN honua.version_conflicts.conflict_type IS 'Conflict classification (ReplicaConflictType ordinal)';
COMMENT ON COLUMN honua.version_conflicts.status IS 'Lifecycle status: 0=pending, 1=resolved';
COMMENT ON COLUMN honua.version_conflicts.base_attributes IS 'Common-ancestor attribute image (display/diff)';
COMMENT ON COLUMN honua.version_conflicts.default_attributes IS 'DEFAULT (target) attribute image (display/diff)';
COMMENT ON COLUMN honua.version_conflicts.version_attributes IS 'Branch (edit) attribute image (display/diff)';
COMMENT ON COLUMN honua.version_conflicts.field_diffs IS 'Per-field three-way diffs (JSON array)';
COMMENT ON COLUMN honua.version_conflicts.resolution_choice IS 'Resolution side once resolved: 0=version,1=default,2=base';
