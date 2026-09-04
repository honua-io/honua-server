-- Migration: Create Raster Sensor Metadata Table
-- Description: Per-raster sensor/camera/orientation/RPC metadata backing ImageServer
--              height mensuration (#1879), orientation-ranked find (#1880), and
--              image-coordinate-system project warps (#1881). Interior/exterior
--              orientation and RPC payloads are JSONB so the model stays extensible
--              without per-field columns.
-- Version: 1.0
-- Date: 2026-06-19
--
-- NOTE: The canonical runtime migration is src/Honua.Server/Migrations/059_AddRasterSensorMetadata.sql
-- (DbUp, embedded in Honua.Server). This file mirrors it for the legacy Postgres migration
-- set; keep both definitions in sync.

CREATE TABLE IF NOT EXISTS honua.raster_sensor_metadata (
    raster_data_id BIGINT PRIMARY KEY REFERENCES honua.raster_data(id) ON DELETE CASCADE,
    sensor_name VARCHAR(255),
    camera_model VARCHAR(255),
    interior_orientation JSONB,
    exterior_orientation JSONB,
    rpc JSONB,
    dem_source VARCHAR(512),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_raster_sensor_metadata_has_exterior
    ON honua.raster_sensor_metadata (raster_data_id)
    WHERE exterior_orientation IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_raster_sensor_metadata_has_rpc
    ON honua.raster_sensor_metadata (raster_data_id)
    WHERE rpc IS NOT NULL;

COMMENT ON TABLE honua.raster_sensor_metadata IS 'Per-raster sensor/camera/orientation/RPC metadata for ImageServer mensuration, orientation-ranked find, and image-coordinate-system project warps.';
