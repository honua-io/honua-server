-- OGC CITE WFS 2.0 Test Data Initialization
-- Seeds the actual Honua catalog tables so WFS can advertise and transact on the data.

CREATE EXTENSION IF NOT EXISTS postgis;

CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE IF NOT EXISTS honua.services (
    service_name VARCHAR(64) PRIMARY KEY,
    description TEXT NOT NULL DEFAULT '',
    srid INT NOT NULL DEFAULT 4326,
    max_record_count INT NOT NULL DEFAULT 1000,
    supported_formats TEXT[] NOT NULL DEFAULT '{JSON,GeoJSON}',
    capabilities TEXT[] NOT NULL DEFAULT '{Query,Extract}',
    service_extent GEOMETRY,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    metadata JSONB,
    connection_id UUID
);

CREATE TABLE IF NOT EXISTS honua.layers (
    layer_id SERIAL PRIMARY KEY,
    layer_name TEXT NOT NULL,
    description TEXT,
    table_schema TEXT NOT NULL DEFAULT current_schema(),
    table_name TEXT NOT NULL,
    primary_key_column TEXT NOT NULL DEFAULT 'objectid',
    geometry_column TEXT DEFAULT 'geometry',
    storage_srid INT,
    temporal_column TEXT,
    storage_options JSONB NOT NULL DEFAULT '{}'::jsonb,
    geometry_type TEXT NOT NULL,
    srid INT NOT NULL DEFAULT 4326,
    extent GEOMETRY(POLYGON, 4326),
    min_scale DOUBLE PRECISION,
    max_scale DOUBLE PRECISION,
    default_visibility BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    metadata JSONB,
    maplibre_style JSONB,
    geoservices_drawing_info JSONB,
    style_version INT DEFAULT 1,
    enabled BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE IF NOT EXISTS honua.service_layers (
    service_name VARCHAR(64) NOT NULL REFERENCES honua.services(service_name) ON DELETE CASCADE,
    layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    layer_order INT NOT NULL,
    PRIMARY KEY (service_name, layer_id),
    UNIQUE (service_name, layer_order)
);

CREATE TABLE IF NOT EXISTS honua.layer_fields (
    layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    field_name VARCHAR(64) NOT NULL,
    field_type VARCHAR(32) NOT NULL,
    field_order INT NOT NULL,
    max_length INT,
    nullable BOOLEAN NOT NULL DEFAULT TRUE,
    default_value TEXT,
    description TEXT,
    PRIMARY KEY (layer_id, field_name)
);

CREATE TABLE IF NOT EXISTS honua.relationships (
    layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    relationship_id INT NOT NULL,
    name TEXT NOT NULL,
    related_layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    relationship_type TEXT NOT NULL,
    origin_foreign_key TEXT NOT NULL,
    destination_foreign_key TEXT NOT NULL,
    description TEXT,
    PRIMARY KEY (layer_id, relationship_id)
);

CREATE TABLE IF NOT EXISTS features (
    objectid BIGSERIAL PRIMARY KEY,
    layer_id INT NOT NULL,
    geometry GEOMETRY,
    attributes JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_service_layers_service_name ON honua.service_layers(service_name);
CREATE INDEX IF NOT EXISTS idx_service_layers_layer_id ON honua.service_layers(layer_id);
CREATE INDEX IF NOT EXISTS idx_layer_fields_layer_id ON honua.layer_fields(layer_id);
CREATE INDEX IF NOT EXISTS idx_relationships_layer_id ON honua.relationships(layer_id);
CREATE INDEX IF NOT EXISTS idx_features_layer_id ON features(layer_id);
CREATE INDEX IF NOT EXISTS idx_features_geometry ON features USING GIST(geometry);
CREATE INDEX IF NOT EXISTS idx_features_attributes ON features USING GIN(attributes);

BEGIN;

TRUNCATE honua.service_layers RESTART IDENTITY CASCADE;
TRUNCATE honua.layer_fields RESTART IDENTITY CASCADE;
TRUNCATE honua.relationships RESTART IDENTITY CASCADE;
TRUNCATE honua.layers RESTART IDENTITY CASCADE;
TRUNCATE honua.services RESTART IDENTITY CASCADE;
TRUNCATE features RESTART IDENTITY;

INSERT INTO honua.services (
    service_name,
    description,
    srid,
    max_record_count,
    supported_formats,
    capabilities,
    service_extent,
    metadata
)
VALUES (
    'cite',
    'Seeded WFS 2.0 service for OGC CITE conformance tests',
    4326,
    1000,
    ARRAY['JSON', 'GeoJSON'],
    ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete'],
    ST_MakeEnvelope(-123.0, 37.1, -122.2, 37.9, 4326),
    '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["Wfs20","OgcFeatures","FeatureServer"]}'::jsonb
)
ON CONFLICT (service_name) DO UPDATE SET
    description = EXCLUDED.description,
    srid = EXCLUDED.srid,
    max_record_count = EXCLUDED.max_record_count,
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
        '{"accessPolicy":{"allowAnonymous":true}}'::jsonb, TRUE),
    (2, 'admin_boundaries', 'Administrative boundaries for WFS CITE testing', 'public', 'features', 'Polygon', 4326,
        ST_MakeEnvelope(-122.45, 37.75, -122.41, 37.79, 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true}}'::jsonb, TRUE),
    (3, 'transport_lines', 'Transportation lines for WFS CITE testing', 'public', 'features', 'LineString', 4326,
        ST_MakeEnvelope(-122.44, 37.76, -122.41, 37.79, 4326), TRUE,
        '{"accessPolicy":{"allowAnonymous":true}}'::jsonb, TRUE)
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
    ('cite', 3, 2)
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
    (3, 'shape', 'Geometry', 7, NULL, TRUE, NULL, 'Line geometry')
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
            'created_date', '2024-01-01T09:00:00Z',
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
            'created_date', '2024-01-02T10:15:00Z',
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
            'created_date', '2024-01-03T12:30:00Z',
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
            'created_date', '2024-02-01T08:00:00Z')
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
            'created_date', '2024-02-02T08:00:00Z')
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
            'created_date', '2024-03-01T07:00:00Z')
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
            'created_date', '2024-03-02T07:00:00Z')
    );

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
