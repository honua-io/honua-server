# Protocol Parity Audit (#305-#310)

Audit date: 2026-02-23

## Scope

This audit covers protocol parity implementation tracking for GitHub issues `#305` to `#310`, plus related admin parity and OGC CITE automation coverage checks.

Per active coordination constraints, this audit is static-only (no build/test execution in this pass).
Multi-node/Redis integration validation is tracked separately and may be in flight on a parallel agent.

## Static API Surface Status

### Endpoint coverage for `#305` to `#310`

| Issue | Scope | Registered Endpoints | Missing Integration Test Coverage |
|---|---|---:|---:|
| `#305` | FeatureServer replication + maintenance | 9 | 0 |
| `#306` | MapServer compatibility (WMS/WMTS/tile) | 3 | 0 |
| `#307` | OGC API Features parity operations | 7 | 0 |
| `#308` | OData parity endpoints | 2 | 0 |
| `#309` | Geometry service operation expansion | 18 | 0 |
| `#310` | OGC tiles enhancements | 15 | 0 |

### Global API surface check

- Endpoint registry vs integration-test endpoint attributes currently reports `0` uncovered routes.
- Service-level query routes added in parity work are now covered:
  - `GET /rest/services/{serviceId}/FeatureServer/query`
  - `POST /rest/services/{serviceId}/FeatureServer/query`
  - `GET /rest/services/{serviceId}/MapServer/query`
  - `POST /rest/services/{serviceId}/MapServer/query`

## Issue Acceptance Mapping (`#305` to `#310`)

| Issue | Acceptance Intent | Static Evidence (code/tests/docs) | Residual Risk |
|---|---|---|---|
| `#305` | Replication + maintenance endpoints implemented and tested | Endpoints mapped in `src/Honua.Server/Features/FeatureServer/FeatureServerEndpoints.cs`; tests in `tests/dotnet/Honua.Server.Tests/Features/FeatureServer/FeatureServerReplicationTests.cs` and `tests/dotnet/Honua.Server.Tests/Features/FeatureServer/FeatureServerMaintenanceTests.cs`; matrix updated in `docs/user/feature-server-matrix.md`. | Replica state is documented as in-memory MVP behavior (no durable replica state store yet). |
| `#306` | MapServer tile/WMS/WMTS and tile metadata parity | Endpoints mapped in `src/Honua.Server/Features/MapServer/MapServerEndpoints.cs`; tests in `tests/dotnet/Honua.Server.Tests/Features/MapServer/MapServerTileEndpointTests.cs`, `tests/dotnet/Honua.Server.Tests/Features/MapServer/MapServerWmsTests.cs`, `tests/dotnet/Honua.Server.Tests/Features/MapServer/MapServerWmtsTests.cs`; matrix updated in `docs/user/map-server-matrix.md`. | WMTS scope remains KVP + WebMercatorQuad-only for MapServer compatibility endpoint. |
| `#307` | OGC API Features PATCH + ids/properties/sortby | Coverage marked implemented in `docs/user/specifications/ogc-api-features-coverage.md`; PATCH tests in `tests/dotnet/Honua.Server.Tests/Features/OgcFeatures/OgcFeaturesTransactionTests.cs`. | Query filter/operator support remains intentionally partial per matrix notes. |
| `#308` | `$skiptoken`, `$deltatoken`, multipart batch | Coverage marked implemented in `docs/user/specifications/odata-v4-coverage.md`; `/odata/$batch` and advanced OData tests present in `tests/dotnet/Honua.Server.Tests/Features/OData/*`. | Delta semantics are MVP-level (timestamp-based; full change annotations not yet complete). |
| `#309` | Geometry operation expansion | Endpoint coverage across intersect/union/clip/difference/area/length in `src/Honua.Server/EndpointRegistry.cs`; tests in `tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/GeometryService/GeometryServiceAdvancedOperationsTests.cs`; coverage matrix at `docs/user/specifications/geometry-service-coverage.md`. | Advanced geodetic edge cases remain bounded by current parser/validation constraints. |
| `#310` | OGC tiles raster + broader matrix/CRS support | OGC tiles tests include CRS and raster scenarios in `tests/dotnet/Honua.Server.Tests/Features/OgcTiles/OgcTilesCrsTests.cs` and `tests/dotnet/Honua.Server.Tests/Features/OgcTiles/OgcTilesRasterTests.cs`; contributor CITE tiles doc reflects raster + matrix-set scope. | Conformance pass rate depends on active CITE suite status; endpoint availability is not equivalent to full conformance. |

## Admin Tool Parity Check

### Finding

The Admin UI `ServiceSettingsClient` used absolute path fragments (`api/v1/admin/...`) while `AdminApi` `HttpClient` already points at `/api/v1/admin/`.

This created path duplication risk (`/api/v1/admin/api/v1/admin/...`) under default configuration.

### Fix

`src/Honua.Admin/Services/ServiceSettingsClient.cs` was updated to use relative routes:

- `services`
- `services/{serviceName}/settings`
- `services/{serviceName}/protocols`
- `services/{serviceName}/mapserver`

This restores parity with other admin clients and the configured base URL resolver behavior.
No remaining `api/v1/admin/services...` hardcoded paths were found under `src/Honua.Admin`.

## OGC CITE Automation Coverage Audit

### Automated CITE suites wired in repository

| Implemented Standard | CITE/ETS Suite | Local Runner Script | CI Workflow |
|---|---|---|---|
| OGC API Features 1.0 | `ets-ogcapi-features10` | `scripts/run-cite-tests.sh` | `.github/workflows/cite-conformance.yml` |
| OGC API Tiles 1.0 | `ets-ogcapi-tiles10` | `scripts/run-cite-tiles-tests.sh` | `.github/workflows/cite-tiles-conformance.yml` |
| OGC WMS 1.3 | `ets-wms13` | `scripts/run-cite-wms-tests.sh` | `.github/workflows/cite-wms-conformance.yml` |
| OGC WMTS 1.0 | `ets-wmts10` | `scripts/run-cite-wmts-tests.sh` | `.github/workflows/cite-wmts-conformance.yml` |

### Coverage conclusion

For implemented OGC standards with public CITE suites integrated in this repository, automated coverage is wired end-to-end (local script + CI workflow).
The WMS/WMTS scripts intentionally fail when `Total Tests == 0` to prevent false-green CI runs.

### Remaining non-CITE areas

These implemented surfaces do not currently have equivalent OGC CITE automation in-repo:

- GeoServices REST (FeatureServer/MapServer)
- OData v4
- OGC API Maps

These rely on integration, architecture, and protocol-specific test suites instead of TeamEngine CITE.

## Protocol Correctness Follow-up (#573)

Audit date: 2026-03-22

A follow-up audit (#571) identified four correctness findings bundled under #573:

| Finding | Surface | Fix Summary | Test Coverage |
|---|---|---|---|
| `ProtocolRequestClassifier` missing `/ogc/maps` | OGC Maps | Add path segment to `IsOgc()` | Error format assertion for `/ogc/maps` paths |
| Collection extent not CRS84-compliant | OGC Tiles | Reuse `OgcExtentTransformer.TryTransformToCrs84()`; omit extent for unsupported CRS | CRS84 extent validation in collection descriptions |
| WKB byte-order assumption (little-endian only) | OGC Tiles | Endian-aware reads via `BinaryPrimitives` | Big-endian and little-endian WKB rendering tests |
| WFS 2.0 XML ExceptionReport coverage gap | WFS 2.0 | No production code change needed (infrastructure correct) | Extended error-path integration tests |

**Design decisions**:
- Unsupported CRS omits spatial extent rather than emitting native-CRS coordinates (matches OGC Features/WFS behavior).
- WKB fix scoped to endian handling only; EWKB/Z/M support deferred.
- WFS error formatting confirmed correct; additional tests verify untested dispatcher error paths.
