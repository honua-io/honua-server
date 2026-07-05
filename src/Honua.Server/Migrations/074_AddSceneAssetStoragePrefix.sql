-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration 074: Record the cloud-storage prefix for a registered scene dataset (#2459, ADR-0060).
-- Closes the last WS0 statelessness leak on the scene publish/serve path: a scene published on
-- one node wrote its 3D Tiles tree only to that node's local disk (asset_root) and was
-- unservable from any other node. Publishing now uploads the promoted tileset tree to the shared
-- object store under a stable "scenes/{sceneId}" prefix and records it here so any node can
-- read-through hydrate a local materialization cache before serving.
--
-- Legacy rows keep asset_storage_prefix NULL and continue to serve straight off local disk with
-- no behavioural change; hydration is skipped entirely for them.

ALTER TABLE honua.scene_datasets
    ADD COLUMN IF NOT EXISTS asset_storage_prefix TEXT;

COMMENT ON COLUMN honua.scene_datasets.asset_storage_prefix IS
    'Optional object-store prefix (e.g. scenes/{sceneId}) holding the replicated 3D Tiles tree (#2459). '
    'NULL for legacy filesystem-only datasets, which stay node-local and are never hydrated.';
