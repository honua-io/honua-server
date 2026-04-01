# Geometry Service Matrix (Esri Enterprise vs Honua)

Canonical GeoServices entry point: [GeoServices REST Parity](geoservices-rest-parity.md)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/geometry-service/

## Status vocabulary

- Implemented: the Esri operation/resource exists in Honua and the documented behavior is supported.
- Partial: the Esri operation/resource exists, but only a subset of documented parameters or behavior is supported.
- Not implemented: the Esri operation/resource is not exposed by Honua.

## Root resource

| Esri resource | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Geometry Service metadata | `/Utilities/Geometry/GeometryServer` | GET | Not implemented | Honua does not expose a root metadata resource equivalent to Esri `GeometryServer`. Only operation endpoints under `/rest/services/geometry/*` are exposed. |

## Esri Geometry Service operation coverage

### Implemented

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Buffer | `/GeometryServer/buffer` | GET, POST | Implemented | `GET/POST /rest/services/geometry/buffer` | Supports `inSR`, optional `outSR`, `bufferSR`, `distances`, `unit`, `unionResults`, `geodesic`. |
| Simplify | `/GeometryServer/simplify` | GET, POST | Implemented | `GET/POST /rest/services/geometry/simplify` | Topological correction via `ST_MakeValid`. |
| Project | `/GeometryServer/project` | GET, POST | Implemented | `GET/POST /rest/services/geometry/project` | Supports numeric WKIDs plus Esri-style spatial-reference JSON with `latestWkid` or `name`. |
| Intersect | `/GeometryServer/intersect` | GET, POST | Implemented | `GET/POST /rest/services/geometry/intersect` | Uses `geometries` plus a single `geometry`. |
| Union | `/GeometryServer/union` | GET, POST | Implemented | `GET/POST /rest/services/geometry/union` | Returns a single unioned geometry. |
| Clip | `/GeometryServer/clip` | GET, POST | Implemented | `GET/POST /rest/services/geometry/clip` | Uses `geometries` plus a clipping `geometry`; see limitations for envelope behavior. |
| Difference | `/GeometryServer/difference` | GET, POST | Implemented | `GET/POST /rest/services/geometry/difference` | Uses `geometries` plus an eraser `geometry`. |

### Not implemented

| Esri operation | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Areas and Lengths | `/GeometryServer/areasAndLengths` | GET, POST | Not implemented | Honua exposes separate non-Esri supplemental routes at `/rest/services/geometry/area` and `/rest/services/geometry/length`. |
| Auto Complete | `/GeometryServer/autoComplete` | GET, POST | Not implemented | |
| Convex Hull | `/GeometryServer/convexHull` | GET, POST | Not implemented | |
| Cut | `/GeometryServer/cut` | GET, POST | Not implemented | |
| Densify | `/GeometryServer/densify` | GET, POST | Not implemented | |
| Distance | `/GeometryServer/distance` | GET, POST | Not implemented | |
| Find Transformations | `/GeometryServer/findTransformations` | GET, POST | Not implemented | |
| From Geo Coordinate String | `/GeometryServer/fromGeoCoordinateString` | GET, POST | Not implemented | |
| Generalize | `/GeometryServer/generalize` | GET, POST | Not implemented | |
| Label Points | `/GeometryServer/labelPoints` | GET, POST | Not implemented | |
| Lengths | `/GeometryServer/lengths` | GET, POST | Not implemented | Honua exposes `/rest/services/geometry/length` instead of the Esri canonical route. |
| Offset | `/GeometryServer/offset` | GET, POST | Not implemented | |
| Relation | `/GeometryServer/relation` | GET, POST | Not implemented | |
| Reshape | `/GeometryServer/reshape` | GET, POST | Not implemented | |
| To Geo Coordinate String | `/GeometryServer/toGeoCoordinateString` | GET, POST | Not implemented | |
| Trim/Extend | `/GeometryServer/trimExtend` | GET, POST | Not implemented | |

## Honua supplemental routes

These routes are implemented in Honua, but they are not Esri Geometry Service operation names.

| Honua route | Honua status | Notes |
| --- | --- | --- |
| `GET/POST /rest/services/geometry/area` | Implemented | Returns `areas[]` for polygon inputs with optional `areaUnit`. |
| `GET/POST /rest/services/geometry/length` | Implemented | Returns `lengths[]` for polyline inputs with optional `lengthUnit`. |

## Request parameter coverage

### Common

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Supports `json` and `pjson` only. Esri `html` output is not supported. |
| `geometries` | Implemented | Accepts ArcGIS wrapper JSON (`geometryType` + `geometries`) and GET-encoded JSON payloads. |

### Buffer

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `inSR` | Implemented | Supports numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), `CRS84` aliases, and JSON objects with `wkid`, `latestWkid`, or `wkt`. |
| `outSR` | Implemented | Same parser forms as `inSR`. Optional. |
| `bufferSR` | Implemented | Same parser forms as `inSR`. Optional. |
| `distances` | Implemented | Supports comma-separated multiple distances. |
| `unit` | Implemented | Used for distance calculations. |
| `unionResults` | Implemented | Returns a single merged geometry when `true`. |
| `geodesic` | Implemented | Supported for geographic and projected inputs. |

### Simplify, Intersect, Union, Clip, Difference

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `sr` | Implemented | Required. Supports numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), `CRS84` aliases, and JSON objects with `wkid`, `latestWkid`, or `wkt`. |
| `geometry` | Implemented | Required for intersect, clip, and difference. |

### Project

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `inSR` | Implemented | Supports numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), `CRS84` aliases, and JSON objects with `wkid`, `latestWkid`, or `wkt`. |
| `outSR` | Implemented | Same parser forms as `inSR`. |

### Honua supplemental `area` and `length`

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `sr` | Implemented | Required. Supports numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), `CRS84` aliases, and JSON objects with `wkid`, `latestWkid`, or `wkt`. |
| `areaUnit` | Implemented | Supported by `/rest/services/geometry/area`. |
| `lengthUnit` | Implemented | Supported by `/rest/services/geometry/length`. |

## Known limitations

- Honua does not expose a root geometry-service metadata endpoint equivalent to Esri `GeometryServer`.
- Only JSON-style responses are supported. `f=html` is rejected by the request parser.
- `clip` uses the envelope of the supplied clip geometry rather than the full geometry shape; this is covered by integration tests and is called out here because it differs from how users often interpret clip semantics.
- The implemented `area` and `length` routes are Honua-specific helpers, not drop-in replacements for Esri `areasAndLengths` and `lengths`.
- Most ArcGIS Geometry Service operations remain intentionally unimplemented in the MVP surface.

## Implementation evidence

- Endpoint mapping: [GeometryServiceEndpoints](../../src/Honua.Server/Features/GeometryService/GeometryServiceEndpoints.cs)
- Request parsing and format validation: [GeometryServiceRequestParser](../../src/Honua.Server/Features/GeometryService/Services/GeometryServiceRequestParser.cs)
- Operation implementation: [GeometryServiceHandler](../../src/Honua.Server/Features/GeometryService/Services/GeometryServiceHandler.cs)
- Integration tests: [GeometryServiceBufferTests](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceBufferTests.cs), [GeometryServiceProjectTests](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceProjectTests.cs), [GeometryServiceSimplifyTests](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceSimplifyTests.cs), [GeometryServiceAdvancedOperationsTests](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceAdvancedOperationsTests.cs)
- Contract depth check: [ContractCoverageMatrixTests](../../tests/Honua.Server.Tests/Comprehensive/ContractCoverageMatrixTests.cs)
