# OData v4 Coverage Matrix

This page summarizes OData v4 coverage in Honua Server, focusing on operations and query options.

Legend:
- Implemented: endpoint exists and behavior is supported.
- Partial: endpoint exists but only a subset of parameters/behavior is supported.
- Not implemented: endpoint or behavior is absent.

## Operations and allowed query options

| OData operation | Methods | Honua endpoint(s) | Allowed query options | Notes |
| --- | --- | --- | --- | --- |
| Service document | GET | `/odata` | None | Lists entity sets. |
| Metadata document | GET | `/odata/$metadata` | None | EDMX metadata only. |
| Layers collection | GET | `/odata/Layers` | `$filter`, `$select`, `$top`, `$skip`, `$count`, `$format` | Collection of layers. |
| Layers count | GET | `/odata/Layers/$count` | `$filter`, `$format` | Plain text count. |
| Single layer | GET | `/odata/Layers({layerId})` | `$select`, `$format` | Layer metadata. |
| Features collection (all layers) | GET | `/odata/Features` | `$filter`, `$select`, `$orderby`, `$top`, `$skip`, `$skiptoken`, `$count`, `$expand`, `$compute`, `$apply`, `$search`, `$format` | Requires a `LayerId` filter when layer is not in the path. |
| Features count (all layers) | GET | `/odata/Features/$count` | `$filter`, `$format` | Requires a `LayerId` filter when layer is not in the path. |
| Features for layer | GET | `/odata/Layers({layerId})/Features` | `$filter`, `$select`, `$orderby`, `$top`, `$skip`, `$skiptoken`, `$count`, `$expand`, `$compute`, `$apply`, `$search`, `$format` | Canonical layer-scoped features. |
| Features count for layer | GET | `/odata/Layers({layerId})/Features/$count` | `$filter`, `$format` | Layer-scoped count. |
| Legacy features for layer | GET | `/odata/Features({layerId})` | `$filter`, `$select`, `$orderby`, `$top`, `$skip`, `$skiptoken`, `$count`, `$expand`, `$compute`, `$apply`, `$search`, `$format` | Legacy layer-scoped route. |
| Legacy features count | GET | `/odata/Features({layerId})/$count` | `$filter`, `$format` | Legacy layer-scoped count. |
| Single feature (canonical) | GET | `/odata/Features(LayerId={layerId},ObjectId={objectId})` | `$select`, `$format` | Entity key is `(LayerId, ObjectId)`. |
| Single feature (layer route) | GET | `/odata/Layers({layerId})/Features({objectId})` | `$select`, `$format` | Alternative key syntax. |
| Single feature (legacy) | GET | `/odata/Features({layerId},{objectId})` | `$select`, `$format` | Legacy key syntax. |
| Create feature | POST | `/odata/Features` | None | Requires `LayerId` in payload when not in path. |
| Create feature (layer) | POST | `/odata/Layers({layerId})/Features` | None | GeoJSON-style OData payload. |
| Update feature | PATCH | `/odata/Features(LayerId={layerId},ObjectId={objectId})` | None | Partial update (PATCH only). |
| Update feature (layer) | PATCH | `/odata/Layers({layerId})/Features({objectId})` | None | Partial update (PATCH only). |
| Update feature (legacy) | PATCH | `/odata/Features({layerId},{objectId})` | None | Legacy key syntax. |
| Delete feature | DELETE | `/odata/Features(LayerId={layerId},ObjectId={objectId})` | None | Delete by key. |
| Delete feature (layer) | DELETE | `/odata/Layers({layerId})/Features({objectId})` | None | Delete by key. |
| Delete feature (legacy) | DELETE | `/odata/Features({layerId},{objectId})` | None | Legacy key syntax. |
| Feature reference | GET | `/odata/Features(LayerId={layerId},ObjectId={objectId})/$ref` | None | Canonical entity reference (`@odata.id`). |
| Feature value | GET | `/odata/Features(LayerId={layerId},ObjectId={objectId})/$value` | `$format` | Raw JSON value without OData envelope. |
| Batch operations | POST | `/odata/$batch` | None | JSON and multipart/mixed batch formats; supports atomicity groups. |
| Aggregation (legacy) | GET | `/odata/Features({layerId})/$apply` | `$apply`, `$filter` | Supports aggregate/groupby/filter/compute (see below). |
| Search (legacy) | GET | `/odata/Features({layerId})/$search` | `$search`, `$top`, `$skip`, `$count` | Full-text search. |

## Query option support details

| Query option | Status | Notes |
| --- | --- | --- |
| `$filter` | Partial | Supported on Layers/Features collections and count endpoints. Function/operator coverage is limited to the implemented OData subset. |
| `$select` | Implemented | Field selection for Layers and Features; `*` returns all fields. |
| `$orderby` | Partial | Simple field names with optional `asc`/`desc`; no expressions or functions. |
| `$top` / `$skip` | Implemented | Validated and normalized by server limits. |
| `$skiptoken` | Implemented | Opaque cursor-based pagination using Base64Url-encoded tokens with query fingerprinting; mutually exclusive with `$skip`. Legacy integer tokens are supported for backward compatibility. |
| `$count` | Implemented | `@odata.count` in payload; `/.../$count` endpoints return text. |
| `$expand` | Implemented | Comma-separated relationship names; nested expand paths are not supported. |
| `$compute` | Implemented | Arithmetic expressions (`field mul 2 as Alias`); cannot be combined with `$apply` or `$search`. |
| `$search` | Implemented | Full-text search across string fields; supports AND/OR/NOT and quoted phrases. |
| `$apply` | Implemented | `aggregate`, `groupby`, `filter`, and `compute` transformations. |
| `$deltatoken` | Implemented | Timestamp-based change tracking. A `@odata.deltaLink` is emitted on the final page of results; subsequent requests with `$deltatoken` retrieve features modified since the encoded timestamp. |
| `$format` | Partial | Only `json` and `application/json` are accepted. |

## $filter operator coverage

| Category | Supported operators | Notes |
| --- | --- | --- |
| Logical | `and`, `or`, `not` | |
| Comparison | `eq`, `ne`, `gt`, `ge`, `lt`, `le` | |
| Arithmetic | `add`, `sub`, `mul`, `div`, `mod` | |
| Null comparisons | `eq null`, `ne null` | Other null comparisons are rejected. |

## $filter function coverage

| Category | Supported functions | Notes |
| --- | --- | --- |
| String | `contains`, `startswith`, `endswith`, `substring`, `tolower`, `toupper`, `length`, `trim`, `indexof`, `replace`, `concat` | |
| Numeric | `round`, `floor`, `ceiling`, `abs` | |
| Date/time | `now`, `year`, `month`, `day`, `hour`, `minute`, `second` | |
| Spatial | `geo.distance`, `geo.intersects` | Requires `geography`/`geometry` WKT literals. |

## Typed literal support

| Literal | Status | Notes |
| --- | --- | --- |
| `date'YYYY-MM-DD'` | Implemented | |
| `datetime'...'/datetimeoffset'...'` | Implemented | RFC 3339 timestamps. |
| `geography'WKT'` / `geometry'WKT'` | Implemented | Optional `SRID=####;` prefix in the literal. |

## $filter unsupported operators and functions (non-exhaustive)

| Category | Not supported | Notes |
| --- | --- | --- |
| Operators | `has`, `in`, `any`, `all` | Not recognized by the filter parser. |
| Type functions | `cast`, `isof` | Not recognized by the filter parser. |
| Spatial functions | `geo.length` and other `geo.*` functions not listed above | Only `geo.distance` and `geo.intersects` are implemented. |
| Any other functions | Any function not listed in the supported list | Unsupported functions return 400. |

## $filter examples

```
# Comparison + logical
population ge 1000000 and state eq 'CA'

# String functions
startswith(name,'San') or contains(name,'Beach')

# Arithmetic
(area_sq_km mul 2) gt 500

# Date/time
year(founded_date) ge 1900

# Spatial (WKT literal)
geo.intersects(Geometry, geography'SRID=4326;POINT(-122.4 37.8)')

# Distance (WKT literal, meters for geography)
geo.distance(Geometry, geography'SRID=4326;POINT(-122.4 37.8)') lt 5000
```

## Unsupported or not implemented

- Delta tracking (`$deltatoken`) supports timestamp-based change detection; row-level change types (created/updated/deleted annotations) are not yet included in delta responses.
- `PUT` updates are not supported (PATCH only).
- `$levels` recursive expansion is not supported.
- `has`, `in`, `any`, `all` filter operators are not supported.
- `cast`, `isof` type functions are not supported.
