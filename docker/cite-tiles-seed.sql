-- Seed data for OGC API Tiles CITE runs.
-- Inserts layers and features suitable for vector tile generation and testing.

BEGIN;

CREATE EXTENSION IF NOT EXISTS postgis;

-- Ensure deterministic layer and feature identifiers for CITE runs.
TRUNCATE honua.layers RESTART IDENTITY CASCADE;
TRUNCATE features RESTART IDENTITY;

-- Create a point layer for CITE tiles testing
WITH point_layer AS (
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
        'CITE Tiles Points',
        'Seeded point features for OGC API Tiles conformance tests',
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
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(-122.4194, 37.7749), 4326),
       jsonb_build_object('name', 'San Francisco', 'category', 'city', 'population', 874961, 'active', true)
FROM point_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(-122.2711, 37.8044), 4326),
       jsonb_build_object('name', 'Oakland', 'category', 'city', 'population', 433031, 'active', true)
FROM point_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(-122.0322, 37.3688), 4326),
       jsonb_build_object('name', 'Sunnyvale', 'category', 'city', 'population', 155805, 'active', true)
FROM point_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(-121.8906, 37.3361), 4326),
       jsonb_build_object('name', 'San Jose', 'category', 'city', 'population', 1013240, 'active', true)
FROM point_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(0.0, 51.5074), 4326),
       jsonb_build_object('name', 'London', 'category', 'city', 'population', 8982000, 'active', true)
FROM point_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(139.6917, 35.6895), 4326),
       jsonb_build_object('name', 'Tokyo', 'category', 'city', 'population', 13960000, 'active', true)
FROM point_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(-43.1729, -22.9068), 4326),
       jsonb_build_object('name', 'Rio de Janeiro', 'category', 'city', 'population', 6748000, 'active', true)
FROM point_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePoint(151.2093, -33.8688), 4326),
       jsonb_build_object('name', 'Sydney', 'category', 'city', 'population', 5312000, 'active', true)
FROM point_layer;

-- Create a polygon layer for CITE tiles testing
WITH polygon_layer AS (
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
        'CITE Tiles Polygons',
        'Seeded polygon features for OGC API Tiles conformance tests',
        'features',
        'Polygon',
        4326,
        ST_MakeEnvelope(-180, -90, 180, 90, 4326),
        TRUE,
        '{"accessPolicy":{"allowAnonymous":true}}'::jsonb
    )
    RETURNING layer_id
)
INSERT INTO features (layer_id, geometry, attributes)
SELECT layer_id,
       ST_SetSRID(ST_MakePolygon(ST_GeomFromText('LINESTRING(-122.5 37.7, -122.3 37.7, -122.3 37.85, -122.5 37.85, -122.5 37.7)')), 4326),
       jsonb_build_object('name', 'Bay Area North', 'category', 'region', 'area_km2', 500.0, 'active', true)
FROM polygon_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePolygon(ST_GeomFromText('LINESTRING(-122.5 37.3, -121.8 37.3, -121.8 37.7, -122.5 37.7, -122.5 37.3)')), 4326),
       jsonb_build_object('name', 'Bay Area South', 'category', 'region', 'area_km2', 800.0, 'active', true)
FROM polygon_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePolygon(ST_GeomFromText('LINESTRING(-0.5 51.3, 0.3 51.3, 0.3 51.7, -0.5 51.7, -0.5 51.3)')), 4326),
       jsonb_build_object('name', 'Greater London', 'category', 'region', 'area_km2', 1572.0, 'active', true)
FROM polygon_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_MakePolygon(ST_GeomFromText('LINESTRING(139.5 35.5, 140.0 35.5, 140.0 35.9, 139.5 35.9, 139.5 35.5)')), 4326),
       jsonb_build_object('name', 'Tokyo Metropolitan', 'category', 'region', 'area_km2', 2194.0, 'active', true)
FROM polygon_layer;

-- Create a linestring layer for CITE tiles testing
WITH line_layer AS (
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
        'CITE Tiles Lines',
        'Seeded line features for OGC API Tiles conformance tests',
        'features',
        'LineString',
        4326,
        ST_MakeEnvelope(-180, -90, 180, 90, 4326),
        TRUE,
        '{"accessPolicy":{"allowAnonymous":true}}'::jsonb
    )
    RETURNING layer_id
)
INSERT INTO features (layer_id, geometry, attributes)
SELECT layer_id,
       ST_SetSRID(ST_GeomFromText('LINESTRING(-122.4194 37.7749, -122.2711 37.8044)'), 4326),
       jsonb_build_object('name', 'Bay Bridge Route', 'category', 'highway', 'length_km', 7.0, 'active', true)
FROM line_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_GeomFromText('LINESTRING(-122.4194 37.7749, -121.8906 37.3361)'), 4326),
       jsonb_build_object('name', 'Highway 101', 'category', 'highway', 'length_km', 70.0, 'active', true)
FROM line_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_GeomFromText('LINESTRING(0.0 51.5074, 0.1276 51.5074, 0.1276 51.6)'), 4326),
       jsonb_build_object('name', 'M25 Segment', 'category', 'motorway', 'length_km', 15.0, 'active', true)
FROM line_layer
UNION ALL
SELECT layer_id,
       ST_SetSRID(ST_GeomFromText('LINESTRING(139.6917 35.6895, 139.7671 35.6812, 139.8107 35.7100)'), 4326),
       jsonb_build_object('name', 'Tokyo Metro Line', 'category', 'rail', 'length_km', 12.0, 'active', true)
FROM line_layer;

COMMIT;
