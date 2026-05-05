# Temporal animation API

Honua exposes a server-first temporal contract that SDK and Admin clients can
consume to build time-aware visualization, playback, and animation experiences
without inferring temporal behavior from field names.

The contract spans three protocol families:

- **GeoServices REST** — ArcGIS-compatible `timeInfo` / `timeExtent` metadata, a
  dedicated `temporalExtent` endpoint, and a `queryDateBins` histogram endpoint
  for animation frame planning.
- **OGC WMS / WMTS classic** — dynamic `<Dimension name="time">` advertising in
  GetCapabilities and TIME parameter parsing in GetMap / GetTile.
- **Honua-native vector tiles** — `?time=` filtering on `/tiles/{layerId}/{z}/{x}/{y}.mvt`.

The same time-window semantics flow through every protocol so a client only has
to learn one parsing model.

## Configuring a layer as time-aware

A layer becomes time-aware when its catalog metadata includes a `timeInfo`
section that names the start time field (and optionally an end time field). The
time fields must resolve to columns of `Date` or `DateTime` type in the layer
schema.

Update via the admin API:

```http
PUT /api/v1/admin/services/{serviceName}/timeinfo
Content-Type: application/json

{
  "layerId": 0,
  "startTimeField": "event_start",
  "endTimeField": "event_end"
}
```

Layers without explicit `timeInfo` configuration do **not** advertise a time
dimension in WMS/WMTS capabilities or expose a `temporalExtent` endpoint, even
if a `Date`/`DateTime` column is present. This is intentional: temporal
discovery is opt-in to avoid surprising clients with arbitrary defaults.

## Accepted date/time formats

All temporal request parameters share the same parsing semantics:

| Form | Example | Where accepted |
| --- | --- | --- |
| ISO 8601 instant | `2024-06-15T12:00:00Z` | All endpoints |
| ISO 8601 interval (RFC 3339 `start/end`) | `2024-01-01T00:00:00Z/2024-12-31T23:59:59Z` | OGC `datetime`, OGC `TIME`, WMTS `time` |
| Esri-style range | `2024-01-01T00:00:00Z,2024-12-31T23:59:59Z` | GeoServices `time`, MVT `?time=` |
| Open-ended start | `null,1735689599000` or `../2024-12-31T23:59:59Z` | GeoServices `time` (`null,...`); OGC `datetime` (`..`/`..`) |
| Open-ended end | `1640995200000,null` or `2024-01-01T00:00:00Z/..` | Same |
| Unix epoch milliseconds | `1718452800000` | GeoServices `time`, MVT `?time=` |

A bare instant T is treated as the half-open interval `[T, T]`. Reversed ranges
(`start > end`) are rejected with HTTP 400. Empty / both-null ranges are
treated as "no temporal filter" and do not regress non-temporal queries.

## Inclusive bounds and timezone behavior

- Start and end bounds are inclusive in all protocols.
- Server-side timestamps are normalized to UTC; ISO 8601 inputs without an
  explicit offset are assumed to be UTC.
- Output ISO 8601 timestamps in metadata responses use the trailing `Z` UTC
  suffix.
- Epoch values are Unix milliseconds (consistent with ArcGIS REST).

## Edition gating

| Capability | Edition | Feature key |
| --- | --- | --- |
| `time` query parameter on `query` endpoints | Community | `temporal.filtering` |
| `timeInfo` / `timeExtent` in GeoServices REST layer metadata | Community | `temporal.extent-discovery` |
| `GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent` | Community | `temporal.extent-discovery` |
| WMS / WMTS `time` dimension in capabilities | Community | `temporal.extent-discovery` |
| `queryDateBins` histogram | Pro | `temporal.histogram` |
| MVT `?time=` filtering on `/tiles/...mvt` | Pro | `temporal.time-series-tiles` |
| Animation API contract for SDK TimeSlider integration | Pro | `temporal.animation-api` |

Edition gates are enforced server-side; SDK and admin clients should read the
authoritative feature catalog at `GET /api/v1/admin/features` (or its public
capability surface) rather than inferring availability from layer field names.

## GeoServices REST examples

### Layer metadata `timeInfo`

```http
GET /rest/services/test/FeatureServer/0?f=json
```

```json
{
  "id": 0,
  "name": "Test Layer",
  "timeInfo": {
    "startTimeField": "event_start",
    "endTimeField": "event_end",
    "trackIdField": null,
    "timeExtent": [1640995200000, 1735689599000]
  }
}
```

### Temporal extent endpoint

```http
GET /rest/services/test/FeatureServer/0/temporalExtent?f=json
```

```json
{
  "layerId": 0,
  "layerName": "Test Layer",
  "startTimeField": "event_start",
  "endTimeField": "event_end",
  "min": "2022-01-01T00:00:00.000Z",
  "max": "2024-12-31T23:59:59.000Z",
  "minEpochMs": 1640995200000,
  "maxEpochMs": 1735689599000
}
```

Response is `404` (problem detail) for layers that are not time-aware.

### Histogram (queryDateBins)

```http
GET /rest/services/test/FeatureServer/0/queryDateBins?binField=event_start
  &bin={"calendarBin":{"unit":"month"}}
  &outStatistics=[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"count"}]
  &f=json
```

`bin` accepts either `calendarBin` (year, month, week, day, hour, minute,
second) or `fixedBin` (`intervalCount`, `intervalUnit`, optional `origin` as
Unix ms). `outStatistics` is optional; the default returns `count`.

### Query with time range

```http
GET /rest/services/test/FeatureServer/0/query?time=2024-01-01T00:00:00Z,2024-12-31T23:59:59Z&f=json
```

Pass a single instant (`time=2024-06-15T12:00:00Z`) or open-ended range
(`time=null,2024-12-31T23:59:59Z`) using the same parameter.

## OGC examples

### WMS GetCapabilities (1.3.0)

```xml
<Layer queryable="1">
  <Name>0</Name>
  <Title>Test Layer</Title>
  <CRS>EPSG:4326</CRS>
  ...
  <Dimension name="time" units="ISO8601" multipleValues="false"
             nearestValue="true" default="2024-12-31T23:59:59Z">
    2022-01-01T00:00:00Z/2024-12-31T23:59:59Z/PT0S
  </Dimension>
</Layer>
```

The `PT0S` step indicates a continuous extent. Clients planning animation
frames should use `temporalExtent` or `queryDateBins` to choose discrete
instants.

### WMS GetMap with TIME

```http
GET /rest/services/test/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap
  &VERSION=1.3.0
  &LAYERS=0&STYLES=&CRS=EPSG:4326
  &BBOX=-90,-180,90,180&WIDTH=512&HEIGHT=512
  &FORMAT=image/png
  &TIME=2024-06-15T00:00:00Z/2024-06-15T23:59:59Z
```

Supplying `TIME=` against a layer without `timeInfo` configuration returns a
`ServiceExceptionReport` with code `InvalidDimensionValue`.

### WMTS GetCapabilities

```xml
<Layer>
  <ows:Identifier>0</ows:Identifier>
  <Dimension>
    <ows:Identifier>time</ows:Identifier>
    <Default>2024-12-31T23:59:59Z</Default>
    <Current>true</Current>
    <Value>2022-01-01T00:00:00Z/2024-12-31T23:59:59Z/PT0S</Value>
  </Dimension>
  ...
</Layer>
```

GetTile accepts `time=` as a normal KVP parameter; the value can be any
RFC 3339 instant or interval. Omitting `time=` returns the layer's full extent.

## Honua-native MVT

```http
GET /tiles/0/8/40/96.mvt?time=2024-01-01T00:00:00Z,2024-12-31T23:59:59Z
```

The HTTP cache automatically distinguishes time-filtered tiles because the
`CacheOutput("MvtTile")` policy varies by full query string. Non-temporal tile
requests (without `?time=`) are unaffected and continue to be served from the
existing cache entries.

`?time=` requires the **Pro** edition. Community-tier requests receive a
`403 Forbidden` problem response with a clear remediation message.

## Empty-range and non-time-aware behavior

| Scenario | Behavior |
| --- | --- |
| `time=null,null` (both bounds empty) | Treated as "no filter"; full result set returned. |
| `time=` parameter omitted | Existing non-temporal behavior, unchanged. |
| Layer has no `Date`/`DateTime` column AND no `timeInfo` | `temporalExtent` returns 404; WMS/WMTS dimensions absent; query `time=` rejected with 400. |
| Layer has `timeInfo` but no rows ingested | `temporalExtent` still returns 200 with `startTimeField` populated and `min`/`max` set to `null`. |
| `time=` reversed (start > end) | Rejected with 400 / `InvalidDimensionValue` (OGC) before any database call. |

## Cache keys and invalidation

- The MVT tile cache (`"MvtTile"` tag) is keyed by the full request query
  string, so a `?time=` value automatically yields a distinct cache entry.
- The `temporalExtent` endpoint and GeoServices REST layer metadata both share
  the `"LayerMetadata"` cache tag and are invalidated whenever the layer's
  catalog metadata is updated.
- WMS / WMTS GetCapabilities are output-cached by the existing protocol caches
  and pick up new temporal extents on the next miss.

## Related tickets

- [#339](https://github.com/honua-io/honua-server/issues/339) — Streaming
  consumes this temporal contract for time-windowed subscriptions.
- [#692](https://github.com/honua-io/honua-server/issues/692) — Durable CDC /
  outbox provides replay durability; not part of the temporal metadata layer.
- [honua-sdk-dotnet#64](https://github.com/honua-io/honua-sdk-dotnet/issues/64)
  — SDK geofence evaluation combines time windows with feature streams.
