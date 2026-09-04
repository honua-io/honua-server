-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Make raster tile reads cheap by switching the monolithic raster payload to
-- EXTERNAL (out-of-line, UNCOMPRESSED) TOAST storage (#1625).
--
-- Background: a logical raster is stored as a single honua.raster_data row whose
-- "raster" column carries the full 25-115 MB PostGIS raster.  The PostGIS raster
-- type defaults to the "main" TOAST strategy, which keeps the value inline where
-- possible and COMPRESSES it.  Every dynamic tile/terrain/statistics/export path
-- (ST_Clip, ST_Value, ST_SummaryStats over honua.raster_data.raster) must then
-- fully detoast AND decompress the entire row before it can read the small window
-- it actually needs -- 7-16 s/tile in production, and a browser's parallel tile
-- burst saturates the connection pool (Npgsql timeouts -> 500s).
--
-- EXTERNAL storage stores the value out-of-line and uncompressed, which lets the
-- TOAST machinery fetch only the chunks a clip/value touches instead of inflating
-- the whole raster.  This is the storage class PostGIS recommends for large
-- rasters served as tiles.  It preserves the single-row logical-raster identity
-- exactly: honua.raster_data.id still keys one row, and the
-- width/height/band_count/srid/pixel_type GENERATED columns (ST_Width(raster) etc.)
-- are unchanged -- only the on-disk storage strategy of the existing column moves.
--
-- ALTER ... SET STORAGE only changes the strategy for rows written AFTER the
-- statement; pre-existing rows keep their current TOAST until rewritten.  The
-- accompanying VACUUM FULL is intentionally NOT issued here (it takes an exclusive
-- lock and can be very slow on large tables); operators who want existing rows
-- converted can run "VACUUM FULL honua.raster_data;" during a maintenance window,
-- and the import path re-applies this storage class before inserting each new row
-- so freshly imported rasters are always stored EXTERNAL.
--
-- Guarded with to_regclass so this is a safe no-op on databases where the raster
-- schema has not been provisioned.
DO $$
BEGIN
    IF to_regclass('honua.raster_data') IS NOT NULL THEN
        EXECUTE 'ALTER TABLE honua.raster_data ALTER COLUMN raster SET STORAGE EXTERNAL';
    END IF;

    -- Pre-rendered tiles are small, snap-to-grid PNG blobs read as whole rows;
    -- EXTERNAL keeps them out-of-line and uncompressed so the indexed tile lookup
    -- returns the bytes without a decompression pass.
    IF to_regclass('honua.raster_tiles') IS NOT NULL THEN
        EXECUTE 'ALTER TABLE honua.raster_tiles ALTER COLUMN tile_data SET STORAGE EXTERNAL';
    END IF;
END
$$;
