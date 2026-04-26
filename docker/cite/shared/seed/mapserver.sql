-- Seed data for WMS/WMTS CITE runs.
-- Inserts deterministic MapServer service + layers used by legacy OGC protocol tests.

BEGIN;

CREATE EXTENSION IF NOT EXISTS postgis;

TRUNCATE honua.service_layers RESTART IDENTITY CASCADE;
TRUNCATE honua.layers RESTART IDENTITY CASCADE;
TRUNCATE honua.services RESTART IDENTITY CASCADE;
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
    'Seeded MapServer service for WMS/WMTS CITE conformance tests',
    4326,
    ARRAY['JSON', 'GeoJSON'],
    ARRAY['Query', 'Extract'],
    ST_MakeEnvelope(-180, -90, 180, 90, 4326),
    '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["FeatureServer","MapServer","OgcFeatures","OData"]}'::jsonb
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
    table_name,
    geometry_type,
    srid,
    extent,
    default_visibility,
    metadata
)
VALUES
    (0, 'cite:BasicPolygons', 'CITE BasicPolygons layer', 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (1, 'cite:Lakes', 'CITE Lakes layer', 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (2, 'cite:Streams', 'CITE Streams layer', 'features', 'LineString', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (3, 'cite:Bridges', 'CITE Bridges layer', 'features', 'Point', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (4, 'cite:RoadSegments', 'CITE RoadSegments layer', 'features', 'LineString', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (5, 'cite:DividedRoutes', 'CITE DividedRoutes layer', 'features', 'LineString', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (6, 'cite:Buildings', 'CITE Buildings layer', 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (7, 'cite:MapNeatline', 'CITE MapNeatline layer', 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (8, 'cite:NamedPlaces', 'CITE NamedPlaces layer', 'features', 'Point', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (9, 'cite:Ponds', 'CITE Ponds layer', 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (10, 'cite:Forests', 'CITE Forests layer', 'features', 'Polygon', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (11, 'cite:Terrain', 'CITE Terrain layer', 'features', 'Polygon', 4326, ST_MakeEnvelope(-1, -1, 1, 1, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb),
    (12, 'cite:Autos', 'CITE Autos layer', 'features', 'Point', 4326, ST_MakeEnvelope(-1, -1, 1, 1, 4326), TRUE, '{"accessPolicy":{"allowAnonymous":true}}'::jsonb)
ON CONFLICT (layer_id) DO UPDATE SET
    layer_name = EXCLUDED.layer_name,
    description = EXCLUDED.description,
    table_name = EXCLUDED.table_name,
    geometry_type = EXCLUDED.geometry_type,
    srid = EXCLUDED.srid,
    extent = EXCLUDED.extent,
    default_visibility = EXCLUDED.default_visibility,
    metadata = EXCLUDED.metadata;

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES
    ('cite', 0, 0),
    ('cite', 1, 1),
    ('cite', 2, 2),
    ('cite', 3, 3),
    ('cite', 4, 4),
    ('cite', 5, 5),
    ('cite', 6, 6),
    ('cite', 7, 7),
    ('cite', 8, 8),
    ('cite', 9, 9),
    ('cite', 10, 10),
    ('cite', 11, 11),
    ('cite', 12, 12)
ON CONFLICT (service_name, layer_id) DO UPDATE SET
    layer_order = EXCLUDED.layer_order;

INSERT INTO features (layer_id, geometry, attributes)
VALUES
    (0, ST_SetSRID(ST_GeomFromText('POLYGON((-123 37,-121 37,-121 38,-123 38,-123 37))'), 4326), jsonb_build_object('name', 'Basic Polygon A', 'category', 'polygon')),
    (0, ST_SetSRID(ST_GeomFromText('POLYGON((-0.20 3.80,0.20 3.80,0.20 4.20,-0.20 4.20,-0.20 3.80))'), 4326), jsonb_build_object('name', 'Basic Polygon B', 'category', 'polygon')),
    (0, ST_SetSRID(ST_GeomFromText('POLYGON((-0.10 3.90,0.30 3.90,0.30 4.30,-0.10 4.30,-0.10 3.90))'), 4326), jsonb_build_object('name', 'Basic Polygon C', 'category', 'polygon')),
    (1, ST_SetSRID(ST_GeomFromText('POLYGON((-122.8 37.2,-122.2 37.2,-122.2 37.7,-122.8 37.7,-122.8 37.2))'), 4326), jsonb_build_object('name', 'Lake A', 'category', 'water')),
    (1, ST_SetSRID(ST_GeomFromText('POLYGON((0.0015 -0.0013,0.0027 -0.0013,0.0027 -0.0004,0.0015 -0.0004,0.0015 -0.0013))'), 4326), jsonb_build_object('name', 'Lake Pixel Test', 'category', 'water')),
    (2, ST_SetSRID(ST_GeomFromText('LINESTRING(-123 37.5,-122 37.6,-121 37.4)'), 4326), jsonb_build_object('name', 'Stream A', 'category', 'waterway')),
    (3, ST_SetSRID(ST_MakePoint(-122.4, 37.8), 4326), jsonb_build_object('name', 'Bridge A', 'category', 'transport')),
    (4, ST_SetSRID(ST_GeomFromText('LINESTRING(-122.7 37.3,-121.9 37.9)'), 4326), jsonb_build_object('name', 'Road Segment A', 'category', 'transport')),
    (5, ST_SetSRID(ST_GeomFromText('LINESTRING(-122.9 37.1,-121.8 37.2)'), 4326), jsonb_build_object('name', 'Divided Route A', 'category', 'transport')),
    (6, ST_SetSRID(ST_GeomFromText('POLYGON((-122.6 37.6,-122.4 37.6,-122.4 37.8,-122.6 37.8,-122.6 37.6))'), 4326), jsonb_build_object('name', 'Building Footprint A', 'category', 'structure')),
    (7, ST_SetSRID(ST_GeomFromText('POLYGON((-124 36,-120 36,-120 39,-124 39,-124 36))'), 4326), jsonb_build_object('name', 'Map Neatline A', 'category', 'boundary')),
    (8, ST_SetSRID(ST_MakePoint(-122.3, 37.7), 4326), jsonb_build_object('name', 'Named Place A', 'category', 'place')),
    (9, ST_SetSRID(ST_GeomFromText('POLYGON((-122.2 37.4,-121.9 37.4,-121.9 37.6,-122.2 37.6,-122.2 37.4))'), 4326), jsonb_build_object('name', 'Pond A', 'category', 'water')),
    (10, ST_SetSRID(ST_GeomFromText('POLYGON((-123 37.8,-122.5 37.8,-122.5 38.2,-123 38.2,-123 37.8))'), 4326), jsonb_build_object('name', 'Forest A', 'category', 'landcover')),
    (11, ST_SetSRID(ST_GeomFromText('POLYGON((-0.25 -0.25,0.25 -0.25,0.25 0.25,-0.25 0.25,-0.25 -0.25))'), 4326), jsonb_build_object('name', 'Terrain A', 'category', 'terrain')),
    (12, ST_SetSRID(ST_MakePoint(-0.001, 0.001), 4326), jsonb_build_object('name', 'Auto A', 'category', 'transport'));

COMMIT;
