-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 062_CreateNetworkDatasets.sql
-- Phase 0 of #1882 (NAServer network-dataset editing): a first-class, addressable
-- network-dataset registry. The routing provider resolves its edge/vertex table
-- names from this registry instead of the previously hardcoded public.ways topology,
-- which makes the network addressable and multi-dataset. This is the prerequisite
-- for later phases (dataset descriptor surface + edge/vertex editing); there is NO
-- editing surface in Phase 0.
--
-- Zero behaviour change: the migration seeds a single "default" dataset that points
-- at the existing public.ways / public.ways_vertices_pgr topology, so an unchanged
-- deployment resolves the same tables it used before. On images WITHOUT pgRouting
-- the routing topology is never provisioned (see 043_CreatePgRoutingTopology.sql);
-- the registry table is still created here (it has no dependency on the pgRouting
-- extension) but is simply unused until a topology exists.
--
-- Idempotency: guarded with IF NOT EXISTS / ON CONFLICT DO NOTHING so re-applying is
-- safe.

CREATE SCHEMA IF NOT EXISTS honua;

-- Registry of addressable network datasets. Each row names the edge/vertex tables a
-- routing solve runs over. The table names are validated as safe schema-qualified
-- identifiers by the provider before being interpolated into the pgRouting edges-SQL.
CREATE TABLE IF NOT EXISTS honua.network_datasets (
    id               TEXT        NOT NULL PRIMARY KEY,
    name             TEXT        NOT NULL,
    edge_table       TEXT        NOT NULL,
    vertex_table     TEXT        NOT NULL,
    srid             INTEGER     NOT NULL DEFAULT 4326,
    status           TEXT        NOT NULL DEFAULT 'active',
    topology_version INTEGER     NOT NULL DEFAULT 1,
    -- 'dirty' marks a dataset whose topology needs a rebuild after edits. Phase 0
    -- never sets it; reserved for the deferred editing phase.
    needs_rebuild    BOOLEAN     NOT NULL DEFAULT false,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT network_datasets_valid_status
        CHECK (status IN ('active', 'building', 'disabled'))
);

COMMENT ON TABLE honua.network_datasets IS
    'Phase 0 of #1882: addressable network-dataset registry. The routing provider resolves edge/vertex table names per dataset id.';

-- Seed the built-in default dataset mapping to the existing public.ways topology so
-- the default deployment behaves exactly as before.
INSERT INTO honua.network_datasets (id, name, edge_table, vertex_table, srid, status)
VALUES ('default', 'Default network dataset', 'public.ways', 'public.ways_vertices_pgr', 4326, 'active')
ON CONFLICT (id) DO NOTHING;
