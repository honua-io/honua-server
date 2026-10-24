-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 048_AddVersionToChangeLog.sql
-- Description: Adds the version dimension to the change log and makes the change-tracking trigger
--              version-aware (#1272 Track B). A nullable version_id column tags each recorded change
--              with the gdb version that produced it; the trigger reads a transaction-scoped GUC
--              (honua.gdb_version) set by the edit session. When the GUC is unset or empty (the
--              DEFAULT version), version_id stays NULL and the recorded change is byte-identical to
--              the pre-migration behavior, so existing replication/change-tracking and CITE paths are
--              unaffected.
-- Dependencies: Requires honua.feature_changes + honua.track_feature_changes (012) and
--               honua.gdb_versions (047).

ALTER TABLE honua.feature_changes
    ADD COLUMN IF NOT EXISTS version_id UUID;

-- Version-scoped delta scans (extractChanges/reconcile within a version).
CREATE INDEX IF NOT EXISTS idx_feature_changes_version_generation
    ON honua.feature_changes(version_id, generation, layer_id);

-- Version-aware trigger: records the producing version from the transaction-scoped GUC. The GUC is
-- read with the missing_ok=true form so an unset GUC yields NULL (DEFAULT version) without error.
CREATE OR REPLACE FUNCTION honua.track_feature_changes()
RETURNS TRIGGER AS $$
DECLARE
    gen BIGINT;
    lid INT;
    oid BIGINT;
    op SMALLINT;
    ver TEXT;
    ver_uuid UUID;
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

    -- Resolve the producing version from the transaction-scoped GUC. Unset/empty == DEFAULT (NULL).
    ver := current_setting('honua.gdb_version', true);
    IF ver IS NULL OR ver = '' THEN
        ver_uuid := NULL;
    ELSE
        ver_uuid := ver::UUID;
    END IF;

    INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, version_id)
    VALUES (gen, lid, oid, op, ver_uuid);

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN honua.feature_changes.version_id IS
    'Branch version that produced the change; NULL == DEFAULT version (byte-identical to pre-versioning) (#1272)';
