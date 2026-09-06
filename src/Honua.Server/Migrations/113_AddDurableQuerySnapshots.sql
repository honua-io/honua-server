-- Immutable, bounded OData query receipts survive process restarts. The opaque
-- identifier is not authorization: every read revalidates the stored query scope.
CREATE TABLE IF NOT EXISTS honua.query_snapshots (
    id uuid PRIMARY KEY,
    payload bytea NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_query_snapshots_expiry ON honua.query_snapshots(expires_at);
