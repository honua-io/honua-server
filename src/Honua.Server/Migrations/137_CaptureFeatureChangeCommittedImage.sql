-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Migration: 137_CaptureFeatureChangeCommittedImage.sql
-- Description: Records the feature row committed by each DEFAULT or branch change. Alert
--              evaluation must judge a generation against that image. Reading the live row
--              collapses a below/above/below burst onto the final snapshot (#3864).
--              NULL committed_attributes means the image was not captured (deletes, and rows
--              written before this migration). An empty object means a captured image.
-- Dependencies: Requires honua.feature_changes and the change-tracking functions from 121 and 105.

ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS committed_geometry geometry;
ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS committed_attributes JSONB;
ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS committed_created_at TIMESTAMPTZ;
ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS committed_updated_at TIMESTAMPTZ;

COMMENT ON COLUMN honua.feature_changes.committed_geometry IS
    'Geometry committed by this insert or update; NULL for deletes and for changes recorded before capture (#3864)';
COMMENT ON COLUMN honua.feature_changes.committed_attributes IS
    'Attributes committed by this insert or update; NULL when not captured so readers do not invent a row (#3864)';
COMMENT ON COLUMN honua.feature_changes.committed_created_at IS
    'created_at committed by this insert or update; NULL when not captured (#3864)';
COMMENT ON COLUMN honua.feature_changes.committed_updated_at IS
    'updated_at or branch modified_at committed by this insert or update; NULL when not captured (#3864)';

-- Migration 121's contract, plus the NEW row image for inserts and updates.
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
    committed_geometry geometry;
    committed_attributes JSONB;
    committed_created_at TIMESTAMPTZ;
    committed_updated_at TIMESTAMPTZ;
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

    IF TG_OP <> 'DELETE' THEN
        committed_geometry := NEW.geometry::geometry;
        committed_attributes := COALESCE(NEW.attributes, '{}'::jsonb);
        committed_created_at := NEW.created_at;
        committed_updated_at := NEW.updated_at;
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
         pre_created_at, pre_updated_at, committed_geometry, committed_attributes,
         committed_created_at, committed_updated_at)
    VALUES
        (gen, lid, oid, public_oid, op, ver_uuid,
         attr_actor, attr_source, attr_operation, attr_source_id, before_geometry, before_attributes,
         before_created_at, before_updated_at, committed_geometry, committed_attributes,
         committed_created_at, committed_updated_at);

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Migration 105's branch contract, plus the overlay image committed by this generation.
CREATE OR REPLACE FUNCTION honua.track_version_edits()
RETURNS TRIGGER AS $$
DECLARE
    gen BIGINT;
    public_oid BIGINT;
    previous_public_oid BIGINT;
    row_attributes JSONB;
    attr_actor TEXT;
    attr_source SMALLINT;
    attr_operation TEXT;
    attr_source_id TEXT;
    raw_source TEXT;
    committed_geometry geometry;
    committed_attributes JSONB;
    committed_created_at TIMESTAMPTZ;
    committed_updated_at TIMESTAMPTZ;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;

    row_attributes := COALESCE(NEW.attributes, NEW.base_attributes);
    IF TG_OP = 'UPDATE'
       AND NEW.operation = 3
       AND row_attributes IS NULL THEN
        row_attributes := COALESCE(OLD.attributes, OLD.base_attributes);
    END IF;
    public_oid := honua.resolve_feature_public_objectid(
        NEW.layer_id,
        NEW.objectid,
        row_attributes);
    IF TG_OP = 'UPDATE' THEN
        previous_public_oid := honua.resolve_feature_public_objectid(
            OLD.layer_id,
            OLD.objectid,
            COALESCE(OLD.attributes, OLD.base_attributes));
    ELSIF NEW.operation <> 1 AND NEW.base_attributes IS NOT NULL THEN
        previous_public_oid := honua.resolve_feature_public_objectid(
            NEW.layer_id,
            NEW.objectid,
            NEW.base_attributes);
    END IF;

    IF previous_public_oid IS NOT NULL
       AND previous_public_oid IS DISTINCT FROM public_oid THEN
        RAISE EXCEPTION
            'Cannot change the public id.primary for layer % object % from % to %',
            NEW.layer_id, NEW.objectid, previous_public_oid, public_oid
            USING ERRCODE = '23514',
                  HINT = 'The protocol-facing primary object identifier is immutable.';
    END IF;

    IF NEW.operation <> 3 THEN
        committed_geometry := NEW.geometry;
        committed_attributes := COALESCE(NEW.attributes, '{}'::jsonb);
        committed_created_at := NEW.created_at;
        committed_updated_at := NEW.modified_at;
    END IF;

    PERFORM pg_advisory_xact_lock(144047712, 0); -- 0x0894FE60
    gen := nextval('honua.sync_generation');

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
         actor, source, operation_name, source_id, committed_geometry, committed_attributes,
         committed_created_at, committed_updated_at)
    VALUES
        (gen, NEW.layer_id, NEW.objectid, public_oid, NEW.operation, NEW.version_id,
         attr_actor, attr_source, attr_operation, attr_source_id, committed_geometry, committed_attributes,
         committed_created_at, committed_updated_at);

    NEW.branch_gen := gen;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION honua.track_feature_changes() IS
    'Records DEFAULT feature changes with the committed row image, pre-change image, public id, version, and attribution (#3864, #4879)';
COMMENT ON FUNCTION honua.track_version_edits() IS
    'Records branch overlay mutations with the committed row image, public id, version, and attribution (#3864, #2430)';
