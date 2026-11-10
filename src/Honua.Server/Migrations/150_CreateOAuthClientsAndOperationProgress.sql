-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- OAuth client registry and operation progress (ADR-0082 rows B12 and D1).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.oauth_clients (
    id           UUID PRIMARY KEY,
    client_id    TEXT COLLATE "C" NOT NULL,
    secret_hash  BYTEA,
    created_at   TIMESTAMPTZ NOT NULL,
    expires_at   TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ,
    record       JSON NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_oauth_clients_client_id
    ON $HonuaSchema$.oauth_clients (client_id);

-- payload is text so that a whole-snapshot comparison is an equality on the stored bytes.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.operation_progress (
    operation_id   TEXT COLLATE "C" PRIMARY KEY CHECK (length(operation_id) BETWEEN 1 AND 512),
    operation_type TEXT NOT NULL,
    status         TEXT NOT NULL,
    payload        TEXT NOT NULL,
    expires_at     TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_operation_progress_type
    ON $HonuaSchema$.operation_progress (operation_type);
CREATE INDEX IF NOT EXISTS idx_operation_progress_expires
    ON $HonuaSchema$.operation_progress (expires_at);

COMMENT ON TABLE $HonuaSchema$.oauth_clients IS 'Registered OAuth clients; the secret is stored as a SHA-256 digest and last_used_at overrides its copy in record.';
COMMENT ON TABLE $HonuaSchema$.operation_progress IS 'Latest progress snapshot per operation, as every job family reports it.';
