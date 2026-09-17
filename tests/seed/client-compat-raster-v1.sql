-- Raster coverage for the client-compatibility certification fixture.
--
-- WHY THIS EXISTS
--
-- tests/seed/client-compat-v1.sql already creates the raster tables, enables
-- postgis_raster, declares an image service (`svc-<service>-image`) advertising
-- ImageServer / Wcs / OGC-API-Coverages, and binds a storage resource to
-- honua.raster_data. What it never does is insert a raster row. So every
-- coverage-serving surface resolves its service and then finds nothing to serve:
-- IRasterStore.GetPrimaryRasterInfoAsync returns null, and WCS GetCoverage,
-- ImageServer exportImage/identify and OGC API Coverages are all unexercisable
-- from any client. That is why the certified client surface covers only the
-- vector protocols.
--
-- This seed adds exactly one deterministic coverage on layer 0, whose
-- georeferencing matches the extent layer 0 already declares in
-- client-compat-v1.sql:
--
--     ST_MakeEnvelope(-122.5, 37.7, -122.35, 37.84, 4326)
--
-- 64 x 64 pixels at 0.00234375 x 0.0021875 degrees lands exactly on that box
-- (-122.5 + 64*0.00234375 = -122.35, 37.84 - 64*0.0021875 = 37.70), so the
-- coverage extent, the layer extent and the vector features all describe the
-- same ground area and a client can compare them.
--
-- ONE raster, not several: IRasterStore.GetPrimaryRasterInfoAsync picks a single
-- primary raster per layer, so adding a second would make which one is served
-- depend on ordering.
--
-- PIXEL VALUES ARE POSITION-REVEALING ON PURPOSE
--
-- A constant-valued raster cannot detect a georeferencing or subsetting defect,
-- because every window of it looks identical - exactly the class of bug that
-- matters for WCS BBOX and ImageServer extent handling. Instead the band carries
-- a constant background with four distinct landmark pixels near the corners, so
-- a request for a sub-window can assert which landmarks it should and should not
-- contain. Values are chosen to be unambiguous in a float band and far from the
-- nodata value.
--
--     background                 100
--     landmark NW  (col 1,  row 1)    10
--     landmark NE  (col 62, row 1)    20
--     landmark SW  (col 1,  row 62)   30
--     landmark SE  (col 62, row 62)   40
--     nodata                     -9999
--
-- Column/row are 1-based in ST_SetValue, and row 1 is the northern edge because
-- the y scale is negative.
--
-- Idempotent: the WHERE NOT EXISTS guard makes re-running a no-op, matching the
-- other seeds in this directory.

INSERT INTO honua.raster_data (layer_id, name, description, raster, acquisition_date)
SELECT
    0,
    'Client Compat Coverage',
    'Deterministic single-band coverage for WCS, ImageServer and OGC API Coverages client certification.',
    ST_SetValue(
        ST_SetValue(
            ST_SetValue(
                ST_SetValue(
                    ST_AddBand(
                        ST_MakeEmptyRaster(
                            64, 64,              -- width, height
                            -122.5, 37.84,       -- upper-left x, y
                            0.00234375,          -- x scale
                            -0.0021875,          -- y scale (north-up)
                            0, 0,                -- skew
                            4326),
                        '32BF'::text, 100, -9999),
                    1, 1, 1, 10),                -- NW landmark
                1, 62, 1, 20),                   -- NE landmark
            1, 1, 62, 30),                       -- SW landmark
        1, 62, 62, 40),                          -- SE landmark
    TIMESTAMPTZ '2024-01-01 00:00:00+00'
WHERE NOT EXISTS (
    SELECT 1 FROM honua.raster_data WHERE layer_id = 0 AND name = 'Client Compat Coverage'
);

-- Band statistics so ImageServer computeStatisticsHistograms and any
-- stretch-based rendering have deterministic inputs rather than having to scan.
-- 4096 pixels total, four of which are landmarks.
WITH coverage AS (
    SELECT id FROM honua.raster_data
     WHERE layer_id = 0 AND name = 'Client Compat Coverage'
     LIMIT 1
)
INSERT INTO honua.raster_statistics
    (raster_data_id, band_number, min_value, max_value, mean_value, std_dev,
     valid_pixel_count, nodata_pixel_count)
SELECT
    coverage.id,
    1,
    10.0,
    100.0,
    -- (4092 * 100 + 10 + 20 + 30 + 40) / 4096
    99.9024,
    2.7,
    4096,
    0
FROM coverage
ON CONFLICT (raster_data_id, band_number) DO UPDATE SET
    min_value = EXCLUDED.min_value,
    max_value = EXCLUDED.max_value,
    mean_value = EXCLUDED.mean_value,
    std_dev = EXCLUDED.std_dev,
    valid_pixel_count = EXCLUDED.valid_pixel_count,
    nodata_pixel_count = EXCLUDED.nodata_pixel_count,
    computed_at = NOW();
