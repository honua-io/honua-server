-- Migration: Create Raster Data Tables
-- Description: Creates the necessary tables for storing raster data with PostGIS raster support
-- Version: 1.0
-- Date: 2026-02-08

-- postgis_raster is provisioned by infrastructure. The canonical runner includes
-- this raster migration root only when that optional extension is already installed;
-- migrations never attempt a privileged extension install on application startup.

-- Ensure the configured metadata schema exists (matches SchemaSearchPath.QualifyTable).
CREATE SCHEMA IF NOT EXISTS $HonuaSchema$;

-- Before this root joined the canonical public journal, raster provisioning and
-- runtime fallbacks always targeted honua. Adopt those tables before any IF NOT EXISTS
-- statement can create an empty configured-schema twin. A mixed source/target state is
-- ambiguous and must be reconciled by an operator rather than merged silently.
DO $$
DECLARE
    target_schema text := (
        SELECT nspname
        FROM pg_catalog.pg_namespace
        WHERE oid = to_regnamespace('$HonuaSchema$'));
    family text[] := ARRAY[
        'raster_data',
        'raster_statistics',
        'raster_tiles',
        'raster_layer_statistics',
        'raster_sensor_metadata',
        'raster_overviews',
        'raster_footprints'];
    source_count integer;
    target_count integer;
    table_name text;
BEGIN
    IF target_schema = 'honua' THEN
        RETURN;
    END IF;

    SELECT count(*) INTO source_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', 'honua', item.table_name)) IS NOT NULL;

    SELECT count(*) INTO target_count
    FROM unnest(family) AS item(table_name)
    WHERE to_regclass(format('%I.%I', target_schema, item.table_name)) IS NOT NULL;

    IF source_count > 0 AND target_count > 0 THEN
        RAISE EXCEPTION
            'Cannot adopt raster schema: % target table(s) in % coexist with % legacy table(s) in honua',
            target_count,
            target_schema,
            source_count;
    END IF;

    IF source_count = 0 THEN
        RETURN;
    END IF;

    FOREACH table_name IN ARRAY family LOOP
        IF to_regclass(format('%I.%I', 'honua', table_name)) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', 'honua', table_name, target_schema);
        END IF;
    END LOOP;
END
$$;

-- Create raster_data table for storing raster datasets.
-- NOTE: acquisition_date and its supporting indexes are added by 002_AddRasterAcquisitionDate.sql
-- (kept out of this migration so existing deployments that ran 001 before the column existed
-- still pick up the change via 002).
CREATE TABLE IF NOT EXISTS $HonuaSchema$.raster_data (
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

-- Store the raster payload EXTERNAL (out-of-line, UNCOMPRESSED) so dynamic
-- tile/terrain/statistics/export reads (ST_Clip / ST_Value / ST_SummaryStats)
-- fetch only the chunks they touch instead of detoasting and decompressing the
-- entire 25-115 MB monolithic row on every request (#1625). The PostGIS raster
-- type defaults to the compressed "main" strategy, which forces a full inflate.
ALTER TABLE $HonuaSchema$.raster_data ALTER COLUMN raster SET STORAGE EXTERNAL;

-- Create indices for performance
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id ON $HonuaSchema$.raster_data(layer_id);
CREATE INDEX IF NOT EXISTS idx_raster_data_name ON $HonuaSchema$.raster_data(name);
CREATE INDEX IF NOT EXISTS idx_raster_data_created_at ON $HonuaSchema$.raster_data(created_at);

-- Composite index for common query pattern: list rasters by layer
CREATE INDEX IF NOT EXISTS idx_raster_data_layer_id_id ON $HonuaSchema$.raster_data(layer_id, id);

-- Create spatial index on the raster column
CREATE INDEX IF NOT EXISTS idx_raster_data_raster_gist ON $HonuaSchema$.raster_data USING GIST (ST_ConvexHull(raster));

-- Create index on the raster envelope for faster spatial queries
CREATE INDEX IF NOT EXISTS idx_raster_data_envelope ON $HonuaSchema$.raster_data USING GIST (ST_Envelope(raster));

-- Create raster statistics table for caching computed statistics
CREATE TABLE IF NOT EXISTS $HonuaSchema$.raster_statistics (
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

    CONSTRAINT raster_statistics_raster_data_id_fk FOREIGN KEY (raster_data_id) REFERENCES $HonuaSchema$.raster_data(id) ON DELETE CASCADE,
    CONSTRAINT raster_statistics_unique_band UNIQUE (raster_data_id, band_number)
);

-- Create indices for raster statistics
CREATE INDEX IF NOT EXISTS idx_raster_statistics_raster_data_id ON $HonuaSchema$.raster_statistics(raster_data_id);

-- Create raster tiles table for pre-computed tiles (optional for performance)
CREATE TABLE IF NOT EXISTS $HonuaSchema$.raster_tiles (
    id BIGSERIAL PRIMARY KEY,
    raster_data_id BIGINT NOT NULL,
    zoom_level INTEGER NOT NULL,
    tile_x INTEGER NOT NULL,
    tile_y INTEGER NOT NULL,
    tile_data BYTEA NOT NULL,
    content_type VARCHAR(50) NOT NULL DEFAULT 'image/png',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT raster_tiles_raster_data_id_fk FOREIGN KEY (raster_data_id) REFERENCES $HonuaSchema$.raster_data(id) ON DELETE CASCADE,
    CONSTRAINT raster_tiles_unique_tile UNIQUE (raster_data_id, zoom_level, tile_x, tile_y)
);

-- Pre-rendered tiles are small PNG blobs read as whole rows; EXTERNAL keeps them
-- out-of-line and uncompressed so the indexed tile lookup returns bytes without a
-- decompression pass (#1625).
ALTER TABLE $HonuaSchema$.raster_tiles ALTER COLUMN tile_data SET STORAGE EXTERNAL;

-- Composite index for tile lookups (zoom + coordinates)
CREATE INDEX IF NOT EXISTS idx_raster_tiles_lookup ON $HonuaSchema$.raster_tiles(raster_data_id, zoom_level, tile_x, tile_y);

-- Create function to update updated_at timestamp
CREATE OR REPLACE FUNCTION $HonuaSchema$.update_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Create trigger to automatically update updated_at on raster_data updates
DROP TRIGGER IF EXISTS trg_raster_data_updated_at ON $HonuaSchema$.raster_data;
CREATE TRIGGER trg_raster_data_updated_at
    BEFORE UPDATE ON $HonuaSchema$.raster_data
    FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.update_updated_at();

-- Add comments for documentation
COMMENT ON TABLE $HonuaSchema$.raster_data IS 'Stores raster datasets using PostGIS raster type';
COMMENT ON COLUMN $HonuaSchema$.raster_data.id IS 'Unique identifier for the raster dataset';
COMMENT ON COLUMN $HonuaSchema$.raster_data.layer_id IS 'Reference to the layer this raster belongs to';
COMMENT ON COLUMN $HonuaSchema$.raster_data.name IS 'Display name for the raster dataset';
COMMENT ON COLUMN $HonuaSchema$.raster_data.raster IS 'PostGIS raster data with all bands and metadata';
COMMENT ON COLUMN $HonuaSchema$.raster_data.width IS 'Width of the raster in pixels (computed from raster)';
COMMENT ON COLUMN $HonuaSchema$.raster_data.height IS 'Height of the raster in pixels (computed from raster)';
COMMENT ON COLUMN $HonuaSchema$.raster_data.band_count IS 'Number of bands in the raster (computed from raster)';
COMMENT ON COLUMN $HonuaSchema$.raster_data.pixel_type IS 'Pixel data type (e.g., 8BUI, 16BSI, 32BF)';
COMMENT ON COLUMN $HonuaSchema$.raster_data.srid IS 'Spatial reference system identifier';

COMMENT ON TABLE $HonuaSchema$.raster_statistics IS 'Cached statistical information for raster bands';
COMMENT ON TABLE $HonuaSchema$.raster_tiles IS 'Pre-computed tiles for fast web mapping access';
