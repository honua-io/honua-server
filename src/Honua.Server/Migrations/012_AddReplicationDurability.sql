-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 012_AddReplicationDurability.sql
-- Description: Adds persistent replica state, trigger-based change tracking, and monotonic
--              generation counters to support incremental sync for offline workflows.
-- Dependencies: Requires honua schema and public.features table from 001_CreateHonuaSchema.sql

-- Monotonic generation counter shared across all change-tracking operations
CREATE SEQUENCE IF NOT EXISTS honua.sync_generation AS BIGINT START WITH 1 INCREMENT BY 1 NO CYCLE;

-- Persistent replica state (replaces cache-only storage)
CREATE TABLE IF NOT EXISTS honua.replicas (
    replica_id TEXT PRIMARY KEY,
    replica_name TEXT NOT NULL,
    service_id TEXT NOT NULL,
    sync_model TEXT NOT NULL DEFAULT 'perReplica',
    layer_ids INT[] NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_sync_time TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_sync_generation BIGINT NOT NULL DEFAULT 0,
    CONSTRAINT replicas_valid_name CHECK(LENGTH(replica_name) > 0 AND LENGTH(replica_name) <= 256),
    CONSTRAINT replicas_valid_service CHECK(LENGTH(service_id) > 0),
    CONSTRAINT replicas_valid_sync_model CHECK(sync_model IN ('none', 'perLayer', 'perReplica'))
);

-- Change log for incremental sync delta computation
CREATE TABLE IF NOT EXISTS honua.feature_changes (
    change_id BIGSERIAL PRIMARY KEY,
    generation BIGINT NOT NULL,
    layer_id INT NOT NULL,
    objectid BIGINT NOT NULL,
    operation SMALLINT NOT NULL,
    changed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT feature_changes_valid_operation CHECK(operation IN (1, 2, 3))
);

-- Primary index for extractChanges queries: scan changes since a given generation for specific layers
CREATE INDEX IF NOT EXISTS idx_feature_changes_generation_layer
    ON honua.feature_changes(generation, layer_id);

-- Index for future cleanup of old change records
CREATE INDEX IF NOT EXISTS idx_feature_changes_changed_at
    ON honua.feature_changes(changed_at);

-- Index for collapsing changes per feature (window function queries)
CREATE INDEX IF NOT EXISTS idx_feature_changes_layer_objectid
    ON honua.feature_changes(layer_id, objectid, generation);

-- Trigger function: records feature changes with a monotonic generation number
CREATE OR REPLACE FUNCTION honua.track_feature_changes()
RETURNS TRIGGER AS $$
DECLARE
    gen BIGINT;
    lid INT;
    oid BIGINT;
    op SMALLINT;
BEGIN
    gen := nextval('honua.sync_generation');

    IF TG_OP = 'INSERT' THEN
        lid := NEW.layer_id;
        oid := NEW.objectid;
        op := 1;
    ELSIF TG_OP = 'UPDATE' THEN
        lid := NEW.layer_id;
        oid := NEW.objectid;
        op := 2;
    ELSIF TG_OP = 'DELETE' THEN
        lid := OLD.layer_id;
        oid := OLD.objectid;
        op := 3;
    END IF;

    INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation)
    VALUES (gen, lid, oid, op);

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Attach the trigger to the features table (fires after each row change)
DROP TRIGGER IF EXISTS trigger_track_feature_changes ON public.features;
CREATE TRIGGER trigger_track_feature_changes
    AFTER INSERT OR UPDATE OR DELETE ON public.features
    FOR EACH ROW
    EXECUTE FUNCTION honua.track_feature_changes();

-- Documentation
COMMENT ON SEQUENCE honua.sync_generation IS 'Monotonic generation counter for replication change tracking';
COMMENT ON TABLE honua.replicas IS 'Persistent replica state for offline sync workflows';
COMMENT ON COLUMN honua.replicas.replica_id IS 'Unique replica identifier (GUID hex)';
COMMENT ON COLUMN honua.replicas.replica_name IS 'Human-readable replica name';
COMMENT ON COLUMN honua.replicas.service_id IS 'Service the replica belongs to';
COMMENT ON COLUMN honua.replicas.sync_model IS 'Sync model: none, perLayer, or perReplica';
COMMENT ON COLUMN honua.replicas.layer_ids IS 'Array of layer IDs included in the replica';
COMMENT ON COLUMN honua.replicas.created_at IS 'Timestamp when the replica was created';
COMMENT ON COLUMN honua.replicas.last_sync_time IS 'Timestamp of the last successful sync';
COMMENT ON COLUMN honua.replicas.last_sync_generation IS 'Generation number at last successful sync';
COMMENT ON TABLE honua.feature_changes IS 'Change log for incremental replication sync';
COMMENT ON COLUMN honua.feature_changes.change_id IS 'Auto-incrementing change identifier';
COMMENT ON COLUMN honua.feature_changes.generation IS 'Monotonic generation number from sync_generation sequence';
COMMENT ON COLUMN honua.feature_changes.layer_id IS 'Layer containing the changed feature';
COMMENT ON COLUMN honua.feature_changes.objectid IS 'Object ID of the changed feature';
COMMENT ON COLUMN honua.feature_changes.operation IS 'Change type: 1=insert, 2=update, 3=delete';
COMMENT ON COLUMN honua.feature_changes.changed_at IS 'Timestamp when the change was recorded';
COMMENT ON INDEX honua.idx_feature_changes_generation_layer IS 'Primary index for extractChanges delta queries';
COMMENT ON INDEX honua.idx_feature_changes_changed_at IS 'Index for change log cleanup by age';
COMMENT ON INDEX honua.idx_feature_changes_layer_objectid IS 'Index for collapsing changes per feature';
