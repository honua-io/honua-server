-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 003_CreateRelationshipsTable.sql
-- Description: Creates the relationships table to support queryRelatedRecords functionality
-- Dependencies: Requires honua.layers table from 001_CreateHonuaSchema.sql

-- Create relationships table
CREATE TABLE honua.relationships (
    id SERIAL PRIMARY KEY,
    layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    relationship_id INT NOT NULL,
    name TEXT NOT NULL,
    related_layer_id INT NOT NULL REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    relationship_type TEXT NOT NULL,
    origin_foreign_key TEXT NOT NULL,
    destination_foreign_key TEXT NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
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

-- Create indexes for efficient relationship queries
CREATE INDEX idx_relationships_layer_id ON honua.relationships(layer_id);
CREATE INDEX idx_relationships_related_layer_id ON honua.relationships(related_layer_id);
CREATE INDEX idx_relationships_lookup ON honua.relationships(layer_id, relationship_id);

-- Create trigger to update the updated_at timestamp
CREATE OR REPLACE FUNCTION honua.update_relationships_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trigger_update_relationships_updated_at
    BEFORE UPDATE ON honua.relationships
    FOR EACH ROW
    EXECUTE FUNCTION honua.update_relationships_updated_at();

-- Add comments for documentation
COMMENT ON TABLE honua.relationships IS 'Defines relationships between layers for queryRelatedRecords functionality';
COMMENT ON COLUMN honua.relationships.id IS 'Primary key for the relationships table';
COMMENT ON COLUMN honua.relationships.layer_id IS 'Source layer ID that defines the relationship';
COMMENT ON COLUMN honua.relationships.relationship_id IS 'Unique relationship identifier within the layer (user-defined)';
COMMENT ON COLUMN honua.relationships.name IS 'Human-readable name for the relationship';
COMMENT ON COLUMN honua.relationships.related_layer_id IS 'Target layer ID that the relationship points to';
COMMENT ON COLUMN honua.relationships.relationship_type IS 'Type of relationship (esriRelRoleOrigin, esriRelRoleDestination, esriRelRoleAny)';
COMMENT ON COLUMN honua.relationships.origin_foreign_key IS 'Field name in the source layer containing the foreign key';
COMMENT ON COLUMN honua.relationships.destination_foreign_key IS 'Field name in the target layer containing the primary key';
COMMENT ON COLUMN honua.relationships.description IS 'Optional description of the relationship';
COMMENT ON COLUMN honua.relationships.created_at IS 'Timestamp when the relationship was created';
COMMENT ON COLUMN honua.relationships.updated_at IS 'Timestamp when the relationship was last updated';

COMMENT ON INDEX honua.idx_relationships_layer_id IS 'Index for finding relationships by source layer';
COMMENT ON INDEX honua.idx_relationships_related_layer_id IS 'Index for finding relationships by target layer';
COMMENT ON INDEX honua.idx_relationships_lookup IS 'Composite index for efficient relationship lookups by layer and relationship ID';

-- Grant permissions (assuming honua_user from 001_CreateHonuaSchema.sql)
-- GRANT SELECT ON honua.relationships TO honua_user;
-- GRANT INSERT, UPDATE, DELETE ON honua.relationships TO honua_admin;