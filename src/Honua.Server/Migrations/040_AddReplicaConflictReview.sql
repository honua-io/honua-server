-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 040_AddReplicaConflictReview.sql
-- Description: Adds durable disconnected-sync conflict records so conflict-producing replica
--              uploads create persistent, reviewable conflicts (#1167) rather than only transient
--              sync errors. Conflicts capture base/client/server feature states, classification,
--              linkage (replica, sync operation, user, device, layer, feature, server generation),
--              and resolution audit evidence.
-- Dependencies: Requires honua schema (001_CreateHonuaSchema.sql) and honua.replicas
--               (012_AddReplicationDurability.sql).

CREATE TABLE IF NOT EXISTS honua.replica_conflicts (
    conflict_id TEXT PRIMARY KEY,
    replica_id TEXT NOT NULL,
    service_id TEXT NOT NULL,
    layer_id INT NOT NULL,
    objectid BIGINT NOT NULL,
    -- Conflict classification (ReplicaConflictType ordinal):
    -- 0=attribute, 1=geometry, 2=delete-update, 3=update-delete, 4=duplicate-insert,
    -- 5=attachment, 6=relationship.
    conflict_type SMALLINT NOT NULL,
    -- Lifecycle status (ReplicaConflictStatus ordinal): 0=pending, 1=resolved, 2=deferred.
    status SMALLINT NOT NULL DEFAULT 0,
    sync_operation_id TEXT,
    device_id TEXT,
    user_id TEXT,
    server_generation BIGINT NOT NULL,
    base_state_json JSONB,
    client_state_json JSONB,
    server_state_json JSONB,
    detected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Resolution audit evidence (ReplicaConflictResolutionAction ordinal):
    -- 0=accept-client, 1=keep-server, 2=merge-fields, 3=choose-geometry, 4=reject-client, 5=defer.
    resolution_action SMALLINT,
    resolved_by TEXT,
    resolved_at TIMESTAMPTZ,
    resolved_server_generation BIGINT,
    CONSTRAINT replica_conflicts_valid_replica CHECK(LENGTH(replica_id) > 0),
    CONSTRAINT replica_conflicts_valid_service CHECK(LENGTH(service_id) > 0),
    CONSTRAINT replica_conflicts_valid_type CHECK(conflict_type BETWEEN 0 AND 6),
    CONSTRAINT replica_conflicts_valid_status CHECK(status BETWEEN 0 AND 2),
    CONSTRAINT replica_conflicts_valid_action CHECK(resolution_action IS NULL OR resolution_action BETWEEN 0 AND 5)
);

-- Primary review query: conflicts for a replica, newest first, optionally filtered by status.
CREATE INDEX IF NOT EXISTS idx_replica_conflicts_replica_status
    ON honua.replica_conflicts(replica_id, status, detected_at DESC);

-- Service-scoped review and temporal linkage by feature.
CREATE INDEX IF NOT EXISTS idx_replica_conflicts_service_feature
    ON honua.replica_conflicts(service_id, layer_id, objectid);

COMMENT ON TABLE honua.replica_conflicts IS 'Durable disconnected-sync conflict records for manual conflict review (#1167)';
COMMENT ON COLUMN honua.replica_conflicts.conflict_id IS 'Unique conflict identifier (GUID hex)';
COMMENT ON COLUMN honua.replica_conflicts.replica_id IS 'Replica whose upload produced the conflict';
COMMENT ON COLUMN honua.replica_conflicts.service_id IS 'Service the replica belongs to';
COMMENT ON COLUMN honua.replica_conflicts.layer_id IS 'Service-local layer id of the conflicting feature';
COMMENT ON COLUMN honua.replica_conflicts.objectid IS 'Stable object id of the conflicting feature';
COMMENT ON COLUMN honua.replica_conflicts.conflict_type IS 'Conflict classification (see ReplicaConflictType)';
COMMENT ON COLUMN honua.replica_conflicts.status IS 'Lifecycle status (see ReplicaConflictStatus)';
COMMENT ON COLUMN honua.replica_conflicts.sync_operation_id IS 'Correlation id of the synchronize call that produced the conflict';
COMMENT ON COLUMN honua.replica_conflicts.device_id IS 'Device/client that uploaded the conflicting edit';
COMMENT ON COLUMN honua.replica_conflicts.user_id IS 'User that owns the conflicting edit';
COMMENT ON COLUMN honua.replica_conflicts.server_generation IS 'Server generation cursor captured when the conflict was recorded (temporal linkage, #1166)';
COMMENT ON COLUMN honua.replica_conflicts.base_state_json IS 'Base/common-ancestor feature state (opaque JSON)';
COMMENT ON COLUMN honua.replica_conflicts.client_state_json IS 'Incoming client feature state (opaque JSON)';
COMMENT ON COLUMN honua.replica_conflicts.server_state_json IS 'Current server feature state (opaque JSON)';
COMMENT ON COLUMN honua.replica_conflicts.detected_at IS 'Timestamp when the conflict was first recorded';
COMMENT ON COLUMN honua.replica_conflicts.resolution_action IS 'Resolution action applied (see ReplicaConflictResolutionAction)';
COMMENT ON COLUMN honua.replica_conflicts.resolved_by IS 'Operator that resolved the conflict (audit evidence)';
COMMENT ON COLUMN honua.replica_conflicts.resolved_at IS 'Timestamp when the conflict was resolved';
COMMENT ON COLUMN honua.replica_conflicts.resolved_server_generation IS 'Server generation produced by the resolution when a new committed server state was created';
