# Terrain-RGB Elevation Tiles

Honua can expose registered raster/DEM sources as server-generated Terrain-RGB
PNG tiles for web clients that understand the Mapbox Terrain-RGB encoding.

## Public routes

| Route | Purpose |
| --- | --- |
| `GET /terrain/{datasetId}/tile.json` | TileJSON 3.0 metadata plus Honua source, encoding, and no-data extensions. |
| `GET /terrain/{datasetId}/{z}/{x}/{y}.png` | 256x256 opaque Terrain-RGB PNG elevation tile in WebMercator XYZ coordinates. |

`datasetId` accepts a numeric layer id or a layer collection name. The Terrain
protocol must be enabled on the owning service or layer metadata. When
`EnabledProtocols` is omitted, Terrain is enabled with the rest of the default
protocol set.

## Encoding contract

Terrain tiles use the standard Mapbox Terrain-RGB formula:

```text
elevationMeters = -10000 + ((R * 256 * 256 + G * 256 + B) * 0.1)
```

Source no-data pixels and areas outside the registered raster coverage are
encoded as opaque RGB `[0, 0, 0]`, which decodes to `-10000m`. Fully uncovered
but valid tile coordinates return a valid all-sentinel PNG rather than `404`.

Honua assumes source band values are meters when no vertical unit is declared.
The metadata response reports `verticalUnit` and `verticalDatum` as `null` when
the source catalog has no vertical CRS information.

## Source requirements

Terrain v1 supports PostGIS raster sources registered for one layer when all
source rasters have:

- a usable CRS/SRID
- one consistent source CRS per dataset
- exactly one numeric elevation band
- a numeric PostGIS pixel type

Unsupported sources return `422 Unprocessable Entity` for tile requests with a
standard problem response. Missing datasets or layers without raster sources
return `404`.

## Tile validation and caching

Tile requests validate `z/x/y` against WebMercator XYZ bounds and the configured
tile zoom limits from `Limits:Tiles`. Metadata and tile responses are output
cache eligible through the `TerrainMetadata` and `TerrainTile` policies.

The HTTP `Cache-Control` header uses `TileOptions:CacheMaxAge`. Cache keys vary
by dataset id and tile coordinates, keeping Terrain-RGB on finite tile-grid keys
instead of arbitrary spatial window response caching.

## Client use

MapLibre GL JS and Mapbox GL style clients can consume the `tiles[0]` template
from `/terrain/{datasetId}/tile.json` as a `raster-dem` source with
`encoding: "mapbox"`. Clients should treat `[0, 0, 0]` as the no-data sentinel
unless the application intentionally renders `-10000m`.
