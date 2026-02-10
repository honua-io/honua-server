-- Migration: Create Raster Data Tables
-- Description: Creates the necessary tables for storing raster data with PostGIS raster support
-- Version: 1.0
-- Date: 2026-02-08

-- Enable PostGIS raster extension if not already enabled
CREATE EXTENSION IF NOT EXISTS postgis_raster;

-- Create raster_data table for storing raster datasets
CREATE TABLE IF NOT EXISTS raster_data (
    id BIGSERIAL PRIMARY KEY,
    layer_id INTEGER NOT NULL,
    name VARCHAR(255) NOT NULL,
    description TEXT,
    raster raster NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    modified_at TIMESTAMPTZ,

    -- Metadata columns for quick access (computed from raster)
    width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
    height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
    band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
    pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
    srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED,

    CONSTRAINT raster_data_layer_id_fk FOREIGN KEY (layer_id) REFERENCES layers(id) ON DELETE CASCADE
);

-- Create indices for performance
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id ON raster_data(layer_id);
CREATE INDEX IF NOT EXISTS idx_raster_data_name ON raster_data(name);
CREATE INDEX IF NOT EXISTS idx_raster_data_created_at ON raster_data(created_at);

-- Create spatial index on the raster column
CREATE INDEX IF NOT EXISTS idx_raster_data_raster_gist ON raster_data USING GIST (ST_ConvexHull(raster));

-- Create index on the raster envelope for faster spatial queries
CREATE INDEX IF NOT EXISTS idx_raster_data_envelope ON raster_data USING GIST (ST_Envelope(raster));

-- Create raster statistics table for caching computed statistics
CREATE TABLE IF NOT EXISTS raster_statistics (
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

    CONSTRAINT raster_statistics_raster_data_id_fk FOREIGN KEY (raster_data_id) REFERENCES raster_data(id) ON DELETE CASCADE,
    CONSTRAINT raster_statistics_unique_band UNIQUE (raster_data_id, band_number)
);

-- Create indices for raster statistics
CREATE INDEX IF NOT EXISTS idx_raster_statistics_raster_data_id ON raster_statistics(raster_data_id);
CREATE INDEX IF NOT EXISTS idx_raster_statistics_band_number ON raster_statistics(band_number);

-- Create raster tiles table for pre-computed tiles (optional for performance)
CREATE TABLE IF NOT EXISTS raster_tiles (
    id BIGSERIAL PRIMARY KEY,
    raster_data_id BIGINT NOT NULL,
    zoom_level INTEGER NOT NULL,
    tile_x INTEGER NOT NULL,
    tile_y INTEGER NOT NULL,
    tile_data BYTEA NOT NULL,
    content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT raster_tiles_raster_data_id_fk FOREIGN KEY (raster_data_id) REFERENCES raster_data(id) ON DELETE CASCADE,
    CONSTRAINT raster_tiles_unique_tile UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)
);

-- Create indices for tile access
CREATE INDEX IF NOT EXISTS idx_raster_tiles_raster_data_id ON raster_tiles(raster_data_id);
CREATE INDEX IF NOT EXISTS idx_raster_tiles_zoom_level ON raster_tiles(zoom_level);
CREATE INDEX IF NOT EXISTS idx_raster_tiles_coordinates ON raster_tiles(tile_x, tile_y);

-- Create function to update modified_at timestamp
CREATE OR REPLACE FUNCTION update_modified_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.modified_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Create trigger to automatically update modified_at on raster_data updates
DROP TRIGGER IF EXISTS trg_raster_data_modified_at ON raster_data;
CREATE TRIGGER trg_raster_data_modified_at
    BEFORE UPDATE ON raster_data
    FOR EACH ROW
    EXECUTE FUNCTION update_modified_at();

-- Grant appropriate permissions (adjust schema and user as needed)
-- GRANT SELECT, INSERT, UPDATE, DELETE ON raster_data TO honua_app_user;
-- GRANT SELECT, INSERT, UPDATE, DELETE ON raster_statistics TO honua_app_user;
-- GRANT SELECT, INSERT, UPDATE, DELETE ON raster_tiles TO honua_app_user;
-- GRANT USAGE, SELECT ON SEQUENCE raster_data_id_seq TO honua_app_user;
-- GRANT USAGE, SELECT ON SEQUENCE raster_statistics_id_seq TO honua_app_user;
-- GRANT USAGE, SELECT ON SEQUENCE raster_tiles_id_seq TO honua_app_user;

-- Add comments for documentation
COMMENT ON TABLE raster_data IS 'Stores raster datasets using PostGIS raster type';
COMMENT ON COLUMN raster_data.id IS 'Unique identifier for the raster dataset';
COMMENT ON COLUMN raster_data.layer_id IS 'Reference to the layer this raster belongs to';
COMMENT ON COLUMN raster_data.name IS 'Display name for the raster dataset';
COMMENT ON COLUMN raster_data.raster IS 'PostGIS raster data with all bands and metadata';
COMMENT ON COLUMN raster_data.width IS 'Width of the raster in pixels (computed from raster)';
COMMENT ON COLUMN raster_data.height IS 'Height of the raster in pixels (computed from raster)';
COMMENT ON COLUMN raster_data.band_count IS 'Number of bands in the raster (computed from raster)';
COMMENT ON COLUMN raster_data.pixel_type IS 'Pixel data type (e.g., 8BUI, 16BSI, 32BF)';
COMMENT ON COLUMN raster_data.srid IS 'Spatial reference system identifier';

COMMENT ON TABLE raster_statistics IS 'Cached statistical information for raster bands';
COMMENT ON TABLE raster_tiles IS 'Pre-computed tiles for fast web mapping access';

-- Log successful migration
-- SELECT 'Raster tables migration completed successfully' AS migration_status;