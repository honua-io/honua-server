-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 067_SerializeChangeLogGenerationAllocation.sql
-- Description: Closes a silent replica-sync data-loss window (#2062). The DEFAULT and version
--              change-tracking triggers assign each change a generation via
--              nextval('honua.sync_generation'). A Postgres sequence is non-transactional:
--              nextval advances last_value immediately and globally and is never rolled back,
--              while the corresponding honua.feature_changes row only becomes visible when ITS
--              transaction commits. Under concurrent editing, transactions commit out of
--              generation order, so a change with a LOWER generation can commit AFTER a higher
--              one. The replica cursor previously read the sequence last_value as its watermark
--              and the next delta selected strictly generation > cursor, so any lower-generation
--              change that committed after the watermark was read was skipped on the replica
--              forever (and was invisible to upload/apply conflict detection, silently clobbering
--              committed server edits).
--
--              This migration mirrors the proven honua.fieldcollection_changes pattern
--              (PostgresFieldCollectionSyncStore): take a transaction-scoped generation-allocation
--              advisory lock IMMEDIATELY before nextval and hold it through commit, so for every
--              honua.feature_changes writer generation order equals commit order. Combined with the
--              cursor read switching from the sequence last_value to
--              COALESCE(MAX(generation),0) FROM honua.feature_changes (PostgresChangeTracker),
--              the committed-MAX watermark can never skip an in-flight lower generation.
--
--              The lock is keyed by a single feature-changes namespace so the DEFAULT trigger and
--              the version-overlay trigger serialize against each other (both write the same table
--              and share the sequence). It is acquired inside the trigger, so it is held only for
--              the brief window between generation allocation and commit and never crosses a
--              user-visible lock boundary. Every other line of both functions is byte-identical to
--              050_AddChangeLogAttribution.sql, so attribution/version behavior is unchanged.
-- Dependencies: Requires honua.feature_changes, honua.sync_generation, honua.track_feature_changes,
--               and honua.track_version_edits (012, 048, 049, 050).

-- Generation-allocation advisory lock namespace for honua.feature_changes writers.
-- Distinct from the FieldCollection generation lock (0x0894FC60) so the two change-log
-- families never alias inside the advisory-lock space. The literal mirrors ticket #2062.

CREATE OR REPLACE FUNCTION honua.track_feature_changes()
RETURNS TRIGGER AS $$
DECLARE
    gen BIGINT;
    lid INT;
    oid BIGINT;
    op SMALLINT;
    ver TEXT;
    ver_uuid UUID;
    attr_actor TEXT;
    attr_source SMALLINT;
    attr_operation TEXT;
    attr_source_id TEXT;
    raw_source TEXT;
BEGIN
    -- Hold the generation-allocation lock from nextval through commit so generation order
    -- equals commit order for honua.feature_changes writers (#2062). Released on commit/rollback.
    PERFORM pg_advisory_xact_lock(144047712, 0); -- 0x0894FE60
    gen := nextval('honua.sync_generation');

    IF TG_OP = 'INSERT' THEN
        lid := NEW.layer_id;
        oid := NEW.objectid;
        op := 1;
    ELSIF TG_OP = 'UPDATE' THEN
        lid := NEW.layer_id;
        oid := NEW.objectid;
        op := 2;
    ELSIF TG_OP = 'DELETE' THEN
        lid := OLD.layer_id;
        oid := OLD.objectid;
        op := 3;
    END IF;

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
        (generation, layer_id, objectid, operation, version_id, actor, source, operation_name, source_id)
    VALUES
        (gen, lid, oid, op, ver_uuid, attr_actor, attr_source, attr_operation, attr_source_id);

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION honua.track_version_edits()
RETURNS TRIGGER AS $$
DECLARE
    gen BIGINT;
    attr_actor TEXT;
    attr_source SMALLINT;
    attr_operation TEXT;
    attr_source_id TEXT;
    raw_source TEXT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;

    -- Same generation-allocation lock as the DEFAULT trigger so DEFAULT and version-overlay
    -- writers share one serialization point and the committed-MAX watermark is gap-free (#2062).
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
        (generation, layer_id, objectid, operation, version_id, actor, source, operation_name, source_id)
    VALUES
        (gen, NEW.layer_id, NEW.objectid, NEW.operation, NEW.version_id,
         attr_actor, attr_source, attr_operation, attr_source_id);

    NEW.branch_gen := gen;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION honua.track_feature_changes() IS
    'Records DEFAULT feature changes with version + attribution; serializes generation allocation so generation order = commit order (#1166 slice 4, #1272, #2062)';
COMMENT ON FUNCTION honua.track_version_edits() IS
    'Records branch overlay mutations with version + attribution; serializes generation allocation so generation order = commit order (#1166 slice 4, #1272, ADR-0051, #2062)';
