-- Persisted gray-plus-alpha fact. PostGIS drops GDAL color interpretation, so tile
-- rendering must not infer alpha from a shared unsigned pixel type.
ALTER TABLE IF EXISTS $HonuaSchema$.raster_data
    ADD COLUMN IF NOT EXISTS second_band_is_alpha boolean NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN $HonuaSchema$.raster_data.second_band_is_alpha IS
    'True when import metadata recorded band two as gray-plus-alpha, not scientific data.';
