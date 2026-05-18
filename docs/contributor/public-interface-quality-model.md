# Public Interface Quality Model

This document explains the canonical proof ledger for ticket `#469`.

The machine-readable source of truth is [`docs/gis/data/public-interface-proof.json`](../gis/data/public-interface-proof.json). That ledger inventories every shipped public surface in this repository, the proof classes that apply to it, the CI or release lane that runs the proof, the immutable evidence location, the owning repo, and the follow-up ticket when the proof is unfinished.

## Canonical Artifacts

| Artifact | Role |
|---|---|
| [`docs/gis/data/public-interface-proof.json`](../gis/data/public-interface-proof.json) | Canonical machine-readable surface and proof inventory |
| [`docs/gis/CLIENT_TEMPLATE_VERSION_MATRIX.md`](../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) | Release ledger for named desktop, BI, and browser client versions |
| [`docs/gis/certification-evidence/20260402T000000Z/`](../gis/certification-evidence/20260402T000000Z/README.md) | Curated immutable example evidence snapshot used to eliminate placeholder-only rows until the next release candidate replaces the links with release artifacts |
| [`docs/contributor/RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md) | Release-time operator checklist for refreshing ledger evidence |
| [`docs/contributor/mcp-certification.md`](mcp-certification.md) | Cross-repo MCP ownership and evidence contract |

## Proof Classes

| Proof class | Meaning |
|---|---|
| `route-coverage` | The shipped HTTP route family is registered and enforced by integration-test coverage and deployed-route drift checks. |
| `operation-coverage` | A logical operation surface that is not fully captured by route metadata alone is enforced by `OperationRegistry` plus drift checks. |
| `scenario-depth` | High-risk singleton-heavy areas keep happy-path plus negative-path depth, not just single-route smoke coverage. |
| `contract-governance` | A declared API or parity contract has its own drift or governance gate outside runtime request handling. |
| `standards-conformance` | The surface is proven by a standards-oriented conformance lane such as CITE. |
| `tool-interoperability` | A named client library or CLI tool is executed against the live server and its version is captured in immutable evidence. |
| `real-client-certification` | A human-usable client or agent-facing workflow is exercised with release-grade evidence and version capture. |

The ledger is intentionally allowed to overlap at the surface level. Example: `map-server` covers the broad GeoServices MapServer route family while `wms-1.3` and `wmts-1.0` describe nested standards surfaces that reuse a subset of those routes but carry their own conformance or follow-up proof obligations.

## Release Evidence Ledgers

### Named Client Lanes

All named desktop, BI, and browser client versions are recorded in [`docs/gis/CLIENT_TEMPLATE_VERSION_MATRIX.md`](../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md).

Rules:

- Never leave a required row at `TBD`.
- During active development branches, keep a curated immutable example link rather than an empty placeholder.
- On every release candidate, replace the curated example links with the exact workflow artifact URL or release asset URL for that release.

### Tool Lanes

| Lane | Surface(s) | Version capture rule | Immutable evidence | Owner | Status |
|---|---|---|---|---|---|
| `Microsoft.OData.Client` integration suite | `odata-v4` | Keep the exact package version pinned in [`tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj`](../../tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj) and mention that version in the release notes for the OData lane. | [`tests/dotnet/Honua.Server.Tests/Features/Protocols/OData/ODataClientIntegrationTests.cs`](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/OData/ODataClientIntegrationTests.cs) and the `all-test-results` artifact in [`ci.yml`](../../.github/workflows/ci.yml) | `honua-server` | implemented |
| `GDAL/OGR` CLI interoperability | `ogc-api-features`, `wfs-2.0` | Capture `ogrinfo --version` in the test evidence collector and preserve the generated `tests/python/gdal-ogr-results*.json` artifact from [`ci.yml`](../../.github/workflows/ci.yml). | [`tests/python/gdal_ogr/conftest.py`](../../tests/python/gdal_ogr/conftest.py) and the `all-test-results` artifact in [`ci.yml`](../../.github/workflows/ci.yml) | `honua-server` | implemented |
| First-party SDK server compatibility | `control-plane-admin`, `feature-server`, `ogc-api-features`, `migration-scan`, `arcgis-import`, `geoserver-dry-run`, `migration-evidence` | Preserve each `compat-result.json` from `sdk-server-compatibility.yml`; it records SDK package versions, SDK refs, Honua Server commit/image field, seed profile, `protocol_surfaces_by_sdk`, migration automation pass/fail or unsupported status, exit code, and bounded failure diagnostics. Current server-matrix coverage is JS admin/FeatureServer/OGC, Python admin/readiness/catalog/FeatureServer, and .NET admin; migration automation and remaining per-SDK protocol surfaces stay bounded child work until the SDK lanes exercise them. | [`sdk-server-compatibility.yml`](../../.github/workflows/sdk-server-compatibility.yml), [`run-sdk-server-compatibility.sh`](../../scripts/ci/run-sdk-server-compatibility.sh), and [`SDK Compatibility Matrix`](../developer/SDK_COMPATIBILITY_MATRIX.md) | `honua-server` coordinates; `honua-sdk-js`, `honua-sdk-dotnet`, and `honua-sdk-python` own repo-local tests | implemented with bounded child surfaces |
| `MCP` certification | `mcp` | Pin `MCP_SDK_REF` to a tag or commit SHA and capture the checked-out `honua-sdk-js/mcp` package version plus the `mcp-certification-{transport}` artifact names. | [`docs/contributor/mcp-certification.md`](mcp-certification.md) and [`ci.yml`](../../.github/workflows/ci.yml) | `honua-sdk-js` | bounded child `#484` |
| `MCP` LLM smoke | `mcp` | Pin `MCP_SDK_REF` to a tag or commit SHA and capture the checked-out `honua-sdk-js/mcp` package version plus the `mcp-llm-smoke-transcripts` artifact name. | [`docs/contributor/mcp-certification.md`](mcp-certification.md) and [`ci.yml`](../../.github/workflows/ci.yml) | `honua-sdk-js` | bounded child `#484` |

## Ticket Reconciliation

Ticket `#469` is now the governing reconciliation ticket, not a request to rebuild coverage gates that already landed.

| Ticket | Reconciled status after this pass | Repo evidence |
|---|---|---|
| `#470` | The shared proof ledger and evidence model are now canonicalized here. Future work is maintenance, not a second greenfield implementation. | [`public-interface-proof.json`](../gis/data/public-interface-proof.json), [`CROSS_CLIENT_CERTIFICATION_MATRIX.md`](../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md), [`CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) |
| `#461` | Route and operation governance for WFS and gRPC are already landed. Keep the ledger current rather than rebuilding the gate. | [`EndpointRegistry.cs`](../../src/Honua.Server/EndpointRegistry.cs), [`OperationRegistry.cs`](../../src/Honua.Server/OperationRegistry.cs), [`OperationCoverageTests.cs`](../../tests/dotnet/Honua.Architecture.Tests/OperationCoverageTests.cs) |
| `#462` | Admin, geocoding, and geometry-service depth work is already represented by the contract matrix. Remaining work is audit-only if new gaps appear. | [`ContractCoverageMatrixTests.cs`](../../tests/dotnet/Honua.Server.Tests/Comprehensive/ContractCoverageMatrixTests.cs) |
| `#467` | The OData client suite already exists. Remaining work is keeping release evidence links current. | [`ODataClientIntegrationTests.cs`](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/OData/ODataClientIntegrationTests.cs) |
| `#468` | The GDAL/OGR interoperability suite already exists. Remaining work is keeping release evidence links current. | [`tests/python/gdal_ogr/`](../../tests/python/gdal_ogr/) |
| `#463` | Esri Leaflet browser compatibility suite is now merge-blocking in CI. Evidence emitted as `tool-interoperability` proofs on `feature-server` and `map-server` surfaces. | [`public-interface-proof.json`](../gis/data/public-interface-proof.json), [`tests/js-browser/`](../../tests/js-browser/), [`CROSS_CLIENT_CERTIFICATION_MATRIX.md`](../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md) |
| `#464`, `#465`, `#466` | These remain the main server-owned gaps for broader browser and desktop proof. | [`public-interface-proof.json`](../gis/data/public-interface-proof.json) planned `real-client-certification` rows |
| `#478` | The visual / style certification slice is now defined and the OpenLayers, Esri Leaflet, and PyQGIS lanes record per-category `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` evidence through their existing collectors. The slice substantiates rendering on the OGC API Features, OGC API Tiles, and FeatureServer surfaces it actually exercises; the three `#478`-linked surface rows (`wms-1.3`, `wmts-1.0`, `ogc-api-maps-and-static-rendering`) all remain `planned` because their `/ogc/services/{serviceId}/wms`, `/ogc/services/{serviceId}/wmts`, `/ogc/maps`, and `/static/` endpoints are not exercised by the slice lanes. Closing those rows is bounded to dedicated WMS / WMTS / OGC API Maps client lanes as a follow-on. | [`visual-style-certification-slice.md`](../gis/visual-style-certification-slice.md), [`CROSS_CLIENT_CERTIFICATION_MATRIX.md`](../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md), [`tests/js/openlayers/rendering/render.spec.ts`](../../tests/js/openlayers/rendering/render.spec.ts), [`tests/js-browser/esri-leaflet/rendering.spec.ts`](../../tests/js-browser/esri-leaflet/rendering.spec.ts), [`tests/python/pyqgis/test_render_path.py`](../../tests/python/pyqgis/test_render_path.py) |
| `#813` | First-party JS, .NET, and Python SDK integration testing is represented by repo-local SDK trackers plus the server-owned SDK/server compatibility matrix. | [`SDK_COMPATIBILITY_MATRIX.md`](../developer/SDK_COMPATIBILITY_MATRIX.md), [`sdk-server-compatibility.yml`](../../.github/workflows/sdk-server-compatibility.yml), [`run-sdk-server-compatibility.sh`](../../scripts/ci/run-sdk-server-compatibility.sh), [`public-interface-proof.json`](../gis/data/public-interface-proof.json) |
| `#484` | MCP remains a sanctioned cross-repo child surface. The SDK repo owns deterministic MCP scripts and artifact generation. | [`mcp-certification.md`](mcp-certification.md) |

## Cross-Repo Boundary

Only approved bounded-child surfaces may point outside `honua-server` in the
proof ledger. Today those are MCP certification, geospatial gRPC proto
governance, and first-party SDK integration tests tracked by `#813`. The server
repo owns:

- Runtime routes and protocol implementations
- Registries and architecture enforcement
- Seed data and CI wiring
- Release ledgers and contributor docs

The SDK repos own their repo-local client test implementations:
`honua-sdk-js#39`, `honua-sdk-dotnet#31`, and `honua-sdk-python#21`. The
server repo owns the shared seed/bootstrap contract and the cross-version
release/nightly matrix that consumes their evidence.
