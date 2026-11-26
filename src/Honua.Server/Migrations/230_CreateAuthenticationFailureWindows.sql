-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Failed sign-in attempts counted across every server instance. A source (client address partition) is kept only
-- as a SHA-256 digest. A row is written per counted failure, never per request; expires_at is when the counting
-- window ends, or when the refusal ends while one is in force.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.authentication_failure_windows (
    source_digest  BYTEA PRIMARY KEY CHECK (octet_length(source_digest) = 32),
    window_started TIMESTAMPTZ NOT NULL,
    failures       INTEGER NOT NULL CHECK (failures > 0),
    refused_until  TIMESTAMPTZ,
    expires_at     TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_authentication_failure_windows_expires
    ON $HonuaSchema$.authentication_failure_windows (expires_at);
CREATE INDEX IF NOT EXISTS idx_authentication_failure_windows_refused
    ON $HonuaSchema$.authentication_failure_windows (refused_until)
    WHERE refused_until IS NOT NULL;

COMMENT ON TABLE $HonuaSchema$.authentication_failure_windows IS 'Failed credential checks per source digest, shared by all server instances; a refusal outlives a restart.';
