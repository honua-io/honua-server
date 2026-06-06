-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 050_AddChangeLogAttribution.sql
-- Description: Adds actor/source/operation attribution to the change log (temporal history slice 4 of
--              honua-server#1166). Four nullable columns tag each recorded change with who/what produced
--              it; the change-tracking triggers read transaction-scoped GUCs (honua.temporal_actor,
--              honua.temporal_source, honua.temporal_operation, honua.temporal_source_id) set by the
--              edit/import/job/release path. When the GUCs are unset the columns stay NULL and the
--              recorded change is byte-identical to the pre-migration behavior, so existing
--              replication/change-tracking and CITE paths are unaffected. A named-checkpoint registry is
--              added so the checkpoint cursor model can resolve operator-assigned checkpoints.
-- Dependencies: Requires honua.feature_changes + honua.track_feature_changes (012, 048) and
--               honua.track_version_edits (049).

ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS actor TEXT,
    ADD COLUMN IF NOT EXISTS source SMALLINT,
    ADD COLUMN IF NOT EXISTS operation_name TEXT,
    ADD COLUMN IF NOT EXISTS source_id TEXT;

COMMENT ON COLUMN honua.feature_changes.actor IS
    'Principal that produced the change (user/service account); NULL when not supplied (#1166 slice 4)';
COMMENT ON COLUMN honua.feature_changes.source IS
    'Attribution source category: 1=edit,2=import,3=job,4=release,5=replica-sync,6=rollback; NULL=unknown (#1166 slice 4)';
COMMENT ON COLUMN honua.feature_changes.operation_name IS
    'Higher-level operation name (applyEdits/import/rollback/...); NULL when not supplied (#1166 slice 4)';
COMMENT ON COLUMN honua.feature_changes.source_id IS
    'Producing source correlation id (job/edit-session/release/audit id); NULL when not supplied (#1166 slice 4)';

-- Attribution-aware lookup index for the checkpoint resolver (job/edit-session/release -> generation).
CREATE INDEX IF NOT EXISTS idx_feature_changes_source_id
    ON honua.feature_changes(layer_id, source_id, generation)
    WHERE source_id IS NOT NULL;

-- Operator-assigned named checkpoints, resolved by the checkpoint cursor model to a generation.
CREATE TABLE IF NOT EXISTS honua.temporal_checkpoints (
    name TEXT NOT NULL,
    layer_id INT NOT NULL,
    generation BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (layer_id, name)
);

COMMENT ON TABLE honua.temporal_checkpoints IS
    'Operator-assigned named checkpoints resolved to a change-tracker generation (#1166 slice 2)';

-- Attribution-aware DEFAULT change-tracking trigger. Reads the version GUC (048) and the new attribution
-- GUCs; all are read with missing_ok=true so unset GUCs yield NULL and pre-migration behavior is preserved.
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

-- Attribution-aware version-overlay change-tracking trigger (049). The version_id is taken from the
-- overlay row (authoritative); attribution rides the same transaction GUCs.
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
    'Records DEFAULT feature changes with version + attribution from transaction GUCs (#1166 slice 4, #1272)';
COMMENT ON FUNCTION honua.track_version_edits() IS
    'Records branch overlay mutations with version + attribution from transaction GUCs (#1166 slice 4, #1272, ADR-0051)';
