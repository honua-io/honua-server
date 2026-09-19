-- Routing fixture for the NAServer certification cells (client-compat stack).
--
-- The fixture database already carries pgRouting (docker/client-compat/postgres/
-- initdb/30-pgrouting.sql) and the server registers the default network dataset
-- over public.ways / public.ways_vertices_pgr, but nothing seeded any edges, so
-- every Route and ServiceArea solve answered "No route could be solved". This
-- seeds a 5x5 street grid centred on (-122.42, 37.77) - the same neighbourhood
-- the GP Buffer fixture point and the browser_compat cluster use - with 0.004
-- degree spacing (about 350 m east-west, 445 m north-south).
--
-- Cost is travel time in minutes at 50 km/h, matching Routing:CostUnit=Minutes,
-- the server default. Vertices and source/target are derived here rather than
-- through pgr_createTopology, whose vertex-table insert needs a serial id the
-- server-created table does not have.

BEGIN;

CREATE TABLE IF NOT EXISTS public.ways (
    gid bigint PRIMARY KEY,
    source bigint,
    target bigint,
    name text,
    cost double precision,
    reverse_cost double precision,
    the_geom geometry(LineString, 4326)
);

CREATE TABLE IF NOT EXISTS public.ways_vertices_pgr (
    id bigint PRIMARY KEY,
    cnt integer,
    chk integer,
    ein integer,
    eout integer,
    the_geom geometry(Point, 4326)
);

DELETE FROM public.ways;
DELETE FROM public.ways_vertices_pgr;

WITH nodes AS (
    SELECT i, j, -122.428 + i * 0.004 AS x, 37.762 + j * 0.004 AS y
    FROM generate_series(0, 4) AS i, generate_series(0, 4) AS j
),
edges AS (
    SELECT a.i AS ai, a.j AS aj, b.i AS bi, b.j AS bj,
           ST_SetSRID(ST_MakeLine(ST_MakePoint(a.x, a.y), ST_MakePoint(b.x, b.y)), 4326) AS g
    FROM nodes a
    JOIN nodes b ON (b.i = a.i + 1 AND b.j = a.j) OR (b.i = a.i AND b.j = a.j + 1)
)
INSERT INTO public.ways (gid, name, cost, reverse_cost, the_geom)
SELECT row_number() OVER (ORDER BY ai, aj, bi, bj),
       format('Grid %s%s-%s%s', ai, aj, bi, bj),
       ST_Length(g::geography) / (50000.0 / 60.0),
       ST_Length(g::geography) / (50000.0 / 60.0),
       g
FROM edges;

INSERT INTO public.ways_vertices_pgr (id, the_geom)
SELECT row_number() OVER (ORDER BY ST_X(p), ST_Y(p)), p
FROM (
    SELECT DISTINCT ST_StartPoint(the_geom) AS p FROM public.ways
    UNION
    SELECT DISTINCT ST_EndPoint(the_geom) FROM public.ways
) AS endpoints;

UPDATE public.ways w SET source = v.id
FROM public.ways_vertices_pgr v
WHERE ST_Equals(v.the_geom, ST_StartPoint(w.the_geom));

UPDATE public.ways w SET target = v.id
FROM public.ways_vertices_pgr v
WHERE ST_Equals(v.the_geom, ST_EndPoint(w.the_geom));

UPDATE public.ways_vertices_pgr v SET
    ein = (SELECT count(*) FROM public.ways w WHERE w.target = v.id),
    eout = (SELECT count(*) FROM public.ways w WHERE w.source = v.id),
    cnt = (SELECT count(*) FROM public.ways w WHERE w.source = v.id OR w.target = v.id);

COMMIT;

-- Fail the seed loudly if the grid did not link up.
DO $$
DECLARE unlinked integer;
BEGIN
    SELECT count(*) INTO unlinked FROM public.ways WHERE source IS NULL OR target IS NULL;
    IF unlinked <> 0 THEN
        RAISE EXCEPTION 'client-compat-routing-v1: % edges have no vertex', unlinked;
    END IF;
END $$;
