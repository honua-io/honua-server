# Four-client protocol status, September 20, 2026

**167 passes, 4 recorded failures, 135 blocked, 52 not started and 18 provisional exclusions** across 94 operations and four client lanes (376 cells). This is a rollup of retained evidence from multiple runs and candidates, not a complete certification of the current runtime. Blocked means the operation still needs proof; it does not mean Honua lacks the protocol.

The active server fix branch is `feat/wcs-10-serving`, [PR 5038](https://github.com/honua-io/honua-server/pull/5038). The latest native QGIS evidence is committed in `honua-client-compat` at `2312dd99970f07b7ca0d38657e1c94a9b632edd3`. ArcPy follow-up evidence is on `fix/native-evidence-scope-20260920` at `b691b03`.

## Client totals

| Client | Pass | Fail | Blocked | Not started | Provisional exclusions | Total |
|---|---:|---:|---:|---:|---:|---:|
| ArcGIS Pro UI 3.7.1.1904 | 20 | 2 | 16 | 52 | 4 | 94 |
| ArcPy 3.7.1 | 36 | 2 | 54 | 0 | 2 | 94 |
| QGIS UI 3.44.14 LTR | 53 | 0 | 35 | 0 | 6 | 94 |
| PyQGIS 3.44.14 LTR | 58 | 0 | 30 | 0 | 6 | 94 |

## Protocol-by-protocol results

Each cell describes the operations in that protocol/version for one client. The operation count is per lane. GUI and Python results are independent. Missing result categories have zero operations.

| Protocol | Version | Operations | Pro UI | ArcPy | QGIS UI | PyQGIS |
|---|---|---:|---|---|---|---|
| wms | 1.3.0 | 6 | 3 pass, 3 unstarted | 2 pass, 4 blocked | 6 pass | 6 pass |
| wmts | 1.0.0 | 4 | 2 pass, 2 unstarted | 4 blocked | 4 pass | 4 pass |
| wfs | 2.0.0 | 8 | 3 pass, 2 unstarted, 3 excluded | 3 pass, 5 blocked | 6 pass, 2 excluded | 6 pass, 2 excluded |
| wcs | 1.0.0 | 3 | 3 blocked | 3 blocked | 3 pass | 3 pass |
| wcs | 2.0.1 | 3 | 3 unstarted | 3 blocked | 3 blocked | 3 pass |
| ogc-api-features | 1.0 | 8 | 7 unstarted, 1 excluded | 8 blocked | 8 pass | 8 pass |
| ogc-api-tiles | 1.0 | 2 | 2 blocked | 2 blocked | 2 blocked | 2 blocked |
| stac | 1.0.0 | 4 | 4 unstarted | 3 pass, 1 blocked | 4 pass | 4 pass |
| sensorthings | 1.1 | 3 | 3 blocked | 3 blocked | 3 pass | 3 pass |
| featureserver | GeoServices REST | 10 | 5 pass, 2 fail, 3 unstarted | 7 pass, 3 blocked | 6 pass, 4 excluded | 6 pass, 4 excluded |
| mapserver | GeoServices REST | 4 | 4 pass | 2 pass, 2 blocked | 4 pass | 4 pass |
| imageserver | GeoServices REST | 3 | 3 unstarted | 3 pass | 2 pass, 1 blocked | 2 pass, 1 blocked |
| vectortileserver | GeoServices REST | 3 | 3 pass | 3 pass | 3 pass | 3 pass |
| gpserver | GeoServices REST | 6 | 6 unstarted | 6 pass | 6 blocked | 6 blocked |
| geocodeserver | GeoServices REST | 4 | 4 unstarted | 4 pass | 4 blocked | 4 blocked |
| geometryserver | GeoServices REST | 4 | 4 unstarted | 4 blocked | 4 blocked | 4 blocked |
| naserver | GeoServices REST | 2 | 2 unstarted | 2 excluded | 2 blocked | 2 blocked |
| versionmanagementserver | GeoServices REST | 2 | 2 unstarted | 2 fail | 2 blocked | 2 blocked |
| geoservices-soap | GeoServices SOAP | 1 | 1 unstarted | 1 blocked | 1 blocked | 1 blocked |
| odata | v4 | 2 | 2 blocked | 2 blocked | 2 blocked | 2 blocked |
| ogc-api-maps | 1.0 | 1 | 1 blocked | 1 blocked | 1 blocked | 1 blocked |
| ogc-api-coverages | 1.0 | 1 | 1 blocked | 1 blocked | 1 blocked | 1 blocked |
| ogc-api-records | 1.0 | 1 | 1 blocked | 1 blocked | 1 blocked | 1 blocked |
| ogc-api-processes | 1.0 | 1 | 1 blocked | 1 blocked | 1 blocked | 1 blocked |
| ogc-api-styles | 1.0 | 1 | 1 blocked | 1 blocked | 1 pass | 1 pass |
| ogc-api-edr | 1.0 | 1 | 1 blocked | 1 blocked | 1 blocked | 1 blocked |
| pmtiles | 3 | 1 | 1 unstarted | 1 blocked | 1 pass | 1 pass |
| tilejson | 3.0.0 | 1 | 1 unstarted | 1 blocked | 1 blocked | 1 pass |
| cog | GeoTIFF | 1 | 1 unstarted | 1 pass | 1 pass | 1 pass |
| i3s-sceneserver | 1.x | 1 | 1 unstarted | 1 pass | 1 blocked | 1 blocked |
| 3d-tiles | 1.0 | 1 | 1 unstarted | 1 blocked | 1 pass | 1 pass |
| elevation | Esri | 1 | 1 unstarted | 1 pass | 1 blocked | 1 pass |

Full operation names, client build bindings, issue references and historical evidence are in the [operation checklist](CLIENT_CERTIFICATION_CHECKLIST.md) and its [JSON](data/client-certification-checklist.v1.json). The checksum below binds this dated snapshot; later checklist changes do not retroactively change this report.

## Recorded failures awaiting a passing native retest

| Client | Operation | Recorded problem |
|---|---|---|
| Pro UI | FeatureServer attachments | Recorded failed native workflow, issue #5012. |
| Pro UI | FeatureServer relatedRecords | Recorded failed native workflow, issue #5021. |
| ArcPy | VersionManagementServer create-version | Recorded wrong-workspace error after admin MapServer discovery returned 404; no request reached VersionManagementServer. Issue #5036. |
| ArcPy | VersionManagementServer reconcile-post | Blocked workflow recorded as failure under the same workspace-discovery problem, issue #5036. |

These are the current checklist failure states, not a claim that every referenced source defect remains unfixed. A code fix alone cannot replace the required passing client rerun.

## Exclusions under review

Of the original 167 exclusions, 149 have been challenged while retaining their history. Fourteen of those now have operation-specific SDK or UI evidence. Eighteen remain provisionally excluded:

| Scope | Cells | Basis and limit |
|---|---:|---|
| Pro WFS-T insert/update/delete and OGC API Features editing | 4 | Vendor documentation describes the native layers as read-only. This says nothing about Honua transaction support. |
| ArcPy synchronous NAServer route/service-area solve | 2 | The observed native `nax` path uses asynchronous GP web tools. It does not certify the distinct synchronous NAServer operation. |
| QGIS/PyQGIS WFS GetPropertyValue and ListStoredQueries | 4 | Prior native URI controls and inspected request implementations support a narrow provider gap. Further native entrypoints can still challenge it. |
| QGIS/PyQGIS FeatureServer attachments, relatedRecords, server statistics and replica-sync | 8 | Prior wire/provider review is more specific than an absent class name. The present empty fixture alone is not valid negative capability evidence. |

The remaining exclusions are client-operation assessments, not declarations that Honua does not implement those server endpoints. The [deeper exclusion audit](client-exclusion-followup-2026-09-20.md) records the evidence limits.

## Latest runtime and desktop findings

The fresh native QGIS run proved ImageServer discovery and exportImage display. Layer Properties then stopped responding, with increasing CPU and memory use after its metadata, legend and image requests had completed. Identify, numeric elevation and TileJSON UI cases remain blocked; no new exclusion was introduced. The [desktop diagnostic](qgis-imageserver-ui-2026-09-20.md) has the request correlation and precise receipts.

The current diagnostic server is Development/JIT source `25fa17d9cfa72340c9de4a33a743f19ff0911800`, image `sha256:0ad6f6c9d81ead9772cf0f0a2e282321811bc5d059c53dc78570a4f6f07f7780`. Enterprise/experimental configuration was already enabled, and both PostGIS and postgis_raster 3.4.3 were installed. The new ImageServer paths did not require installing raster support or obtaining a new license. These runtime facts do not imply that every optional protocol contract is implemented.

## Server conformance is a separate result

The authoritative [CITE snapshot](../cite-status.md), reviewed September 15, reports **1138/1138 passing across 14 suites**, source `b8ea218d07a52fe025d9382063d11b9b2c17c922`. That server conformance result does not supply missing native-client receipts. The client checklist also is not an exhaustive enumeration of every HTTP route, format variant, provider, authentication mode, SDK tool or licensed extension.

## Snapshot verification

- Source checklist SHA-256: `10f2a929696a1ce86ea0dbb1ec54e7d2016d797ccdb479f953437ff83cd0d514`.
- Protocol/version groups: 32; operation rows: 94; lane cells: 376.
- Counts were recomputed from every retained lane state; no excluded or blocked case was removed.
- Native QGIS evidence contract, input and image hashes, secret scan and all 240 client harness tests passed. The full native run gate remains blocked by three unattempted operations.
