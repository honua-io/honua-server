-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Committed gRPC ApplyEdits idempotency receipt (#5512). The row is inserted in the same
-- transaction as the feature edit, so a Redis reservation that lapses after commit cannot
-- let another replica apply that edit again. The scope is a server hash, not the client key.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.grpc_apply_edits_receipt (
    scope_key text PRIMARY KEY,
    receipt bytea NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT grpc_apply_edits_receipt_scope_length CHECK (char_length(scope_key) BETWEEN 1 AND 128),
    CONSTRAINT grpc_apply_edits_receipt_payload_size CHECK (octet_length(receipt) BETWEEN 1 AND 16777216)
);

CREATE INDEX IF NOT EXISTS ix_grpc_apply_edits_receipt_expires
    ON $HonuaSchema$.grpc_apply_edits_receipt (expires_at);

COMMENT ON TABLE $HonuaSchema$.grpc_apply_edits_receipt IS
    'Committed gRPC ApplyEdits idempotency receipt. Written in the same transaction as the edit so a Redis reservation lapse cannot replay it.';
