# Honua evidence index

The single map for compatibility, conformance, parity, certification, and migration evidence across the repo. Use it when you need to defend a claim — to a reviewer, an external auditor, marketing copy, or yourself.

Evidence lives in four trees:

- **`docs/contributor/`** — release-evidence anchors and decision records (refresh on release).
- **`docs/gis/`** — protocol parity matrices and client-certification evidence (mostly refreshed via nightly CI, see notes).
- **`docs/developer/`** — SDK ↔ server parity contracts (refresh per SDK release).
- **`docs/operator/`** — operator-side support matrices (refresh when provider support changes).
- **`docs/compatibility/`** — single orphan, see [Cross-server interop](#cross-server-interop).

This file does not store evidence itself. It points at the doc that does.

## Standards conformance (OGC, OData, STAC)

What we run, what passes, what the formal certification posture is.

- **CITE runbook** — [`contributor/cite-runbook.md`](../contributor/cite-runbook.md). How to run every automated CITE suite locally and in CI (Features, Tiles, Maps, WMS 1.3, WMTS 1.0, WFS 2.0, WCS 2.0.1, KML 2.2, GML 3.2, GeoPackage 1.2). Per-suite scope, scripts, workflow files, and open issues.
- **OGC CITE conformance evidence** — [`contributor/ogc-cite-conformance-evidence.md`](../contributor/ogc-cite-conformance-evidence.md). Stable, website-linkable summary of which suites are currently passing on trunk. Refreshed when a suite's state changes.
- **OGC certification path** — [`contributor/ogc-certification-path.md`](../contributor/ogc-certification-path.md). Decision record: formal OGC certification is currently deferred. Includes the evidence baseline matrix and the criteria for reopening a submission.
- **Legacy CITE (manual)** — [`archive/contributor/cite-legacy-ogc-conformance-testing.md`](../archive/contributor/cite-legacy-ogc-conformance-testing.md). WMS 1.1.1, WFS 1.0, WFS 1.1 — manual procedures only, not part of the automated runbook.
- **Per-spec coverage matrices** — [`gis/specifications/`](../gis/specifications/):
  - [OGC API Features](../gis/specifications/ogc-api-features-coverage.md) (umbrella + [Part 1 Core](../gis/specifications/ogc-api-features-part1-core.md) / [Part 2 CRS](../gis/specifications/ogc-api-features-part2-crs.md) / [Part 3 Filtering](../gis/specifications/ogc-api-features-part3-filtering.md))
  - [OGC API Tiles](../gis/specifications/ogc-api-tiles-coverage.md), [Records](../gis/specifications/ogc-api-records-coverage.md), [Coverages](../gis/specifications/ogc-api-coverages-coverage.md), [Processes](../gis/specifications/ogc-api-processes-coverage.md)
  - [WCS 2.0.1](../gis/specifications/wcs-2.0.1-coverage.md), [OData v4](../gis/specifications/odata-v4-coverage.md)
  - OGC API Maps coverage doesn't ship as a spec doc; see the Maps section in [`contributor/cite-runbook.md`](../contributor/cite-runbook.md#ogc-api-maps) (integration-test-driven, not TeamEngine-driven).
- **Visual style certification** — [`gis/visual-style-certification-slice.md`](../gis/visual-style-certification-slice.md). Render-regression evidence for style fidelity across protocols.

## Esri / ArcGIS protocol parity

How Honua's GeoServices REST surface compares to Esri's.

- **GeoServices REST parity overview** — [`gis/geoservices-rest-parity.md`](../gis/geoservices-rest-parity.md). Canonical landing page.
- **Per-service matrices** — [FeatureServer](../gis/feature-server-matrix.md), [MapServer / WMS / WMTS](../gis/map-server-matrix.md), [ImageServer](../gis/image-server-matrix.md), [Geometry Service](../gis/geometry-service-matrix.md).
- **I3S scene compatibility** — [`gis/i3s-compatibility-matrix.md`](../gis/i3s-compatibility-matrix.md).
- **ArcGIS Pro licensed evidence** — [`gis/ARCGIS_PRO_LICENSED_EVIDENCE.md`](../gis/ARCGIS_PRO_LICENSED_EVIDENCE.md).

## Client interop (real-world clients against Honua)

What gets verified against actual desktop apps, browser clients, GDAL, BI tools, etc.

- **Cross-client certification matrix** — [`gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md`](../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md). The CERT-* ID vocabulary (CONN/AUTH/DISC/SCHM/QFLT/PAGE/GEOM/ERRH/RNDR) and lane coverage.
- **Cross-client certification envelope** — [`gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md). The JSON envelope schema each lane emits.
- **Client template version matrix** — [`gis/CLIENT_TEMPLATE_VERSION_MATRIX.md`](../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md). Which versions of which clients have been certified.
- **Client template runbook** — [`gis/CLIENT_TEMPLATE_RUNBOOK.md`](../gis/CLIENT_TEMPLATE_RUNBOOK.md). How to run a manual client smoke-cert.
- **Nightly gap report** — [`gis/gap-report.md`](../gis/gap-report.md). Auto-refreshed by the `client-interop-nightly` workflow; do not hand-edit.
- **Historical certification snapshot** — [`gis/certification-evidence/`](../gis/certification-evidence/). Timestamped snapshots retained for release evidence.

## Cross-server interop

How well Honua-as-client consumes other geospatial servers (the inverse direction of the parity matrices above).

- **Cross-server consume gap report** — [`compatibility/cross-server-consume-gap-report.md`](../compatibility/cross-server-consume-gap-report.md). Auto-refreshed nightly.

## Migration and import capability

What we can import, scan, or migrate from existing systems, and the website-safe wording for those claims.

- **Compatibility and migration evidence (overarching)** — [`contributor/compatibility-and-migration-evidence.md`](../contributor/compatibility-and-migration-evidence.md). The claim-governance index that gates "compatible with X" and "automated migration from X" copy.
- **Import & scan capability evidence** — [`contributor/import-capability-evidence.md`](../contributor/import-capability-evidence.md). ArcGIS GeoServices REST, GeoServer, and OGC import/scan capability matrix.
- **Process migration evidence** — [`contributor/process-migration-evidence.md`](../contributor/process-migration-evidence.md). Server-side evidence slice for geoprocessing workload migration. Classification contract (Automated / Assisted / Manual review / Unsupported).
- **Migration performance evidence** — [`evidence/migration-performance-evidence.md`](migration-performance-evidence.md). Release-gated `honua.migration.performance-evidence` artifact (fingerprinted) and the workflow that emits it. Required reading before using "minimal-cost migration" wording.
- **MVP compatibility contract** — [`gis/MVP_COMPATIBILITY_CONTRACT.md`](../gis/MVP_COMPATIBILITY_CONTRACT.md). The launch-facing what-works / what-is-partial / what-is-pending contract for protocols and formats.

## SDK parity

Cross-repo coordination between Honua server and the first-party SDKs (`honua-sdk-dotnet`, `honua-sdk-js`, `honua-sdk-python`).

- **SDK compatibility matrix** — [`developer/SDK_COMPATIBILITY_MATRIX.md`](../developer/SDK_COMPATIBILITY_MATRIX.md). SDK ↔ server version pairing rules.
- **SDK compatibility metadata** — [`developer/SDK_COMPATIBILITY_METADATA.md`](../developer/SDK_COMPATIBILITY_METADATA.md). The `/api/v1/admin/capabilities` contract SDKs handshake against.
- **SDK standards coverage** — [`developer/SDK_STANDARDS_COVERAGE.md`](../developer/SDK_STANDARDS_COVERAGE.md). Per-language coverage ledger (supported convenience, generic protocol, deferred).
- **Metadata / catalog parity matrix** — [`developer/metadata-catalog-parity-matrix.md`](../developer/metadata-catalog-parity-matrix.md). Cross-SDK alignment for metadata and catalog reads.
- **SDK migration guide template** — [`developer/sdk-migration-template.md`](../developer/sdk-migration-template.md). Template SDK repos follow when their release breaks compatibility.

## Cross-repo quality

- **Public-interface quality model** — [`contributor/public-interface-quality-model.md`](../contributor/public-interface-quality-model.md). The canonical proof ledger (`public-interface-proof.json`) and the five proof classes (route coverage, operation coverage, standards conformance, tool interoperability, real-client certification).
- **MCP certification** — [`contributor/mcp-certification.md`](../contributor/mcp-certification.md). Cross-repo MCP certification testing, seed data, and CI jobs.

## Operator-side compatibility

- **Database support matrix** — [`operator/database-support-matrix.md`](../operator/database-support-matrix.md). Tested PostgreSQL/PostGIS versions, Aurora, Azure, plus the read-only DuckDB / SQL Server / Oracle / MySQL provider envelopes.

## Refresh cadence at a glance

| Refreshed | Where to look |
|---|---|
| Nightly (auto-generated) | [client interop gap report](../gis/gap-report.md), [cross-server consume gap report](../compatibility/cross-server-consume-gap-report.md), [migration performance evidence](migration-performance-evidence.md) |
| Per release | All `contributor/*-evidence.md` files, the [public-interface quality model](../contributor/public-interface-quality-model.md), the [release checklist](../contributor/RELEASE_CHECKLIST.md) |
| Per SDK release | [`developer/SDK_COMPATIBILITY_*`](../developer/), [`developer/metadata-catalog-parity-matrix.md`](../developer/metadata-catalog-parity-matrix.md) |
| When suite state changes | [`contributor/ogc-cite-conformance-evidence.md`](../contributor/ogc-cite-conformance-evidence.md), [`contributor/ogc-certification-path.md`](../contributor/ogc-certification-path.md) |
| When the code changes | The per-protocol parity matrices in `gis/`, the spec-coverage docs in `gis/specifications/` |

If you add a new evidence-bearing doc, please add a one-line pointer here so this map stays useful.
