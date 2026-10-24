-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Tile cache catalog tables for the XYZ/TMS tile-cache export emitted by the
-- WMTS migration planner (slice 4 of issue #1016). The catalog stores one row
-- per (source layer, tile-matrix-set, style, format) tile cache and one row
-- per (z, x, y) tile. Repeated exports are idempotent because tile rows are
-- keyed on (cache_id, zoom_level, tile_column, tile_row).

CREATE TABLE IF NOT EXISTS honua.tile_caches (
    tile_cache_id      TEXT PRIMARY KEY,
    layer_identifier   TEXT NOT NULL,
    tile_matrix_set    TEXT NOT NULL,
    source_service_url TEXT NOT NULL,
    tile_format        TEXT NOT NULL DEFAULT 'image/png',
    style_identifier   TEXT NOT NULL DEFAULT 'default',
    min_zoom           INTEGER NOT NULL,
    max_zoom           INTEGER NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (layer_identifier, tile_matrix_set, style_identifier, tile_format, source_service_url)
);

COMMENT ON TABLE honua.tile_caches IS 'Tile-cache catalog populated by the WMTS migration tile-cache exporter (#1016 slice 4).';
COMMENT ON COLUMN honua.tile_caches.tile_cache_id IS 'Stable identifier derived from layer + tile-matrix-set + style + format + source URL.';

CREATE TABLE IF NOT EXISTS honua.tile_cache_entries (
    tile_cache_id  TEXT NOT NULL REFERENCES honua.tile_caches (tile_cache_id) ON DELETE CASCADE,
    zoom_level     INTEGER NOT NULL,
    tile_column    INTEGER NOT NULL,
    tile_row       INTEGER NOT NULL,
    content_type   TEXT NOT NULL,
    content        BYTEA NOT NULL,
    source_url     TEXT NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tile_cache_id, zoom_level, tile_column, tile_row),
    CHECK (zoom_level >= 0),
    CHECK (tile_column >= 0),
    CHECK (tile_row >= 0)
);

COMMENT ON TABLE honua.tile_cache_entries IS 'Per-tile rows for a tile-cache populated by the WMTS migration tile-cache exporter (#1016 slice 4).';

CREATE INDEX IF NOT EXISTS tile_cache_entries_cache_zoom_idx
    ON honua.tile_cache_entries (tile_cache_id, zoom_level);
