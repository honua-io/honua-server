# NAServer API Matrix (Esri Network Analyst vs Honua)

Canonical GeoServices entry point: [GeoServices REST Parity](geoservices-rest-parity.md)

Sources:
- https://developers.arcgis.com/rest/network-analyst/api-reference/route/
- https://developers.arcgis.com/rest/network-analyst/api-reference/service-area/
- https://developers.arcgis.com/rest/network-analyst/api-reference/closest-facility/

ADR: [ADR-0050 — Routing engine choice and NAServer compatibility](../contributor/adr/0050-routing-engine-choice-and-naserver-compat.md)

## Status vocabulary

- Implemented: the Esri operation/resource exists in Honua at a compatible path and documented behavior is supported.
- Partial: the Esri operation/resource exists, but Honua only supports a subset of documented parameters or behavior.
- Stub: the Esri operation/resource exists and returns a well-formed deterministic envelope, but no canonical solver is wired up.
- Not implemented: the Esri operation/resource is not exposed by Honua.

## Architecture

Honua exposes NAServer as a thin GeoServices REST protocol adapter over the shared `IRoutingProvider` abstraction
(see `src/Honua.Routing/Features/Routing/Abstractions/IRoutingProvider.cs`). The pgRouting provider
(`pgr_dijkstra` for routes, `pgr_drivingDistance` + alpha-shape polygonization for service areas) is the default
routing engine as documented in ADR-0050. The adapter is stateless — there is no per-service NAServer catalog,
layer tree, or network dataset concept in Honua; the routing network topology lives in the PostGIS database.

NAServer endpoints are anonymous by design (mirroring the GeometryService compute endpoints, per `#1144` /
`#1266`): Route/ServiceArea solves are stateless geospatial computations, not feature-service data with RBAC-gated
layers. Capability gating ensures providers that do not advertise route or service-area support return an Esri-shaped
400 error rather than attempting a solve.

## NAServer Route

### Implemented

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Route Solve | `/rest/services/{serviceId}/NAServer/Route/solve` | POST | Implemented | `POST /rest/services/{serviceId}/NAServer/Route/solve` | Multi-stop route via `pgr_dijkstra` over the pgRouting topology. Returns an Esri route feature set (`Routes`) with optional turn-by-turn directions (`Directions`). Capability-gated: the `IRoutingProvider.Capabilities.SupportsRoute` flag must be `true` or the solve returns Esri 400. Parameters: `stops` (JSON feature set or serialized geometry list), `outSR` (default WKID 4326), `returnRoutes` (default `true`), `returnDirections` (default `false`). |

### Not implemented

| Esri operation or resource | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Route service metadata | `/rest/services/{serviceId}/NAServer/Route` | GET | Not implemented | NAServer root and named-solve metadata resources are not exposed. |
| `travelMode` object | parameter on `solve` | — | Not implemented | Named travel modes and travel-mode JSON objects are not parsed; the solve always uses the default cost column configured in the pgRouting topology. |
| Barriers (point/line/polygon) | `pointBarriers`, `polylineBarriers`, `polygonBarriers` | — | Not implemented | Barrier input is accepted in the request body but silently ignored by the solver. |
| Hierarchy | `useHierarchy` | — | Not implemented | pgRouting topology does not include Esri-style hierarchy levels. |
| Traffic | `startTime`, `startTimeIsUTC`, traffic layers | — | Not implemented | Time-dependent routing is not supported. |

## NAServer ServiceArea

### Implemented

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| ServiceArea Solve | `/rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea` | POST | Implemented | `POST /rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea` | Drive-time or drive-distance isochrone polygons via `pgr_drivingDistance` + alpha-shape / concave-hull polygonization. Returns `SaPolygons` feature set with `ObjectID`, `FacilityID`, `Name`, `FromBreak`, and `ToBreak` attributes. Capability-gated: `IRoutingProvider.Capabilities.SupportsServiceArea` and the requested `travelDirection` must be in `SupportedTravelDirections`. Parameters: `facilities` (JSON feature set), `defaultBreaks` (comma-separated cost cutoffs), `travelDirection` (`esriNATravelDirectionFromFacility` runs the outbound graph; `esriNATravelDirectionToFacility` reverses source/target so the polygon covers who can reach the facility), `outSR` (default WKID 4326). |

### Not implemented

| Esri operation or resource | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| ServiceArea service metadata | `/rest/services/{serviceId}/NAServer/ServiceArea` | GET | Not implemented | NAServer named-solve metadata resources are not exposed. |
| `travelMode` object | parameter on `solveServiceArea` | — | Not implemented | Named travel modes and travel-mode JSON objects are not parsed. |
| Line output (`returnLines`) | `returnLines` | — | Not implemented | Only polygon service-area output (`saPolygons`) is produced. |
| Detailed polygons | `mergeSimilarPolygonRanges`, `splitPolygonsAtBreaks`, `trimOuterPolygon` | — | Not implemented | Polygon options are not forwarded to the solver. |
| Traffic | `startTime`, time-of-day aware solves | — | Not implemented | Time-dependent routing is not supported. |

## NAServer ClosestFacility

### Stub

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| ClosestFacility Solve | `/rest/services/{serviceId}/NAServer/ClosestFacility/solveClosestFacility` | POST | Stub | `POST /rest/services/{serviceId}/NAServer/ClosestFacility/solveClosestFacility` | Returns a deterministic envelope (`Routes` with a single `Incident - Facility A` route, `Directions` with a summary) for first-party mobile routing client probes. No canonical closest-facility solver is wired up (ADR-0050 defers closest-facility until a dedicated canonical contract exists). |

### Not implemented

| Esri operation or resource | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| ClosestFacility service metadata | `/rest/services/{serviceId}/NAServer/ClosestFacility` | GET | Not implemented | NAServer named-solve metadata resources are not exposed. |
| Real closest-facility solve | parameter-driven solve | — | Not implemented | The stub always returns the same deterministic result regardless of input facilities or incidents. |

## NAServer root resource

| Esri operation | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| NAServer root metadata | `/rest/services/{serviceId}/NAServer` | GET | Not implemented | The service-level NAServer root resource (listing available route solvers and their constraints) is not exposed. Honua does not model a per-service NAServer catalog. |

## Parameter coverage

### Route solve (`/NAServer/Route/solve`)

| Parameter | Status | Notes |
| --- | --- | --- |
| `stops` | implemented | JSON feature set (`{"features":[...]}`) or comma-delimited `x,y` pairs. Required. |
| `outSR` | implemented | Accepts numeric WKID; defaults to 4326. |
| `returnRoutes` | implemented | `true` (default) returns route polylines; `false` omits the Routes feature set. |
| `returnDirections` | implemented | `false` (default) omits turn-by-turn steps; `true` populates Directions. |
| `f` | partial | `json` and `pjson` only. |
| `travelMode` | not_implemented | Ignored. |
| `pointBarriers`, `polylineBarriers`, `polygonBarriers` | not_implemented | Accepted in body but not applied to the solve. |
| `useHierarchy`, `startTime`, `startTimeIsUTC` | not_implemented | Not forwarded to pgRouting. |

### ServiceArea solve (`/NAServer/ServiceArea/solveServiceArea`)

| Parameter | Status | Notes |
| --- | --- | --- |
| `facilities` | implemented | JSON feature set (`{"features":[...]}`) or comma-delimited `x,y` pairs. Required. |
| `defaultBreaks` | implemented | Comma-separated cost cutoffs (minutes or meters depending on routing cost column). |
| `travelDirection` | implemented | `esriNATravelDirectionFromFacility` (outbound graph) or `esriNATravelDirectionToFacility` (reversed graph). Validated against `IRoutingProvider.Capabilities.SupportedTravelDirections`. |
| `outSR` | implemented | Accepts numeric WKID; defaults to 4326. |
| `f` | partial | `json` and `pjson` only. |
| `travelMode` | not_implemented | Ignored. |
| `returnLines` | not_implemented | Only polygon output is produced. |
| `mergeSimilarPolygonRanges`, `splitPolygonsAtBreaks`, `trimOuterPolygon` | not_implemented | Polygon shaping options are not forwarded to the solver. |

## Known limitations

- The NAServer root resource and named-solver metadata endpoints are not exposed; Honua does not model a per-service NAServer catalog comparable to ArcGIS Enterprise.
- ClosestFacility is a deterministic compatibility stub; the returned route distances and travel times are fixed values and do not reflect actual input geometry.
- `travelMode` objects are not parsed. All solves run against the default cost column configured in the pgRouting topology (typically `length_m` for distance or `cost_s` for time, depending on the network data import).
- Barriers (point, polyline, polygon) are accepted in the request body but are not forwarded to pgRouting.
- Service-area output is polygon-only (`saPolygons`); `returnLines` and detailed polygon options are not supported.
- Time-dependent routing (`startTime`, traffic data, time-of-day dependent costs) is not supported.
- The NAServer output format is JSON-only (`f=json`/`f=pjson`); Esri HTML output is not supported.
- pgRouting service-area polygons use alpha-shape / concave-hull polygonization, which may differ from Esri's native network-distance polygon shaping for non-convex road networks.

## Evidence

| Area | Code | Tests |
| --- | --- | --- |
| Endpoints | [NAServerEndpoints.cs](../../src/Honua.Protocols.GeoServices/NAServer/NAServerEndpoints.cs) | [NAServerEndpointTests.cs](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/NAServer/NAServerEndpointTests.cs) |
| Parameter translation | [NAServerParameterTranslation.cs](../../src/Honua.Protocols.GeoServices/NAServer/NAServerParameterTranslation.cs) | [NAServerTranslationUnitTests.cs](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/NAServer/NAServerTranslationUnitTests.cs) |
| Result mapping | [NAServerResultMapping.cs](../../src/Honua.Protocols.GeoServices/NAServer/NAServerResultMapping.cs) | [NAServerEndpointTests.cs](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/NAServer/NAServerEndpointTests.cs) |
| pgRouting end-to-end | [pgRouting provider](../../src/Honua.Routing/) | [NAServerPgRoutingEndToEndTests.cs](../../tests/dotnet/Honua.Server.Tests/Routing/NAServerPgRoutingEndToEndTests.cs) |
