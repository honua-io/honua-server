-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 066_AddNetworkDatasetEditingColumns.sql
-- Editing surface for #1882 (NAServer network-dataset editing), built on the Phase-0
-- registry (062_CreateNetworkDatasets). Phase 0 made the routing network addressable
-- and read-only; this adds the columns the admin editing surface needs to register /
-- update / delete a dataset's source-table mapping + descriptive metadata with proper
-- audit attribution:
--   * description  — optional human-readable note about the dataset.
--   * created_by / updated_by — admin attribution mirroring the scene-dataset registry.
--
-- It is intentionally additive: the existing read path
-- (NetworkDatasetRegistry.ResolveAsync) selects a fixed column list and is unaffected.
-- Edge/vertex feature editing, turn/restriction-attribute editing, and topology
-- rebuild remain deferred (tracked separately); this increment edits the dataset
-- *registry* (mapping + metadata), which is the addressable editing surface.
--
-- Idempotency: ADD COLUMN IF NOT EXISTS so re-applying is safe. The honua schema and
-- network_datasets table are created by 062; this migration only runs its ALTERs when
-- that table exists so plain-PostGIS images (no routing topology) skip it cleanly.

DO $$
BEGIN
    IF to_regclass('honua.network_datasets') IS NOT NULL THEN
        ALTER TABLE honua.network_datasets
            ADD COLUMN IF NOT EXISTS description TEXT;
        ALTER TABLE honua.network_datasets
            ADD COLUMN IF NOT EXISTS created_by TEXT;
        ALTER TABLE honua.network_datasets
            ADD COLUMN IF NOT EXISTS updated_by TEXT;

        COMMENT ON COLUMN honua.network_datasets.description IS
            'Optional operator note (editing metadata, #1882 editing surface).';
        COMMENT ON COLUMN honua.network_datasets.created_by IS
            'Admin identity that registered the dataset (#1882 editing surface).';
        COMMENT ON COLUMN honua.network_datasets.updated_by IS
            'Admin identity that last updated the dataset (#1882 editing surface).';
    END IF;
END $$;
