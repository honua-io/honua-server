-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Durable, caller-scoped receipts make browser-friendly unary upload calls retryable.
-- These tables deliberately have no foreign key to the physical attachment schema:
-- attachments may use a separately configured schema and sessions precede attachments.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.attachment_upload_sessions (
    session_id UUID PRIMARY KEY,
    owner_id TEXT COLLATE "C" NOT NULL CHECK (octet_length(owner_id) BETWEEN 1 AND 256),
    service_id TEXT COLLATE "C" NOT NULL CHECK (octet_length(service_id) BETWEEN 1 AND 256),
    layer_id INTEGER NOT NULL CHECK (layer_id >= 0),
    feature_id BIGINT NOT NULL,
    operation TEXT COLLATE "C" NOT NULL CHECK (operation IN ('unary', 'chunked')),
    idempotency_key TEXT COLLATE "C" NOT NULL CHECK (octet_length(idempotency_key) BETWEEN 1 AND 256),
    request_sha256 BYTEA NOT NULL CHECK (octet_length(request_sha256) = 32),
    filename TEXT NOT NULL CHECK (octet_length(filename) BETWEEN 1 AND 1024),
    content_type TEXT NOT NULL CHECK (octet_length(content_type) BETWEEN 1 AND 256),
    keywords TEXT[] NOT NULL DEFAULT '{}',
    expected_size BIGINT NOT NULL CHECK (expected_size >= 0),
    content_sha256 BYTEA NOT NULL CHECK (octet_length(content_sha256) = 32),
    replace_attachment_id BIGINT CHECK (replace_attachment_id > 0),
    max_chunk_bytes INTEGER NOT NULL CHECK (max_chunk_bytes > 0),
    next_offset_bytes BIGINT NOT NULL DEFAULT 0 CHECK (next_offset_bytes BETWEEN 0 AND expected_size),
    state TEXT COLLATE "C" NOT NULL DEFAULT 'uploading' CHECK (state IN ('uploading', 'committed', 'aborted')),
    committed_attachment JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL CHECK (expires_at > created_at),
    UNIQUE (owner_id, service_id, layer_id, feature_id, operation, idempotency_key),
    CHECK ((state = 'committed' AND next_offset_bytes = expected_size
            AND committed_attachment IS NOT NULL AND jsonb_typeof(committed_attachment) = 'object')
        OR (state <> 'committed' AND committed_attachment IS NULL))
);

CREATE INDEX IF NOT EXISTS idx_attachment_upload_sessions_expiry
    ON $HonuaSchema$.attachment_upload_sessions (expires_at);

-- The provider locks the session row before checking the negotiated chunk bound,
-- appending at next_offset_bytes, or comparing an earlier chunk's retry digest.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.attachment_upload_chunks (
    session_id UUID NOT NULL REFERENCES $HonuaSchema$.attachment_upload_sessions(session_id) ON DELETE CASCADE,
    offset_bytes BIGINT NOT NULL CHECK (offset_bytes >= 0),
    content BYTEA NOT NULL CHECK (octet_length(content) > 0),
    content_sha256 BYTEA NOT NULL CHECK (octet_length(content_sha256) = 32),
    PRIMARY KEY (session_id, offset_bytes)
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.attachment_mutation_receipts (
    owner_id TEXT COLLATE "C" NOT NULL CHECK (octet_length(owner_id) BETWEEN 1 AND 256),
    service_id TEXT COLLATE "C" NOT NULL CHECK (octet_length(service_id) BETWEEN 1 AND 256),
    layer_id INTEGER NOT NULL CHECK (layer_id >= 0),
    feature_id BIGINT NOT NULL,
    operation TEXT COLLATE "C" NOT NULL CHECK (operation = 'delete'),
    idempotency_key TEXT COLLATE "C" NOT NULL CHECK (octet_length(idempotency_key) BETWEEN 1 AND 256),
    request_sha256 BYTEA NOT NULL CHECK (octet_length(request_sha256) = 32),
    attachment_id BIGINT NOT NULL CHECK (attachment_id > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL CHECK (expires_at > created_at),
    PRIMARY KEY (owner_id, service_id, layer_id, feature_id, operation, idempotency_key)
);

CREATE INDEX IF NOT EXISTS idx_attachment_mutation_receipts_expiry
    ON $HonuaSchema$.attachment_mutation_receipts (expires_at);
