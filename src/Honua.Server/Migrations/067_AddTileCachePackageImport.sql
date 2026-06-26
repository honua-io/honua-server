-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 067_AddTileCachePackageImport.sql
-- Adds the serving-binding columns the Esri tile/vector-tile cache package importer
-- (#1269) needs on top of the WMTS tile-cache catalog (033_CreateTileCacheCatalog).
--
-- The catalog already stores one row per tile-cache and one row per (z, x, y) tile,
-- but it was modelled for the WMTS export planner: a tile-cache was implicitly raster,
-- always sourced from a live WMTS service URL. Importing prebuilt Esri packages
-- (.tpk/.tpkx/.vtpk) adds two needs:
--   * data_type    — packages may carry vector (.vtpk -> MVT/PBF) or raster
--                     (.tpk/.tpkx -> PNG/JPEG) tiles; the serving binding must know
--                     which so it advertises the right media type / TileJSON.
--   * tileset_title — an optional human-readable title carried into the served
--                     tileset descriptor (TileJSON "name").
--
-- It is intentionally additive and idempotent (ADD COLUMN IF NOT EXISTS), and only
-- runs its ALTERs when the catalog table exists so plain images that never ran 033
-- skip it cleanly. The existing WMTS export sink keeps writing rows without these
-- columns; data_type defaults to 'raster' to preserve the pre-import semantics.

DO $$
BEGIN
    IF to_regclass('honua.tile_caches') IS NOT NULL THEN
        ALTER TABLE honua.tile_caches
            ADD COLUMN IF NOT EXISTS data_type TEXT NOT NULL DEFAULT 'raster';
        ALTER TABLE honua.tile_caches
            ADD COLUMN IF NOT EXISTS tileset_title TEXT;

        COMMENT ON COLUMN honua.tile_caches.data_type IS
            'Tile payload kind served from this cache: ''raster'' (PNG/JPEG) or ''vector'' (MVT/PBF). Set by the package importer (#1269); defaults to raster for the WMTS exporter.';
        COMMENT ON COLUMN honua.tile_caches.tileset_title IS
            'Optional human-readable title carried into the served TileJSON descriptor (#1269).';
    END IF;
END $$;
