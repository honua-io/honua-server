-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 037_AddReplicaConflicts.sql
-- Description: Adds operator-visible named-replica metadata and a durable disconnected-sync
--              conflict record so conflict-producing synchronizeReplica uploads can be
--              inspected and resolved after the sync response (#1167).
-- Dependencies: Requires honua.replicas and honua.feature_changes from
--               012_AddReplicationDurability.sql.

-- ---------------------------------------------------------------------------
-- Named replica metadata: operator-visible owner/device, sync direction,
-- lifecycle status, replica spatial filter, and an optional branch-version
-- reference reserved for #371 named versioned editing interop.
-- ADD COLUMN ... DEFAULT is metadata-only on Postgres 11+ (no table rewrite).
-- ---------------------------------------------------------------------------
ALTER TABLE honua.replicas
    ADD COLUMN IF NOT EXISTS owner                 TEXT,
    ADD COLUMN IF NOT EXISTS device_client         TEXT,
    ADD COLUMN IF NOT EXISTS sync_direction        TEXT NOT NULL DEFAULT 'bidirectional',
    ADD COLUMN IF NOT EXISTS status                TEXT NOT NULL DEFAULT 'active',
    ADD COLUMN IF NOT EXISTS replica_geometry_json TEXT,
    ADD COLUMN IF NOT EXISTS branch_version_id     TEXT;

-- Guard the enumerated metadata columns. Constraints are added idempotently so
-- re-running the migration on an already-upgraded database is a no-op.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'replicas_valid_sync_direction'
    ) THEN
        ALTER TABLE honua.replicas
            ADD CONSTRAINT replicas_valid_sync_direction
                CHECK (sync_direction IN ('upload', 'download', 'bidirectional'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'replicas_valid_status'
    ) THEN
        ALTER TABLE honua.replicas
            ADD CONSTRAINT replicas_valid_status
                CHECK (status IN ('active', 'stale', 'expired', 'unregistered'));
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- Durable disconnected-sync conflict records. Survive the sync response so
-- Console / operators can review base/client/server state and resolve later.
-- Conflict and resolution codes are stored as SMALLINT enums (see
-- Honua.Core ReplicaConflictType / ReplicaConflictResolution).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS honua.replica_conflicts (
    conflict_id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    replica_id              TEXT        NOT NULL REFERENCES honua.replicas(replica_id) ON DELETE CASCADE,
    sync_op_id              UUID        NOT NULL,
    service_id              TEXT        NOT NULL,
    layer_id                INT         NOT NULL,
    object_id               BIGINT      NOT NULL,
    conflict_type           SMALLINT    NOT NULL,
    base_generation         BIGINT      NOT NULL,
    client_payload_json     TEXT        NOT NULL,
    server_payload_json     TEXT        NOT NULL,
    base_payload_json       TEXT,
    resolution              SMALLINT,
    resolved_by             TEXT,
    resolved_at             TIMESTAMPTZ,
    resolution_payload_json TEXT,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- 1=Attribute 2=Geometry 3=UpdateDelete 4=DeleteUpdate 5=DeleteDelete 6=DuplicateInsert
    CONSTRAINT replica_conflicts_valid_type CHECK (conflict_type BETWEEN 1 AND 6),
    -- 1=AcceptClient 2=KeepServer 3=MergeFields 4=RejectClient 5=Deferred
    CONSTRAINT replica_conflicts_valid_resolution CHECK (resolution IS NULL OR resolution BETWEEN 1 AND 5)
);

-- Pending-conflict review queue per replica (partial index keeps it small).
CREATE INDEX IF NOT EXISTS ix_replica_conflicts_replica_pending
    ON honua.replica_conflicts (replica_id, created_at DESC, conflict_id)
    WHERE resolution IS NULL;

-- All conflicts for a replica ordered for keyset pagination.
CREATE INDEX IF NOT EXISTS ix_replica_conflicts_replica_created
    ON honua.replica_conflicts (replica_id, created_at DESC, conflict_id);

-- Group conflicts produced by a single synchronizeReplica upload.
CREATE INDEX IF NOT EXISTS ix_replica_conflicts_sync_op
    ON honua.replica_conflicts (sync_op_id);

-- Resolve conflicts back to a feature's temporal history (#1166 interop).
CREATE INDEX IF NOT EXISTS ix_replica_conflicts_layer_object
    ON honua.replica_conflicts (service_id, layer_id, object_id);

COMMENT ON COLUMN honua.replicas.owner IS 'Principal that registered the replica (operator-visible).';
COMMENT ON COLUMN honua.replicas.device_client IS 'Device or client identifier that created the replica.';
COMMENT ON COLUMN honua.replicas.sync_direction IS 'Replica sync direction: upload, download, or bidirectional.';
COMMENT ON COLUMN honua.replicas.status IS 'Replica lifecycle status: active, stale, expired, or unregistered.';
COMMENT ON COLUMN honua.replicas.replica_geometry_json IS 'Optional GeoJSON spatial filter for the replica (stored raw; CRS validated on createReplica).';
COMMENT ON COLUMN honua.replicas.branch_version_id IS 'Optional named branch-version reference; reconcile/post remains #371 scope.';

COMMENT ON TABLE honua.replica_conflicts IS 'Durable disconnected-sync conflict records for manual review and resolution (#1167). No retention policy in the first slice.';
COMMENT ON COLUMN honua.replica_conflicts.sync_op_id IS 'Groups all conflicts produced by one synchronizeReplica upload.';
COMMENT ON COLUMN honua.replica_conflicts.base_generation IS 'Replica LastSyncGeneration when the conflict was detected.';
COMMENT ON COLUMN honua.replica_conflicts.base_payload_json IS 'Server state at base_generation; NULL in the first slice (reserved for #1166 temporal snapshots).';
COMMENT ON COLUMN honua.replica_conflicts.resolution IS 'NULL while pending; otherwise the applied ReplicaConflictResolution code.';
