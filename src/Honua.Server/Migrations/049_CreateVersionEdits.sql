-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 049_CreateVersionEdits.sql
-- Description: Adds the branch-versioning overlay table that stores branch row *values* (#1272 Track B,
--              ADR-0051). DEFAULT data stays the live base `features` table and is never copied; a
--              version read overlays this table onto DEFAULT at a generation "moment" and a version
--              write routes the row image here instead of `features`. This migration is additive and
--              completely inert for DEFAULT (no DEFAULT read/write path touches `honua.version_edits`),
--              so existing replication/change-tracking and CITE paths stay byte-identical.
-- Dependencies: Requires honua.gdb_versions (047) and the current-schema `features` table (001).

-- Branch overlay: one row per (version_id, layer_id, objectid) the version shadows. The row carries the
-- branch's current image (operation/geometry/attributes) and, captured on the version's FIRST touch of
-- the (layer_id, objectid), the then-current DEFAULT base image (base_geometry/base_attributes). The
-- base image feeds reconcile conflict classification (base vs current-DEFAULT vs current-branch) and
-- #1287's BaseStateJson. operation mirrors honua.feature_changes (1=insert, 2=update, 3=delete).
CREATE TABLE IF NOT EXISTS honua.version_edits (
    version_id UUID NOT NULL REFERENCES honua.gdb_versions(version_id) ON DELETE CASCADE,
    layer_id INT NOT NULL,
    objectid BIGINT NOT NULL,
    -- Branch operation against DEFAULT: 1=insert, 2=update, 3=delete (matches honua.feature_changes).
    operation SMALLINT NOT NULL,
    -- Branch row image. NULL geometry/attributes are valid (geometryless features, delete rows).
    geometry GEOMETRY,
    attributes JSONB,
    -- DEFAULT base image captured on first branch-touch of this (layer_id, objectid). NULL when the
    -- branch row is a pure insert (no pre-existing DEFAULT row) or before first-touch capture runs.
    base_geometry GEOMETRY,
    base_attributes JSONB,
    -- Branch generation at which this overlay row was last written (from honua.sync_generation).
    branch_gen BIGINT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    modified_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (version_id, layer_id, objectid),
    CONSTRAINT version_edits_valid_operation CHECK(operation IN (1, 2, 3))
);

-- Covering btree for the overlay read anti-join / shadowed-OID lookup and per-version delta scans.
CREATE INDEX IF NOT EXISTS idx_version_edits_version_layer_objectid
    ON honua.version_edits(version_id, layer_id, objectid);

-- Spatial index over the overlay geometry so versioned spatial reads can use a GIST scan like DEFAULT.
CREATE INDEX IF NOT EXISTS idx_version_edits_geometry
    ON honua.version_edits USING GIST(geometry);

-- Partial index over delete rows: the overlay read subtracts shadowed OIDs and never emits delete rows,
-- and reconcile/post enumerate net deletes; both benefit from a delete-only partial index.
CREATE INDEX IF NOT EXISTS idx_version_edits_deletes
    ON honua.version_edits(version_id, layer_id, objectid)
    WHERE operation = 3;

COMMENT ON TABLE honua.version_edits IS
    'Branch-versioning overlay: branch row values overlaid onto DEFAULT; inert for DEFAULT (#1272, ADR-0051)';
COMMENT ON COLUMN honua.version_edits.version_id IS 'Owning branch version (honua.gdb_versions)';
COMMENT ON COLUMN honua.version_edits.layer_id IS 'Storage layer of the shadowed feature';
COMMENT ON COLUMN honua.version_edits.objectid IS 'Object id of the shadowed feature';
COMMENT ON COLUMN honua.version_edits.operation IS 'Branch operation against DEFAULT: 1=insert, 2=update, 3=delete';
COMMENT ON COLUMN honua.version_edits.geometry IS 'Branch geometry image; NULL for geometryless or delete rows';
COMMENT ON COLUMN honua.version_edits.attributes IS 'Branch attribute image (JSONB); NULL for delete rows';
COMMENT ON COLUMN honua.version_edits.base_geometry IS 'DEFAULT geometry captured on first branch-touch (feeds reconcile/BaseStateJson)';
COMMENT ON COLUMN honua.version_edits.base_attributes IS 'DEFAULT attributes captured on first branch-touch (feeds reconcile/BaseStateJson)';
COMMENT ON COLUMN honua.version_edits.branch_gen IS 'sync_generation at which this overlay row was last written';

-- Version-aware change tracking for the overlay. The DEFAULT change-tracking trigger (012/048) is on
-- `features` and never fires for overlay writes, so a sibling trigger records each overlay mutation into
-- honua.feature_changes tagged with the producing version. The version_id is taken from the overlay row
-- itself (authoritative), independent of the transaction GUC, so a tagged version delta exists for
-- reconcile/post even though DEFAULT data was never touched. operation maps INSERT/UPDATE of the overlay
-- row to the row's branch operation; a hard DELETE of an overlay row (e.g. on version drop via cascade)
-- is not change-tracked. This is inert for DEFAULT: nothing writes honua.version_edits on the DEFAULT path.
CREATE OR REPLACE FUNCTION honua.track_version_edits()
RETURNS TRIGGER AS $$
DECLARE
    gen BIGINT;
BEGIN
    -- Overlay-row removals (version drop cascade) are not feature changes; skip them.
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;

    gen := nextval('honua.sync_generation');

    INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, version_id)
    VALUES (gen, NEW.layer_id, NEW.objectid, NEW.operation, NEW.version_id);

    -- Track the branch generation produced by this edit on the overlay row.
    NEW.branch_gen := gen;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trigger_track_version_edits ON honua.version_edits;
CREATE TRIGGER trigger_track_version_edits
    BEFORE INSERT OR UPDATE ON honua.version_edits
    FOR EACH ROW
    EXECUTE FUNCTION honua.track_version_edits();

COMMENT ON FUNCTION honua.track_version_edits() IS
    'Records branch overlay mutations into honua.feature_changes tagged with the producing version (#1272, ADR-0051)';
