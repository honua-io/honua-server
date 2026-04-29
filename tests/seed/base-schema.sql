-- Shared base schema and seed data for CI integration tests.
-- Used by: js-integration-tests, mcp-certification, mcp-llm-smoke.
-- Idempotent: safe to re-run (IF NOT EXISTS / ON CONFLICT).
--
-- This is a CI-focused schema that covers the tables and columns needed by
-- integration tests. It is NOT a mirror of the canonical migration set in
-- src/Honua.Server/Migrations/; the server runs its own migrations at startup.
-- The schema here is a pragmatic superset/subset: it includes columns from
-- several migrations (001, 002, 003, 005, 007, 009, 011, 021) and adds seed data
-- that migrations do not provide. It intentionally excludes migrations that
-- are not exercised by CI integration tests:
--   004 (import functions), 006 (secure connections), 010 (metadata resources),
--   012 (replication), 013/014 (alerts).
-- When adding columns from new migrations, update this file and check that
-- existing CI seed data remains compatible.

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_raster;

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
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS honua.layers (
    layer_id SERIAL PRIMARY KEY,
    layer_name TEXT NOT NULL,
    description TEXT,
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
    created_at TIMESTAMPTZ DEFAULT NOW()
);

ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS metadata JSONB;

ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS connection_id UUID;

-- Columns from migrations 005, 007, 009, 011 — keep in sync.
ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS table_schema TEXT NOT NULL DEFAULT current_schema();

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS primary_key_column TEXT NOT NULL DEFAULT 'objectid';

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS geometry_column TEXT DEFAULT 'geometry';

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS storage_srid INT;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS temporal_column TEXT;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS storage_options JSONB NOT NULL DEFAULT '{}'::jsonb;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS metadata JSONB;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS maplibre_style JSONB;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS geoservices_drawing_info JSONB;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS style_version INT DEFAULT 1;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS enabled BOOLEAN NOT NULL DEFAULT TRUE;

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

CREATE TABLE IF NOT EXISTS honua.raster_data (
    id BIGSERIAL PRIMARY KEY,
    layer_id INTEGER NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    name VARCHAR(255) NOT NULL,
    description TEXT,
    raster raster NOT NULL,
    acquisition_date TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
    height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
    band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
    pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
    srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
);

CREATE TABLE IF NOT EXISTS honua.raster_statistics (
    id BIGSERIAL PRIMARY KEY,
    raster_data_id BIGINT NOT NULL REFERENCES honua.raster_data(id) ON DELETE CASCADE,
    band_number INTEGER NOT NULL,
    min_value DOUBLE PRECISION,
    max_value DOUBLE PRECISION,
    mean_value DOUBLE PRECISION,
    std_dev DOUBLE PRECISION,
    valid_pixel_count BIGINT,
    nodata_pixel_count BIGINT,
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT raster_statistics_unique_band UNIQUE (raster_data_id, band_number)
);

CREATE TABLE IF NOT EXISTS honua.raster_tiles (
    id BIGSERIAL PRIMARY KEY,
    raster_data_id BIGINT NOT NULL REFERENCES honua.raster_data(id) ON DELETE CASCADE,
    zoom_level INTEGER NOT NULL,
    tile_x INTEGER NOT NULL,
    tile_y INTEGER NOT NULL,
    tile_data BYTEA NOT NULL,
    content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT raster_tiles_unique_tile UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)
);

CREATE TABLE IF NOT EXISTS honua.cloud_raster_catalog (
    id              BIGSERIAL PRIMARY KEY,
    layer_id        INTEGER NOT NULL,
    name            VARCHAR(255) NOT NULL,
    description     TEXT,
    provider        VARCHAR(50) NOT NULL,
    bucket          VARCHAR(255) NOT NULL,
    object_key      VARCHAR(1024) NOT NULL,
    width           INTEGER,
    height          INTEGER,
    band_count      INTEGER,
    pixel_type      VARCHAR(10),
    srid            INTEGER,
    compression     VARCHAR(50),
    tile_width      INTEGER,
    tile_height     INTEGER,
    overview_levels JSONB,
    extent_xmin     DOUBLE PRECISION,
    extent_ymin     DOUBLE PRECISION,
    extent_xmax     DOUBLE PRECISION,
    extent_ymax     DOUBLE PRECISION,
    ifd_cache       BYTEA,
    metadata_scanned_at TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ,
    CONSTRAINT fk_cloud_raster_layer FOREIGN KEY (layer_id)
        REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    CONSTRAINT uq_cloud_raster_object UNIQUE (layer_id, provider, bucket, object_key)
);

CREATE INDEX IF NOT EXISTS idx_cloud_raster_layer ON honua.cloud_raster_catalog(layer_id);

CREATE OR REPLACE FUNCTION honua.update_cloud_raster_catalog_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_cloud_raster_catalog_updated_at ON honua.cloud_raster_catalog;
CREATE TRIGGER trg_cloud_raster_catalog_updated_at
    BEFORE UPDATE ON honua.cloud_raster_catalog
    FOR EACH ROW
    EXECUTE FUNCTION honua.update_cloud_raster_catalog_updated_at();

CREATE TABLE IF NOT EXISTS honua.attachments (
    id BIGSERIAL PRIMARY KEY,
    feature_id BIGINT NOT NULL,
    layer_id INT NOT NULL,
    filename TEXT NOT NULL,
    content_type TEXT NOT NULL,
    size BIGINT NOT NULL CHECK (size >= 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    storage_path TEXT NOT NULL,
    keywords TEXT
);

CREATE TABLE IF NOT EXISTS honua.relationships (
    id SERIAL PRIMARY KEY,
    layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    relationship_id INT NOT NULL,
    name TEXT NOT NULL,
    related_layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    relationship_type TEXT NOT NULL,
    origin_foreign_key TEXT NOT NULL,
    destination_foreign_key TEXT NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT relationships_layer_relationship_unique UNIQUE(layer_id, relationship_id),
    CONSTRAINT relationships_valid_ids CHECK(layer_id >= 0 AND related_layer_id >= 0 AND relationship_id > 0),
    CONSTRAINT relationships_valid_fields CHECK(
        LENGTH(name) > 0 AND LENGTH(name) <= 128 AND
        LENGTH(relationship_type) > 0 AND LENGTH(relationship_type) <= 64 AND
        LENGTH(origin_foreign_key) > 0 AND LENGTH(origin_foreign_key) <= 128 AND
        LENGTH(destination_foreign_key) > 0 AND LENGTH(destination_foreign_key) <= 128
    ),
    CONSTRAINT relationships_valid_type CHECK(
        relationship_type IN ('esriRelRoleOrigin', 'esriRelRoleDestination', 'esriRelRoleAny')
    )
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
CREATE INDEX IF NOT EXISTS idx_relationships_related_layer_id ON honua.relationships(related_layer_id);
CREATE INDEX IF NOT EXISTS idx_features_layer_id ON features(layer_id);
CREATE INDEX IF NOT EXISTS idx_features_geometry ON features USING GIST(geometry);
CREATE INDEX IF NOT EXISTS idx_features_attributes ON features USING GIN(attributes);
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id ON honua.raster_data(layer_id);
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id_id ON honua.raster_data(layer_id, id);
CREATE INDEX IF NOT EXISTS idx_raster_data_acquisition_date ON honua.raster_data(acquisition_date);
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_acquisition ON honua.raster_data(layer_id, acquisition_date DESC, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS idx_raster_statistics_raster_data_id ON honua.raster_statistics(raster_data_id);
CREATE INDEX IF NOT EXISTS idx_raster_tiles_lookup ON honua.raster_tiles(raster_data_id, zoom_level, tile_x, tile_y);

-- Base test service
INSERT INTO honua.services (
    service_name, description, srid, max_record_count,
    supported_formats, capabilities, service_extent
)
VALUES (
    'test_service', 'Test Feature Service', 4326, 1000,
    ARRAY['JSON', 'GeoJSON'],
    ARRAY['Query', 'Extract', 'Create', 'Update', 'Delete'],
    ST_MakeEnvelope(-122.5, 37.7, -122.35, 37.84, 4326)
)
ON CONFLICT (service_name) DO UPDATE SET
    description = EXCLUDED.description,
    srid = EXCLUDED.srid,
    max_record_count = EXCLUDED.max_record_count,
    supported_formats = EXCLUDED.supported_formats,
    capabilities = EXCLUDED.capabilities,
    service_extent = EXCLUDED.service_extent,
    updated_at = NOW();

UPDATE honua.services
SET metadata = jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
WHERE service_name = 'test_service';

-- Base test layer (layer 0, Point)
INSERT INTO honua.layers (
    layer_id, layer_name, description, table_name,
    geometry_type, srid, extent, default_visibility
)
VALUES (
    0, 'Test Layer', 'Default layer for integration tests',
    'features', 'Point', 4326,
    ST_MakeEnvelope(-122.5, 37.7, -122.35, 37.84, 4326), true
)
ON CONFLICT (layer_id) DO UPDATE SET
    layer_name = EXCLUDED.layer_name,
    description = EXCLUDED.description,
    table_name = EXCLUDED.table_name,
    geometry_type = EXCLUDED.geometry_type,
    srid = EXCLUDED.srid,
    extent = EXCLUDED.extent,
    default_visibility = EXCLUDED.default_visibility;

UPDATE honua.layers
SET metadata = jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
WHERE layer_id = 0;

-- Layer 0 fields — core set
INSERT INTO honua.layer_fields (
    layer_id, field_name, field_type, field_order,
    max_length, nullable, default_value, description
)
VALUES
    (0, 'objectid', 'Integer', 0, NULL, false, NULL, 'Object ID'),
    (0, 'name', 'String', 1, 255, true, NULL, 'Name'),
    (0, 'description', 'String', 2, 1024, true, NULL, 'Description'),
    (0, 'shape', 'Geometry', 3, NULL, true, NULL, 'Geometry')
ON CONFLICT (layer_id, field_name) DO NOTHING;

-- Layer 0 fields — extended set
INSERT INTO honua.layer_fields (
    layer_id, field_name, field_type, field_order,
    max_length, nullable, default_value, description
)
VALUES
    (0, 'status', 'String', 4, 64, true, NULL, 'Status'),
    (0, 'count', 'Integer', 5, NULL, true, NULL, 'Count'),
    (0, 'ratio', 'Double', 6, NULL, true, NULL, 'Ratio'),
    (0, 'active', 'Boolean', 7, NULL, true, NULL, 'Active flag'),
    (0, 'created_at', 'DateTime', 8, NULL, true, NULL, 'Created timestamp'),
    (0, 'event_date', 'Date', 9, NULL, true, NULL, 'Event date'),
    (0, 'event_time', 'Time', 10, NULL, true, NULL, 'Event time'),
    (0, 'uid', 'Uuid', 11, NULL, true, NULL, 'Unique identifier'),
    (0, 'tags', 'Json', 12, NULL, true, NULL, 'Tag array'),
    (0, 'numbers', 'Json', 13, NULL, true, NULL, 'Number array')
ON CONFLICT (layer_id, field_name) DO NOTHING;

-- Bind layer 0 to test_service
INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES ('test_service', 0, 0)
ON CONFLICT (service_name, layer_id) DO NOTHING;

-- Deterministic feature rows for CI lanes that only apply base-schema.sql.
-- Keep these aligned with the JS/Python shared test attributes so query
-- contracts validate against a populated layer instead of an empty catalog.
WITH seeded_features AS (
    SELECT *
    FROM (
        VALUES
            ('alpha',   'active',   1,  1.25, true,  '2024-01-01T12:00:00Z', '2024-02-01', '12:34:56', '00000000-0000-0000-0000-000000000001', '["red","blue"]'::jsonb, '[0,1,2]'::jsonb, NULL::text,            'POINT(-122.4900 37.7100)'),
            ('beta',    'inactive', 2,  2.50, false, '2024-01-02T12:00:00Z', '2024-02-02', '12:34:56', '00000000-0000-0000-0000-000000000002', '["green"]'::jsonb,      '[1,2,3]'::jsonb, 'description_1',     'POINT(-122.4750 37.7200)'),
            ('gamma',   'active',   3,  3.75, true,  '2024-01-03T12:00:00Z', '2024-02-03', '12:34:56', '00000000-0000-0000-0000-000000000003', '["red","blue"]'::jsonb, '[2,3,4]'::jsonb, 'description_2',     'POINT(-122.4600 37.7300)'),
            ('delta',   'inactive', 4,  5.00, false, '2024-01-04T12:00:00Z', '2024-02-04', '12:34:56', '00000000-0000-0000-0000-000000000004', '["green"]'::jsonb,      '[3,4,5]'::jsonb, NULL::text,            'POINT(-122.4450 37.7400)'),
            ('epsilon', 'active',   5,  6.25, true,  '2024-01-05T12:00:00Z', '2024-02-05', '12:34:56', '00000000-0000-0000-0000-000000000005', '["red","blue"]'::jsonb, '[4,5,6]'::jsonb, 'description_4',     'POINT(-122.4300 37.7500)'),
            ('zeta',    'inactive', 6,  7.50, false, '2024-01-06T12:00:00Z', '2024-02-06', '12:34:56', '00000000-0000-0000-0000-000000000006', '["green"]'::jsonb,      '[5,6,7]'::jsonb, 'description_5',     'POINT(-122.4150 37.7600)'),
            ('eta',     'active',   7,  8.75, true,  '2024-01-07T12:00:00Z', '2024-02-07', '12:34:56', '00000000-0000-0000-0000-000000000007', '["red","blue"]'::jsonb, '[6,7,8]'::jsonb, NULL::text,            'POINT(-122.4194 37.7749)'),
            ('theta',   'inactive', 8, 10.00, false, '2024-01-08T12:00:00Z', '2024-02-08', '12:34:56', '00000000-0000-0000-0000-000000000008', '["green"]'::jsonb,      '[7,8,9]'::jsonb, 'description_7',     'POINT(-122.3850 37.7800)'),
            ('iota',    'active',   9, 11.25, true,  '2024-01-09T12:00:00Z', '2024-02-09', '12:34:56', '00000000-0000-0000-0000-000000000009', '["red","blue"]'::jsonb, '[8,9,10]'::jsonb, 'description_8',    'POINT(-122.3700 37.7900)'),
            ('lambda',  'inactive',10, 12.50, false, '2024-01-10T12:00:00Z', '2024-02-10', '12:34:56', '00000000-0000-0000-0000-000000000010', '["green"]'::jsonb,      '[9,10,11]'::jsonb, NULL::text,          NULL::text)
    ) AS seed(
        name,
        status,
        feature_count,
        ratio,
        active_flag,
        created_at,
        event_date,
        event_time,
        uid,
        tags,
        numbers,
        description,
        wkt
    )
)
INSERT INTO features (layer_id, geometry, attributes)
SELECT
    0,
    CASE
        WHEN wkt IS NULL THEN NULL
        ELSE ST_SetSRID(ST_GeomFromText(wkt), 4326)
    END,
    jsonb_build_object(
        'name', name,
        'status', status,
        'count', feature_count,
        'ratio', ratio,
        'active', active_flag,
        'created_at', created_at,
        'event_date', event_date,
        'event_time', event_time,
        'uid', uid,
        'tags', tags,
        'numbers', numbers,
        'description', description
    )
FROM seeded_features
WHERE NOT EXISTS (
    SELECT 1
    FROM features
    WHERE layer_id = 0
);

-- Deterministic one-band raster for OGC API Maps and ImageServer client lanes.
WITH inserted_raster AS (
    INSERT INTO honua.raster_data (layer_id, name, description, raster)
    SELECT
        0,
        'Test Raster',
        'Deterministic raster for client compatibility tests',
        ST_AddBand(
            ST_MakeEmptyRaster(64, 64, -122.5, 37.84, 0.00234375, -0.0021875, 0, 0, 4326),
            '8BUI'::text,
            128,
            0)
    WHERE NOT EXISTS (
        SELECT 1
        FROM honua.raster_data
        WHERE layer_id = 0 AND name = 'Test Raster'
    )
    RETURNING id
),
target_raster AS (
    SELECT id FROM inserted_raster
    UNION ALL
    SELECT id
    FROM honua.raster_data
    WHERE layer_id = 0 AND name = 'Test Raster'
    LIMIT 1
)
INSERT INTO honua.raster_statistics (
    raster_data_id,
    band_number,
    min_value,
    max_value,
    mean_value,
    std_dev,
    valid_pixel_count,
    nodata_pixel_count
)
SELECT id, 1, 128, 128, 128, 0, 4096, 0
FROM target_raster
ON CONFLICT (raster_data_id, band_number) DO UPDATE SET
    min_value = EXCLUDED.min_value,
    max_value = EXCLUDED.max_value,
    mean_value = EXCLUDED.mean_value,
    std_dev = EXCLUDED.std_dev,
    valid_pixel_count = EXCLUDED.valid_pixel_count,
    nodata_pixel_count = EXCLUDED.nodata_pixel_count,
    computed_at = NOW();
