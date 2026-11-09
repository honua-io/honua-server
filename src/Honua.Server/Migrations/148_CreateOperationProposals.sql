CREATE TABLE IF NOT EXISTS honua.operation_proposals (
    proposal_id TEXT PRIMARY KEY,
    version BIGINT NOT NULL CHECK (version >= 0),
    kind INTEGER NOT NULL,
    status INTEGER NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    payload JSONB NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_operation_proposals_active
    ON honua.operation_proposals (kind, updated_at DESC, proposal_id)
    WHERE status NOT IN (4, 5, 6, 7, 9);

CREATE TABLE IF NOT EXISTS honua.operation_proposal_leases (
    operation_id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
