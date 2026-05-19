-- Migration: 025_AddMultidimensionalCoverageCatalog
-- Description: Cloud-optimized HDF5 / NetCDF4 multidimensional coverage registration catalog.
-- See ADR-0039 for the reader strategy that this catalog feeds.

CREATE TABLE IF NOT EXISTS honua.multidim_coverage_catalog (
    id                  BIGSERIAL PRIMARY KEY,
    layer_id            INTEGER NOT NULL,
    name                VARCHAR(255) NOT NULL,
    description         TEXT,
    format              VARCHAR(50) NOT NULL,
    provider            VARCHAR(50) NOT NULL,
    bucket              VARCHAR(255) NOT NULL,
    object_key          VARCHAR(1024) NOT NULL,
    variables           JSONB NOT NULL DEFAULT '[]'::jsonb,
    metadata            JSONB,
    metadata_scanned_at TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ,
    CONSTRAINT fk_multidim_coverage_layer FOREIGN KEY (layer_id)
        REFERENCES honua.layers(layer_id) ON DELETE CASCADE,
    CONSTRAINT uq_multidim_coverage_object UNIQUE (layer_id, provider, bucket, object_key),
    CONSTRAINT ck_multidim_coverage_format
        CHECK (format IN ('CloudOptimizedHdf5', 'NetCdf4'))
);

CREATE INDEX IF NOT EXISTS idx_multidim_coverage_layer ON honua.multidim_coverage_catalog(layer_id);

CREATE OR REPLACE FUNCTION honua.update_multidim_coverage_catalog_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_multidim_coverage_catalog_updated_at ON honua.multidim_coverage_catalog;
CREATE TRIGGER trg_multidim_coverage_catalog_updated_at
    BEFORE UPDATE ON honua.multidim_coverage_catalog
    FOR EACH ROW
    EXECUTE FUNCTION honua.update_multidim_coverage_catalog_updated_at();
