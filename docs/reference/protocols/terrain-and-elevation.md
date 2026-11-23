---
type: reference
title: "Terrain and elevation"
description: "Honua serves registered raster/DEM datasets as Terrain-RGB PNG tiles, quantized-mesh terrain tiles, and JSON elevation queries."
resource: "honua://capability/serve.elevation"
resources:
  - "honua://capability/raster.terrain-rgb"
  - "honua://capability/serve.terrain-quantized-mesh"
  - "honua://capability/analytics.line-of-sight"
  - "honua://capability/analytics.viewshed"
  - "honua://capability/analytics.sun-shadow"
  - "honua://capability/analytics.slice"
---
# Terrain and elevation

Honua serves registered raster/DEM datasets as Terrain-RGB PNG tiles for MapLibre/Mapbox `raster-dem` rendering, quantized-mesh terrain tiles for Cesium clients, and JSON elevation endpoints for numeric point, profile, and surface-analysis queries. These surfaces share the same raster catalog and mosaic pipeline.

## Base endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/terrain/{datasetId}/tile.json` | TileJSON 3.0 metadata with Honua terrain extensions. |
| GET | `/terrain/{datasetId}/{z}/{x}/{y}.png` | 256x256 Terrain-RGB elevation tile (Mapbox encoding). |
| GET | `/terrain/{datasetId}/layer.json` | Quantized-mesh metadata with geographic TMS availability and cross-format links. |
| GET | `/terrain/{datasetId}/{z}/{x}/{y}.terrain` | Gzipped quantized-mesh-1.0 elevation tile. |
| GET | `/elevation/{datasetId}/value` | Numeric elevation at one coordinate. |
| GET | `/elevation/{datasetId}/profile` | Elevation samples along a WKT LineString. |
| POST | `/elevation/{datasetId}/line-of-sight` | Point-to-point visibility over the elevation surface (Pro tier; `402` when entitlement inactive). |
| POST | `/elevation/{datasetId}/viewshed` | Visible-area analysis from an observer point (Pro tier; `402` when entitlement inactive). |
| POST | `/elevation/{datasetId}/sun-shadow` | Sun/shadow analysis for a date/time (Pro tier; `402` when entitlement inactive). |
| POST | `/elevation/{datasetId}/slice` | Vertical slice through the elevation surface (Pro tier; `402` when entitlement inactive). |

`{datasetId}` accepts a numeric layer id or a layer collection name. The Terrain protocol must be enabled for the tile routes and the Elevation protocol for the query routes (both are part of the default protocol set when `EnabledProtocols` is omitted). Tile coordinates are validated against the configured `Limits:Tiles` zoom range.

Terrain-RGB tiles are 8-bit RGBA PNGs. Finite elevation samples have alpha `255` and retain the Terrain-RGB formula `meters = -10000 + (R * 65536 + G * 256 + B) * 0.1`. Source NoData, non-finite samples and uncovered pixels have RGBA `[0, 0, 0, 0]`. Honor alpha when interpreting missing data: a valid elevation of `-10000 m` has the same RGB bytes but alpha `255`. A fully uncovered valid tile returns a transparent PNG with `200`, not `404`.

## Quantized-mesh terrain

`layer.json` declares `format: "quantized-mesh-1.0"`, `scheme: "tms"`, and `projection: "EPSG:4326"`, with `bounds`, `minzoom`, `maxzoom`, tile URL templates, and per-level `available` ranges. Honua adds `coverage`, `capabilities`, `links`, `source`, `supported`, and `unsupportedReasons`. Check `supported` before requesting tiles; an unsupported source can still return metadata with `200` and empty availability.

Mesh tiles use a geographic TMS grid: level `z` has `2 × 2^z` columns and `2^z` rows, with row zero at the south edge. Use the manifest's tile template and availability rather than reusing Terrain-RGB Web Mercator coordinates.

Tile responses use `Content-Type: application/vnd.quantized-mesh` and `Content-Encoding: gzip`. Request the advertised vertex-normal extension with `extensions=octvertexnormals` or an `Accept: application/vnd.quantized-mesh; extensions=octvertexnormals` header. Responses with normals include that extension parameter in their content type; tile responses include `Vary: Accept`.

## Point query parameters — `/elevation/{datasetId}/value`

| Parameter | Required | Notes |
| --- | --- | --- |
| `x`, `y` | yes | Coordinate (lon/lat when `srid=4326`); must be finite. |
| `srid` | no | EPSG SRID of the input; default `4326`. |
| `mosaicRule` | no | Merge strategy override: `newest`, `oldest`, `average`, `max`, `min`. |

The response includes `elevation`, `noData`, `outOfBounds`, and a `source` block (raster ids, source CRS, pixel type, vertical unit/datum when declared). Out-of-extent or no-data lookups still return `200` with `elevation: null` and the corresponding flags set.

## Profile query parameters — `/elevation/{datasetId}/profile`

| Parameter | Required | Notes |
| --- | --- | --- |
| `line` | yes | WKT LineString, e.g. `LINESTRING(lon1 lat1, lon2 lat2)`. |
| `sampleCount` | no | Samples including endpoints; default `Limits:Elevation:DefaultSampleCount` (100), bounded `[2, MaxSampleCount]` (default max 500). |
| `interval` | no | Target sampling interval in meters; used only when `sampleCount` is omitted; bounded by `Min`/`MaxIntervalMeters`. |
| `srid` | no | EPSG SRID of the line; default `4326`. Projected inputs are transformed to WGS 84 before geodesic sampling. |
| `mosaicRule` | no | Same values as the point query. |

Samples are ordered start-to-end, each with `distanceMeters` (cumulative geodesic arc length), `elevation`, and a per-sample `noData` flag; `isAllNoData` summarizes full-gap responses. Profile sampling requires PostGIS 3.4+ (geography overload of `ST_LineInterpolatePoint`); the point query works on older PostGIS.

Values are dataset-native: band 1 only, no vertical-datum transformation (`null` vertical unit means meters by assumption).

## Errors

Handler errors use `application/problem+json`: `400` for missing/non-finite inputs (malformed non-numeric query parameters such as `x=notanumber` are rejected by route parameter binding with HTTP `400` and an empty body before reaching the handler), `422` for invalid WKT, out-of-range `sampleCount`/`interval`, unknown CRS, out-of-envelope WGS 84 coordinates, or source rasters with missing/mixed/unknown SRIDs, `404` for unknown datasets, datasets without registered rasters, or a disabled protocol, `402` for Pro elevation-analysis endpoints (`line-of-sight`, `viewshed`, `sun-shadow`, `slice`) when the required entitlement is inactive (checked before dataset lookup, so an unknown dataset also returns `402` when unlicensed), and `401`/`403` for access denial.

## Limits configuration

```jsonc
{
  "Limits": {
    "Elevation": {
      "DefaultSampleCount": 100,
      "MaxSampleCount": 500,
      "MinIntervalMeters": 1.0,
      "MaxIntervalMeters": 50000
    }
  }
}
```

Misconfigured limits fail validation at startup.

## Examples

> Open `https://server.example.com/terrain/dem/tile.json`, `https://server.example.com/terrain/dem/12/655/1586.png` in a browser.

> Open `https://server.example.com/elevation/dem/value?x=-122.4194&y=37.7749` in a browser.

> Open `https://server.example.com/elevation/dem/profile?line=LINESTRING(-122.5%2037.7,-122.3%2037.9)&interval=50` in a browser.

Use `SceneView.lineOfSight` from `@honua/sdk-js/scene-workspace` with dataset `dem`, observer `{-122.42, 37.77, height 2}`, and target `{-122.40, 37.79, height 0}`. The SDK invokes the elevation endpoint and decodes the result.

## Conformance

Terrain-RGB and the elevation JSON endpoints are Honua surfaces, not OGC standards; standards-based raster access is covered by OGC API Coverages and WCS — see the [API standards summary](../compatibility/ogc-conformance.md).

## Guides that use this

- [Publish terrain and elevation](../../guides/publish/publish-terrain-and-elevation.md) — registration and publication workflow.
- [Publish rasters](../../guides/publish/publish-rasters.md)
- [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md)
