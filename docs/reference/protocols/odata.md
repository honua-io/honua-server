# OData v4

Honua exposes layers and features as an OData v4 service at `/odata`, so BI tools (Power BI, Excel, Tableau via connectors) and OData clients can query geospatial data with standard query options.

## Base endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/odata` | Service document (entity sets). |
| GET | `/odata/$metadata` | EDMX metadata document. |
| GET | `/odata/Layers`, `/odata/Layers/$count`, `/odata/Layers({layerId})` | Layer catalog. |
| GET | `/odata/Layers({layerId})/Features`, `.../Features/$count` | Layer-scoped feature query. |
| GET | `/odata/Features`, `/odata/Features/$count` | Cross-layer feature query (requires a `LayerId` filter). |
| GET | `/odata/Features(LayerId={layerId},ObjectId={objectId})` | Single feature by canonical key (also `/$ref`, `/$value`). |
| POST | `/odata/Features`, `/odata/Layers({layerId})/Features` | Create feature. |
| PATCH | `/odata/Features(LayerId={layerId},ObjectId={objectId})` | Partial update. |
| PUT | `/odata/Features(LayerId={layerId},ObjectId={objectId})` | Replace feature. |
| DELETE | `/odata/Features(LayerId={layerId},ObjectId={objectId})` | Delete feature. |
| POST | `/odata/$batch` | Batch (JSON and multipart/mixed; atomicity groups supported). |

PATCH, PUT, and DELETE are also available on the layer-scoped key form `/odata/Layers({layerId})/Features({objectId})` and the legacy form `/odata/Features({layerId},{objectId})`. Legacy aggregation/search routes `/odata/Features({layerId})/$apply` and `/$search` remain available.

## Query options

| Option | Status | Notes |
| --- | --- | --- |
| `$filter` | Partial | Operators and functions limited to the implemented subset below. |
| `$select` | Implemented | Field projection; `*` returns all fields. |
| `$orderby` | Partial | Simple field names with `asc`/`desc`; no expressions. |
| `$top` / `$skip` | Implemented | Normalized by server limits; `$top` is capped to the server page size (`OData:MaxPageSize`, see below). |
| `$skiptoken` | Implemented | Opaque cursor paging; mutually exclusive with `$skip`. |
| `$count` | Implemented | `@odata.count` in payload; `/$count` routes return text. |
| `$expand` | Implemented | Relationship names; nested expand paths not supported. |
| `$compute` | Implemented | Arithmetic expressions; not combinable with `$apply` or `$search`. |
| `$search` | Implemented | Full-text across string fields; AND/OR/NOT and quoted phrases. |
| `$apply` | Implemented | `aggregate`, `groupby`, `filter`, `compute` transformations. |
| `$deltatoken` | Implemented | Timestamp-based change tracking via `@odata.deltaLink`. |
| `$format` | Partial | `json` / `application/json` only. |

## Server-driven paging

The server applies a page-size cap so a single request never tries to materialize an
unbounded result set. The effective page size is `min($top, OData:MaxPageSize)`
(default `1000`, configurable via the `OData:MaxPageSize` setting or the
`OData__MaxPageSize` environment variable). When a client requests a `$top` larger than
the cap, the server returns the first page (up to the cap) and an `@odata.nextLink` that
carries the clamped `$top` and the next `$skip`; clients follow `@odata.nextLink` to page
through the remaining rows. This is standard OData server-driven paging and keeps ad hoc
spatial queries (for example `geo.intersects` combined with `$select`) from forcing a
pathological database plan on a very large `LIMIT`.

> **Behind a proxy/CDN:** `@odata.nextLink` (and all emitted links) use the configured
> public origin, not the inbound `Host` header. Set `PUBLIC_BASE_URL` (or `Public:BaseUrl`)
> so paging links resolve to the external URL. Clients that resolve `@odata.nextLink`
> relative to the request URL are unaffected.

## $filter support

| Category | Supported |
| --- | --- |
| Logical | `and`, `or`, `not` |
| Comparison | `eq`, `ne`, `gt`, `ge`, `lt`, `le` (including `eq null` / `ne null`) |
| Arithmetic | `add`, `sub`, `mul`, `div`, `mod` |
| String functions | `contains`, `startswith`, `endswith`, `substring`, `tolower`, `toupper`, `length`, `trim`, `indexof`, `replace`, `concat` |
| Numeric functions | `round`, `floor`, `ceiling`, `abs` |
| Date/time functions | `now`, `year`, `month`, `day`, `hour`, `minute`, `second` |
| Spatial functions | `geo.distance`, `geo.intersects` — with `geography'WKT'` / `geometry'WKT'` literals (optional `SRID=####;` prefix) |
| Typed literals | `date'...'`, `datetime'...'`/`datetimeoffset'...'`, `geography'...'`/`geometry'...'` |

Not supported (rejected with 400): `has`, `in`, `any`, `all` operators; `cast`/`isof`; `geo.length` and any other `geo.*` function beyond the two listed; `$levels`.

## Examples

```bash
# Filtered, projected, paged query
curl "https://server.example.com/odata/Layers(0)/Features?\$filter=population ge 1000000&\$select=name,population&\$orderby=population desc&\$top=10"
```

```bash
# Spatial filter: features within 5 km of a point
curl "https://server.example.com/odata/Layers(0)/Features?\$filter=geo.distance(Geometry, geography'SRID=4326;POINT(-122.4 37.8)') lt 5000"
```

```bash
# Create a feature
curl -X POST "https://server.example.com/odata/Layers(0)/Features" \
  -H "Content-Type: application/json" \
  -d '{"Geometry":{"type":"Point","coordinates":[-122.4,37.8]},"name":"New point"}'
```

```bash
# Aggregation
curl "https://server.example.com/odata/Layers(0)/Features?\$apply=groupby((state),aggregate(population with sum as totalPopulation))"
```

## Conformance

OData is not part of the OGC CITE matrix; overall standards status lives in the [API standards summary](../compatibility/ogc-conformance.md).

## Guides that use this

- [Connect from Excel and Power BI](../../guides/connect/excel-power-bi.md)
- [Query features](../../guides/query-analyze/query-features.md)
- [Work with time](../../guides/query-analyze/work-with-time.md)
