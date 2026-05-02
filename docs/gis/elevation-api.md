# Elevation Query and Profile API

Honua exposes registered raster/DEM datasets as machine-readable elevation
endpoints for field workflows that need numeric elevation values, not RGB-encoded
tiles. The API surfaces a single-point lookup and a line-based profile aligned to
the same raster catalog and mosaic foundations used by the Terrain-RGB service
(`#839`) and OGC API Coverages (`#521`).

## Public routes

| Route | Purpose |
| --- | --- |
| `GET /elevation/{datasetId}/value` | Sample elevation for a single coordinate against a registered raster dataset. |
| `GET /elevation/{datasetId}/profile` | Sample elevation along a WKT LineString returning ordered distance/elevation samples. |

`datasetId` accepts a numeric layer id or a layer collection name. The Elevation
protocol must be enabled on the owning service or layer metadata. When
`EnabledProtocols` is omitted, Elevation is enabled with the rest of the default
protocol set.

## Point query — `GET /elevation/{datasetId}/value`

| Query parameter | Required | Description |
| --- | --- | --- |
| `x` | yes | X coordinate (longitude when `srid=4326`). Must be a finite numeric value. |
| `y` | yes | Y coordinate (latitude when `srid=4326`). Must be a finite numeric value. |
| `srid` | no | EPSG SRID of the supplied coordinate. Defaults to `4326`. Validated through the spatial reference registry. |
| `mosaicRule` | no | Override layer-default merge strategy: `newest`, `oldest`, `average`, `max`, `min`. |

Successful response (`200 application/json`):

```json
{
  "datasetId": "0",
  "layerId": 0,
  "elevation": 123.4,
  "noData": false,
  "outOfBounds": false,
  "x": -122.4194,
  "y": 37.7749,
  "querySrid": 4326,
  "mosaicRule": "newest",
  "source": {
    "rasterIds": [42],
    "rasterCount": 1,
    "sourceSrid": 3857,
    "sourceCrs": "EPSG:3857",
    "pixelType": "32BF",
    "noDataValue": null,
    "verticalUnit": null,
    "verticalDatum": null,
    "verticalUnitAssumption": "Source values are assumed to be meters when no vertical unit is declared.",
    "band": 1
  }
}
```

When the coordinate falls outside the registered raster extent, the response is
still `200 OK` with `elevation: null`, `noData: true`, `outOfBounds: true`, and
`source.rasterCount: 0`. When the coordinate falls inside the extent but on a
no-data pixel, the response is `200 OK` with `elevation: null`, `noData: true`,
`outOfBounds: false`, and the selected raster ids preserved for diagnostics.

## Profile query — `GET /elevation/{datasetId}/profile`

| Query parameter | Required | Description |
| --- | --- | --- |
| `line` | yes | LineString geometry encoded as WKT, e.g. `LINESTRING(lon1 lat1, lon2 lat2)`. |
| `sampleCount` | no | Number of samples (including endpoints). Defaults to `Limits:Elevation:DefaultSampleCount` (100). Must be `>=2` and `<= Limits:Elevation:MaxSampleCount` (default 500). |
| `interval` | no | Target sampling interval in meters. Validated against `Limits:Elevation:MinIntervalMeters` and `Limits:Elevation:MaxIntervalMeters`. When supplied without `sampleCount`, the effective sample count is derived inline by the profile SQL as `ceil(geodesicLengthMeters / interval) + 1`, clamped to `[2, Limits:Elevation:MaxSampleCount]`. |
| `srid` | no | EPSG SRID of the line geometry. Defaults to `4326`. Lines in projected SRIDs are transformed to WGS 84 (`EPSG:4326`) before any geographic length or interpolation is performed (PostGIS `geography` only accepts SRID 4326). |
| `mosaicRule` | no | Override layer-default merge strategy: `newest`, `oldest`, `average`, `max`, `min`. |

When both `sampleCount` and `interval` are supplied, `sampleCount` wins and
`interval` is ignored. Sample positions and reported distances are computed in
the same metric space — the input line is transformed to WGS 84 and cast to
PostGIS `geography`, then sampled with `ST_LineInterpolatePoint(geog, frac, true)`
on the WGS 84 spheroid. Reported `distanceMeters` are the cumulative geodesic
arc length of the input line, so the position of each sample matches the
distance reported for it. The `sampleCount` field on the response always
reflects the effective number of samples returned, including the value derived
from `interval`.

Successful response (`200 application/json`):

```json
{
  "datasetId": "0",
  "layerId": 0,
  "sampleCount": 5,
  "lineLengthMeters": 156543.03,
  "lineSrid": 4326,
  "mosaicRule": "newest",
  "isAllNoData": false,
  "samples": [
    { "distanceMeters": 0,        "elevation": 12.3,  "noData": false },
    { "distanceMeters": 39135.76, "elevation": 18.7,  "noData": false },
    { "distanceMeters": 78271.51, "elevation": null,  "noData": true  },
    { "distanceMeters": 117407.27,"elevation": 45.0,  "noData": false },
    { "distanceMeters": 156543.03,"elevation": 52.6,  "noData": false }
  ],
  "source": {
    "rasterIds": [42, 43],
    "rasterCount": 2,
    "sourceSrid": 3857,
    "sourceCrs": "EPSG:3857",
    "pixelType": "32BF",
    "noDataValue": null,
    "verticalUnit": null,
    "verticalDatum": null,
    "verticalUnitAssumption": "Source values are assumed to be meters when no vertical unit is declared.",
    "band": 1
  }
}
```

Samples are ordered from the start of the input line to the end. Each sample
carries its own `distanceMeters` and `noData` flag so callers can detect coverage
gaps along the profile without inspecting `isAllNoData`. When the line runs
entirely outside the registered raster coverage, the response is still `200 OK`
with `isAllNoData: true` and every sample's `elevation` set to `null`.

## Error responses

All error responses use the shared `application/problem+json` envelope.

| Scenario | HTTP status |
| --- | --- |
| Missing `x`/`y` query parameters | `400 Bad Request` |
| Non-finite coordinate values | `400 Bad Request` |
| Missing or empty `line` parameter | `400 Bad Request` |
| Invalid WKT or non-LineString geometry | `422 Unprocessable Entity` |
| `sampleCount` below 2 | `422 Unprocessable Entity` |
| `sampleCount` above `Limits:Elevation:MaxSampleCount` | `422 Unprocessable Entity` |
| `interval` outside the configured `Min`/`Max` range | `422 Unprocessable Entity` |
| Unknown or unsupported CRS via `srid` | `422 Unprocessable Entity` |
| Coordinates outside WGS 84 lon `[-180, 180]` / lat `[-90, 90]` when `srid` is omitted or `4326` | `422 Unprocessable Entity` |
| Unknown dataset / layer | `404 Not Found` |
| Layer access denied | `401 Unauthorized` / `403 Forbidden` |
| Dataset has no registered rasters | `404 Not Found` |
| Elevation protocol not enabled for service or layer | `404 Not Found` |

When `srid` is omitted or set to `4326`, the endpoint validates that all
input coordinates fall inside the WGS 84 lon/lat envelope before any PostGIS
work — PostGIS `geography` only accepts SRID 4326, so out-of-range lon/lat
values are rejected at the edge as a stable `422` rather than leaking a
provider-side geography exception. Inputs in projected SRIDs are validated
against the spatial reference registry but not against WGS 84 bounds.

## Limits and configuration

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

Operators can tighten or relax these limits per environment. The
`LimitsOptionsValidator` rejects misconfigurations at startup — the server
fails fast when `Elevation.DefaultSampleCount` exceeds `MaxSampleCount`, when
`Elevation.MinIntervalMeters` exceeds `MaxIntervalMeters`, or when any
`ElevationLimits` field falls outside the declared `[Range]`. Profile sampling
is implemented as a single PostGIS query that transforms the input line to
WGS 84, computes the geodesic length in `geography`, derives the effective
sample count (from `sampleCount`, `interval`/length, or `DefaultSampleCount`),
fans out via `generate_series`, samples positions with
`ST_LineInterpolatePoint(geog, frac, true)`, and looks up raster values with
`ST_Value` against the merged mosaic. Sample bounds protect the database from
pathological large fan-outs while still allowing dense profiles up to the
configured maximum. The interval branch clamps the derived count against
`MaxSampleCount` in `float8` space *before* casting to `integer`, so
pathological combinations of long lines and very small intervals never
overflow `int4` — the request always returns a bounded sample count instead
of a 500.

## PostGIS compatibility

The profile SQL relies on the geography overload of
`ST_LineInterpolatePoint(geography, double, boolean)`, which was introduced in
PostGIS 3.4. This matches the
[Database Support Matrix](../operator/database-support-matrix.md) — the
minimum tested deployment configuration is PostgreSQL 16.x with PostGIS 3.4.
Older PostGIS releases (≤ 3.3) ship only the geometry overload and will
return a `function does not exist` error from the elevation profile endpoint;
the point endpoint is unaffected because it samples a single point with
`ST_Value` only.

## Known limitations

- **Band 1 only**: The MVP samples band 1 of each raster. Multi-band elevation
  datasets are out of scope; explicit band selection (`?band=`) is a follow-up.
- **No vertical datum transformation**: Returned values are dataset native. The
  response surfaces `verticalUnit` and `verticalDatum` when the catalog declares
  them and otherwise leaves them `null`. Clients should treat `null` as
  *meters by assumption* per the explicit assumption note in the response.
- **GET only**: Profile inputs are encoded as WKT in the URL. Very long lines
  may approach URL length limits; chunk into multiple shorter queries when
  necessary. POST body support is deferred follow-up scope.
- **No exact response caching**: Both endpoints accept ad hoc spatial inputs
  and skip exact response caching by design (per the cross-cutting caching
  rule). Layer/raster catalog metadata caching from `ILayerCatalog` still
  applies for dataset resolution.

## Observability

Elevation requests are classified under the `Elevation` protocol with
`elevation.value` and `elevation.profile` operations. Spans include the layer
id, dataset/collection id, selected raster count, and (for profile requests)
sample count, line length in meters, and an `all-no-data` flag.
