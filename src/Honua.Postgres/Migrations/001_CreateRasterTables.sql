-- Migration: Create Raster Data Tables
-- Description: Creates the necessary tables for storing raster data with PostGIS raster support
-- Version: 1.0
-- Date: 2026-02-08

-- Enable PostGIS raster extension if not already enabled
CREATE EXTENSION IF NOT EXISTS postgis_raster;

-- Ensure the honua schema exists (matches application default from SchemaSearchPath.QualifyTable)
CREATE SCHEMA IF NOT EXISTS honua;

-- Create raster_data table for storing raster datasets.
-- NOTE: acquisition_date and its supporting indexes are added by 002_AddRasterAcquisitionDate.sql
-- (kept out of this migration so existing deployments that ran 001 before the column existed
-- still pick up the change via 002).
CREATE TABLE IF NOT EXISTS honua.raster_data (
    id BIGSERIAL PRIMARY KEY,
    layer_id INTEGER NOT NULL,
    name VARCHAR(255) NOT NULL,
    description TEXT,
    raster raster NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ,

    -- Metadata columns for quick access (computed from raster)
    width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
    height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
    band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
    pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
    srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED,

    CONSTRAINT raster_data_layer_id_fk FOREIGN KEY (layer_id) REFERENCES honua.layers(layer_id) ON DELETE CASCADE
);

-- Create indices for performance
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id ON honua.raster_data(layer_id);
CREATE INDEX IF NOT EXISTS idx_raster_data_name ON honua.raster_data(name);
CREATE INDEX IF NOT EXISTS idx_raster_data_created_at ON honua.raster_data(created_at);

-- Composite index for common query pattern: list rasters by layer
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id_id ON honua.raster_data(layer_id, id);

-- Create spatial index on the raster column
CREATE INDEX IF NOT EXISTS idx_raster_data_raster_gist ON honua.raster_data USING GIST (ST_ConvexHull(raster));

-- Create index on the raster envelope for faster spatial queries
CREATE INDEX IF NOT EXISTS idx_raster_data_envelope ON honua.raster_data USING GIST (ST_Envelope(raster));

-- Create raster statistics table for caching computed statistics
CREATE TABLE IF NOT EXISTS honua.raster_statistics (
    id BIGSERIAL PRIMARY KEY,
    raster_data_id BIGINT NOT NULL,
    band_number INTEGER NOT NULL,
    min_value DOUBLE PRECISION,
    max_value DOUBLE PRECISION,
    mean_value DOUBLE PRECISION,
    std_dev DOUBLE PRECISION,
    valid_pixel_count BIGINT,
    nodata_pixel_count BIGINT,
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT raster_statistics_raster_data_id_fk FOREIGN KEY (raster_data_id) REFERENCES honua.raster_data(id) ON DELETE CASCADE,
    CONSTRAINT raster_statistics_unique_band UNIQUE (raster_data_id, band_number)
);

-- Create indices for raster statistics
CREATE INDEX IF NOT EXISTS idx_raster_statistics_raster_data_id ON honua.raster_statistics(raster_data_id);

-- Create raster tiles table for pre-computed tiles (optional for performance)
CREATE TABLE IF NOT EXISTS honua.raster_tiles (
    id BIGSERIAL PRIMARY KEY,
    raster_data_id BIGINT NOT NULL,
    zoom_level INTEGER NOT NULL,
    tile_x INTEGER NOT NULL,
    tile_y INTEGER NOT NULL,
    tile_data BYTEA NOT NULL,
    content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT raster_tiles_raster_data_id_fk FOREIGN KEY (raster_data_id) REFERENCES honua.raster_data(id) ON DELETE CASCADE,
    CONSTRAINT raster_tiles_unique_tile UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)
);

-- Composite index for tile lookups (zoom + coordinates)
CREATE INDEX IF NOT EXISTS idx_raster_tiles_lookup ON honua.raster_tiles(raster_data_id, zoom_level, tile_x, tile_y);

-- Create function to update updated_at timestamp
CREATE OR REPLACE FUNCTION honua.update_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Create trigger to automatically update updated_at on raster_data updates
DROP TRIGGER IF EXISTS trg_raster_data_updated_at ON honua.raster_data;
CREATE TRIGGER trg_raster_data_updated_at
    BEFORE UPDATE ON honua.raster_data
    FOR EACH ROW
    EXECUTE FUNCTION honua.update_updated_at();

-- Add comments for documentation
COMMENT ON TABLE honua.raster_data IS 'Stores raster datasets using PostGIS raster type';
COMMENT ON COLUMN honua.raster_data.id IS 'Unique identifier for the raster dataset';
COMMENT ON COLUMN honua.raster_data.layer_id IS 'Reference to the layer this raster belongs to';
COMMENT ON COLUMN honua.raster_data.name IS 'Display name for the raster dataset';
COMMENT ON COLUMN honua.raster_data.raster IS 'PostGIS raster data with all bands and metadata';
COMMENT ON COLUMN honua.raster_data.width IS 'Width of the raster in pixels (computed from raster)';
COMMENT ON COLUMN honua.raster_data.height IS 'Height of the raster in pixels (computed from raster)';
COMMENT ON COLUMN honua.raster_data.band_count IS 'Number of bands in the raster (computed from raster)';
COMMENT ON COLUMN honua.raster_data.pixel_type IS 'Pixel data type (e.g., 8BUI, 16BSI, 32BF)';
COMMENT ON COLUMN honua.raster_data.srid IS 'Spatial reference system identifier';

COMMENT ON TABLE honua.raster_statistics IS 'Cached statistical information for raster bands';
COMMENT ON TABLE honua.raster_tiles IS 'Pre-computed tiles for fast web mapping access';
