# Vector tiles

Honua serves Mapbox Vector Tiles (MVT) per layer with TileJSON metadata and a generated MapLibre style, plus a range proxy for published PMTiles artifacts.

## Base endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | Vector tile for a layer (XYZ scheme, WebMercator). |
| GET | `/tiles/{layerId}/h3/{z}/{x}/{y}.mvt` | H3 hexagon-aggregated vector tile. |
| GET | `/tiles/{layerId}/tile.json` | TileJSON 3.0 metadata for the layer. |
| GET | `/api/styles/{layerId}.json` | Generated MapLibre style document for the layer. |
| GET, HEAD | `/api/v1/tiles/pmtiles/{*artifactId}` | Range-proxied access to a published PMTiles artifact. |

OGC API Tiles offers the standards-based equivalent of the XYZ routes — see [OGC APIs](ogc-apis.md). Cached raster tiles for GeoServices clients are served at `/rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}` ([GeoServices REST](geoservices-rest.md)).

## Key parameters

| Endpoint | Parameter | Notes |
| --- | --- | --- |
| `{z}/{x}/{y}.mvt` | `where` | SQL-style attribute filter applied before tiling. |
| `{z}/{x}/{y}.mvt` | `time` | Temporal filter for time-enabled layers. |
| `h3/{z}/{x}/{y}.mvt` | `where` | Attribute filter. |
| `h3/{z}/{x}/{y}.mvt` | `resolution` | H3 resolution for hexagon aggregation. |

Unknown query parameters are rejected with 400. Zoom is validated against the configured `Limits:Tiles` range; out-of-range `x`/`y` return 400. Tiles require the FeatureServer protocol to be enabled on the layer and respect layer access policies.

Cache lifecycle: tile responses carry a `Cache-Control: max-age=N` header. The TTL is resolved per tileset — a per-tileset override from the `TileOptions:TilesetLifecycle` configuration (keyed `serviceId/layerId/tileMatrixSetId`) takes precedence, otherwise the global `TileOptions:CacheMaxAge` applies.

Metatiling: the seed/render path renders tiles in aligned N×N metatile blocks per pass, controlled by `TileOptions:MetatileFactor` (default `1`, which keeps the per-tile behavior). Larger factors group neighbouring tiles so the provider amortizes per-tile setup.

Size-quota / LRU eviction: `TileOptions:Eviction` (`Enabled`, `MaxEntries`, `MaxBytes`) declares when least-recently-used cached tiles are dropped. The decision policy is built in; for the Redis-backed tile cache, operators can alternatively rely on the infrastructure `maxmemory-policy allkeys-lru`. Disabled by default (TTL-only).

Scheduled invalidation: `TileCacheExpiry` (`Enabled`, `IntervalSeconds`, `Targets[]` of `serviceId`/`layerId`/`tileMatrixSetId`) periodically dispatches an `invalidate` tile operation per target — the time-based complement to the per-tileset TTL. Disabled by default; the sweep interval is clamped to a 60s minimum.

## PMTiles range proxy

`GET/HEAD /api/v1/tiles/pmtiles/{artifactId}` streams a published PMTiles artifact with `Accept-Ranges: bytes`, `ETag`, and `Last-Modified` headers so MapLibre/PMTiles browser clients can issue HTTP range requests against private object storage. Unknown artifacts return 404. Artifacts are produced by the publish pipeline — see [Publish tiles — PMTiles](../../guides/publish/publish-tiles.md#pmtiles).

## Examples

> Open `https://server.example.com/tiles/0/tile.json`, `https://server.example.com/tiles/0/12/655/1586.mvt?where=status%3D` in a browser.

> Open `https://server.example.com/tiles/0/h3/8/40/98.mvt?resolution=7` in a browser.

> Open `https://server.example.com/api/styles/0.json` in a browser.

## Conformance

OGC API Tiles CITE: 16/16 — see the [API standards summary](../compatibility/ogc-conformance.md) and [cite-status.md](../../cite-status.md). The raw `/tiles` XYZ routes are a Honua convenience surface, not an OGC standard.

## Guides that use this

- [Publish tiles](../../guides/publish/publish-tiles.md)
- [MapLibre web maps](../../guides/connect/maplibre-web-maps.md)
- [Style maps](../../guides/style/style-maps.md)
