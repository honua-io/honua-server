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

## Metadata response contract

`tile.json` returns TileJSON 3.0 fields plus Honua terrain extensions:

```json
{
  "tilejson": "3.0.0",
  "name": "Layer name",
  "description": "Layer description",
  "scheme": "xyz",
  "tiles": ["https://example.com/terrain/0/{z}/{x}/{y}.png"],
  "minzoom": 0,
  "maxzoom": 18,
  "bounds": [-180, -85.0511, 180, 85.0511],
  "center": [0, 0, 0],
  "format": "terrain-rgb",
  "encoding": {
    "type": "mapbox-terrain-rgb",
    "formula": "elevationMeters = -10000 + ((R * 256 * 256 + G * 256 + B) * 0.1)",
    "units": "meters",
    "tileSize": 256
  },
  "source": {
    "datasetId": "0",
    "layerId": 0,
    "rasterIds": [42],
    "rasterCount": 1,
    "sourceCrs": "EPSG:3857",
    "sourceSrid": 3857,
    "sourceExtent": {
      "xmin": -20037508.342789244,
      "ymin": -20037508.342789244,
      "xmax": 20037508.342789244,
      "ymax": 20037508.342789244,
      "srid": 3857
    },
    "pixelType": "32BF",
    "bandCount": 1,
    "verticalUnit": null,
    "verticalDatum": null,
    "verticalUnitAssumption": "Source values are encoded as meters when no vertical unit is declared."
  },
  "noData": {
    "sourceNoDataValue": null,
    "terrainRgbSentinelMeters": -10000,
    "terrainRgbSentinel": [0, 0, 0],
    "semantics": "Source no-data and uncovered pixels are encoded as opaque Terrain-RGB [0,0,0] (-10000m)."
  },
  "supported": true,
  "unsupportedReasons": []
}
```

`minzoom` and `maxzoom` reflect the configured `Limits:Tiles` range; the default
maximum is 18. `bounds`, `center`, and `sourceExtent` are nullable when the
source CRS or extent cannot be transformed. `sourceSrid` and `sourceCrs` reflect
the stored raster SRID when available; unsupported SRIDs such as `0` are reported
through `supported: false` and `unsupportedReasons`. Metadata can still return
`200 OK` with `supported: false` so clients and operators can diagnose why tile
requests would fail.

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

## Response status contract

| Request | Success | Error cases |
| --- | --- | --- |
| `GET /terrain/{datasetId}/tile.json` | `200 application/json` TileJSON metadata. | `400` request validation failure, `401/403` access failure, `404` unknown dataset or no raster source. |
| `GET /terrain/{datasetId}/{z}/{x}/{y}.png` | `200 image/png` Terrain-RGB tile. | `400` invalid zoom or tile matrix coordinate, `401/403` access failure, `404` unknown dataset or no raster source, `422` unsupported raster source. |

## Tile validation and caching

Tile requests validate `z/x/y` against WebMercator XYZ bounds and the configured
tile zoom limits from `Limits:Tiles`. Metadata and tile responses are output
cache eligible through the `TerrainMetadata` and `TerrainTile` policies.

The HTTP `Cache-Control` header uses `TileOptions:CacheMaxAge`. Metadata cache
keys vary by `datasetId` and `Accept`; tile cache keys vary by `datasetId`,
`z`, `x`, and `y`. Layer/raster invalidation and admin service, collection, or
all-cache invalidation evict the shared `terrain` output-cache tag, clearing
cached Terrain TileJSON and tile responses after raster imports or other
dataset-scoped changes. Terrain-RGB stays on finite tile-grid keys instead of
arbitrary spatial window response caching.

## Client use

MapLibre GL JS and Mapbox GL style clients can consume the `tiles[0]` template
from `/terrain/{datasetId}/tile.json` as a `raster-dem` source with
`encoding: "mapbox"`. Clients should treat `[0, 0, 0]` as the no-data sentinel
unless the application intentionally renders `-10000m`.
