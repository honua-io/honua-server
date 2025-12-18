-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Initial Honua schema migration
-- Creates honua schema, enables PostGIS, and sets up core metadata tables

-- Create honua schema for isolation
CREATE SCHEMA IF NOT EXISTS honua;

-- Enable PostGIS extension for spatial functionality
CREATE EXTENSION IF NOT EXISTS postgis;

-- Services table - top-level service definitions
CREATE TABLE honua.services (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Layers table - layer configuration within services
CREATE TABLE honua.layers (
    id SERIAL PRIMARY KEY,
    service_id TEXT NOT NULL REFERENCES honua.services(id) ON DELETE CASCADE,
    layer_index INT NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    table_name TEXT NOT NULL,
    geometry_field TEXT NOT NULL DEFAULT 'geom',
    object_id_field TEXT NOT NULL DEFAULT 'id',
    srid INT NOT NULL DEFAULT 4326,
    geometry_type TEXT NOT NULL,
    extent_xmin DOUBLE PRECISION,
    extent_ymin DOUBLE PRECISION,
    extent_xmax DOUBLE PRECISION,
    extent_ymax DOUBLE PRECISION,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    mvt_min_zoom INT DEFAULT 0,
    mvt_max_zoom INT DEFAULT 22,
    mvt_max_features INT DEFAULT 10000,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(service_id, layer_index)
);

-- Layer fields table - field metadata for each layer
CREATE TABLE honua.layer_fields (
    id SERIAL PRIMARY KEY,
    layer_id INT NOT NULL REFERENCES honua.layers(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    column_name TEXT NOT NULL,
    field_type TEXT NOT NULL,
    alias TEXT,
    is_nullable BOOLEAN DEFAULT TRUE,
    is_editable BOOLEAN DEFAULT TRUE,
    length INT
);

-- Indexes for performance
CREATE INDEX idx_layers_service_id ON honua.layers(service_id);
CREATE INDEX idx_layer_fields_layer_id ON honua.layer_fields(layer_id);

-- Comments for documentation
COMMENT ON SCHEMA honua IS 'Honua geospatial server metadata and configuration';
COMMENT ON TABLE honua.services IS 'Top-level service definitions (FeatureServer, OGC API Features, etc.)';
COMMENT ON TABLE honua.layers IS 'Layer configuration within services, referencing PostGIS tables';
COMMENT ON TABLE honua.layer_fields IS 'Field metadata and configuration for layer attributes';

COMMENT ON COLUMN honua.layers.geometry_type IS 'PostGIS geometry type: Point, LineString, Polygon, MultiPoint, MultiLineString, MultiPolygon';
COMMENT ON COLUMN honua.layers.srid IS 'Spatial reference system identifier (e.g., 4326 for WGS84)';
COMMENT ON COLUMN honua.layer_fields.field_type IS 'Field data type: text, integer, double, boolean, date, timestamp';
