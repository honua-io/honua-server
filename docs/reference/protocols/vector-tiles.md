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

## PMTiles range proxy

`GET/HEAD /api/v1/tiles/pmtiles/{artifactId}` streams a published PMTiles artifact with `Accept-Ranges: bytes`, `ETag`, and `Last-Modified` headers so MapLibre/PMTiles browser clients can issue HTTP range requests against private object storage. Unknown artifacts return 404. Artifacts are produced by the publish pipeline — see [Publish tiles — PMTiles](../../guides/publish/publish-tiles.md#pmtiles).

## Examples

```bash
# TileJSON metadata, then a tile
curl "https://server.example.com/tiles/0/tile.json"
curl -o tile.mvt "https://server.example.com/tiles/0/12/655/1586.mvt?where=status%3D'active'"
```

```bash
# H3-aggregated tile at resolution 7
curl -o h3.mvt "https://server.example.com/tiles/0/h3/8/40/98.mvt?resolution=7"
```

```bash
# MapLibre style for the layer
curl "https://server.example.com/api/styles/0.json"
```

## Conformance

OGC API Tiles CITE: 16/16 — see the [API standards summary](../compatibility/ogc-conformance.md) and [cite-status.md](../../cite-status.md). The raw `/tiles` XYZ routes are a Honua convenience surface, not an OGC standard.

## Guides that use this

- [Publish tiles](../../guides/publish/publish-tiles.md)
- [MapLibre web maps](../../guides/connect/maplibre-web-maps.md)
- [Style maps](../../guides/style/style-maps.md)
