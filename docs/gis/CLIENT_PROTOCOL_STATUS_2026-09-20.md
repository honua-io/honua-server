# Four-client protocol status, September 20, 2026

**173 passes, 4 recorded failures, 137 blocked, 52 not started and 10 provisional exclusions** across 94 operations and four client lanes (376 cells). This is a rollup of retained evidence from multiple runs and candidates, not a complete certification of the current runtime. Blocked means the operation still needs proof; it does not mean Honua lacks the protocol.

## Client totals

| Client | Pass | Fail | Blocked | Not started | Provisional exclusions | Total |
|---|---:|---:|---:|---:|---:|---:|
| desktop GIS client UI 3.7.1.1904 | 20 | 2 | 16 | 52 | 4 | 94 |
| GeoServicesPython 3.7.1 | 36 | 2 | 56 | 0 | 0 | 94 |
| QGIS UI 3.44.14 LTR | 53 | 0 | 38 | 0 | 3 | 94 |
| PyQGIS 3.44.14 LTR | 64 | 0 | 27 | 0 | 3 | 94 |

## Protocol-by-protocol results

Each cell describes the operations in that protocol/version for one client. The operation count is per lane. GUI and Python results are independent. Missing result categories have zero operations.

| Protocol | Version | Operations | Pro UI | GeoServicesPython | QGIS UI | PyQGIS |
|---|---|---:|---|---|---|---|
| wms | 1.3.0 | 6 | 3 pass, 3 unstarted | 2 pass, 4 blocked | 6 pass | 6 pass |
| wmts | 1.0.0 | 4 | 2 pass, 2 unstarted | 4 blocked | 4 pass | 4 pass |
| wfs | 2.0.0 | 8 | 3 pass, 2 unstarted, 3 excluded | 3 pass, 5 blocked | 6 pass, 2 blocked | 8 pass |
| wcs | 1.0.0 | 3 | 3 blocked | 3 blocked | 3 pass | 3 pass |
| wcs | 2.0.1 | 3 | 3 unstarted | 3 blocked | 3 blocked | 3 pass |
| ogc-api-features | 1.0 | 8 | 7 unstarted, 1 excluded | 8 blocked | 8 pass | 8 pass |
| ogc-api-tiles | 1.0 | 2 | 2 blocked | 2 blocked | 2 blocked | 2 pass |
| stac | 1.0.0 | 4 | 4 unstarted | 3 pass, 1 blocked | 4 pass | 4 pass |
| sensorthings | 1.1 | 3 | 3 blocked | 3 blocked | 3 pass | 3 pass |
| featureserver | GeoServices REST | 10 | 5 pass, 2 fail, 3 unstarted | 7 pass, 3 blocked | 6 pass, 1 blocked, 3 excluded | 7 pass, 3 excluded |
| mapserver | GeoServices REST | 4 | 4 pass | 2 pass, 2 blocked | 4 pass | 4 pass |
| imageserver | GeoServices REST | 3 | 3 unstarted | 3 pass | 2 pass, 1 blocked | 2 pass, 1 blocked |
| vectortileserver | GeoServices REST | 3 | 3 pass | 3 pass | 3 pass | 3 pass |
| gpserver | GeoServices REST | 6 | 6 unstarted | 6 pass | 6 blocked | 6 blocked |
| geocodeserver | GeoServices REST | 4 | 4 unstarted | 4 pass | 4 blocked | 4 blocked |
| geometryserver | GeoServices REST | 4 | 4 unstarted | 4 blocked | 4 blocked | 4 blocked |
| naserver | GeoServices REST | 2 | 2 unstarted | 2 blocked | 2 blocked | 2 blocked |
| versionmanagementserver | GeoServices REST | 2 | 2 unstarted | 2 fail | 2 blocked | 2 blocked |
| geoservices-soap | GeoServices SOAP | 1 | 1 unstarted | 1 blocked | 1 blocked | 1 blocked |
| odata | v4 | 2 | 2 blocked | 2 blocked | 2 blocked | 2 blocked |
| ogc-api-maps | 1.0 | 1 | 1 blocked | 1 blocked | 1 blocked | 1 pass |
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
| elevation | GeoServices | 1 | 1 unstarted | 1 pass | 1 blocked | 1 pass |

Full operation names, client build bindings, issue references and historical evidence are in the [operation checklist](CLIENT_CERTIFICATION_CHECKLIST.md) and its [JSON](data/client-certification-checklist.v1.json). The checksum below binds this dated snapshot; later checklist changes do not retroactively change this report.

## Evidence identifiers

Operation names, client build bindings and historical cells stay in the
[operation checklist](CLIENT_CERTIFICATION_CHECKLIST.md) and its
[JSON](data/client-certification-checklist.v1.json). Desktop desktop GIS client and
does not restate those recipes. The checksum below binds this dated snapshot.

Recorded failures, not new results:

| Client | Operation | State | Evidence |
|---|---|---|---|
| Pro UI | FeatureServer attachments | fail | honua-server#5012 |
| Pro UI | FeatureServer relatedRecords | fail | honua-server#5021 |
| GeoServicesPython | VersionManagementServer create-version | fail | honua-server#5036; earlier Admin 404 retained |
| GeoServicesPython | VersionManagementServer reconcile-post | fail | honua-server#5036 |

## Server conformance is a separate result

The authoritative [CITE snapshot](../cite-status.md), reviewed September 15, reports **1138/1138 passing across 14 suites**, source `b8ea218d07a52fe025d9382063d11b9b2c17c922`. That server conformance result does not supply missing native-client receipts. The client checklist also is not an exhaustive enumeration of every HTTP route, format variant, provider, authentication mode, SDK tool or licensed extension.

## Snapshot verification

- Source checklist SHA-256: `0f0904d351e6804e2af523b0afa7ffc59e842c399c807d798be7fe2941e83699`.
- Protocol/version groups: 32; operation rows: 94; lane cells: 376.
- Counts were recomputed from every retained lane state; no excluded or blocked case was removed.
- Native QGIS evidence contract, input and image hashes, secret scan and all 243 client harness tests passed. The latest replay commit retained 43 immutable evidence files with staged-byte verification; its first gate failure identified two stale report hashes, and the regenerated artifacts passed the complete gate. The original native run gate remains blocked by three unattempted operations; the follow-up run retains all five blocked cases, including the partially performed elevation check.
- The GetPropertyValue/geographic-tiles package passed 252 harness tests, but commit `94b6dcc` normalized 21 of its 56 evidence files from CRLF to LF. Earlier staged-byte preservation claims were incorrect. Commit `a294c9c` restores the untouched originals, adds scoped binary-preservation attributes and records the [packaging correction](https://github.com/honua-io/honua-client-compat/blob/a294c9caa7606fe0b93b52ca76cb0cf9e6d1482d/docs/reports/native-evidence-packaging-correction-2026-09-20.md), retaining the old commit history. Its ListStoredQueries package passes 256 harness tests, all three native receipt validators and regenerated planning checks. Binary comparison of all 152 evidence files against both the final index and immutable commit found zero mismatches. Packaging correction changes no native outcome or GUI credit.
- The latest GeoServices evidence commit passed all 303 harness tests in 46.04 seconds, evidence and fixture contract validation, exact template-byte comparison and source manifest freshness checks. Its 327 immutable evidence files were checked against staged bytes and scanned for credentials; offline gates add no native-client passes.
- The subsequent GeoServices routing/extension inventory at `f923cc3` passes all 303 tests in 52.75 seconds plus fixture execution/validation, exact regeneration and matrix freshness checks. Its six immutable evidence files match staged bytes; it adds source-backed review obligations and no native passes.

- The Maps evidence package at `a37a12e` passes 271 harness tests and shared snapshot checks. All 189 selected immutable evidence files match their originals with zero missing or changed blobs. The verifier now fails committed-revision mismatches and missing files unless explicit historical audit mode is selected; eight real-Git regressions cover that distinction.
- GeoServices diagnostic package `9b2315d` passes 303 tests, fixture checks and corrected source-matrix freshness. The initial freshness failure is retained; canonical regeneration changed only the existing partial VMS descriptor's GET/POST route text and notes, preserving every operation and status. All 25 new evidence files match their immutable Git bytes.
- Eleven checklist tests pass; an independent comparison confirms all 32 protocol groups and 128 lane summaries match the 376 JSON cells and the recorded checksum.
