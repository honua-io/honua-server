-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- pgRouting topology provisioning (issue #1266: pgRouting engine + NAServer
-- route/service-area compatibility).
--
-- This migration enables the pgRouting extension and provisions a routing
-- topology compatible with pgRouting 3.x and the osm2pgrouting import schema so
-- the canonical Honua.Routing PgRoutingProvider can solve routes and service
-- areas with pgr_dijkstra / pgr_drivingDistance over a "ways" edge table snapped
-- to a "ways_vertices_pgr" vertex table.
--
-- Idempotency: every statement is guarded (CREATE ... IF NOT EXISTS) so the
-- migration is safe to (re-)apply against existing databases. The tables live in
-- the public schema rather than honua.* because osm2pgrouting and the pgRouting
-- function family default to unqualified "ways" / "ways_vertices_pgr" names and
-- operate over the connection search_path; the migration runner sets
-- search_path=public (see PostgresDatabaseMigrationRunner.BuildMigrationConnectionString),
-- which keeps these names resolvable by both the importer and the provider SQL.
--
-- DEPLOYMENT NOTE: pgRouting is a SEPARATE extension from PostGIS. The default
-- runtime image (postgis/postgis:17-3.5-alpine) does NOT bundle the pgrouting
-- extension, so "CREATE EXTENSION pgrouting" would fail on that image. To keep
-- this migration safe to run on ANY Postgres/PostGIS image, the entire routing
-- topology provisioning is guarded behind a runtime availability check against
-- pg_available_extensions. On images WITHOUT pgRouting the migration emits a
-- NOTICE and completes successfully WITHOUT creating any routing objects.
-- Operators that want routing must run an image that ships pgRouting (e.g.
-- pgrouting/pgrouting) or install the pgrouting package into the PostGIS image.

-- Routing requires PostGIS (geometry types + KNN <-> operator). PostGIS is
-- already provisioned by earlier migrations / the base image; this guard is
-- defensive and idempotent.
CREATE EXTENSION IF NOT EXISTS postgis;

-- ---------------------------------------------------------------------------
-- Availability-guarded routing topology provisioning.
--
-- Only enable pgRouting and create the routing topology when the pgrouting
-- extension is actually available on this server. This keeps plain-PostGIS
-- environments (which still run migrations) from crashing on a missing
-- extension. Everything inside the IF branch is idempotent
-- (CREATE EXTENSION/TABLE/INDEX IF NOT EXISTS), so re-runs stay safe.
--
-- Note: CREATE EXTENSION cannot run as a bare statement inside a plpgsql DO
-- block, so it is invoked via EXECUTE. The CREATE TABLE / CREATE INDEX DDL is
-- likewise run via EXECUTE for uniformity within the guard.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_available_extensions WHERE name = 'pgrouting'
    ) THEN
        -- Enable pgRouting.
        EXECUTE 'CREATE EXTENSION IF NOT EXISTS pgrouting';

        -- -------------------------------------------------------------------
        -- ways_vertices_pgr: the node/vertex table for the routing graph.
        -- Matches the osm2pgrouting vertex schema (id bigint PK, the_geom
        -- point/4326).
        -- -------------------------------------------------------------------
        EXECUTE $ddl$
            CREATE TABLE IF NOT EXISTS public.ways_vertices_pgr (
                id        BIGINT PRIMARY KEY,
                cnt       INTEGER,
                chk       INTEGER,
                ein       INTEGER,
                eout      INTEGER,
                the_geom  geometry(Point, 4326)
            )
        $ddl$;

        -- -------------------------------------------------------------------
        -- ways: the edge table for the routing graph.
        -- Matches the osm2pgrouting edge schema closely enough for
        -- pgr_dijkstra / pgr_drivingDistance (gid PK, source/target vertex ids,
        -- cost / reverse_cost, the_geom LineString/4326). cost/reverse_cost are
        -- generic graph weights; the provider treats them as both length and
        -- travel-time weights for the MVP and documents the unit mapping in the
        -- canonical model XML docs.
        -- -------------------------------------------------------------------
        EXECUTE $ddl$
            CREATE TABLE IF NOT EXISTS public.ways (
                gid           BIGINT PRIMARY KEY,
                source        BIGINT,
                target        BIGINT,
                name          TEXT,
                cost          DOUBLE PRECISION,
                reverse_cost  DOUBLE PRECISION,
                the_geom      geometry(LineString, 4326)
            )
        $ddl$;

        -- Foreign keys from edge endpoints to vertices. Added defensively only
        -- when the constraint is not already present, so re-runs and
        -- partially-provisioned databases stay safe.
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint WHERE conname = 'ways_source_fk'
        ) THEN
            EXECUTE 'ALTER TABLE public.ways
                ADD CONSTRAINT ways_source_fk
                FOREIGN KEY (source) REFERENCES public.ways_vertices_pgr (id)
                ON DELETE SET NULL';
        END IF;

        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint WHERE conname = 'ways_target_fk'
        ) THEN
            EXECUTE 'ALTER TABLE public.ways
                ADD CONSTRAINT ways_target_fk
                FOREIGN KEY (target) REFERENCES public.ways_vertices_pgr (id)
                ON DELETE SET NULL';
        END IF;

        -- -------------------------------------------------------------------
        -- Indexes.
        -- GiST on geometry columns for KNN nearest-vertex snapping
        -- (the_geom <-> point) and spatial joins; btree on source/target for
        -- the routing function joins.
        -- -------------------------------------------------------------------
        EXECUTE 'CREATE INDEX IF NOT EXISTS idx_ways_the_geom_gist
            ON public.ways USING GIST (the_geom)';
        EXECUTE 'CREATE INDEX IF NOT EXISTS idx_ways_source
            ON public.ways (source)';
        EXECUTE 'CREATE INDEX IF NOT EXISTS idx_ways_target
            ON public.ways (target)';
        EXECUTE 'CREATE INDEX IF NOT EXISTS idx_ways_vertices_pgr_the_geom_gist
            ON public.ways_vertices_pgr USING GIST (the_geom)';

        -- -------------------------------------------------------------------
        -- Documentation.
        -- -------------------------------------------------------------------
        EXECUTE $cmt$COMMENT ON TABLE public.ways IS
            'pgRouting edge table (osm2pgrouting-compatible) used by Honua.Routing PgRoutingProvider for pgr_dijkstra / pgr_drivingDistance.'$cmt$;
        EXECUTE $cmt$COMMENT ON TABLE public.ways_vertices_pgr IS
            'pgRouting vertex table (osm2pgrouting-compatible) used for nearest-vertex snapping and as routing graph nodes.'$cmt$;
        EXECUTE $cmt$COMMENT ON COLUMN public.ways.cost IS
            'Forward traversal weight (generic graph cost; mapped to both length-meters and travel-time by the routing provider for the MVP).'$cmt$;
        EXECUTE $cmt$COMMENT ON COLUMN public.ways.reverse_cost IS
            'Reverse traversal weight; negative or NULL marks the edge as one-way in the forward direction.'$cmt$;

        RAISE NOTICE 'pgRouting extension available: routing topology (ways / ways_vertices_pgr) provisioned.';
    ELSE
        RAISE NOTICE 'pgRouting extension not available on this server image: skipping routing topology provisioning. Install pgRouting (e.g. pgrouting/pgrouting image) to enable routing.';
    END IF;
END
$$;
