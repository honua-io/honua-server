-- Migration: Create per-raster footprint / seamline store
-- Description: Stores a footprint polygon and an optional seamline (cutline) polygon per source
--              raster so esriMosaicSeamline can clip each raster to its seamline before the
--              union, instead of the ordering-only seam the other mosaic methods reuse (#1804,
--              the deferred companion to the mosaic-rule ordering methods shipped in #1789).
-- Version: 1.0
-- Date: 2026-06-19

DO $$
BEGIN
    IF to_regclass('honua.raster_data') IS NOT NULL THEN
        CREATE TABLE IF NOT EXISTS honua.raster_footprints (
            raster_data_id BIGINT PRIMARY KEY,
            -- Valid-data footprint of the raster (convex hull of the raster envelope at import).
            -- Untyped geometry so any raster SRID is accepted; the SRID is tracked in the srid
            -- column and carried on the geometry value itself.
            footprint geometry NOT NULL,
            -- Optional seamline / cutline used by esriMosaicSeamline to clip the raster before the
            -- union. Defaults to the footprint at import; a real cutline can replace it later.
            seamline geometry,
            -- SRID the footprint/seamline geometries are expressed in (mirrors the raster SRID so
            -- the mosaic clip transforms correctly).
            srid INTEGER NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMPTZ,

            CONSTRAINT raster_footprints_raster_data_id_fk
                FOREIGN KEY (raster_data_id) REFERENCES honua.raster_data(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_raster_footprints_footprint
            ON honua.raster_footprints USING GIST (footprint);

        COMMENT ON TABLE honua.raster_footprints IS
            'Per-raster footprint and optional seamline (cutline) for esriMosaicSeamline (#1804).';
    END IF;
END $$;
