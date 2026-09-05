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
    default_visibility,
    metadata
)
VALUES (
    'CITE Features',
    'Seeded features for OGC API Features conformance tests',
    'features',
    'Point',
    4326,
    ST_MakeEnvelope(-180, -90, 180, 90, 4326),
    TRUE,
    '{"accessPolicy":{"allowAnonymous":true}}'::jsonb
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

-- Register the JSON attributes as public fields. Honua uses layer_fields for
-- queryables and feature projection; without these rows the feature payload
-- intentionally exposes an empty properties object, which cannot exercise the
-- CQL2 text/JSON equality and numeric filter paths.
INSERT INTO honua.layer_fields (
    layer_id, field_name, field_type, field_order,
    max_length, nullable, default_value, description
)
SELECT layers.layer_id,
       fields.field_name,
       fields.field_type,
       fields.field_order,
       fields.max_length,
       fields.nullable,
       NULL,
       fields.description
FROM honua.layers AS layers
CROSS JOIN (VALUES
    ('objectid', 'Integer', 0, NULL::integer, FALSE, 'Object ID'),
    ('name', 'String', 1, 255, TRUE, 'Display name'),
    ('category', 'String', 2, 50, TRUE, 'Filter category'),
    ('population', 'Integer', 3, NULL::integer, TRUE, 'Population count'),
    ('active', 'Boolean', 4, NULL::integer, TRUE, 'Active flag'),
    ('shape', 'Geometry', 5, NULL::integer, TRUE, 'Point geometry')
) AS fields(field_name, field_type, field_order, max_length, nullable, description)
WHERE layers.layer_name = 'CITE Features';

COMMIT;
