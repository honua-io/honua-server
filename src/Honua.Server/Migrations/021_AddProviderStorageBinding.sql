-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Provider-ready connection and layer storage binding.
-- This is runtime binding metadata only; it is not a separate metadata repository.

ALTER TABLE IF EXISTS honua.data_connections
    ADD COLUMN IF NOT EXISTS provider_name TEXT NOT NULL DEFAULT 'postgis';

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS primary_key_column TEXT NOT NULL DEFAULT 'objectid';

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS geometry_column TEXT DEFAULT 'geometry';

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS storage_srid INT;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS temporal_column TEXT;

ALTER TABLE IF EXISTS honua.layers
    ADD COLUMN IF NOT EXISTS storage_options JSONB NOT NULL DEFAULT '{}'::jsonb;

UPDATE honua.layers
SET storage_srid = srid
WHERE storage_srid IS NULL;

CREATE INDEX IF NOT EXISTS idx_data_connections_provider_name
    ON honua.data_connections(provider_name);

COMMENT ON COLUMN honua.data_connections.provider_name IS
    'Canonical provider engine used to resolve feature-store implementation, e.g. postgis, postgresql, sqlserver, mysql, duckdb';

COMMENT ON COLUMN honua.layers.primary_key_column IS
    'Physical primary key or object identifier column for provider-backed execution';

COMMENT ON COLUMN honua.layers.geometry_column IS
    'Physical geometry column for provider-backed execution';

COMMENT ON COLUMN honua.layers.storage_srid IS
    'SRID/CRS used by the stored geometry';

COMMENT ON COLUMN honua.layers.temporal_column IS
    'Optional physical temporal column used by time-aware layers';

COMMENT ON COLUMN honua.layers.storage_options IS
    'Provider-specific storage binding options when neutral layer fields are not sufficient';
