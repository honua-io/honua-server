-- Shared base schema and seed data for CI integration tests.
-- Used by: js-integration-tests, mcp-certification, mcp-llm-smoke.
-- Idempotent: safe to re-run (IF NOT EXISTS / ON CONFLICT).
--
-- This is a CI-focused schema that covers the tables and columns needed by
-- integration tests. It is NOT a mirror of the canonical migration set in
-- src/Honua.Server/Migrations/; the server runs its own migrations at startup.
-- The schema here is a pragmatic superset/subset: it includes columns from
-- several migrations (001, 002, 003, 005, 007, 009, 011) and adds seed data
-- that migrations do not provide. It intentionally excludes migrations that
-- are not exercised by CI integration tests:
--   004 (import functions), 006 (secure connections), 010 (metadata resources),
--   012 (replication), 013/014 (alerts).
-- When adding columns from new migrations, update this file and check that
-- existing CI seed data remains compatible.

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
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS honua.layers (
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

ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS metadata JSONB;

ALTER TABLE IF EXISTS honua.services
    ADD COLUMN IF NOT EXISTS connection_id UUID;

-- Columns from migrations 005, 007, 009, 011 — keep in sync.
ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS table_schema TEXT NOT NULL DEFAULT current_schema();

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
