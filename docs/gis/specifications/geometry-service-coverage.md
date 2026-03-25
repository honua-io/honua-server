# Geometry Service Coverage Matrix

This page summarizes geometry service operation coverage in Honua Server.

Legend:
- Implemented: endpoint exists and behavior is supported.
- Partial: endpoint exists but only part of expected behavior is supported.
- Not implemented: endpoint or behavior is absent.

## Operations

| Operation | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- |
| Buffer | GET, POST | Implemented | `/rest/services/geometry/buffer` | Supports `inSR`, optional `outSR`, `bufferSR`, `distances`, `unit`, `unionResults`, `geodesic`. |
| Simplify | GET, POST | Implemented | `/rest/services/geometry/simplify` | Topological correction via `ST_MakeValid`; uses `sr`. |
| Project | GET, POST | Implemented | `/rest/services/geometry/project` | Reprojects geometries using `inSR` and `outSR`. |
| Intersect | GET, POST | Implemented | `/rest/services/geometry/intersect` | Uses `geometries` + single `geometry` with `sr`. |
| Union | GET, POST | Implemented | `/rest/services/geometry/union` | Unions all input geometries; returns one output geometry. |
| Clip | GET, POST | Implemented | `/rest/services/geometry/clip` | Uses `geometries` + clipping `geometry` with `sr`. |
| Difference | GET, POST | Implemented | `/rest/services/geometry/difference` | Uses `geometries` + eraser `geometry` with `sr`. |
| Area | GET, POST | Implemented | `/rest/services/geometry/area` | Returns `areas` array; optional `areaUnit`. |
| Length | GET, POST | Implemented | `/rest/services/geometry/length` | Returns `lengths` array; optional `lengthUnit`. |

## Common request parameters

| Parameter | Status | Notes |
| --- | --- | --- |
| `f` | Implemented | Supports `json` and `pjson` (JSON responses). |
| `geometries` | Implemented | ArcGIS wrapper object (`geometryType` + `geometries`) or legacy JSON array. |
| `sr` | Implemented | Required for intersect/union/clip/difference/area/length. |
| `geometry` | Implemented | Required for intersect/clip/difference operations. |
| `areaUnit` | Implemented | Used by `/area`; defaults to native SR units when omitted. |
| `lengthUnit` | Implemented | Used by `/length`; defaults to native SR units when omitted. |

