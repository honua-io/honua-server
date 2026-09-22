-- A replica created with a geometry, layerQueries or replicaSR must keep delivering only that
-- scope on every later synchronization, so the scope is persisted with the replica instead of
-- being dropped at creation (honua-server#4018). Existing rows stay unscoped (NULL).
ALTER TABLE honua.replicas
    ADD COLUMN IF NOT EXISTS scope_definition JSONB;

COMMENT ON COLUMN honua.replicas.scope_definition IS
    'Protocol-owned replica data scope (layer queries, filter geometry, output spatial reference); NULL replicates whole layers';
