-- Seed data for WCS 2.0 CITE runs.
-- Inserts a deterministic WCS-enabled service with two small local PostGIS rasters.

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
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    metadata JSONB,
    connection_id UUID
);

ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS max_record_count INT NOT NULL DEFAULT 1000;
ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS metadata JSONB;
ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS connection_id UUID;

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

ALTER TABLE IF EXISTS honua.raster_data
    ADD COLUMN IF NOT EXISTS acquisition_date TIMESTAMPTZ;

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

CREATE INDEX IF NOT EXISTS idx_service_layers_service_name ON honua.service_layers(service_name);
CREATE INDEX IF NOT EXISTS idx_service_layers_layer_id ON honua.service_layers(layer_id);
CREATE INDEX IF NOT EXISTS idx_layer_fields_layer_id ON honua.layer_fields(layer_id);
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id ON honua.raster_data(layer_id);
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id_id ON honua.raster_data(layer_id, id);
CREATE INDEX IF NOT EXISTS idx_raster_data_acquisition_date ON honua.raster_data(acquisition_date);
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_acquisition ON honua.raster_data(layer_id, acquisition_date DESC, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS idx_raster_statistics_raster_data_id ON honua.raster_statistics(raster_data_id);
CREATE INDEX IF NOT EXISTS idx_raster_tiles_lookup ON honua.raster_tiles(raster_data_id, zoom_level, tile_x, tile_y);

BEGIN;

TRUNCATE honua.service_layers RESTART IDENTITY CASCADE;
TRUNCATE honua.layer_fields RESTART IDENTITY CASCADE;
TRUNCATE honua.raster_tiles RESTART IDENTITY CASCADE;
TRUNCATE honua.raster_statistics RESTART IDENTITY CASCADE;
TRUNCATE honua.raster_data RESTART IDENTITY CASCADE;
TRUNCATE honua.layers RESTART IDENTITY CASCADE;
TRUNCATE honua.services RESTART IDENTITY CASCADE;

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
    'Seeded WCS 2.0.1 service for OGC CITE conformance tests',
    4326,
    1000,
    ARRAY['image/tiff', 'image/png', 'image/jpeg'],
    ARRAY['Query', 'Extract'],
    ST_MakeEnvelope(-122.52, 37.68, -122.32, 37.86, 4326),
    '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["Wcs","ImageServer"]}'::jsonb
);

INSERT INTO honua.layers (
    layer_id,
    layer_name,
    description,
    table_schema,
    table_name,
    primary_key_column,
    geometry_column,
    geometry_type,
    srid,
    extent,
    default_visibility,
    metadata,
    enabled
)
VALUES
    (
        101,
        'cite-dem-west',
        'Deterministic 16x16 rectified grid coverage for WCS CITE',
        'honua',
        'raster_data',
        'id',
        NULL,
        'None',
        4326,
        ST_MakeEnvelope(-122.52, 37.70, -122.36, 37.86, 4326),
        TRUE,
        '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["Wcs","ImageServer"]}'::jsonb,
        TRUE
    ),
    (
        102,
        'cite-dem-east',
        'Second deterministic rectified grid coverage for WCS CITE',
        'honua',
        'raster_data',
        'id',
        NULL,
        'None',
        4326,
        ST_MakeEnvelope(-122.44, 37.68, -122.32, 37.80, 4326),
        TRUE,
        '{"accessPolicy":{"allowAnonymous":true},"enabledProtocols":["Wcs","ImageServer"]}'::jsonb,
        TRUE
    );

INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
VALUES
    ('cite', 101, 0),
    ('cite', 102, 1);

INSERT INTO honua.layer_fields (
    layer_id,
    field_name,
    field_type,
    field_order,
    nullable,
    description
)
VALUES
    (101, 'id', 'Integer', 0, FALSE, 'Raster identifier'),
    (102, 'id', 'Integer', 0, FALSE, 'Raster identifier');

INSERT INTO honua.raster_data (
    layer_id,
    name,
    description,
    raster,
    acquisition_date,
    created_at
)
VALUES
    (
        101,
        'cite-dem-west-primary',
        'Constant elevation fixture for WCS CITE coverage 101',
        ST_AddBand(
            ST_MakeEmptyRaster(16, 16, -122.52, 37.86, 0.01, -0.01, 0, 0, 4326),
            '32BF'::text,
            125.0,
            -9999.0
        ),
        '2024-01-01T00:00:00Z',
        '2024-01-01T00:00:00Z'
    ),
    (
        102,
        'cite-dem-east-primary',
        'Constant elevation fixture for WCS CITE coverage 102',
        ST_AddBand(
            ST_MakeEmptyRaster(24, 24, -122.44, 37.80, 0.005, -0.005, 0, 0, 4326),
            '32BF'::text,
            80.0,
            -9999.0
        ),
        '2024-01-02T00:00:00Z',
        '2024-01-02T00:00:00Z'
    );

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
SELECT
    id,
    1,
    CASE layer_id WHEN 101 THEN 125.0 ELSE 80.0 END,
    CASE layer_id WHEN 101 THEN 125.0 ELSE 80.0 END,
    CASE layer_id WHEN 101 THEN 125.0 ELSE 80.0 END,
    0.0,
    width * height,
    0
FROM honua.raster_data
ON CONFLICT (raster_data_id, band_number) DO UPDATE SET
    min_value = EXCLUDED.min_value,
    max_value = EXCLUDED.max_value,
    mean_value = EXCLUDED.mean_value,
    std_dev = EXCLUDED.std_dev,
    valid_pixel_count = EXCLUDED.valid_pixel_count,
    nodata_pixel_count = EXCLUDED.nodata_pixel_count,
    computed_at = NOW();

COMMIT;
