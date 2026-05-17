-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Initial Honua schema migration
-- Creates honua schema, enables PostGIS, and sets up core metadata tables

-- Create honua schema for isolation
CREATE SCHEMA IF NOT EXISTS honua;
CREATE SCHEMA IF NOT EXISTS honua_data;

-- Note: PostGIS extension is enabled by the test fixture/infrastructure setup
-- This ensures proper permissions and avoids duplicate extension creation

-- Services table - top-level service definitions
CREATE TABLE IF NOT EXISTS honua.services (
    service_name VARCHAR(64) PRIMARY KEY,
    description TEXT NOT NULL DEFAULT '',
    srid INT NOT NULL DEFAULT 4326,
    supported_formats TEXT[] NOT NULL DEFAULT '{JSON,GeoJSON}',
    capabilities TEXT[] NOT NULL DEFAULT '{Query,Extract}',
    service_extent GEOMETRY,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Layers table - layer configuration within services
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
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Keep the initial migration safe for pre-created metadata tables used by
-- integration-test seeds and early preview deployments.
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

-- Service layers junction table - maps layers to services with layer index
CREATE TABLE IF NOT EXISTS honua.service_layers (
    service_name VARCHAR(64) NOT NULL REFERENCES honua.services(service_name) ON DELETE CASCADE,
    layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    layer_order INT NOT NULL,
    PRIMARY KEY (service_name, layer_id),
    UNIQUE (service_name, layer_order)
);

-- Layer fields table - field metadata for each layer
CREATE TABLE IF NOT EXISTS honua.layer_fields (
    layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    field_name VARCHAR(64) NOT NULL,
    field_type VARCHAR(32) NOT NULL,
    field_order INT NOT NULL,
    max_length INT,
    nullable BOOLEAN NOT NULL DEFAULT TRUE,
    default_value TEXT,
    description TEXT,
    domain JSONB,
    PRIMARY KEY (layer_id, field_name)
);


-- Features table - stores actual feature data
CREATE TABLE IF NOT EXISTS features (
    objectid BIGSERIAL PRIMARY KEY,
    layer_id INT NOT NULL,
    geometry GEOMETRY,
    attributes JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_service_layers_service_name ON honua.service_layers(service_name);
CREATE INDEX IF NOT EXISTS idx_service_layers_layer_id ON honua.service_layers(layer_id);
CREATE INDEX IF NOT EXISTS idx_layer_fields_layer_id ON honua.layer_fields(layer_id);
CREATE INDEX IF NOT EXISTS idx_features_layer_id ON features(layer_id);
CREATE INDEX IF NOT EXISTS idx_features_geometry ON features USING GIST(geometry);
CREATE INDEX IF NOT EXISTS idx_features_geography ON features USING GIST ((ST_Transform(geometry, 4326)::geography));
CREATE INDEX IF NOT EXISTS idx_features_attributes ON features USING GIN(attributes);

-- Comments for documentation
COMMENT ON SCHEMA honua IS 'Honua geospatial server metadata and configuration';
COMMENT ON SCHEMA honua_data IS 'Honua operational data tables imported or managed by operators';
COMMENT ON TABLE honua.services IS 'Top-level service definitions (FeatureServer, OGC API Features, etc.)';
COMMENT ON TABLE honua.layers IS 'Layer definitions with geometry and field information';
COMMENT ON TABLE honua.service_layers IS 'Junction table mapping layers to services with display order';
COMMENT ON TABLE honua.layer_fields IS 'Field metadata and configuration for layer attributes';
COMMENT ON TABLE features IS 'Feature data with geometry and attributes stored as JSONB';

COMMENT ON COLUMN honua.layers.geometry_type IS 'PostGIS geometry type: Point, LineString, Polygon, MultiPoint, MultiLineString, MultiPolygon';
COMMENT ON COLUMN honua.layers.srid IS 'Spatial reference system identifier (e.g., 4326 for WGS84)';
COMMENT ON COLUMN honua.layers.primary_key_column IS 'Physical primary key or object identifier column for provider-backed execution';
COMMENT ON COLUMN honua.layers.geometry_column IS 'Physical geometry column for provider-backed execution';
COMMENT ON COLUMN honua.layers.storage_srid IS 'SRID/CRS used by the stored geometry';
COMMENT ON COLUMN honua.layers.temporal_column IS 'Optional physical temporal column used by time-aware layers';
COMMENT ON COLUMN honua.layers.storage_options IS 'Provider-specific storage binding options when neutral layer fields are not sufficient';
COMMENT ON COLUMN honua.layer_fields.field_type IS 'Field data type: text, integer, double, boolean, date, timestamp';
COMMENT ON COLUMN honua.layer_fields.domain IS 'Operator-managed field domain metadata such as coded-value domains';
COMMENT ON COLUMN features.attributes IS 'Feature properties stored as JSONB for flexible schema';
