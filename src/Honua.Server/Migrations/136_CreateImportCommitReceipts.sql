-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- This receipt must commit in the SAME transaction as the destination mutation.
-- It has no TTL: expiration of a Redis job or execution lease cannot permit the
-- same accepted operation to write again. Recovery still requires authorization.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.import_commit_receipt (
    operation_id text PRIMARY KEY,
    intent_fingerprint text NOT NULL,
    receipt bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT import_commit_receipt_operation CHECK (
        char_length(operation_id) = 32 AND operation_id !~ '[^0-9a-f]'),
    CONSTRAINT import_commit_receipt_intent CHECK (
        char_length(intent_fingerprint) = 73 AND
        left(intent_fingerprint, 9) = 'v1:sha256:' AND
        substring(intent_fingerprint FROM 10) !~ '[^0-9a-f]'),
    CONSTRAINT import_commit_receipt_size CHECK (octet_length(receipt) BETWEEN 1 AND 1048576)
);

COMMENT ON TABLE $HonuaSchema$.import_commit_receipt IS
    'Original accepted import result, committed atomically with destination changes; no expiry or replacement permits replay.';
