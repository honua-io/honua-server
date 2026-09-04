-- Migration: 059_AddRasterSensorMetadata
-- Description: Add the raster_sensor_metadata companion table that models per-raster
--   sensor/camera/orientation/RPC metadata for ImageServer height mensuration (#1879),
--   orientation-ranked find (#1880), and image-coordinate-system project warps (#1881).
--   The interior/exterior orientation and RPC payloads are stored as JSONB so the model
--   stays extensible without per-field columns.
--
--   The raster_data table itself is provisioned outside the Honua.Server DbUp migration
--   set (Honua.Postgres/Migrations/001_CreateRasterTables.sql for Docker, tests/seed for
--   CI). Mirroring the to_regclass guard used by 055_SetRasterDataExternalStorage.sql,
--   this migration is a safe no-op on databases where the raster schema has not been
--   provisioned, and the companion table is only created (with its FK) once raster_data
--   exists. ADR-0045: forward-only, non-colliding prefix > current maximum.

CREATE SCHEMA IF NOT EXISTS honua;

DO $$
BEGIN
    IF to_regclass('honua.raster_data') IS NULL THEN
        RAISE NOTICE 'honua.raster_data not present; skipping raster_sensor_metadata creation (provisioned with the raster schema).';
        RETURN;
    END IF;

    CREATE TABLE IF NOT EXISTS honua.raster_sensor_metadata (
        raster_data_id BIGINT PRIMARY KEY
            REFERENCES honua.raster_data(id) ON DELETE CASCADE,
        sensor_name VARCHAR(255),
        camera_model VARCHAR(255),
        interior_orientation JSONB,
        exterior_orientation JSONB,
        rpc JSONB,
        dem_source VARCHAR(512),
        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    );

    -- Filter to rasters that carry orientation/RPC metadata quickly (the
    -- orientation-ranked find and image-CS warp paths only care about rows with payloads).
    CREATE INDEX IF NOT EXISTS idx_raster_sensor_metadata_has_exterior
        ON honua.raster_sensor_metadata (raster_data_id)
        WHERE exterior_orientation IS NOT NULL;

    CREATE INDEX IF NOT EXISTS idx_raster_sensor_metadata_has_rpc
        ON honua.raster_sensor_metadata (raster_data_id)
        WHERE rpc IS NOT NULL;

    COMMENT ON TABLE honua.raster_sensor_metadata IS
        'Per-raster sensor/camera/orientation/RPC metadata for ImageServer mensuration, orientation-ranked find, and image-coordinate-system project warps.';
    COMMENT ON COLUMN honua.raster_sensor_metadata.sensor_name IS 'Human-readable sensor name (e.g. WorldView-3).';
    COMMENT ON COLUMN honua.raster_sensor_metadata.camera_model IS 'Camera/instrument model identifier.';
    COMMENT ON COLUMN honua.raster_sensor_metadata.interior_orientation IS 'Interior orientation (focal length, principal point, distortion) as JSON.';
    COMMENT ON COLUMN honua.raster_sensor_metadata.exterior_orientation IS 'Exterior orientation (camera position, look vector, nadir point, off-nadir angle) as JSON.';
    COMMENT ON COLUMN honua.raster_sensor_metadata.rpc IS 'Rational Polynomial Coefficients image-to-ground model as JSON.';
    COMMENT ON COLUMN honua.raster_sensor_metadata.dem_source IS 'DEM source (layer id or named source) used for base/top elevation differencing.';
END
$$;
