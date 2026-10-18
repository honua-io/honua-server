-- Immutable recovery proof, committed in the same transaction as governed style bytes.
-- No credentials, approval payloads, or historical style documents are stored here.
-- Retain receipts indefinitely until a coordinated operation/proposal retention policy exists.
CREATE TABLE IF NOT EXISTS honua.style_mutation_receipts (
    operation_id text PRIMARY KEY,
    proposal_id text NOT NULL,
    identity_hash text NOT NULL CHECK (identity_hash ~ '^[0-9a-f]{64}$'),
    request_hash text NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
    actor_id text NOT NULL,
    tenant_id text NOT NULL,
    schema_name text NOT NULL,
    target_id text NOT NULL,
    style_id text NOT NULL,
    expected_version integer NOT NULL CHECK (expected_version >= 0),
    result_version integer NOT NULL CHECK (result_version > expected_version),
    result_content_hash text NOT NULL CHECK (result_content_hash ~ '^[0-9a-f]{64}$'),
    committed_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_style_mutation_receipts_proposal ON honua.style_mutation_receipts (proposal_id);
