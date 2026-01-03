-- Test data for JavaScript integration tests
-- Creates service 'test_service_gw0' with layer 1000

-- 1. Create the test service
INSERT INTO honua.services (service_name, description, srid, max_record_count, supported_formats, capabilities, service_extent)
VALUES (
    'test_service_gw0',
    'Test service for JavaScript integration tests',
    4326,
    1000,
    '{JSON,GeoJSON}',
    '{Query,Extract,Create,Update,Delete}',
    ST_GeomFromText('POLYGON((-180 -90, 180 -90, 180 90, -180 90, -180 -90))', 4326)
) ON CONFLICT (service_name) DO NOTHING;

-- 2. Create the test layer (ensure it gets layer_id = 1000)
INSERT INTO honua.layers (layer_id, layer_name, description, table_name, geometry_type, srid, extent, default_visibility)
VALUES (
    1000,
    'Test Layer',
    'Test layer for JavaScript integration tests',
    'features',
    'Point',
    4326,
    ST_GeomFromText('POLYGON((-180 -90, 180 -90, 180 90, -180 90, -180 -90))', 4326),
    true
) ON CONFLICT (layer_id) DO NOTHING;

-- 3. Link the layer to the service
INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES ('test_service_gw0', 1000, 1)
ON CONFLICT (service_name, layer_id) DO NOTHING;

-- 4. Define layer fields
INSERT INTO honua.layer_fields (layer_id, field_name, field_type, field_order, nullable, description)
VALUES
    (1000, 'objectid', 'integer', 1, false, 'Object ID'),
    (1000, 'name', 'text', 2, true, 'Feature name'),
    (1000, 'category', 'text', 3, true, 'Feature category'),
    (1000, 'value', 'integer', 4, true, 'Numeric value'),
    (1000, 'description', 'text', 5, true, 'Feature description')
ON CONFLICT (layer_id, field_name) DO NOTHING;

-- 5. Add test features for the JavaScript tests
INSERT INTO features (objectid, layer_id, geometry, attributes)
VALUES
    (1, 1000, ST_GeomFromText('POINT(-157.8583 21.3069)', 4326),
     '{"name": "Honolulu", "category": "city", "value": 1000000, "description": "Capital of Hawaii"}'),
    (2, 1000, ST_GeomFromText('POINT(-122.4194 37.7749)', 4326),
     '{"name": "San Francisco", "category": "city", "value": 875000, "description": "City in California"}'),
    (3, 1000, ST_GeomFromText('POINT(-74.0060 40.7128)', 4326),
     '{"name": "New York", "category": "city", "value": 8400000, "description": "Largest city in the US"}'),
    (4, 1000, ST_GeomFromText('POINT(-87.6298 41.8781)', 4326),
     '{"name": "Chicago", "category": "city", "value": 2700000, "description": "City in Illinois"}'),
    (5, 1000, ST_GeomFromText('POINT(-95.3698 29.7604)', 4326),
     '{"name": "Houston", "category": "city", "value": 2300000, "description": "City in Texas"}'),
    (6, 1000, ST_GeomFromText('POINT(-112.0740 33.4484)', 4326),
     '{"name": "Phoenix", "category": "city", "value": 1600000, "description": "City in Arizona"}'),
    (7, 1000, ST_GeomFromText('POINT(-117.1611 32.7157)', 4326),
     '{"name": "San Diego", "category": "city", "value": 1400000, "description": "City in California"}'),
    (8, 1000, ST_GeomFromText('POINT(-97.7431 30.2672)', 4326),
     '{"name": "Austin", "category": "city", "value": 960000, "description": "Capital of Texas"}'),
    (9, 1000, ST_GeomFromText('POINT(-80.1918 25.7617)', 4326),
     '{"name": "Miami", "category": "city", "value": 470000, "description": "City in Florida"}'),
    (10, 1000, ST_GeomFromText('POINT(-121.4944 38.5816)', 4326),
     '{"name": "Sacramento", "category": "city", "value": 510000, "description": "Capital of California"}')
ON CONFLICT (objectid) DO NOTHING;

-- 6. Update sequence for features table to avoid conflicts
SELECT setval('features_objectid_seq', (SELECT MAX(objectid) FROM features) + 1);