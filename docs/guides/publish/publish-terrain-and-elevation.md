# Publish terrain and elevation

You'll have a DEM served as Terrain-RGB tiles for MapLibre 3D terrain, plus numeric elevation queries, in about 10 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and a single-band DEM GeoTIFF.

Honua encodes registered single-band raster sources as Mapbox Terrain-RGB PNG tiles and exposes the same data through point/profile elevation endpoints. This guide covers the workflow; the full endpoint contract lives in the [terrain and elevation reference](../../reference/protocols/terrain-and-elevation.md).

## Steps

### 1. Import the DEM into a layer

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /api/v1/admin/import/raster` with form values `file=dem.tif`, `layerId=1`, and `name=City DEM`.

Terrain requires exactly one numeric elevation band and a consistent source CRS across the layer's rasters (see [Publish rasters](publish-rasters.md) for import options). Band values are treated as meters when no vertical unit is declared.

### 2. Fetch the terrain TileJSON

Open `http://localhost:8080/terrain/{layerId}/tile.json` in a browser, substituting the layer id.

`{datasetId}` accepts the numeric layer id or a layer collection name. The response is TileJSON 3.0 plus Honua `encoding`, `source`, and `noData` extensions; check `"supported": true` before wiring up clients.

### 3. Add the source to MapLibre

```js
map.addSource("dem", {
  type: "raster-dem",
  url: "http://localhost:8080/terrain/1/tile.json",
  encoding: "mapbox",
  tileSize: 256
});
map.setTerrain({ source: "dem" });
```

Tiles use the standard formula `elevationMeters = -10000 + ((R * 256 * 256 + G * 256 + B) * 0.1)`; no-data and uncovered pixels encode as `[0, 0, 0]` (`-10000 m`), so treat that value as the no-data sentinel.

### 4. Query elevation values

Open `http://localhost:8080/elevation/{layerId}/value?x=-122.45&y=37.76` in a browser.

Coordinates default to WGS 84; pass `srid` for projected input and `mosaicRule` (`newest`, `oldest`, `average`, `max`, `min`) to override the layer's mosaic default for the point lookup.

### 5. Sample an elevation profile

Use `SceneView.elevationProfile` from `@honua/sdk-js/scene-workspace` with the published layer id, the line from `[-122.45, 37.76]` to `[-122.40, 37.80]`, and `sampleCount: 100`.

Returns ordered distance/elevation samples along the line (geodesic distances, PostGIS 3.4+ required). `sampleCount` defaults to 100, capped by `Limits:Elevation:MaxSampleCount`.

## Verify

Open `http://localhost:8080/terrain/{layerId}/12/655/1583.png` in a browser. The response should be:

```text
200 image/png
```

A fully uncovered (but valid) tile coordinate still returns a valid all-sentinel PNG rather than `404`.

## Troubleshoot

- **`tile.json` reports `"supported": false`** — read `unsupportedReasons`: the usual causes are a multi-band source, an unusable SRID (such as `0`), or mixed CRSs across the layer's rasters.
- **Tile requests return `422`** — same source requirements as above; metadata can return `200` while tiles fail, so diagnose from `tile.json` first.
- **`404` for the dataset** — the layer id is unknown or has no registered raster source; confirm the step 1 import succeeded.
- **Terrain looks flat or spiky at edges** — clients rendering the `-10000 m` sentinel as real elevation; filter `[0, 0, 0]` pixels in the application.
- **Stale tiles after re-importing the DEM** — raster import invalidates the `terrain` cache tag automatically; if a proxy caches tiles, honor `Cache-Control` or purge it.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Terrain and elevation reference](../../reference/protocols/terrain-and-elevation.md) — full response contracts, caching, and limits.
- [Publish rasters](publish-rasters.md) — raster import options and mosaic strategies.
- [Publish 3D scenes](publish-3d-scenes.md) — 3D Tiles on top of your terrain.
