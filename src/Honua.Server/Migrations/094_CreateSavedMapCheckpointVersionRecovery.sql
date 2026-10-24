-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Durable completion record joining an immutable Studio version to the operation cursor it
-- contains (#3067). A version and this association are inserted in one Studio-store transaction.
-- If the process stops after that commit but before saved_map_operation_log_heads advances, the
-- next checkpoint discovers this record and advances without replaying the already-versioned
-- prefix or minting a duplicate immutable version.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.saved_map_checkpoint_versions (
    map_id              text NOT NULL,
    checkpoint_cursor   bigint NOT NULL CHECK (checkpoint_cursor >= 0),
    version_id          uuid PRIMARY KEY,
    created_at          timestamptz NOT NULL DEFAULT now(),
    acknowledged_at     timestamptz NULL,
    CONSTRAINT fk_saved_map_checkpoint_versions_head
        FOREIGN KEY (map_id)
        REFERENCES $HonuaSchema$.saved_map_operation_log_heads (map_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_saved_map_checkpoint_versions_version
        FOREIGN KEY (version_id)
        REFERENCES $HonuaSchema$.studio_content_versions (version_id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_saved_map_checkpoint_versions_recovery
    ON $HonuaSchema$.saved_map_checkpoint_versions (map_id, checkpoint_cursor DESC, created_at DESC)
    WHERE acknowledged_at IS NULL;

-- Advancing the operation-log cursor and acknowledging the immutable version must be one
-- database commit. In particular, an UPDATE at an unchanged cursor (including cursor zero)
-- still fires this trigger: that distinguishes a completion whose cursor write committed from
-- one that still needs recovery, without making a later same-cursor checkpoint reuse a stale
-- version after direct draft edits.
CREATE OR REPLACE FUNCTION $HonuaSchema$.acknowledge_saved_map_checkpoint_versions()
RETURNS trigger
LANGUAGE plpgsql
AS '
BEGIN
    UPDATE $HonuaSchema$.saved_map_checkpoint_versions
    SET acknowledged_at = COALESCE(acknowledged_at, NEW.updated_at)
    WHERE map_id = NEW.map_id
      AND checkpoint_cursor <= NEW.checkpoint_cursor
      AND acknowledged_at IS NULL;
    RETURN NEW;
END;
';

DROP TRIGGER IF EXISTS trg_acknowledge_saved_map_checkpoint_versions
    ON $HonuaSchema$.saved_map_operation_log_heads;

CREATE TRIGGER trg_acknowledge_saved_map_checkpoint_versions
AFTER UPDATE OF checkpoint_cursor ON $HonuaSchema$.saved_map_operation_log_heads
FOR EACH ROW
EXECUTE FUNCTION $HonuaSchema$.acknowledge_saved_map_checkpoint_versions();
