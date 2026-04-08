# OGC API Features Coverage Matrix

This page summarizes OGC API Features coverage in Honua Server, focusing on the public operations and query parameters. It complements the specification notes in:
- ogc-api-features-part1-core.md
- ogc-api-features-part2-crs.md
- ogc-api-features-part3-filtering.md

Legend:
- Implemented: endpoint exists and behavior is supported.
- Partial: endpoint exists but only a subset of parameters/behavior is supported.
- Not implemented: endpoint or behavior is absent.

## Operations

| OGC operation | Spec path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Landing page | `/ogc/features` | GET | Implemented | `GET /ogc/features` | Supports `f=json|html` and Accept negotiation. |
| Conformance | `/ogc/features/conformance` | GET | Implemented | `GET /ogc/features/conformance` | Supports `f=json|html`. |
| OpenAPI definition | `/api` (spec) | GET | Implemented | `GET /openapi.json` | Service-wide OpenAPI document (includes OGC Features). |
| Collections list | `/ogc/features/collections` | GET | Implemented | `GET /ogc/features/collections` | Supports `f=json|html`. |
| Collection metadata | `/ogc/features/collections/{collectionId}` | GET | Implemented | `GET /ogc/features/collections/{collectionId}` | Supports `f=json|html`. |
| Queryables | `/ogc/features/collections/{collectionId}/queryables` | GET | Implemented | `GET /ogc/features/collections/{collectionId}/queryables` | Supports `f=json|html`. |
| Items (features) | `/ogc/features/collections/{collectionId}/items` | GET | Implemented | `GET /ogc/features/collections/{collectionId}/items` | Filtering + CRS + paging supported (see parameter matrix). |
| Single item | `/ogc/features/collections/{collectionId}/items/{featureId}` | GET | Implemented | `GET /ogc/features/collections/{collectionId}/items/{featureId}` | Supports `f` + `crs`. |
| Create item | `/ogc/features/collections/{collectionId}/items` | POST | Implemented | `POST /ogc/features/collections/{collectionId}/items` | GeoJSON request body only. |
| Replace item | `/ogc/features/collections/{collectionId}/items/{featureId}` | PUT | Implemented | `PUT /ogc/features/collections/{collectionId}/items/{featureId}` | GeoJSON request body only; upsert behavior. |
| Patch item | `/ogc/features/collections/{collectionId}/items/{featureId}` | PATCH | Implemented | `PATCH /ogc/features/collections/{collectionId}/items/{featureId}` | Merge-style partial updates for `properties` and/or `geometry`. |
| Delete item | `/ogc/features/collections/{collectionId}/items/{featureId}` | DELETE | Implemented | `DELETE /ogc/features/collections/{collectionId}/items/{featureId}` | No query parameters. |
| Batch operations (extension) | N/A | POST | Implemented | `POST /ogc/features/collections/{collectionId}/items/batch` | Honua extension (not in OGC standard). |
| Spatial analytics — clusters (extension, Pro) | N/A | POST | Implemented | `POST /ogc/features/collections/{collectionId}/clusters` | Honua extension. DBSCAN or K-Means clustering. Mirrors `queryClusters` from FeatureServer; identical request body, response payload, and edition gate (`analytics.clustering`, ADR-0024). |
| Spatial analytics — spatial join (extension, Pro) | N/A | POST | Implemented | `POST /ogc/features/collections/{collectionId}/spatial-join` | Honua extension. Joins the source collection against `joinLayerId` using `intersects`/`contains`/`within`/`dwithin`. Mirrors `spatialJoin` from FeatureServer; the join layer is resolved through the same `IResourceValidator` access policy as the source so its parent service must also be enabled and readable (`analytics.spatial-join`, ADR-0024). |
| Spatial analytics — buffer aggregate (extension, Pro) | N/A | POST | Implemented | `POST /ogc/features/collections/{collectionId}/buffer-aggregate` | Honua extension. Buffers features by `distance` in `unit` (meters/kilometers/feet/miles), with `dissolve` and `groupByFields`. Mirrors `queryBufferAggregate` from FeatureServer; the buffer cap is enforced in meters after unit conversion (`analytics.buffer-aggregate`, ADR-0024). |
| Spatial analytics — density (extension, Pro) | N/A | POST | Implemented | `POST /ogc/features/collections/{collectionId}/density` | Honua extension. Hex or square grid binning at `cellSize` meters with optional `weightField`. Mirrors `queryDensity` from FeatureServer (`analytics.density`, ADR-0024). |

The four spatial analytics extensions are Pro-tier (ADR-0024) and return HTTP 402 with code `edition.upgrade_required` on Community. They share their handler implementations with the GeoServices REST surface — see the [FeatureServer matrix](../feature-server-matrix.md#honua-spatial-analytics-extensions-pro-tier) for parameter semantics, limits, and response shape (`metadata.operation`, `metadata.inputTruncated`, `metadata.resultTruncated`, `metadata.maxInputFeatures`, `metadata.maxOutputRows`).

## Items query parameter coverage

Applies to `GET /ogc/features/collections/{collectionId}/items` unless noted.

| Parameter | Status | Notes |
| --- | --- | --- |
| `f` | Implemented | `geojson`, `json`, `gml`, `html` for feature content. GML SF0 is advertised as a conformance class and CITE-validated at format level. |
| `limit` | Implemented | Validated and normalized by server limits. |
| `offset` | Implemented | Standard offset paging. |
| `ids` | Implemented | Comma-separated feature IDs. |
| `properties` | Implemented | Comma-separated property projection list (`*` keeps default behavior). |
| `sortby` | Implemented | Comma-separated sort expressions (supports `+field`, `-field`, and `field asc|desc`). |
| `bbox` | Implemented | 4 or 6 comma-separated values; anti-meridian supported for geographic CRS. |
| `bbox-crs` | Implemented | CRS for interpreting `bbox`; must be in collection `crs` list. |
| `crs` | Implemented | Output CRS; must be in collection `crs` list. Response includes `Content-Crs`. |
| `datetime` | Implemented | RFC 3339 instant or interval; requires temporal fields on the layer. |
| `filter` | Partial | CQL2-Text and CQL2-JSON supported; function/operator coverage is limited to the implemented CQL subset. |
| `filter-lang` | Partial | Supports `cql2-text` (default) and `cql2-json` only. |
| `filter-crs` | Partial | CRS for filter geometries; requires `filter` and a supported CRS. |
| Queryable properties | Partial | Simple queryables (string, numeric, boolean, date/time, UUID) are supported as equality filters. |

## CQL2 operator coverage

Applies to both `cql2-text` and `cql2-json` filters. Unsupported operators return a 400.

| Category | Supported operators | Notes |
| --- | --- | --- |
| Logical | `AND`, `OR`, `NOT` | Standard boolean logic. |
| Comparison | `=`, `<>`, `<`, `<=`, `>`, `>=` | Type coercion follows layer field types. |
| Null checks | `IS NULL`, `IS NOT NULL` | |
| Pattern | `LIKE`, `NOT LIKE` | Works with text fields. |
| Set | `IN`, `NOT IN` | Value list or array literal. |
| Range | `BETWEEN`, `NOT BETWEEN` | |
| Arithmetic | `+`, `-`, `*`, `/`, `%`, `DIV`, `^` | Includes unary `-`. |

## CQL2 spatial predicates

| Predicate | Status | Notes |
| --- | --- | --- |
| `S_INTERSECTS` | Implemented | |
| `S_CONTAINS` | Implemented | |
| `S_WITHIN` | Implemented | |
| `S_CROSSES` | Implemented | |
| `S_TOUCHES` | Implemented | |
| `S_OVERLAPS` | Implemented | |
| `S_DISJOINT` | Implemented | |
| `S_EQUALS` | Implemented | |
| `S_DWITHIN` | Implemented | Distance predicate. |
| `S_BEYOND` | Implemented | Distance predicate. |

## CQL2 temporal predicates

| Predicate | Status |
| --- | --- |
| `T_AFTER`, `T_BEFORE` | Implemented |
| `T_CONTAINS`, `T_DISJOINT`, `T_DURING` | Implemented |
| `T_EQUALS` | Implemented |
| `T_FINISHEDBY`, `T_FINISHES` | Implemented |
| `T_INTERSECTS` | Implemented |
| `T_MEETS`, `T_METBY` | Implemented |
| `T_OVERLAPPEDBY`, `T_OVERLAPS` | Implemented |
| `T_STARTEDBY`, `T_STARTS` | Implemented |

## CQL2 array predicates

| Predicate | Status | Notes |
| --- | --- | --- |
| `A_EQUALS` | Implemented | Requires JSON array field. |
| `A_CONTAINS` | Implemented | Requires JSON array field. |
| `A_CONTAINEDBY` | Implemented | Requires JSON array field. |
| `A_OVERLAPS` | Implemented | Requires JSON array field. |

## CQL2 function coverage

Functions are case-insensitive. Unsupported functions return a 400.

| Category | Supported functions | Notes |
| --- | --- | --- |
| String | `UPPER`, `LOWER`, `LENGTH`, `CHAR_LENGTH`, `CHARACTER_LENGTH`, `TRIM`, `LTRIM`, `RTRIM`, `SUBSTRING`, `SUBSTR`, `REPLACE`, `CONCAT`, `POSITION` | |
| Numeric | `ABS`, `CEIL`, `CEILING`, `FLOOR`, `ROUND`, `POWER`, `MOD` | |
| Date/time | `NOW`, `YEAR`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `SECOND`, `CURRENT_DATE`, `CURRENT_TIMESTAMP`, `CURRENT_TIME` | |
| Null handling | `COALESCE`, `NULLIF` | |
| Geometry | `GEODISTANCE` | Uses geography when possible. |
| Collation helpers | `CASEI`, `ACCENTI` | `ACCENTI` requires PostgreSQL `unaccent` extension. |

## CQL2 unsupported operators and functions (non-exhaustive)

| Category | Not supported | Notes |
| --- | --- | --- |
| Spatial predicates | Any `S_*` predicate not listed above | Only the supported list is implemented. |
| Temporal predicates | Any `T_*` predicate not listed above | Only the supported list is implemented. |
| Array predicates | Any `A_*` predicate not listed above | Only the supported list is implemented. |
| Functions | Any function not listed above | Functions are matched case-insensitively. |

## CQL2 examples

```
# Comparison + logical
population >= 1000000 AND state = 'CA'

# Pattern + range
name LIKE 'San%' AND founded BETWEEN 1850 AND 1950

# Set membership
type IN ('park', 'forest', 'reserve')

# Spatial predicate
S_INTERSECTS(geometry, POINT(-122.4 37.8))

# Distance predicate
S_DWITHIN(geometry, POINT(-122.4 37.8), 5000)

# Temporal predicate (interval)
T_INTERSECTS(date_field, INTERVAL('2020-01-01','2021-01-01'))

# Function
LOWER(name) = 'honolulu'

# Array predicate (JSON array field)
A_CONTAINS(tags, ('coastal','urban'))
```

## Unsupported or not implemented

- Filter languages other than `cql2-text` and `cql2-json` are not supported.
- Write operations do not accept GML; GeoJSON is required.
