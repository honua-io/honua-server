-- Seed data for OGC API Features CITE runs.
-- Inserts a minimal layer and point features that cover common bbox cases.

BEGIN;

CREATE EXTENSION IF NOT EXISTS postgis;

-- Ensure deterministic layer and feature identifiers for CITE runs.
TRUNCATE honua.layers RESTART IDENTITY CASCADE;
TRUNCATE features RESTART IDENTITY;

WITH layer AS (
    INSERT INTO honua.layers (
        layer_name,
        description,
        table_name,
        geometry_type,
        srid,
        extent,
        default_visibility
    )
    VALUES (
        'CITE Features',
        'Seeded features for OGC API Features conformance tests',
        'features',
        'Point',
        4326,
        ST_MakeEnvelope(-180, -90, 180, 90, 4326),
        TRUE
    )
    RETURNING layer_id
)
INSERT INTO features (layer_id, geometry, attributes)
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(-122.4194, 37.7749), 4326),
       jsonb_build_object('name', 'Harbor City', 'category', 'city', 'population', 1000000, 'active', true)
FROM layer
UNION ALL
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(-122.2711, 37.8044), 4326),
       jsonb_build_object('name', 'Baytown', 'category', 'city', 'population', 430000, 'active', true)
FROM layer
UNION ALL
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(0.0, 51.4779), 4326),
       jsonb_build_object('name', 'Meridian Marker', 'category', 'reference', 'active', true)
FROM layer
UNION ALL
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(-75.0, 0.0), 4326),
       jsonb_build_object('name', 'Equator Station', 'category', 'reference', 'active', true)
FROM layer
UNION ALL
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(179.5, 0.5), 4326),
       jsonb_build_object('name', 'Dateline Post', 'category', 'reference', 'active', true)
FROM layer
UNION ALL
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(179.0, 67.0), 4326),
       jsonb_build_object('name', 'Arctic Dateline Station', 'category', 'reference', 'active', true)
FROM layer
UNION ALL
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(0.0, 86.0), 4326),
       jsonb_build_object('name', 'North Polar Outpost', 'category', 'reference', 'active', true)
FROM layer
UNION ALL
SELECT layer.layer_id,
       ST_SetSRID(ST_MakePoint(0.0, -86.0), 4326),
       jsonb_build_object('name', 'South Polar Outpost', 'category', 'reference', 'active', true)
FROM layer;

COMMIT;
