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
| `$compute` | Implemented | Arithmetic (`add`/`sub`/`mul`/`div`/`mod`) plus `floor`/`ceiling`/`round`; not combinable with `$apply` or `$search`. See [$compute and compute()](#compute-and-compute). |
| `$search` | Implemented | Full-text across string fields; AND/OR/NOT and quoted phrases. |
| `$apply` | Implemented | `aggregate`, `groupby`, `filter`, `compute` transformations, composable into a `/`-separated pipeline. See [$apply aggregation](#apply-aggregation). |
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

## $apply aggregation

`$apply` is a transformation pipeline. One or more transforms are chained with a top-level `/`
and applied left to right; the **last** transform decides whether the response is an aggregation
result or a feature set. Slashes inside parentheses or quoted literals (for example a WKT
`geography'…'` value) are not treated as pipeline separators.

`$apply` is available both as a query option on a feature query and on the dedicated route
`GET /odata/Features({layerId})/$apply?$apply=…`. A `$filter` query option composes with the
pipeline (it is `AND`-ed with any `filter(...)` transforms).

### Transforms

| Transform | Form | Notes |
| --- | --- | --- |
| `aggregate` | `aggregate(<field> with <fn> as <alias>[, …])` and `aggregate($count as <alias>)` | Produces a single aggregate row. |
| `groupby` | `groupby((<field>[, <field>…])[, aggregate(<field> with <fn> as <alias>[, …])])` | One row per group; group-key fields are echoed in each row. The nested `aggregate(...)` is optional. |
| `filter` | `filter(<$filter expression>)` | Narrows rows before aggregation; uses the same operators/functions as `$filter`. Multiple `filter(...)` segments are combined with `AND`. |
| `compute` | `compute(<expr> as <alias>[, …])` | Adds derived numeric columns to each feature (see [compute()](#compute-and-compute)). |

### Aggregate functions

| Function | Result |
| --- | --- |
| `sum` | Sum of numeric values. |
| `avg` (alias `average`) | Arithmetic mean. |
| `min` / `max` | Minimum / maximum. |
| `count` / `$count` | Row count (`$count as <alias>` counts all rows). |
| `countdistinct` | Count of distinct values. |

Aggregation is computed over numeric values; non-numeric or null values are skipped. An empty
input yields `0` for `count`/`countdistinct` and `null` for the other functions.

## $compute and compute()

`$compute` (query option) and `compute(...)` (the `$apply` transform) share one grammar, so a
derived column behaves identically in either place. Each comma-separated segment has the form:

```
[<func>(] <operand> [ <op> <operand> ] [)] as <alias>
```

- **operand** — a numeric field name or a numeric literal.
- **op** — `add`, `sub`, `mul`, `div`, or `mod` (modulo). The operator is optional, so
  `compute(field as alias)` copies a value and `floor(field) as alias` bins a single column.
- **func** — an optional canonical OData v4 numeric function wrapping the value: `floor`,
  `ceiling`, or `round` (banker's rounding to the nearest integer).

`div` or `mod` by zero, and any segment referencing a missing field, yields `null` for that
column. Aliases must be unique within a single `$compute`/`compute(...)`.

### Histogram binning with floor()

`floor()` makes histogram/binning first-class. To bucket a value into fixed-width bins of width
`w`, divide by `w` and floor:

```
compute(floor(population div 1000000) as PopBin)
```

This replaces the older arithmetic workaround `compute(population sub (population mod 1000000) as PopBinStart)`,
which is still valid (`mod` is supported) but emits the bin's lower bound rather than the bin index.

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

```bash
# Histogram binning: bucket population into 1,000,000-wide bins with floor()
curl "https://server.example.com/odata/Layers(0)/Features?\$apply=compute(floor(population div 1000000) as popBin)"
```

```bash
# Pipeline: filter then group and aggregate
curl "https://server.example.com/odata/Layers(0)/Features?\$apply=filter(population gt 500000)/groupby((state),aggregate(\$count as cityCount))"
```

## Conformance

OData is not part of the OGC CITE matrix; overall standards status lives in the [API standards summary](../compatibility/ogc-conformance.md).

## Guides that use this

- [Connect from Excel and Power BI](../../guides/connect/excel-power-bi.md)
- [Query features](../../guides/query-analyze/query-features.md)
- [Work with time](../../guides/query-analyze/work-with-time.md)
