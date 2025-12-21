-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Initial Honua schema migration
-- Creates catalog schema, enables PostGIS, and sets up core metadata tables

-- Create catalog schema for isolation
CREATE SCHEMA IF NOT EXISTS catalog;

-- Enable PostGIS extension for spatial functionality
CREATE EXTENSION IF NOT EXISTS postgis;

-- Services table - top-level service definitions
CREATE TABLE catalog.services (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Layers table - layer configuration within services
CREATE TABLE catalog.layers (
    layer_id SERIAL PRIMARY KEY,
    layer_name TEXT NOT NULL,
    description TEXT,
    table_name TEXT NOT NULL,
    geometry_type TEXT NOT NULL,
    srid INT NOT NULL DEFAULT 4326,
    extent GEOMETRY(POLYGON, 4326),
    min_scale DOUBLE PRECISION,
    max_scale DOUBLE PRECISION,
    default_visibility BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Service layers junction table - maps layers to services with layer index
CREATE TABLE catalog.service_layers (
    service_id TEXT NOT NULL REFERENCES catalog.services(id) ON DELETE CASCADE,
    layer_id INT NOT NULL REFERENCES catalog.layers(layer_id) ON DELETE CASCADE,
    layer_index INT NOT NULL,
    PRIMARY KEY (service_id, layer_id),
    UNIQUE (service_id, layer_index)
);

-- Layer fields table - field metadata for each layer
CREATE TABLE catalog.layer_fields (
    field_id SERIAL PRIMARY KEY,
    layer_id INT NOT NULL REFERENCES catalog.layers(layer_id) ON DELETE CASCADE,
    field_name TEXT NOT NULL,
    field_type TEXT NOT NULL,
    field_alias TEXT,
    is_nullable BOOLEAN DEFAULT TRUE,
    is_editable BOOLEAN DEFAULT TRUE,
    field_length INT
);

-- Relationships table - defines relationships between layers
CREATE TABLE catalog.relationships (
    relationship_id INT NOT NULL,
    origin_layer_id INT NOT NULL REFERENCES catalog.layers(layer_id) ON DELETE CASCADE,
    related_layer_id INT NOT NULL REFERENCES catalog.layers(layer_id) ON DELETE CASCADE,
    relationship_name TEXT NOT NULL,
    relationship_type TEXT NOT NULL,
    origin_foreign_key_field TEXT NOT NULL,
    destination_foreign_key_field TEXT NOT NULL,
    description TEXT,
    PRIMARY KEY (origin_layer_id, relationship_id)
);

-- Features table - stores actual feature data
CREATE TABLE features (
    objectid BIGSERIAL PRIMARY KEY,
    layer_id INT NOT NULL,
    geometry GEOMETRY,
    attributes JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- Indexes for performance
CREATE INDEX idx_service_layers_service_id ON catalog.service_layers(service_id);
CREATE INDEX idx_service_layers_layer_id ON catalog.service_layers(layer_id);
CREATE INDEX idx_layer_fields_layer_id ON catalog.layer_fields(layer_id);
CREATE INDEX idx_relationships_origin_layer ON catalog.relationships(origin_layer_id);
CREATE INDEX idx_relationships_related_layer ON catalog.relationships(related_layer_id);
CREATE INDEX idx_features_layer_id ON features(layer_id);
CREATE INDEX idx_features_geometry ON features USING GIST(geometry);
CREATE INDEX idx_features_attributes ON features USING GIN(attributes);

-- Comments for documentation
COMMENT ON SCHEMA catalog IS 'Honua geospatial server metadata and configuration';
COMMENT ON TABLE catalog.services IS 'Top-level service definitions (FeatureServer, OGC API Features, etc.)';
COMMENT ON TABLE catalog.layers IS 'Layer definitions with geometry and field information';
COMMENT ON TABLE catalog.service_layers IS 'Junction table mapping layers to services with display order';
COMMENT ON TABLE catalog.layer_fields IS 'Field metadata and configuration for layer attributes';
COMMENT ON TABLE catalog.relationships IS 'Relationship definitions between layers for related record queries';
COMMENT ON TABLE features IS 'Feature data with geometry and attributes stored as JSONB';

COMMENT ON COLUMN catalog.layers.geometry_type IS 'PostGIS geometry type: Point, LineString, Polygon, MultiPoint, MultiLineString, MultiPolygon';
COMMENT ON COLUMN catalog.layers.srid IS 'Spatial reference system identifier (e.g., 4326 for WGS84)';
COMMENT ON COLUMN catalog.layer_fields.field_type IS 'Field data type: text, integer, double, boolean, date, timestamp';
COMMENT ON COLUMN features.attributes IS 'Feature properties stored as JSONB for flexible schema';
