-- Migration: Add raster acquisition timestamps for temporal mosaics
-- Description: Adds acquisition_date metadata and supporting indexes to raster_data
-- Version: 1.1
-- Date: 2026-04-09

ALTER TABLE IF EXISTS honua.raster_data
    ADD COLUMN IF NOT EXISTS acquisition_date TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS idx_raster_data_acquisition_date
    ON honua.raster_data(acquisition_date);

CREATE INDEX IF NOT EXISTS idx_raster_data_layer_acquisition
    ON honua.raster_data(layer_id, acquisition_date DESC, created_at DESC, id DESC);

COMMENT ON COLUMN honua.raster_data.acquisition_date
    IS 'Optional acquisition timestamp for temporal mosaic selection';
