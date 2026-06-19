-- Migration: 058_AllowGribMultidimCoverageFormat
-- Description: Allow the 'Grib' multidimensional-coverage format value (#1795).
--   Migration 025 constrained honua.multidim_coverage_catalog.format to
--   {'CloudOptimizedHdf5', 'NetCdf4'}. GRIB is served via the same ADR-0039
--   Path B convert-to-Zarr machinery as NetCDF, so widen the CHECK constraint
--   rather than editing the shipped migration (ADR-0045: forward-only,
--   non-colliding prefix > current maximum).

ALTER TABLE honua.multidim_coverage_catalog
    DROP CONSTRAINT IF EXISTS ck_multidim_coverage_format;

ALTER TABLE honua.multidim_coverage_catalog
    ADD CONSTRAINT ck_multidim_coverage_format
        CHECK (format IN ('CloudOptimizedHdf5', 'NetCdf4', 'Grib'));
