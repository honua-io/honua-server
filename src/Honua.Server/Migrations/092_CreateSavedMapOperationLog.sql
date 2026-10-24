-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Restart-durable saved-map collaboration operation log and checkpoint cursor (#3067).
-- The head row is the per-map serialization point for cursor assignment. Keeping the
-- checkpoint cursor on that same row gives operation replay and checkpoint replay one
-- durability/locking story instead of pairing a durable log with process-local state.
CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.saved_map_operation_log_heads (
    map_id              text PRIMARY KEY,
    head_cursor         bigint NOT NULL DEFAULT 0 CHECK (head_cursor >= 0),
    checkpoint_cursor   bigint NOT NULL DEFAULT 0 CHECK (checkpoint_cursor >= 0),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_saved_map_operation_checkpoint_not_ahead
        CHECK (checkpoint_cursor <= head_cursor)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.saved_map_operations (
    map_id              text NOT NULL,
    server_cursor       bigint NOT NULL CHECK (server_cursor > 0),
    operation_id        text NOT NULL,
    actor_id            text NOT NULL,
    base_cursor         bigint NOT NULL CHECK (base_cursor >= 0),
    operation_kind      text NOT NULL,
    payload             jsonb NOT NULL DEFAULT '{}'::jsonb,
    idempotency_key     text NULL,
    accepted_at         timestamptz NOT NULL,
    PRIMARY KEY (map_id, server_cursor),
    CONSTRAINT fk_saved_map_operations_head
        FOREIGN KEY (map_id)
        REFERENCES $HonuaSchema$.saved_map_operation_log_heads (map_id)
        ON DELETE CASCADE,
    CONSTRAINT uq_saved_map_operations_operation_id
        UNIQUE (map_id, operation_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_saved_map_operations_idempotency_key
    ON $HonuaSchema$.saved_map_operations (map_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_saved_map_operations_replay
    ON $HonuaSchema$.saved_map_operations (map_id, server_cursor);
