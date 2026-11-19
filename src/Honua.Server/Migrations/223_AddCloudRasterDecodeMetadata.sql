-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- A COG scan records how its samples decode (bit depth, predictor, byte order, photometric
-- interpretation, planar configuration) and its GDAL NoData value, so reads from the catalog
-- decode and skip NoData exactly as the scan saw them. Rows scanned before this migration keep
-- NULLs, which read back as the previous defaults until the COG is scanned again. Additive only.
ALTER TABLE honua.cloud_raster_catalog
    ADD COLUMN IF NOT EXISTS bits_per_sample INTEGER,
    ADD COLUMN IF NOT EXISTS predictor INTEGER,
    ADD COLUMN IF NOT EXISTS is_little_endian BOOLEAN,
    ADD COLUMN IF NOT EXISTS photometric_interpretation INTEGER,
    ADD COLUMN IF NOT EXISTS planar_configuration INTEGER,
    ADD COLUMN IF NOT EXISTS nodata TEXT;
