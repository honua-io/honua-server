-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 121_CaptureFeatureChangePreImage.sql
-- Description: Records the row image before each DEFAULT-version update or delete in the replication
--              change log. A scoped or visibility-filtered replica download evaluates the replica
--              scope and the caller's row visibility against the image before a window's first change
--              and against the live row, so a row updated into the scope is delivered as an add, a
--              row that left it (or was deleted) as a delete only when the client could hold it, and
--              deletes are re-authorised under row visibility instead of being withheld (#4879).
-- Dependencies: Requires honua.feature_changes and the change-tracking contract from migration 105.

-- NULL for inserts, for branch-overlay changes and for every change recorded before this migration;
-- readers treat a NULL image as "prior state unknown" and keep their current-state classification.
ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS pre_geometry geometry;
ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS pre_attributes JSONB;
ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS pre_created_at TIMESTAMPTZ;
ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS pre_updated_at TIMESTAMPTZ;

COMMENT ON COLUMN honua.feature_changes.pre_geometry IS
    'Row geometry before this update/delete (DEFAULT version); NULL when not captured (#4879)';
COMMENT ON COLUMN honua.feature_changes.pre_attributes IS
    'Row attributes before this update/delete (DEFAULT version); NULL when not captured (#4879)';
COMMENT ON COLUMN honua.feature_changes.pre_created_at IS
    'Row creation timestamp before this update/delete (DEFAULT version); NULL when not captured (#4879)';
COMMENT ON COLUMN honua.feature_changes.pre_updated_at IS
    'Row update timestamp before this update/delete (DEFAULT version); NULL when not captured (#4879)';

-- Migration 105's contract, plus the pre-change image of OLD for updates and deletes. The attributes
-- image is never NULL for a captured change so that a NULL reliably means "not captured".
CREATE OR REPLACE FUNCTION honua.track_feature_changes()
RETURNS TRIGGER AS $$
DECLARE
    gen BIGINT;
    lid INT;
    oid BIGINT;
    public_oid BIGINT;
    previous_public_oid BIGINT;
    row_attributes JSONB;
    op SMALLINT;
    ver TEXT;
    ver_uuid UUID;
    attr_actor TEXT;
    attr_source SMALLINT;
    attr_operation TEXT;
    attr_source_id TEXT;
    raw_source TEXT;
    before_geometry geometry;
    before_attributes JSONB;
    before_created_at TIMESTAMPTZ;
    before_updated_at TIMESTAMPTZ;
BEGIN
    IF TG_OP = 'INSERT' THEN
        lid := NEW.layer_id;
        oid := NEW.objectid;
        row_attributes := NEW.attributes;
        op := 1;
    ELSIF TG_OP = 'UPDATE' THEN
        lid := NEW.layer_id;
        oid := NEW.objectid;
        row_attributes := NEW.attributes;
        op := 2;
    ELSIF TG_OP = 'DELETE' THEN
        lid := OLD.layer_id;
        oid := OLD.objectid;
        row_attributes := OLD.attributes;
        op := 3;
    END IF;

    IF TG_OP <> 'INSERT' THEN
        before_geometry := OLD.geometry::geometry;
        before_attributes := COALESCE(OLD.attributes, '{}'::jsonb);
        before_created_at := OLD.created_at;
        before_updated_at := OLD.updated_at;
    END IF;

    public_oid := honua.resolve_feature_public_objectid(lid, oid, row_attributes);
    IF TG_OP = 'UPDATE' THEN
        previous_public_oid := honua.resolve_feature_public_objectid(
            OLD.layer_id,
            OLD.objectid,
            OLD.attributes);
        IF previous_public_oid IS DISTINCT FROM public_oid THEN
            RAISE EXCEPTION
                'Cannot change the public id.primary for layer % object % from % to %',
                lid, oid, previous_public_oid, public_oid
                USING ERRCODE = '23514',
                      HINT = 'The protocol-facing primary object identifier is immutable.';
        END IF;
    END IF;

    PERFORM pg_advisory_xact_lock(144047712, 0); -- 0x0894FE60
    gen := nextval('honua.sync_generation');

    ver := current_setting('honua.gdb_version', true);
    IF ver IS NULL OR ver = '' THEN
        ver_uuid := NULL;
    ELSE
        ver_uuid := ver::UUID;
    END IF;

    attr_actor := NULLIF(current_setting('honua.temporal_actor', true), '');
    raw_source := NULLIF(current_setting('honua.temporal_source', true), '');
    IF raw_source IS NULL THEN
        attr_source := NULL;
    ELSE
        attr_source := raw_source::SMALLINT;
    END IF;
    attr_operation := NULLIF(current_setting('honua.temporal_operation', true), '');
    attr_source_id := NULLIF(current_setting('honua.temporal_source_id', true), '');

    INSERT INTO honua.feature_changes
        (generation, layer_id, objectid, public_objectid, operation, version_id,
         actor, source, operation_name, source_id, pre_geometry, pre_attributes,
         pre_created_at, pre_updated_at)
    VALUES
        (gen, lid, oid, public_oid, op, ver_uuid,
         attr_actor, attr_source, attr_operation, attr_source_id, before_geometry, before_attributes,
         before_created_at, before_updated_at);

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION honua.track_feature_changes() IS
    'Records DEFAULT feature changes with durable public ids, pre-change images, version + attribution; serializes generation allocation (#1166 slice 4, #1272, #2062, #2430, #4879)';
