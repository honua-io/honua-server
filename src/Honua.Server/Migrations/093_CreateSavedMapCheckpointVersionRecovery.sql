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
    version_id          uuid NOT NULL UNIQUE,
    created_at          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (map_id, checkpoint_cursor),
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
    ON $HonuaSchema$.saved_map_checkpoint_versions (map_id, checkpoint_cursor DESC);
