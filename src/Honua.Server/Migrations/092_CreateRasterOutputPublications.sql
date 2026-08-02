-- Migration: 092_CreateRasterOutputPublications
-- Description: Durable, idempotent visibility registry for referenced GP raster outputs.

CREATE TABLE IF NOT EXISTS honua.raster_output_publications (
    idempotency_key      VARCHAR(80) PRIMARY KEY,
    store_reference     VARCHAR(128) NOT NULL,
    object_key          VARCHAR(1024) NOT NULL,
    object_version      VARCHAR(256) NOT NULL,
    checksum_algorithm  VARCHAR(16) NOT NULL,
    checksum_value      VARCHAR(128) NOT NULL,
    size_bytes          BIGINT NOT NULL CHECK (size_bytes > 0),
    media_type          VARCHAR(127) NOT NULL,
    target_kind         VARCHAR(32) NOT NULL,
    target_reference    VARCHAR(128) NOT NULL,
    published_descriptor JSONB NOT NULL,
    output_descriptor    JSONB NOT NULL,
    visible_at          TIMESTAMPTZ NOT NULL,
    expires_at          TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_raster_output_publication_object
        UNIQUE (store_reference, object_key),
    CONSTRAINT ck_raster_output_publication_expiry
        CHECK (expires_at > visible_at)
);

CREATE INDEX IF NOT EXISTS idx_raster_output_publications_expiry
    ON honua.raster_output_publications (expires_at);

COMMENT ON TABLE honua.raster_output_publications IS
    'Atomic visibility and replay identity for referenced geoprocessing raster outputs.';
