-- Replica ownership prevents one editor from enumerating or mutating another editor's
-- offline replica. Existing rows remain ownerless and are intentionally admin-only.
ALTER TABLE honua.replicas
    ADD COLUMN IF NOT EXISTS owner_id TEXT;

CREATE INDEX IF NOT EXISTS ix_replicas_service_owner
    ON honua.replicas (service_id, owner_id, created_at DESC);
