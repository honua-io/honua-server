-- Metadata-only additions preserve every pre-existing tile payload. Existing rows and
-- old-binary inserts default to renderer_version = 0. A matching hash is required because
-- an old writer can overwrite tile_data on an existing versioned row without updating it.
ALTER TABLE $HonuaSchema$.raster_tiles
    ADD COLUMN IF NOT EXISTS renderer_version integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS renderer_payload_sha256 bytea;

COMMENT ON COLUMN $HonuaSchema$.raster_tiles.renderer_version IS
    'Tile sampling implementation version; zero means provenance is unknown.';
COMMENT ON COLUMN $HonuaSchema$.raster_tiles.renderer_payload_sha256 IS
    'SHA-256 of the exact versioned tile_data bytes, checked before cache admission.';
