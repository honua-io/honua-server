# Four-client protocol status, September 20, 2026

**166 passes, 6 recorded failures, 136 blocked, 52 not started and 16 provisional exclusions** across 94 operations and four client lanes (376 cells). This is a rollup of retained evidence from multiple runs and candidates, not a complete certification of the current runtime. Blocked means the operation still needs proof; it does not mean Honua lacks the protocol.

The active server fix branch is `feat/wcs-10-serving`, [PR 5038](https://github.com/honua-io/honua-server/pull/5038). The latest native QGIS evidence is committed in `honua-client-compat` at `ac9e4dc`. ArcPy follow-up evidence is on `fix/native-evidence-scope-20260920` at [e6fa8bc](https://github.com/honua-io/honua-esri-compat/commit/e6fa8bc877282d4563197040414b9c27cb3265ca).

## Client totals

| Client | Pass | Fail | Blocked | Not started | Provisional exclusions | Total |
|---|---:|---:|---:|---:|---:|---:|
| ArcGIS Pro UI 3.7.1.1904 | 20 | 2 | 16 | 52 | 4 | 94 |
| ArcPy 3.7.1 | 35 | 3 | 54 | 0 | 2 | 94 |
| QGIS UI 3.44.14 LTR | 53 | 0 | 36 | 0 | 5 | 94 |
| PyQGIS 3.44.14 LTR | 58 | 1 | 30 | 0 | 5 | 94 |

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
| featureserver | GeoServices REST | 10 | 5 pass, 2 fail, 3 unstarted | 6 pass, 1 fail, 3 blocked | 6 pass, 1 blocked, 3 excluded | 6 pass, 1 fail, 3 excluded |
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

## Corrected evidence claim

One earlier ArcPy statistics pass counted rows locally instead of demonstrating a remote `outStatistics` request. That unsupported pass and receipt remain retained as history. A subsequent fresh native DBMS Statistics run now sends actual aggregate POSTs and exposes a server response-schema failure, so the cell is failed. The [verdict audit](arcpy-local-calculation-verdict-audit-2026-09-20.md) explains the earlier evidence correction; the new failure does not validate the old local calculation.

## Recorded failures awaiting a passing native retest

| Client | Operation | Recorded problem |
|---|---|---|
| Pro UI | FeatureServer attachments | Recorded failed native workflow, issue #5012. |
| Pro UI | FeatureServer relatedRecords | Recorded failed native workflow, issue #5021. |
| ArcPy | VersionManagementServer create-version | Recorded wrong-workspace error after admin MapServer discovery returned 404; no request reached VersionManagementServer. Issue #5036. |
| ArcPy | VersionManagementServer reconcile-post | Blocked workflow recorded as failure under the same workspace-discovery problem, issue #5036. |
| ArcPy | FeatureServer statistics | Native DBMS Statistics receives correct ungrouped numbers without field definitions; independent native readback finds only OBJECTID in the output table. Issue [#5045](https://github.com/honua-io/honua-server/issues/5045). Its grouped POST also reproduces #5043. |
| PyQGIS | FeatureServer statistics | Native OGR reads ungrouped aggregates, but grouped statistics ordered by a JSONB-backed group field produce PostgreSQL 42803 and an invalid native layer. Issue [#5043](https://github.com/honua-io/honua-server/issues/5043). |

These are the current checklist failure states, not a claim that every referenced source defect remains unfixed. A code fix alone cannot replace the required passing client rerun.

## Exclusions under review

Of the original 167 exclusions, 151 have been challenged while retaining their history. Fourteen of those now have operation-specific SDK or UI evidence. Sixteen remain provisionally excluded:

| Scope | Cells | Basis and limit |
|---|---:|---|
| Pro WFS-T insert/update/delete and OGC API Features editing | 4 | Vendor documentation describes the native layers as read-only. This says nothing about Honua transaction support. |
| ArcPy synchronous NAServer route/service-area solve | 2 | The observed native `nax` path uses asynchronous GP web tools. It does not certify the distinct synchronous NAServer operation. |
| QGIS/PyQGIS WFS GetPropertyValue and ListStoredQueries | 4 | Prior native URI controls and inspected request implementations support a narrow provider gap. Further native entrypoints can still challenge it. |
| QGIS/PyQGIS FeatureServer attachments, relatedRecords and replica-sync | 6 | Prior wire/provider review is more specific than an absent class name. The present empty fixture alone is not valid negative capability evidence. |

The two statistics exclusions were disproven by the built-in OGR/ESRIJSON path. Its ungrouped count/sum values and project reload pass; grouped output exposes issue #5043, while GUI remains untested. The full operation stays open. See the [native follow-up report](https://github.com/honua-io/honua-client-compat/blob/ac9e4dc3336f361d5bfe8ffb38c485fd11cfb292/docs/reports/pyqgis-featureserver-exclusion-followup-2026-09-20.md).

The remaining exclusions are client-operation assessments, not declarations that Honua does not implement those server endpoints. The [deeper exclusion audit](client-exclusion-followup-2026-09-20.md) records the evidence limits.

## Latest runtime and desktop findings

The first native QGIS run proved ImageServer discovery and exportImage display. A subsequent run also read numeric elevation 100 before and after saving/reopening its native project, but Properties hung before the required grid/type inspection. That full elevation cell remains blocked. The isolated offscreen QtWebKit diagnostic also failed on trivial local HTML, so it does not prove a Honua cause. In the original run, Layer Properties then stopped responding, with increasing CPU and memory use after its metadata, legend and image requests had completed. Identify, numeric elevation and TileJSON UI cases remain blocked; no new exclusion was introduced. The [desktop diagnostic](qgis-imageserver-ui-2026-09-20.md) has the request correlation and precise receipts.

The current diagnostic server is Development/JIT source `25fa17d9cfa72340c9de4a33a743f19ff0911800`, image `sha256:0ad6f6c9d81ead9772cf0f0a2e282321811bc5d059c53dc78570a4f6f07f7780`. Enterprise/experimental configuration was already enabled, and both PostGIS and postgis_raster 3.4.3 were installed. The new ImageServer paths did not require installing raster support or obtaining a new license. These runtime facts do not imply that every optional protocol contract is implemented.

Further WCS1 traces retain complete native XML/TIFF HTTP 200 responses with no receive drops. Native ArcPy can read the exact returned TIFF locally; MakeWCSLayer still fails with both the bundled and Pro-selected Conda environments. A fresh owned TEMP/TMP directory causes a new GetCapabilities request but still fails after eight successful responses. Localhost diagnostics construct layers with the same description/TIFF bodies, including gzip/chunked XML and a separate full-original-path control; they change the authority, transport and capabilities links and earn no native service credit. The original path ancestry alone does not reproduce the failure. Neither a cache cause nor a server transport defect is proven. No new WCS exclusion or pass was introduced.

A separate HTTPS responder using the existing matching certificate reproduces the WCS failure without Honua or Caddy serving its responses. The corresponding HTTP control succeeds. An original-service replay with process-local CA-bundle variables still fails; whether the failing ArcPy path consumes those variables is unproven. These diagnostics narrow the connection investigation without establishing a server fix or a new exclusion.

A separate owned-branch REST/SQL diagnostic confirms [#5044](https://github.com/honua-io/honua-server/issues/5044): a successful branch edit appears in the SQL overlay, but the storage-mapped FeatureServer query returns DEFAULT despite the exact branch GUID. The owned branch/delta were removed and DEFAULT remained unchanged. This is a newly tested query variant, not a retroactive failure of the historical DEFAULT-query receipt. Its fix must preserve mapped filters, row-level security and masks.

Repair work now has 58 passing mapped-provider/version-query tests covering grouped ordering, branch overlays and raw numeric filters, five final versioning metadata endpoint tests, and 41 passing raster tests after three new optimizer regressions reproduced repeated expressions. All 41 selected GeoServices endpoint tests pass, including 23 new statistics field-schema cases for layer/service POSTs, empty/null results, dates and restricted fields. These source/test results do not replace client replays.

The first compiled branch-provider run passed 55 of 56 tests and exposed a further raw-filter defect, [#5046](https://github.com/honua-io/honua-server/issues/5046): a numeric JSONB field was compared as text against a numeric literal, producing PostgreSQL 42883. A separate two-case run reproduced the same error on both DEFAULT and branch reads. The repair preserves those assertions; the final 58-case run passes with no failures or skips. This provider result adds no native-client pass or exclusion.

Read-only review also identified [#5047](https://github.com/honua-io/honua-server/issues/5047): versioning metadata uses host-wide eligibility and can over-advertise external publications that the reader correctly rejects. The managed fixture is unaffected. Per-publication capability and service/Admin aggregation tests are the next repair; arbitrary external mappings remain uncertified.

## Server conformance is a separate result

The authoritative [CITE snapshot](../cite-status.md), reviewed September 15, reports **1138/1138 passing across 14 suites**, source `b8ea218d07a52fe025d9382063d11b9b2c17c922`. That server conformance result does not supply missing native-client receipts. The client checklist also is not an exhaustive enumeration of every HTTP route, format variant, provider, authentication mode, SDK tool or licensed extension.

## Snapshot verification

- Source checklist SHA-256: `c122a32b4d725b328849c7f3ebb2b164973f60b5dbd7fa64cbb604851e9680cb`.
- Protocol/version groups: 32; operation rows: 94; lane cells: 376.
- Counts were recomputed from every retained lane state; no excluded or blocked case was removed.
- Native QGIS evidence contract, input and image hashes, secret scan and all 243 client harness tests passed. The original native run gate remains blocked by three unattempted operations; the follow-up run retains all five blocked cases, including the partially performed elevation check.
- The Esri evidence commit passed all 303 harness tests, fixture contract validation, exact template-byte comparison and source manifest freshness checks. Its 344 immutable evidence files were checked against staged bytes and scanned for credentials; offline gates add no native-client passes.
