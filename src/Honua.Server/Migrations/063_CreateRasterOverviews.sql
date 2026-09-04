-- Migration: Create persisted raster overview pyramids
-- Description: Stores reduced-resolution overview rasters per source raster so the dynamic
--              tile read path reuses a pre-built pyramid level instead of recomputing an
--              ST_Rescale reduction of the full-resolution raster on every low-zoom request
--              (#1836, follow-up to the on-the-fly overview selection shipped in #1793).
-- Version: 1.0
-- Date: 2026-06-19

-- Guard: only create the overview table when raster_data exists in this schema. Schemas that
-- never provisioned the raster tables (001_CreateRasterTables) skip this safely, matching the
-- defensive to_regclass guards used by 055_SetRasterDataExternalStorage.
DO $$
BEGIN
    IF to_regclass('honua.raster_data') IS NOT NULL THEN
        CREATE TABLE IF NOT EXISTS honua.raster_overviews (
            id BIGSERIAL PRIMARY KEY,
            raster_data_id BIGINT NOT NULL,
            -- Power-of-two reduction factor relative to the source (2 = half resolution, 4 = quarter, ...).
            overview_factor INTEGER NOT NULL,
            -- Reduced-resolution raster, reprojected to EPSG:3857 (the tile CRS) so the tile read
            -- path resamples a pyramid level directly without an extra reprojection.
            raster raster NOT NULL,
            -- Ground sample distance (metres/pixel in EPSG:3857) cached for level selection without
            -- detoasting the raster payload.
            ground_resolution DOUBLE PRECISION NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

            CONSTRAINT raster_overviews_raster_data_id_fk
                FOREIGN KEY (raster_data_id) REFERENCES honua.raster_data(id) ON DELETE CASCADE,
            CONSTRAINT raster_overviews_unique_factor UNIQUE (raster_data_id, overview_factor),
            CONSTRAINT raster_overviews_factor_positive CHECK (overview_factor >= 2)
        );

        -- Overview rasters are monolithic payloads read chunk-wise like raster_data; EXTERNAL
        -- storage keeps them out-of-line and uncompressed so ST_Resample fetches only the
        -- chunks it touches (#1625 rationale).
        ALTER TABLE honua.raster_overviews ALTER COLUMN raster SET STORAGE EXTERNAL;

        CREATE INDEX IF NOT EXISTS idx_raster_overviews_raster_data_id
            ON honua.raster_overviews(raster_data_id);

        -- Level-selection lookup: scan a raster's overviews ordered by resolution.
        CREATE INDEX IF NOT EXISTS idx_raster_overviews_lookup
            ON honua.raster_overviews(raster_data_id, ground_resolution);

        COMMENT ON TABLE honua.raster_overviews IS
            'Persisted reduced-resolution overview pyramids for raster_data (tile read-path reuse, #1836).';
    END IF;
END $$;
