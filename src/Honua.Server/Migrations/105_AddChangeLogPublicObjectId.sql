-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 105_AddChangeLogPublicObjectId.sql
-- Description: Persists the protocol-facing object id beside the internal feature id in the
--              replication change log. A deleted row can no longer be resolved through the live
--              feature reader, so replica uploads using a custom id.primary otherwise miss the
--              delete forever and repeatedly retry an unguardable edit (#2430).
-- Dependencies: Requires honua.layers, honua.feature_changes, honua.version_edits, and the
--               serialized attribution-aware tracking functions (001, 012, 049, 050, 067).

ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS public_objectid BIGINT;

-- Historical ordinary-id rows continue to match through objectid. A custom-id delete that predates
-- this migration cannot be reconstructed after its source row is gone. Replicas whose cursors span
-- one of those irrecoverable deletes are therefore invalidated: their next sync fails as not-found
-- and the client must create a fresh replica rather than silently miss the delete or retry an
-- unguardable upload forever. Replicas on ordinary-id layers and custom-id layers with no historical
-- delete are preserved.
DELETE FROM honua.replicas AS replicas
WHERE EXISTS (
    SELECT 1
    FROM honua.feature_changes AS changes
    INNER JOIN honua.layers AS layers
        ON layers.layer_id = changes.layer_id
    WHERE changes.generation > replicas.last_sync_generation
      AND changes.layer_id = ANY(replicas.layer_ids)
      AND lower(COALESCE(NULLIF(layers.primary_key_column, ''), 'objectid')) <> 'objectid'
    GROUP BY changes.layer_id, changes.objectid
    HAVING (array_agg(changes.operation ORDER BY changes.generation, changes.change_id))[1] <> 1
       AND (array_agg(changes.operation ORDER BY changes.generation DESC, changes.change_id DESC))[1] = 3
       AND (array_agg(changes.public_objectid ORDER BY changes.generation DESC, changes.change_id DESC))[1] IS NULL
);

CREATE INDEX IF NOT EXISTS idx_feature_changes_layer_public_objectid
    ON honua.feature_changes(layer_id, public_objectid, generation);

COMMENT ON COLUMN honua.feature_changes.public_objectid IS
    'Protocol-facing id.primary captured at change time; remains available after the feature row is deleted (#2430)';

-- Resolve the configured public identity from the row image while failing safely to the internal
-- objectid. Layer metadata and JSON keys are compared case-insensitively because protocol field
-- names are case-insensitive even though JSONB lookup is not.
CREATE OR REPLACE FUNCTION honua.resolve_feature_public_objectid(
    target_layer_id INT,
    storage_objectid BIGINT,
    row_attributes JSONB)
RETURNS BIGINT AS $$
DECLARE
    primary_id_field TEXT;
    raw_public_objectid TEXT;
    resolved_public_objectid BIGINT;
BEGIN
    SELECT NULLIF(primary_key_column, '')
    INTO primary_id_field
    FROM honua.layers
    WHERE layer_id = target_layer_id;

    IF primary_id_field IS NULL
       OR lower(primary_id_field) = 'objectid'
       OR row_attributes IS NULL
       OR jsonb_typeof(row_attributes) <> 'object' THEN
        RETURN storage_objectid;
    END IF;

    raw_public_objectid := row_attributes ->> primary_id_field;
    IF raw_public_objectid IS NULL THEN
        SELECT value
        INTO raw_public_objectid
        FROM jsonb_each_text(row_attributes)
        WHERE lower(key) = lower(primary_id_field)
        LIMIT 1;
    END IF;

    IF raw_public_objectid IS NULL OR raw_public_objectid !~ '^[+-]?[0-9]+$' THEN
        RETURN storage_objectid;
    END IF;

    BEGIN
        resolved_public_objectid := raw_public_objectid::BIGINT;
    EXCEPTION
        WHEN invalid_text_representation OR numeric_value_out_of_range THEN
            RETURN storage_objectid;
    END;

    RETURN resolved_public_objectid;
END;
$$ LANGUAGE plpgsql STABLE;

COMMENT ON FUNCTION honua.resolve_feature_public_objectid(INT, BIGINT, JSONB) IS
    'Maps a feature row image to its configured public id.primary, falling back to storage objectid (#2430)';

-- Preserve the lock, version, and attribution behavior from migration 067 while adding the durable
-- public identity captured from NEW for inserts/updates and OLD for deletes.
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
         actor, source, operation_name, source_id)
    VALUES
        (gen, lid, oid, public_oid, op, ver_uuid,
         attr_actor, attr_source, attr_operation, attr_source_id);

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Branch delete rows may carry their last row image in base_attributes, so use it when the current
-- overlay attributes are NULL. The rest remains byte-for-byte equivalent to migration 067.
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
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;

    row_attributes := COALESCE(NEW.attributes, NEW.base_attributes);
    IF TG_OP = 'UPDATE'
       AND NEW.operation = 3
       AND row_attributes IS NULL THEN
        -- A branch-created row has no DEFAULT base image. Its delete overlay clears NEW.attributes,
        -- so retain the public identity from the branch row being replaced.
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
         actor, source, operation_name, source_id)
    VALUES
        (gen, NEW.layer_id, NEW.objectid, public_oid, NEW.operation, NEW.version_id,
         attr_actor, attr_source, attr_operation, attr_source_id);

    NEW.branch_gen := gen;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION honua.track_feature_changes() IS
    'Records DEFAULT feature changes with durable public ids, version + attribution; serializes generation allocation (#1166 slice 4, #1272, #2062, #2430)';
COMMENT ON FUNCTION honua.track_version_edits() IS
    'Records branch overlay mutations with durable public ids, version + attribution; serializes generation allocation (#1166 slice 4, #1272, ADR-0051, #2062, #2430)';
