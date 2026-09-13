-- OGC CITE WFS fixture data. Schema is owned by the pinned server migrations.
-- Applied only after Honua has migrated and passed readiness.

BEGIN;

TRUNCATE honua.service_layers RESTART IDENTITY CASCADE;
TRUNCATE honua.layer_fields RESTART IDENTITY CASCADE;
TRUNCATE honua.relationships RESTART IDENTITY CASCADE;
TRUNCATE honua.layers RESTART IDENTITY CASCADE;
TRUNCATE honua.services RESTART IDENTITY CASCADE;
TRUNCATE honua.feature_change_outbox RESTART IDENTITY;
TRUNCATE features RESTART IDENTITY;

INSERT INTO honua.services (
    service_name,
    description,
    srid,
    supported_formats,
    capabilities,
    service_extent,
    metadata
)
VALUES (
    'cite',
    'Seeded WFS 2.0 service for OGC CITE conformance tests',
    4326,
    ARRAY['JSON', 'GeoJSON'],
    ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete'],
    ST_MakeEnvelope(-123.0, 37.1, -122.2, 37.9, 4326),
    '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true},"enabledProtocols":["Wfs20","OgcFeatures","FeatureServer"]}'::jsonb
)
ON CONFLICT (service_name) DO UPDATE SET
    description = EXCLUDED.description,
    srid = EXCLUDED.srid,
    supported_formats = EXCLUDED.supported_formats,
    capabilities = EXCLUDED.capabilities,
    service_extent = EXCLUDED.service_extent,
    metadata = EXCLUDED.metadata,
    updated_at = NOW();

INSERT INTO honua.layers (
    layer_id,
    layer_name,
    description,
    table_schema,
    table_name,
    geometry_type,
    srid,
    extent,
    default_visibility,
    metadata,
    enabled
)
VALUES
    (1, 'poi', 'Points of interest for WFS CITE testing', 'public', 'features', 'Point', 4326,
        ST_MakeEnvelope(-122.42, 37.76, -122.40, 37.79, 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (2, 'admin_boundaries', 'Administrative boundaries for WFS CITE testing', 'public', 'features', 'Polygon', 4326,
        ST_MakeEnvelope(-122.45, 37.75, -122.41, 37.79, 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (3, 'transport_lines', 'Transportation lines for WFS CITE testing', 'public', 'features', 'LineString', 4326,
        ST_MakeEnvelope(-122.44, 37.76, -122.41, 37.79, 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (10, 'Other', 'WFS 1.0 CITE data feature', 'public', 'features', 'Point', 32615,
        ST_Transform(ST_MakeEnvelope(500000, 500000, 500100, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (11, 'Fifteen', 'WFS 1.0 CITE count feature with fifteen rows', 'public', 'features', 'Point', 32615,
        ST_Transform(ST_MakeEnvelope(500000, 500000, 500100, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (12, 'Seven', 'WFS 1.0 CITE count feature with seven rows', 'public', 'features', 'Point', 32615,
        ST_Transform(ST_MakeEnvelope(500000, 500000, 500100, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (13, 'Nulls', 'WFS 1.0 CITE null-value feature', 'public', 'features', 'Point', 32615,
        ST_Transform(ST_MakeEnvelope(500000, 500000, 500100, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (14, 'Locks', 'WFS 1.0 CITE lock placeholder feature', 'public', 'features', 'Point', 32615,
        ST_Transform(ST_MakeEnvelope(500000, 500000, 500100, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (20, 'Points', 'WFS 1.0 CITE point geometry feature', 'public', 'features', 'Point', 32615,
        ST_Transform(ST_MakeEnvelope(500000, 500000, 500100, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (21, 'Lines', 'WFS 1.0 CITE line geometry feature', 'public', 'features', 'LineString', 32615,
        ST_Transform(ST_MakeEnvelope(500100, 500000, 500200, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (22, 'Polygons', 'WFS 1.0 CITE polygon geometry feature', 'public', 'features', 'Polygon', 32615,
        ST_Transform(ST_MakeEnvelope(500200, 500000, 500300, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (23, 'MPoints', 'WFS 1.0 CITE multipoint geometry feature', 'public', 'features', 'MultiPoint', 32615,
        ST_Transform(ST_MakeEnvelope(500300, 500000, 500400, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (24, 'MLines', 'WFS 1.0 CITE multiline geometry feature', 'public', 'features', 'MultiLineString', 32615,
        ST_Transform(ST_MakeEnvelope(500400, 500000, 500500, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE),
    (25, 'MPolygons', 'WFS 1.0 CITE multipolygon geometry feature', 'public', 'features', 'MultiPolygon', 32615,
        ST_Transform(ST_MakeEnvelope(500500, 500000, 500600, 500100, 32615), 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true,"allowAnonymousWrite":true}}'::jsonb, TRUE)
ON CONFLICT (layer_id) DO UPDATE SET
    layer_name = EXCLUDED.layer_name,
    description = EXCLUDED.description,
    table_schema = EXCLUDED.table_schema,
    table_name = EXCLUDED.table_name,
    geometry_type = EXCLUDED.geometry_type,
    srid = EXCLUDED.srid,
    extent = EXCLUDED.extent,
    default_visibility = EXCLUDED.default_visibility,
    metadata = EXCLUDED.metadata,
    enabled = EXCLUDED.enabled;

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES
    ('cite', 1, 0),
    ('cite', 2, 1),
    ('cite', 3, 2),
    ('cite', 10, 10),
    ('cite', 11, 11),
    ('cite', 12, 12),
    ('cite', 13, 13),
    ('cite', 14, 14),
    ('cite', 20, 20),
    ('cite', 21, 21),
    ('cite', 22, 22),
    ('cite', 23, 23),
    ('cite', 24, 24),
    ('cite', 25, 25)
ON CONFLICT (service_name, layer_id) DO UPDATE SET
    layer_order = EXCLUDED.layer_order;

INSERT INTO honua.layer_fields (
    layer_id,
    field_name,
    field_type,
    field_order,
    max_length,
    nullable,
    default_value,
    description
)
VALUES
    (1, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (1, 'fid', 'String', 1, 50, FALSE, NULL, 'Stable feature identifier'),
    (1, 'name', 'String', 2, 100, FALSE, NULL, 'Display name'),
    (1, 'category', 'String', 3, 50, TRUE, NULL, 'POI category'),
    (1, 'description', 'String', 4, 500, TRUE, NULL, 'Narrative description'),
    (1, 'rating', 'Double', 5, NULL, TRUE, NULL, 'Numeric comparison property'),
    (1, 'is_public', 'Boolean', 6, NULL, TRUE, NULL, 'Boolean property'),
    (1, 'created_date', 'DateTime', 7, NULL, TRUE, NULL, 'Timestamp property'),
    (1, 'event_date', 'Date', 8, NULL, TRUE, NULL, 'Date property'),
    (1, 'shape', 'Geometry', 9, NULL, TRUE, NULL, 'Point geometry'),
    (2, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (2, 'fid', 'String', 1, 50, FALSE, NULL, 'Stable feature identifier'),
    (2, 'name', 'String', 2, 100, FALSE, NULL, 'Display name'),
    (2, 'admin_level', 'Integer', 3, NULL, TRUE, NULL, 'Administrative level'),
    (2, 'area_km2', 'Double', 4, NULL, TRUE, NULL, 'Area in square kilometres'),
    (2, 'population', 'Integer', 5, NULL, TRUE, NULL, 'Population count'),
    (2, 'status', 'String', 6, 32, TRUE, NULL, 'Enumerated-style string'),
    (2, 'created_date', 'DateTime', 7, NULL, TRUE, NULL, 'Timestamp property'),
    (2, 'shape', 'Geometry', 8, NULL, TRUE, NULL, 'Polygon geometry'),
    (3, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (3, 'fid', 'String', 1, 50, FALSE, NULL, 'Stable feature identifier'),
    (3, 'name', 'String', 2, 100, TRUE, NULL, 'Display name'),
    (3, 'transport_type', 'String', 3, 50, TRUE, NULL, 'Transport category'),
    (3, 'length_km', 'Double', 4, NULL, TRUE, NULL, 'Length in kilometres'),
    (3, 'is_active', 'Boolean', 5, NULL, TRUE, NULL, 'Boolean property'),
    (3, 'created_date', 'DateTime', 6, NULL, TRUE, NULL, 'Timestamp property'),
    (3, 'shape', 'Geometry', 7, NULL, TRUE, NULL, 'Line geometry'),
    (10, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (10, 'string1', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE required string'),
    (10, 'string2', 'String', 2, 255, TRUE, NULL, 'WFS 1.0 CITE optional string'),
    (10, 'integers', 'Integer', 3, NULL, TRUE, NULL, 'WFS 1.0 CITE integer'),
    (10, 'dates', 'Date', 4, NULL, TRUE, NULL, 'WFS 1.0 CITE date'),
    (10, 'pointProperty', 'Geometry', 5, NULL, TRUE, NULL, 'WFS 1.0 CITE point'),
    (11, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (11, 'pointProperty', 'Geometry', 1, NULL, FALSE, NULL, 'WFS 1.0 CITE point'),
    (12, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (12, 'pointProperty', 'Geometry', 1, NULL, FALSE, NULL, 'WFS 1.0 CITE point'),
    (13, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (13, 'name', 'String', 1, 255, TRUE, NULL, 'WFS 1.0 CITE nullable GML name'),
    (13, 'integers', 'Integer', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE nullable integer'),
    (13, 'dates', 'Date', 3, NULL, TRUE, NULL, 'WFS 1.0 CITE nullable date'),
    (13, 'pointProperty', 'Geometry', 4, NULL, TRUE, NULL, 'WFS 1.0 CITE nullable point'),
    (14, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (14, 'id', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE lock identifier'),
    (14, 'pointProperty', 'Geometry', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE point'),
    (20, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (20, 'id', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE geometry identifier'),
    (20, 'pointProperty', 'Geometry', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE point'),
    (21, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (21, 'id', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE geometry identifier'),
    (21, 'lineStringProperty', 'Geometry', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE line'),
    (22, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (22, 'id', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE geometry identifier'),
    (22, 'polygonProperty', 'Geometry', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE polygon'),
    (23, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (23, 'id', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE geometry identifier'),
    (23, 'multiPointProperty', 'Geometry', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE multipoint'),
    (24, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (24, 'id', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE geometry identifier'),
    (24, 'multiLineStringProperty', 'Geometry', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE multiline'),
    (25, 'objectid', 'Integer', 0, NULL, FALSE, NULL, 'Primary key'),
    (25, 'id', 'String', 1, 255, FALSE, NULL, 'WFS 1.0 CITE geometry identifier'),
    (25, 'multiPolygonProperty', 'Geometry', 2, NULL, TRUE, NULL, 'WFS 1.0 CITE multipolygon')
ON CONFLICT (layer_id, field_name) DO UPDATE SET
    field_type = EXCLUDED.field_type,
    field_order = EXCLUDED.field_order,
    max_length = EXCLUDED.max_length,
    nullable = EXCLUDED.nullable,
    default_value = EXCLUDED.default_value,
    description = EXCLUDED.description;

INSERT INTO features (objectid, layer_id, geometry, attributes)
VALUES
    (
        1,
        1,
        ST_GeomFromText('POINT(-122.4194 37.7749)', 4326),
        jsonb_build_object(
            'fid', 'poi.1',
            'name', 'City Hall',
            'category', 'government',
            'description', 'Municipal government building',
            'rating', 4.5,
            'is_public', true,
            'created_date', '2024-01-01T18:00:00.123+09:00',
            'event_date', '2024-01-01')
    ),
    (
        2,
        1,
        ST_GeomFromText('POINT(-122.4158 37.7803)', 4326),
        jsonb_build_object(
            'fid', 'poi.2',
            'name', 'Central Library',
            'category', 'public',
            'description', NULL,
            'rating', 4.9,
            'is_public', true,
            'created_date', '2024-01-02T19:15:00.123+09:00',
            'event_date', '2024-01-02')
    ),
    (
        3,
        1,
        ST_GeomFromText('POINT(-122.4064 37.7749)', 4326),
        jsonb_build_object(
            'fid', 'poi.3',
            'name', 'Hospital',
            'category', 'medical',
            'description', 'Regional hospital',
            'rating', 4.2,
            'is_public', false,
            'created_date', '2024-01-03T21:30:00.123+09:00',
            'event_date', NULL)
    ),
    (
        101,
        2,
        ST_GeomFromText('POLYGON((-122.43 37.77, -122.41 37.77, -122.41 37.79, -122.43 37.79, -122.43 37.77))', 4326),
        jsonb_build_object(
            'fid', 'admin.1',
            'name', 'Downtown District',
            'admin_level', 1,
            'area_km2', 5.2,
            'population', 15000,
            'status', 'active',
            'created_date', '2024-02-01T17:00:00.123+09:00')
    ),
    (
        102,
        2,
        ST_GeomFromText('POLYGON((-122.45 37.75, -122.42 37.75, -122.42 37.78, -122.45 37.78, -122.45 37.75))', 4326),
        jsonb_build_object(
            'fid', 'admin.2',
            'name', 'Residential Zone A',
            'admin_level', 2,
            'area_km2', 12.8,
            'population', 45000,
            'status', 'planned',
            'created_date', '2024-02-02T17:00:00.123+09:00')
    ),
    (
        201,
        3,
        ST_GeomFromText('LINESTRING(-122.43 37.77, -122.42 37.775, -122.41 37.78)', 4326),
        jsonb_build_object(
            'fid', 'transport.1',
            'name', 'Main Street',
            'transport_type', 'road',
            'length_km', 2.5,
            'is_active', true,
            'created_date', '2024-03-01T16:00:00.123+09:00')
    ),
    (
        202,
        3,
        ST_GeomFromText('LINESTRING(-122.44 37.76, -122.43 37.77, -122.42 37.78, -122.41 37.79)', 4326),
        jsonb_build_object(
            'fid', 'transport.2',
            'name', NULL,
            'transport_type', 'rail',
            'length_km', 8.2,
            'is_active', false,
            'created_date', '2024-03-02T16:00:00.123+09:00')
    );

INSERT INTO features (objectid, layer_id, geometry, attributes)
VALUES
    (
        1001,
        10,
        ST_GeomFromText('POINT(500050 500050)', 32615),
        jsonb_build_object(
            'string1', 'always',
            'string2', 'sometimes',
            'integers', 7,
            'dates', '2002-12-02')
    ),
    (
        1301,
        13,
        NULL,
        '{}'::jsonb
    ),
    (
        1401,
        14,
        ST_GeomFromText('POINT(500050 500050)', 32615),
        jsonb_build_object('id', 'lock-1')
    ),
    (
        2001,
        20,
        ST_GeomFromText('POINT(500050 500050)', 32615),
        jsonb_build_object('id', 't0000')
    ),
    (
        2101,
        21,
        ST_GeomFromText('LINESTRING(500125 500025,500175 500075)', 32615),
        jsonb_build_object('id', 't0001')
    ),
    (
        2201,
        22,
        ST_GeomFromText('POLYGON((500225 500025,500225 500075,500275 500050,500275 500025,500225 500025))', 32615),
        jsonb_build_object('id', 't0002')
    ),
    (
        2301,
        23,
        ST_GeomFromText('MULTIPOINT((500325 500025),(500375 500075))', 32615),
        jsonb_build_object('id', 't0003')
    ),
    (
        2401,
        24,
        ST_GeomFromText('MULTILINESTRING((500425 500025,500475 500075),(500425 500075,500475 500025))', 32615),
        jsonb_build_object('id', 't0004')
    ),
    (
        2501,
        25,
        ST_GeomFromText('MULTIPOLYGON(((500525 500025,500550 500050,500575 500025,500525 500025)),((500525 500050,500525 500075,500550 500075,500550 500050,500525 500050)))', 32615),
        jsonb_build_object('id', 't0005')
    );

INSERT INTO features (objectid, layer_id, geometry, attributes)
SELECT
    1100 + value,
    11,
    ST_GeomFromText('POINT(500050 500050)', 32615),
    '{}'::jsonb
FROM generate_series(1, 15) AS value;

INSERT INTO features (objectid, layer_id, geometry, attributes)
SELECT
    1200 + value,
    12,
    ST_GeomFromText('POINT(500050 500050)', 32615),
    '{}'::jsonb
FROM generate_series(1, 7) AS value;

SELECT setval(
    pg_get_serial_sequence('features', 'objectid'),
    COALESCE((SELECT MAX(objectid) FROM features), 1),
    true);

COMMIT;

DO $$
BEGIN
    RAISE NOTICE 'CITE WFS test data initialization completed';
    RAISE NOTICE 'Services: %', (SELECT COUNT(*) FROM honua.services);
    RAISE NOTICE 'Layers: %', (SELECT COUNT(*) FROM honua.layers);
    RAISE NOTICE 'Features: %', (SELECT COUNT(*) FROM features);
END $$;
