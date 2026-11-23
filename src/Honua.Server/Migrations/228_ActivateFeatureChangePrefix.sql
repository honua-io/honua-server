-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Migration: 228_ActivateFeatureChangePrefix.sql
-- Description: Feature-change writers stop serializing on one global lock. Each writer takes the
--              generation lock in SHARED mode before nextval and holds it through commit, so
--              writers on any layer run concurrently and may commit out of generation order.
--              The read horizon returns the published committed prefix instead of
--              MAX(generation). The publisher reads the allocated sequence value, then
--              records which transactions hold the shared writer lock. It publishes that
--              value once none of those transactions still holds the lock. Only feature writers
--              can hold back the prefix, never unrelated transactions or other databases, and
--              an idle database publishes in one call.
--              Forward-only (founder ruling 22 Nov: no N-1 reader contract). Function bodies,
--              one added column and a one-time seed of the prefix; nothing is dropped.
-- Dependencies: Requires migrations 189 (writer triggers), 213 (read horizon) and 214 (prefix table).

-- Activate once. Until this transaction commits every writer still holds the generation lock
-- exclusively from nextval through commit, so at most one writer is in flight and its generation
-- is above every committed one: the committed MAX is a complete prefix. Re-running the seed after
-- activation could pass an in-flight lower generation, so the block runs only once.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'honua'
          AND table_name = 'feature_change_prefix'
          AND column_name = 'pending_writers'
    ) THEN
        ALTER TABLE honua.feature_change_prefix ADD COLUMN pending_writers TEXT[];
        UPDATE honua.feature_change_prefix
        SET published = GREATEST(
                published,
                (SELECT COALESCE(MAX(generation), 0)::BIGINT FROM honua.feature_changes)),
            pending = NULL,
            fence = NULL
        WHERE singleton;
    END IF;
END
$$;

-- Virtual transaction ids of granted holders of the shared writer lock in this database.
CREATE OR REPLACE FUNCTION honua.feature_change_writers()
RETURNS TEXT[]
LANGUAGE sql
VOLATILE
AS $$
    SELECT COALESCE(array_agg(locks.virtualtransaction), ARRAY[]::TEXT[])
    FROM pg_locks AS locks
    WHERE locks.locktype = 'advisory'
      AND locks.database = (SELECT oid FROM pg_database WHERE datname = current_database())
      AND locks.classid = 144047712
      AND locks.objid = 0
      AND locks.objsubid = 2
      AND locks.granted
$$;

-- One call per fresh READ COMMITTED transaction, outside feature writes and cursor reads.
-- Publishers compete only with each other; a busy publisher never blocks a feature writer.
CREATE OR REPLACE FUNCTION honua.publish_feature_change_prefix()
RETURNS BIGINT
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    current_state honua.feature_change_prefix%ROWTYPE;
    allocated BIGINT;
    writers TEXT[];
    settled BOOLEAN := FALSE;
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed'
       OR pg_current_xact_id_if_assigned() IS NOT NULL THEN
        RAISE EXCEPTION 'Feature-change publisher requires a fresh READ COMMITTED transaction'
            USING ERRCODE = '25001';
    END IF;
    IF current_setting('transaction_read_only') = 'on' THEN
        RAISE EXCEPTION 'Feature-change publisher requires a writable transaction'
            USING ERRCODE = '25006';
    END IF;
    -- A cached sequence can hand out a value below one another session already read.
    IF NOT EXISTS (
        SELECT 1 FROM pg_sequence
        WHERE seqrelid = 'honua.sync_generation'::regclass
          AND seqcache = 1 AND seqincrement = 1 AND NOT seqcycle AND seqmin >= 1
    ) THEN
        RAISE EXCEPTION 'Feature-change publisher requires a monotonic CACHE 1 sequence'
            USING ERRCODE = '55000';
    END IF;
    IF NOT pg_try_advisory_xact_lock(144047712, 2) THEN
        RETURN (SELECT published FROM honua.feature_change_prefix WHERE singleton);
    END IF;

    SELECT * INTO STRICT current_state FROM honua.feature_change_prefix WHERE singleton;
    IF current_state.pending IS NOT NULL THEN
        IF current_state.pending_writers IS NULL THEN
            -- Fenced by migration 214's rule; finish it that way.
            settled := pg_snapshot_xmin(pg_current_snapshot()) >= current_state.fence;
        ELSE
            settled := NOT (honua.feature_change_writers() && current_state.pending_writers);
        END IF;
    END IF;
    IF settled THEN
        current_state.published := current_state.pending;
        current_state.pending := NULL;
        current_state.fence := NULL;
        current_state.pending_writers := NULL;
    END IF;
    IF current_state.pending IS NULL THEN
        -- Read the sequence BEFORE the lock table. A writer takes the shared lock before nextval
        -- and keeps it through commit or rollback, so every allocation at or below this value
        -- belongs to a transaction that has finished or appears in the lock table read next.
        SELECT CASE WHEN is_called THEN last_value ELSE 0 END
        INTO allocated FROM honua.sync_generation;
        IF allocated > current_state.published THEN
            writers := honua.feature_change_writers();
            IF cardinality(writers) = 0 THEN
                current_state.published := allocated;
            ELSE
                -- The fence keeps migration 214's CHECK and rule valid: the sequence was read
                -- before this transaction took an id.
                current_state.pending := allocated;
                current_state.fence := pg_current_xact_id();
                current_state.pending_writers := writers;
            END IF;
        END IF;
    END IF;
    UPDATE honua.feature_change_prefix
    SET published = current_state.published,
        pending = current_state.pending,
        fence = current_state.fence,
        pending_writers = current_state.pending_writers
    WHERE singleton
      AND (published, pending, fence, pending_writers)
          IS DISTINCT FROM (current_state.published, current_state.pending, current_state.fence,
                            current_state.pending_writers);
    RETURN current_state.published;
END;
$$;

CREATE OR REPLACE FUNCTION honua.feature_change_read_horizon()
RETURNS BIGINT
LANGUAGE sql
STABLE
AS $$
    SELECT published FROM honua.feature_change_prefix WHERE singleton
$$;

-- Migration 189's DEFAULT writer with the generation lock taken in SHARED mode.
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

    -- The shared lock must be held before nextval; the publisher fences on its holders.
    PERFORM pg_current_xact_id();
    PERFORM pg_advisory_xact_lock_shared(144047712, 0); -- 0x0894FE60
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

-- Migration 189's branch-overlay writer with the generation lock taken in SHARED mode.
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

    -- The shared lock must be held before nextval; the publisher fences on its holders.
    PERFORM pg_current_xact_id();
    PERFORM pg_advisory_xact_lock_shared(144047712, 0); -- 0x0894FE60
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
    'Records DEFAULT feature changes under the shared generation lock';
COMMENT ON FUNCTION honua.track_version_edits() IS
    'Records branch overlay changes under the shared generation lock';
COMMENT ON FUNCTION honua.feature_change_read_horizon() IS
    'Committed feature-change prefix published by honua.publish_feature_change_prefix()';
