-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Security state with expiry (ADR-0082 group B). Bearer values and client keys are stored only as
-- SHA-256 digests.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.security_tokens (
    kind       TEXT COLLATE "C" NOT NULL,
    token_hash BYTEA NOT NULL CHECK (octet_length(token_hash) = 32),
    payload    BYTEA NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (kind, token_hash)
);
CREATE INDEX IF NOT EXISTS idx_security_tokens_expires
    ON $HonuaSchema$.security_tokens (expires_at);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.token_replay (
    replay_key TEXT COLLATE "C" PRIMARY KEY CHECK (length(replay_key) BETWEEN 1 AND 512),
    marker     TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_token_replay_expires
    ON $HonuaSchema$.token_replay (expires_at);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.idempotency_reservations (
    scope_hash        BYTEA PRIMARY KEY CHECK (octet_length(scope_hash) = 32),
    reservation_token TEXT NOT NULL,
    receipt           BYTEA,
    expires_at        TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_idempotency_reservations_expires
    ON $HonuaSchema$.idempotency_reservations (expires_at);

COMMENT ON TABLE $HonuaSchema$.security_tokens IS 'Sessions, tokens, one-time codes and revocation markers by kind; a one-time value is deleted by the statement that returns it.';
COMMENT ON TABLE $HonuaSchema$.token_replay IS 'Token and assertion identifiers already presented, with the surface or continuation they are bound to.';
COMMENT ON TABLE $HonuaSchema$.idempotency_reservations IS 'Edit idempotency: a reservation has no receipt; a completed request keeps its receipt until expiry.';
