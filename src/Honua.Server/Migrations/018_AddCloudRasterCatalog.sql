-- Migration: 018_AddCloudRasterCatalog
-- Description: Cloud-hosted COG registration catalog for direct range-request serving.

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
    CONSTRAINT uq_cloud_raster_object UNIQUE (provider, bucket, object_key)
);

CREATE INDEX IF NOT EXISTS idx_cloud_raster_layer ON honua.cloud_raster_catalog(layer_id);

-- Auto-update trigger for updated_at
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
