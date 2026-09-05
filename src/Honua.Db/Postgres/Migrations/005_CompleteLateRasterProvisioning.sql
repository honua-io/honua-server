-- Migration: Complete raster provisioning after late extension enablement
-- Description: Creates raster capabilities whose historical server migrations were safely
--              journaled as no-ops when postgis_raster was not yet provisioned. This provider
--              migration runs when infrastructure later enables the optional extension.
-- Version: 1.0
-- Date: 2026-09-02

CREATE TABLE IF NOT EXISTS $HonuaSchema$.raster_overviews (
    id BIGSERIAL PRIMARY KEY,
    raster_data_id BIGINT NOT NULL,
    overview_factor INTEGER NOT NULL,
    raster raster NOT NULL,
    ground_resolution DOUBLE PRECISION NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT raster_overviews_raster_data_id_fk
        FOREIGN KEY (raster_data_id) REFERENCES $HonuaSchema$.raster_data(id) ON DELETE CASCADE,
    CONSTRAINT raster_overviews_unique_factor UNIQUE (raster_data_id, overview_factor),
    CONSTRAINT raster_overviews_factor_positive CHECK (overview_factor >= 2)
);

ALTER TABLE $HonuaSchema$.raster_overviews ALTER COLUMN raster SET STORAGE EXTERNAL;

CREATE INDEX IF NOT EXISTS idx_raster_overviews_raster_data_id
    ON $HonuaSchema$.raster_overviews(raster_data_id);

CREATE INDEX IF NOT EXISTS idx_raster_overviews_lookup
    ON $HonuaSchema$.raster_overviews(raster_data_id, ground_resolution);

COMMENT ON TABLE $HonuaSchema$.raster_overviews IS
    'Persisted reduced-resolution overview pyramids for raster_data (tile read-path reuse, #1836).';

CREATE TABLE IF NOT EXISTS $HonuaSchema$.raster_footprints (
    raster_data_id BIGINT PRIMARY KEY,
    footprint geometry NOT NULL,
    seamline geometry,
    srid INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ,

    CONSTRAINT raster_footprints_raster_data_id_fk
        FOREIGN KEY (raster_data_id) REFERENCES $HonuaSchema$.raster_data(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_raster_footprints_footprint
    ON $HonuaSchema$.raster_footprints USING GIST (footprint);

COMMENT ON TABLE $HonuaSchema$.raster_footprints IS
    'Per-raster footprint and optional seamline (cutline) for esriMosaicSeamline (#1804).';
