-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.catalog_activation_watches (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    environment TEXT COLLATE "C" NOT NULL,
    revision BIGINT NOT NULL CHECK (revision > 0),
    expected_etag TEXT NOT NULL,
    previous_revision BIGINT NOT NULL CHECK (previous_revision > 0),
    previous_document JSONB NOT NULL,
    activated_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    state TEXT NOT NULL DEFAULT 'watching'
        CHECK (state IN ('watching', 'superseded', 'expired', 'rolled_back', 'rollback')),
    rollback_revision BIGINT NULL,
    rollback_change_id TEXT NULL,
    rollback_reason TEXT NULL,
    purge_completed_at TIMESTAMPTZ NULL,
    CHECK (state <> 'rolled_back' OR
        (rollback_revision IS NOT NULL AND rollback_revision > revision
         AND rollback_change_id IS NOT NULL AND rollback_reason IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS idx_catalog_activation_watches_environment
    ON $HonuaSchema$.catalog_activation_watches (environment, id DESC);
CREATE INDEX IF NOT EXISTS idx_catalog_activation_watches_pending_purge
    ON $HonuaSchema$.catalog_activation_watches (environment, id)
    WHERE state = 'rolled_back' AND purge_completed_at IS NULL;

CREATE OR REPLACE FUNCTION $HonuaSchema$.record_catalog_activation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.revision = OLD.revision AND NEW.etag = OLD.etag THEN
        RETURN NULL;
    END IF;

    UPDATE $HonuaSchema$.catalog_activation_watches SET state = 'superseded'
        WHERE environment = NEW.environment AND state = 'watching';

    INSERT INTO $HonuaSchema$.catalog_activation_watches
        (environment, revision, expected_etag, previous_revision, previous_document)
        SELECT NEW.environment, NEW.revision, NEW.etag, OLD.revision, document
        FROM $HonuaSchema$.metadata_v2_snapshots
        WHERE environment = OLD.environment AND revision = OLD.revision;

    -- Keep rollback material independent of snapshot pruning; unacknowledged purges survive retention.
    DELETE FROM $HonuaSchema$.catalog_activation_watches
        WHERE environment = NEW.environment
          AND NOT (state = 'rolled_back' AND purge_completed_at IS NULL)
          AND id NOT IN (
              SELECT id FROM $HonuaSchema$.catalog_activation_watches
              WHERE environment = NEW.environment ORDER BY id DESC LIMIT 32);
    RETURN NULL;
END;
$$;

CREATE OR REPLACE TRIGGER metadata_current_activation_watch
    AFTER UPDATE ON $HonuaSchema$.metadata_v2_current FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.record_catalog_activation();
