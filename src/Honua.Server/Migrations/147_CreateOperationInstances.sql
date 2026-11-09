CREATE TABLE IF NOT EXISTS honua.operation_instances (
    operation_instance_id TEXT PRIMARY KEY,
    version BIGINT NOT NULL CHECK (version >= 0),
    status INTEGER NOT NULL,
    payload JSONB NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_operation_instances_active
    ON honua.operation_instances (status)
    WHERE status IN (0, 2, 3, 4);

CREATE TABLE IF NOT EXISTS honua.operation_instance_leases (
    operation_id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
