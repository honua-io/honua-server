# CQL2 and filtering

Honua accepts three filter languages, each tied to a protocol surface. This page lists the verified operator and function sets for each, side by side, and ends with the same query written in all three.

| Language | Where it is accepted |
| --- | --- |
| CQL2 (text and JSON) | OGC API Features `filter=` + `filter-lang=cql2-text` (default) or `cql2-json`, with optional `filter-crs=`; also used by metadata permanent filters. |
| GeoServices `where` + spatial parameters | FeatureServer/MapServer query endpoints (`/rest/services/...`). |
| OData `$filter` | OData v4 endpoints (`/odata`). |

## CQL2

Both encodings parse to the same expression tree; everything below is available in text and JSON form.

### Comparison and logic

| Category | CQL2 text | CQL2 JSON `op` |
| --- | --- | --- |
| Comparison | `=`, `<>` (`!=`), `<`, `>`, `<=`, `>=` | `"="`, `"<>"`/`"!="`, `"<"`, `">"`, `"<="`, `">="` |
| Pattern | `LIKE` | `"like"` |
| Range | `BETWEEN ... AND ...` | `"between"` |
| Set | `IN (...)` | `"in"` |
| Null test | `IS NULL` / `IS NOT NULL` | `"isNull"` |
| Logic | `AND`, `OR`, `NOT` | `"and"`, `"or"`, `"not"` |
| Arithmetic | `+`, `-`, `*`, `/`, `DIV` | `"+"`, `"-"`, `"*"`, `"/"`, `"%"`, `"div"`, `"^"` |
| String case/accent | `CASEI(...)`, `ACCENTI(...)` | text encoding only |

### Spatial predicates

`S_INTERSECTS` (alias `INTERSECTS`), `S_CONTAINS`, `S_WITHIN`, `S_CROSSES`, `S_TOUCHES`, `S_OVERLAPS`, `S_DISJOINT`, `S_EQUALS`, plus the distance predicates `S_DWITHIN` and `S_BEYOND`. JSON uses the lowercase forms (`"s_intersects"`, ...).

Geometry operands are WKT literals (`POINT`, `LINESTRING`, `POLYGON`, `MULTIPOINT`, `MULTILINESTRING`, `MULTIPOLYGON`, `GEOMETRYCOLLECTION`) or `BBOX(minx, miny, maxx, maxy)` in text, and GeoJSON geometry or `bbox` objects in JSON.

### Temporal predicates

All fifteen CQL2 temporal operators are supported: `T_AFTER`, `T_BEFORE`, `T_CONTAINS`, `T_DISJOINT`, `T_DURING`, `T_EQUALS`, `T_FINISHEDBY`, `T_FINISHES`, `T_INTERSECTS`, `T_MEETS`, `T_METBY`, `T_OVERLAPPEDBY`, `T_OVERLAPS`, `T_STARTEDBY`, `T_STARTS` (JSON: lowercase). Temporal literals: `DATE('2026-01-01')`, `TIMESTAMP('2026-01-01T00:00:00Z')`, `INTERVAL('2026-01-01', '2026-06-01')`.

### Array predicates

`A_EQUALS`, `A_CONTAINS`, `A_CONTAINEDBY`, `A_OVERLAPS` (JSON: lowercase).

## GeoServices `where` and spatial parameters

The GeoServices surface splits filtering across parameters instead of one expression language:

| Parameter | Purpose |
| --- | --- |
| `where` | SQL-92-style attribute predicate (e.g. `STATUS = 'active' AND POP > 1000`). |
| `objectIds` | Comma-separated feature ids. |
| `geometry`, `geometryType`, `inSR` | Spatial filter geometry (envelope, polygon, etc.) and its spatial reference. |
| `spatialRel` | `esriSpatialRelIntersects` (default), `esriSpatialRelContains`, `esriSpatialRelWithin`, and other `esriSpatialRel*` values. |
| `distance`, `units` | Buffer distance applied to the filter geometry. |
| `time` | Temporal filter (instant or extent, epoch milliseconds). |

Full parameter list, statistics, and pagination: [GeoServices REST reference](protocols/geoservices-rest.md).

## OData `$filter`

| Category | Supported |
| --- | --- |
| Logic | `and`, `or`, `not` |
| Comparison | `eq`, `ne`, `gt`, `ge`, `lt`, `le`, `in` |
| Arithmetic | `add`, `sub`, `mul`, `div`, `mod` |
| String functions | `contains`, `startswith`, `endswith`, `substring`, `tolower`, `toupper`, `length`, `trim`, `indexof`, `replace`, `concat` |
| Numeric functions | `round`, `floor`, `ceiling`, `abs` |
| Date functions | `now`, `year`, `month`, `day`, `hour`, `minute`, `second` |
| Spatial functions | `geo.distance(geometry, geography'...')`, `geo.intersects(geometry, geography'...')` |
| Literals | numbers, strings, `true`/`false`, `null` |

`geo.length` is **not** implemented; unsupported functions return HTTP 400 with `Unsupported function '...'`. `$filter` must be a boolean expression — a bare property or literal is rejected. See the [OData reference](protocols/odata.md) for `$select`, `$orderby`, `$top`/`$skip`, and `$count`.

## The same query in all three languages

Find active hydrants within the same polygon:

CQL2 text (OGC API Features):

```
GET /ogc/features/collections/hydrants/items?filter-lang=cql2-text&filter=
  status = 'active' AND S_INTERSECTS(geom, POLYGON((-122.5 47.5, -122.5 47.7, -122.2 47.7, -122.2 47.5, -122.5 47.5)))
```

CQL2 JSON (same endpoint, `filter-lang=cql2-json`):

```json
{
  "op": "and",
  "args": [
    { "op": "=", "args": [ { "property": "status" }, "active" ] },
    {
      "op": "s_intersects",
      "args": [
        { "property": "geom" },
        { "type": "Polygon", "coordinates": [[[-122.5,47.5],[-122.5,47.7],[-122.2,47.7],[-122.2,47.5],[-122.5,47.5]]] }
      ]
    }
  ]
}
```

GeoServices (FeatureServer query):

```
GET /rest/services/hydrants/FeatureServer/0/query?where=status='active'
  &geometry={"rings":[[[-122.5,47.5],[-122.5,47.7],[-122.2,47.7],[-122.2,47.5],[-122.5,47.5]]]}
  &geometryType=esriGeometryPolygon&inSR=4326&spatialRel=esriSpatialRelIntersects&outFields=*&f=geojson
```

OData:

```
GET /odata/Layers(12)/Features?$filter=status eq 'active' and geo.intersects(geometry, geography'POLYGON((-122.5 47.5, -122.5 47.7, -122.2 47.7, -122.2 47.5, -122.5 47.5))')
```

## Related pages

- [OGC APIs reference](protocols/ogc-apis.md)
- [Query features guide](../guides/query-analyze/query-features.md)
