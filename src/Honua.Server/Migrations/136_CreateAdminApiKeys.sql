-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Shared admin API key registry for multi-instance hosts that run without Redis (AWS Lambda,
-- Deployment:Mode=MultiNode) (#6487). A per-process dictionary leaves a key minted on one
-- instance unknown to the others. Only the SHA-256 of the key material is stored.
-- Expand-only: a new table; nothing existing is altered.
CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.admin_api_keys (
    id UUID PRIMARY KEY,
    name TEXT NOT NULL,
    key_prefix TEXT NOT NULL,
    key_hash BYTEA NOT NULL,
    permissions TEXT[] NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ,
    rotated_at TIMESTAMPTZ,
    revoked_at TIMESTAMPTZ,
    created_by TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_admin_api_keys_key_hash
    ON $HonuaSchema$.admin_api_keys (key_hash);
