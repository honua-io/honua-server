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
| Geometry Service metadata | `/Utilities/Geometry/GeometryServer` | GET, POST | Implemented | Returns the ArcGIS-style service descriptor (`currentVersion`, `serviceDescription`, `maxBufferCount`, `maxSimplifyCount`) so probing clients (ArcGIS Pro, the ArcGIS Maps SDK for JavaScript, the ArcGIS API for Python) can complete their discovery handshake. Both GET and the `{"f":"json"}` POST companion are served. |

## Esri Geometry Service operation coverage

All operations are served under the canonical Esri route
`/rest/services/Utilities/Geometry/GeometryServer/<operation>` and accept both
GET and POST, mirroring the ArcGIS specification. Operations are thin adapters
over the shared geometry pipeline (NetTopologySuite for topology, the
PROJ-backed projection service for CRS transforms, and the geography-based
measurement path for geodesic area/length).

### Implemented

| Esri operation | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Buffer | `/GeometryServer/buffer` | GET, POST | Implemented | Supports `inSR`, optional `outSR`, `bufferSR`, `distances` (comma-separated), `unit`, `unionResults`, and `geodesic`. `bufferSR` cascades (`bufferSR ?? outSR ?? inSR`); a `bufferSR` that conflicts with `geodesic=true` is rejected with a clear 400. |
| Simplify | `/GeometryServer/simplify` | GET, POST | Implemented | Topological correction via `ST_MakeValid`. Handles multipart and self-intersecting inputs. |
| Project | `/GeometryServer/project` | GET, POST | Implemented | Supports numeric WKIDs and Esri-style spatial-reference JSON (`wkid`/`latestWkid`/`wkt`/`name`). Verified across datum transforms (e.g. `4326 -> 4267` NAD27) and mixed/multipart geometry batches. |
| Intersect | `/GeometryServer/intersect` | GET, POST | Implemented | Uses `geometries` plus a single `geometry`. |
| Union | `/GeometryServer/union` | GET, POST | Implemented | Returns a single merged `geometry` (not a `geometries` array), matching Esri. |
| Clip | `/GeometryServer/clip` | GET, POST | Implemented | Uses `geometries` plus a clipping `geometry`; see limitations for envelope behavior. |
| Difference | `/GeometryServer/difference` | GET, POST | Implemented | Uses `geometries` plus an eraser `geometry`. |
| Areas and Lengths | `/GeometryServer/areasAndLengths` | GET, POST | Implemented | Returns `areas[]` and `lengths[]`. Supports `calculationType` (`planar`, `geodesic`, `preserveShape`), `areaUnit`, and `lengthUnit`. Geodesic measurements run through the geography-based pipeline. |
| Lengths | `/GeometryServer/lengths` | GET, POST | Implemented | Returns `lengths[]` for polylines. Supports `calculationType` and `lengthUnit`. |
| Distance | `/GeometryServer/distance` | GET, POST | Implemented | Distance between `geometry1` and `geometry2`. Supports `distanceUnit` and `geodesic` (geodesic distance measures the closest-points line through the geography pipeline). |
| Relation | `/GeometryServer/relation` | GET, POST | Implemented | Pairwise topological relations between `geometries1` and `geometries2`. Supports the Esri `esriGeometryRelation*` set plus the DE-9IM `esriGeometryRelationRelation` form (requires `relationParam`). |
| Densify | `/GeometryServer/densify` | GET, POST | Implemented | Adds vertices so no segment exceeds `maxSegmentLength`. |
| Convex Hull | `/GeometryServer/convexHull` | GET, POST | Implemented | Single hull over the union of all input geometries. |
| Generalize | `/GeometryServer/generalize` | GET, POST | Implemented | Douglas–Peucker simplification controlled by `maxDeviation`. |
| Label Points | `/GeometryServer/labelPoints` | GET, POST | Implemented | Returns interior label points under the Esri `labelPoints` key. |
| Cut | `/GeometryServer/cut` | GET, POST | Implemented | Splits each `target` geometry by a `cutter` polyline; returns the resulting pieces and a `cutIndexes` array mapping each piece to its source. |
| Trim/Extend | `/GeometryServer/trimExtend` | GET, POST | Implemented | Trims polylines at their intersection with `trimExtendTo`, or extends the terminal segment to reach it. |
| Offset | `/GeometryServer/offset` | GET, POST | Implemented | Offsets lines (`OffsetCurve`) and polygons (buffer) by `offsetDistance`. Supports `offsetUnit`, `offsetHow` (rounded/bevelled/mitered), and `bevelRatio`. |
| Auto Complete | `/GeometryServer/autoComplete` | GET, POST | Implemented | Forms new polygons from existing `polygons` boundaries plus connecting `polylines` by noding and polygonizing. |
| Reshape | `/GeometryServer/reshape` | GET, POST | Implemented | Reshapes a `target` polygon/polyline using a `reshaper` line. |
| Find Transformations | `/GeometryServer/findTransformations` | GET, POST | Implemented | Returns the applicable transformation list for `inSR -> outSR`. Honua performs CRS transformation through the shared PROJ-backed pipeline rather than exposing a discrete Esri datum-transformation catalog, so the list is empty when no explicit transformation is required; see limitations. |
| To Geo Coordinate String | `/GeometryServer/toGeoCoordinateString` | GET, POST | Implemented | Supports `conversionType` of `MGRS` and `USNG`. Coordinates are reprojected to WGS84 when `sr` is not 4326. `UTM`, `GARS`, `GEOREF`, `DD`, `DDM`, and `DMS` return a clear 400. |
| From Geo Coordinate String | `/GeometryServer/fromGeoCoordinateString` | GET, POST | Implemented | Supports `conversionType` of `MGRS` and `USNG`. Decoded WGS84 coordinates are reprojected to `sr` when it is not 4326. |

### Not implemented

All ArcGIS Geometry Service operations are implemented. No operation-level gaps remain.

## Request parameter coverage

### Common

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Supports `json` and `pjson` only. Esri `html` output is not supported. |
| `geometries` | Implemented | Accepts ArcGIS wrapper JSON (`geometryType` + `geometries`) and GET-encoded JSON payloads. |
| `geometry` | Implemented | Single operand geometry. Required for intersect, clip, and difference. |
| `sr` / `inSR` / `outSR` / `bufferSR` | Implemented | Each accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), `CRS84` aliases, and JSON objects with `wkid`, `latestWkid`, or `wkt`. |

### Buffer

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `distances` | Implemented | Supports comma-separated multiple distances. |
| `unit` | Implemented | Linear unit applied to `distances`. |
| `unionResults` | Implemented | Returns a single merged geometry per distance when `true`. |
| `geodesic` | Implemented | Geodesic buffering for geographic and projected inputs. |

### Areas and Lengths / Lengths / Distance

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `calculationType` | Implemented | `planar`, `geodesic`, or `preserveShape`; falls back to `geodesic` boolean when omitted. |
| `areaUnit` | Implemented | Esri `esriSquare*`, ares, hectares, acres, plus derived linear-unit squares. |
| `lengthUnit` / `distanceUnit` | Implemented | Esri linear units. |

### Densify / Generalize / Offset / Cut / Trim-Extend / Reshape / Relation / GeoCoordinateString

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `maxSegmentLength` (densify) | Implemented | Required positive number. |
| `maxDeviation` (generalize) | Implemented | Required positive number. |
| `offsetDistance`, `offsetUnit`, `offsetHow`, `bevelRatio` (offset) | Implemented | Round/bevel/mitre join styles. |
| `cutter` (cut) | Implemented | Cutting polyline. |
| `trimExtendTo`, `extendHow` (trimExtend) | Implemented | Trim/extend reference line. |
| `target`, `reshaper` (reshape) | Implemented | Reshape operands. |
| `relation`, `relationParam` (relation) | Implemented | Esri relation set plus DE-9IM. |
| `conversionType`, `conversionMode`, `numOfDigits` (to/from GCS) | Implemented | MGRS/USNG. |

## Known limitations

- Only JSON-style responses are supported. `f=html` is rejected by the request parser.
- `clip` uses the envelope of the supplied clip geometry rather than the full geometry shape; this is covered by integration tests and is called out here because it differs from how users often interpret clip semantics.
- `findTransformations` returns an empty transformation list because Honua applies CRS transformations through its PROJ-backed projection pipeline instead of exposing the discrete Esri geographic-transformation catalog. Clients that drive a transformation picker from this list will see no entries; `project` still performs the correct datum transform directly.
- `toGeoCoordinateString` / `fromGeoCoordinateString` support `MGRS` and `USNG`; the remaining Esri conversion types (`UTM`, `GARS`, `GEOREF`, `DD`, `DDM`, `DMS`) return a clear 400.

## Verification

The live surface was exercised end-to-end through the ArcGIS API for Python
(`arcgis.geometry`) against a running Honua stack, covering every operation
including mixed and multipart geometries, geodesic buffer/area/length/distance,
datum transforms (`4326 -> 4267`), the buffer-SR cascade, cut/reshape/trimExtend,
and MGRS round-trips.

## Implementation evidence

- Endpoint mapping: [GeometryServiceEndpoints](../../src/Honua.Protocols.GeoServices/GeometryService/GeometryServiceEndpoints.cs)
- Request parsing and format validation: [GeometryServiceRequestParser](../../src/Honua.Protocols.GeoServices/GeometryService/Services/GeometryServiceRequestParser.cs)
- Operation implementation: [GeometryServiceHandler](../../src/Honua.Protocols.GeoServices/GeometryService/Services/GeometryServiceHandler.cs)
- Route registration: [EndpointRegistry](../../src/Honua.Server/EndpointRegistry.cs)
- Integration tests: [GeometryServiceBufferTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceBufferTests.cs), [GeometryServiceProjectTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceProjectTests.cs), [GeometryServiceSimplifyTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceSimplifyTests.cs), [GeometryServiceAdvancedOperationsTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceAdvancedOperationsTests.cs), [GeometryServiceEditOperationsTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceEditOperationsTests.cs), [GeometryServiceMeasureAnalysisTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceMeasureAnalysisTests.cs), [GeometryServiceGeoCoordinateStringTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceGeoCoordinateStringTests.cs), [GeometryServiceInfoTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GeometryService/GeometryServiceInfoTests.cs)
