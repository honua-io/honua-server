-- Migration: Create Raster Layer Statistics Table
-- Description: Persists layer-level (mosaic) band statistics so GeoServices ImageServer
--              service metadata is served from persisted values instead of recomputing
--              ST_SummaryStats over every raster tile per request (#1639).
-- Version: 1.0
-- Date: 2026-06-12
--
-- Rows are keyed by (layer_id, merge_strategy, raster_signature, band_number) where
-- raster_signature is a deterministic digest of the layer's raster-id set. Importing or
-- deleting a raster changes the signature, so stale rows are ignored and pruned by the
-- next compute-once-then-persist backfill.
--
-- NOTE: PostgresRasterStore also self-provisions this table at runtime
-- (CREATE TABLE IF NOT EXISTS) so deployments registered before this migration backfill
-- lazily. Keep this definition byte-compatible with
-- PostgresRasterStore.TryEnsureLayerStatisticsTableAsync.

CREATE TABLE IF NOT EXISTS honua.raster_layer_statistics (
    layer_id INTEGER NOT NULL,
    merge_strategy VARCHAR(32) NOT NULL,
    raster_signature TEXT NOT NULL,
    band_number INTEGER NOT NULL,
    min_value DOUBLE PRECISION,
    max_value DOUBLE PRECISION,
    mean_value DOUBLE PRECISION,
    std_dev DOUBLE PRECISION,
    valid_pixel_count BIGINT,
    nodata_pixel_count BIGINT,
    computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (layer_id, merge_strategy, raster_signature, band_number)
);

COMMENT ON TABLE honua.raster_layer_statistics IS 'Persisted layer-level (mosaic) band statistics served by ImageServer/WCS metadata endpoints; invalidated by raster_signature when the layer''s raster set changes';
