-- Migration: 093_CreateRasterOutputImportChunks
-- Description: Transactional, bounded-chunk staging for durable PostGIS raster registration.

CREATE TABLE IF NOT EXISTS honua.raster_output_import_chunks (
    idempotency_key VARCHAR(80) NOT NULL,
    chunk_ordinal   INTEGER NOT NULL CHECK (chunk_ordinal >= 0),
    content         BYTEA NOT NULL CHECK (octet_length(content) > 0),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (idempotency_key, chunk_ordinal)
);

-- Staging rows normally exist only inside the registration transaction. The timestamp index
-- supports an operator cleanup sweep if a database crash leaves an uncommitted-import recovery
-- artifact visible after failover.
CREATE INDEX IF NOT EXISTS idx_raster_output_import_chunks_created
    ON honua.raster_output_import_chunks (created_at);

COMMENT ON TABLE honua.raster_output_import_chunks IS
    'Short-lived bounded chunks used to decode a staged raster without one web/Npgsql byte array.';
