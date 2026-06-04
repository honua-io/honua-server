-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.
--
-- pgRouting test/dev database seed (issue #1266).
--
-- This script runs once via the Docker entrypoint
-- (/docker-entrypoint-initdb.d) when the honua_routing container is first
-- created. It enables PostGIS + pgRouting and provisions the
-- osm2pgrouting-compatible `ways` / `ways_vertices_pgr` topology (matching
-- migration 043_CreatePgRoutingTopology.sql EXACTLY) seeded with a tiny,
-- deterministic 3x3 lattice so a developer can `docker compose up -d` and
-- immediately solve routes / service areas against the canonical
-- PgRoutingProvider SQL.
--
-- IMPORTANT: the same topology is seeded by the gated integration test fixture
-- (tests/dotnet/Honua.TestKit/PgRoutingFixture.cs). If you change the network
-- here, change it there too so the compose DB and the Testcontainer stay
-- identical.

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS pgrouting;

-- ---------------------------------------------------------------------------
-- Topology tables (schema copied verbatim from migration 043).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.ways_vertices_pgr (
    id        BIGINT PRIMARY KEY,
    cnt       INTEGER,
    chk       INTEGER,
    ein       INTEGER,
    eout      INTEGER,
    the_geom  geometry(Point, 4326)
);

CREATE TABLE IF NOT EXISTS public.ways (
    gid           BIGINT PRIMARY KEY,
    source        BIGINT,
    target        BIGINT,
    name          TEXT,
    cost          DOUBLE PRECISION,
    reverse_cost  DOUBLE PRECISION,
    the_geom      geometry(LineString, 4326)
);

CREATE INDEX IF NOT EXISTS idx_ways_the_geom_gist
    ON public.ways USING GIST (the_geom);
CREATE INDEX IF NOT EXISTS idx_ways_source ON public.ways (source);
CREATE INDEX IF NOT EXISTS idx_ways_target ON public.ways (target);
CREATE INDEX IF NOT EXISTS idx_ways_vertices_pgr_the_geom_gist
    ON public.ways_vertices_pgr USING GIST (the_geom);

-- ---------------------------------------------------------------------------
-- Seed: deterministic 3x3 lattice, 0.01-degree spacing anchored at (0,0).
--
--   7(0,.02) --h5-- 8(.01,.02) --h6-- 9(.02,.02)
--      |               |                |
--     v2              v4               v6
--      |               |                |
--   4(0,.01) --h3-- 5(.01,.01) --h4-- 6(.02,.01)
--      |               |                |
--     v1              v3               v5
--      |               |                |
--   1(0,0)   --h1-- 2(.01,0)   --h2-- 3(.02,0)
--
-- Every edge has cost = reverse_cost = 1 (uniform grid). One grid step is a
-- ~1.11 km geodesic span, so a 4-edge corner-to-corner route is ~4.44 km.
--
-- Expected shortest path, vertex 1 (SW corner) -> vertex 9 (NE corner):
--   total cost 4 (4 hops). One valid least-cost path is 1->2->5->6->9
--   (edges 1, 9, 4, 12). The grid has several equal-cost Manhattan paths;
--   pgr_dijkstra returns one deterministic 4-hop path.
-- ---------------------------------------------------------------------------
INSERT INTO public.ways_vertices_pgr (id, the_geom) VALUES
 (1, ST_SetSRID(ST_MakePoint(0.00, 0.00), 4326)),
 (2, ST_SetSRID(ST_MakePoint(0.01, 0.00), 4326)),
 (3, ST_SetSRID(ST_MakePoint(0.02, 0.00), 4326)),
 (4, ST_SetSRID(ST_MakePoint(0.00, 0.01), 4326)),
 (5, ST_SetSRID(ST_MakePoint(0.01, 0.01), 4326)),
 (6, ST_SetSRID(ST_MakePoint(0.02, 0.01), 4326)),
 (7, ST_SetSRID(ST_MakePoint(0.00, 0.02), 4326)),
 (8, ST_SetSRID(ST_MakePoint(0.01, 0.02), 4326)),
 (9, ST_SetSRID(ST_MakePoint(0.02, 0.02), 4326))
ON CONFLICT (id) DO NOTHING;

INSERT INTO public.ways (gid, source, target, name, cost, reverse_cost, the_geom) VALUES
 (1,  1, 2, 'h1', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.00), ST_MakePoint(0.01,0.00)),4326)),
 (2,  2, 3, 'h2', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.00), ST_MakePoint(0.02,0.00)),4326)),
 (3,  4, 5, 'h3', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.01), ST_MakePoint(0.01,0.01)),4326)),
 (4,  5, 6, 'h4', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.01), ST_MakePoint(0.02,0.01)),4326)),
 (5,  7, 8, 'h5', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.02), ST_MakePoint(0.01,0.02)),4326)),
 (6,  8, 9, 'h6', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.02), ST_MakePoint(0.02,0.02)),4326)),
 (7,  1, 4, 'v1', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.00), ST_MakePoint(0.00,0.01)),4326)),
 (8,  4, 7, 'v2', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.00,0.01), ST_MakePoint(0.00,0.02)),4326)),
 (9,  2, 5, 'v3', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.00), ST_MakePoint(0.01,0.01)),4326)),
 (10, 5, 8, 'v4', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.01,0.01), ST_MakePoint(0.01,0.02)),4326)),
 (11, 3, 6, 'v5', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.02,0.00), ST_MakePoint(0.02,0.01)),4326)),
 (12, 6, 9, 'v6', 1, 1, ST_SetSRID(ST_MakeLine(ST_MakePoint(0.02,0.01), ST_MakePoint(0.02,0.02)),4326))
ON CONFLICT (gid) DO NOTHING;
