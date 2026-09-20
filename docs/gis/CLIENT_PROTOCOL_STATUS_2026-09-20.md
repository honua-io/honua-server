# Four-client protocol status, September 20, 2026

**172 passes, 4 recorded failures, 138 blocked, 52 not started and 10 provisional exclusions** across 94 operations and four client lanes (376 cells). This is a rollup of retained evidence from multiple runs and candidates, not a complete certification of the current runtime. Blocked means the operation still needs proof; it does not mean Honua lacks the protocol.

The active server fix branch is `feat/wcs-10-serving`, [PR 5038](https://github.com/honua-io/honua-server/pull/5038). The latest native QGIS evidence is committed in `honua-client-compat` at [a294c9c](https://github.com/honua-io/honua-client-compat/commit/a294c9caa7606fe0b93b52ca76cb0cf9e6d1482d), following the statistics/raster replay commit [9d6e362](https://github.com/honua-io/honua-client-compat/commit/9d6e362c4bd46d56ced7ea1ffedc5b8aac84698e). ArcPy follow-up evidence is on `fix/native-evidence-scope-20260920` at [58e23f3](https://github.com/honua-io/honua-esri-compat/commit/58e23f3f87a40688611010f954865d25f9f4d3ca); the later source-only routing/extension inventory is committed at [f923cc3](https://github.com/honua-io/honua-esri-compat/commit/f923cc3bb52b9731a75030bd4d66d7c7c80aeba9).

## Client totals

| Client | Pass | Fail | Blocked | Not started | Provisional exclusions | Total |
|---|---:|---:|---:|---:|---:|---:|
| ArcGIS Pro UI 3.7.1.1904 | 20 | 2 | 16 | 52 | 4 | 94 |
| ArcPy 3.7.1 | 36 | 2 | 56 | 0 | 0 | 94 |
| QGIS UI 3.44.14 LTR | 53 | 0 | 38 | 0 | 3 | 94 |
| PyQGIS 3.44.14 LTR | 63 | 0 | 28 | 0 | 3 | 94 |

## Protocol-by-protocol results

Each cell describes the operations in that protocol/version for one client. The operation count is per lane. GUI and Python results are independent. Missing result categories have zero operations.

| Protocol | Version | Operations | Pro UI | ArcPy | QGIS UI | PyQGIS |
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

One earlier ArcPy statistics pass counted rows locally instead of demonstrating a remote `outStatistics` request. That unsupported pass and receipt remain retained as history. Run e then sent actual aggregate POSTs and exposed a server response-schema failure. Fresh run g on the repaired candidate now passes with complete native POST traces, correct field definitions, independent saved-GDB readback and matching SQL. The checklist preserves both the invalid local calculation and the intervening native failure. The [verdict audit](arcpy-local-calculation-verdict-audit-2026-09-20.md) explains the earlier evidence correction; the passing replay does not validate the old local calculation.

## Recorded failures awaiting a passing native retest

| Client | Operation | Recorded problem |
|---|---|---|
| Pro UI | FeatureServer attachments | Recorded failed native workflow, issue #5012. |
| Pro UI | FeatureServer relatedRecords | Recorded failed native workflow, issue #5021. |
| ArcPy | VersionManagementServer create-version | Fresh native CreateVersion still reports ERROR 000301 after corrected branch flags and successful Admin discovery. The original Admin404 failure is retained. Issue #5036. |
| ArcPy | VersionManagementServer reconcile-post | Blocked workflow recorded as failure under the same workspace-discovery problem, issue #5036. |

PyQGIS statistics is now resolved by native run d on source `8b7aea9f6`: both configured-URL OGR/ESRIJSON cases match SQL before and after project reload. ArcPy run g also passes: native POSTs 6/7, complete HTTP 200 bodies, Integer counts/Double sums and saved geodatabase values match SQL for both ungrouped count 3/sum 6 and grouped active count 2/sum 4, inactive count 1/sum 2. Its seven captured pairs have no drops/gaps; two unrelated reads receive no credit. Run f remains preserved with its incomplete trace and earns no remote-operation pass. Fresh ArcPy versioning still reports ERROR 000301 despite corrected branch flags and successful Admin discovery.

These are the current checklist failure states, not a claim that every referenced source defect remains unfixed. A code fix alone cannot replace the required passing client rerun.

## Exclusions under review

Of the original 167 exclusions, 157 have been challenged while retaining their history. Nineteen of those now have operation-specific SDK or UI evidence. Ten remain provisionally excluded:

| Scope | Cells | Basis and limit |
|---|---:|---|
| Pro WFS-T insert/update/delete and OGC API Features editing | 4 | Vendor documentation describes the native layers as read-only. The separate Data Interoperability investigation also finds WFS and OGCAPI_FEATURES listed as readers, with WFS transaction support absent in the format documentation. A license alone is not a documented writer path; this says nothing about Honua transaction support. |
| QGIS/PyQGIS FeatureServer attachments, relatedRecords and replica-sync | 6 | Prior wire/provider review is more specific than an absent class name. The present empty fixture alone is not valid negative capability evidence. |

The two statistics exclusions were disproven by the built-in OGR/ESRIJSON path. Fresh run d now passes ungrouped and grouped count/sum against independent SQL, including project reload, on repaired source `8b7aea9f6`. The PyQGIS statistics cell is passed; the QGIS UI cell remains blocked. The earlier grouped failure and original exclusion are retained in the checklist history. See the [native follow-up report](https://github.com/honua-io/honua-client-compat/blob/9d6e362c4bd46d56ced7ea1ffedc5b8aac84698e/docs/reports/pyqgis-featureserver-exclusion-followup-2026-09-20.md).

The remaining exclusions are client-operation assessments, not declarations that Honua does not implement those server endpoints. The [deeper exclusion audit](client-exclusion-followup-2026-09-20.md) records the evidence limits.

The two ArcPy synchronous NAServer exclusions now reopen as blocked. Installed `arcpy.na.MakeRouteAnalysisLayer` and `MakeServiceAreaAnalysisLayer`, followed by `arcpy.na.Solve`, are distinct from the earlier `nax` path. Their documented portal-backed inputs justify an owned-layer probe, but their compiled transport has not yet been captured. The retained source inventory earns no native pass and does not assume a direct NAServer URL is a supported constructor input. This later routing review extends the fourteen-cell snapshot in the new PyQGIS exclusion report.

## Latest runtime and desktop findings

The first native QGIS run proved ImageServer discovery and exportImage display. A subsequent run also read numeric elevation 100 before and after saving/reopening its native project, but Properties hung before the required grid/type inspection. That full elevation cell remains blocked. The isolated offscreen QtWebKit diagnostic also failed on trivial local HTML, so it does not prove a Honua cause. In the original run, Layer Properties then stopped responding, with increasing CPU and memory use after its metadata, legend and image requests had completed. Identify, numeric elevation and TileJSON UI cases remain blocked; no new exclusion was introduced. The [desktop diagnostic](qgis-imageserver-ui-2026-09-20.md) has the request correlation and precise receipts.

Desktop capture was retried after the user re-enabled access. Both fresh selection attempts failed with `computer-use request timed out: get_window_state`; no UI action or pass resulted.

The current diagnostic server is Development/JIT source `8b7aea9f6c73504d968560227926e3b8b4c5ddd0`, image `sha256:28d09586daf74f3930407ed9695c672a66ff3a47a52ec068fb722503865c232a`, container `6eebbd8f16512d336e1a5ee69445dee66edb10ba47770c9cbee788d51c920b48`. Activation at 12:50 UTC verified copied fixture paths, sizes and SHA-256 hashes before replacing the runtime; the previous container is preserved stopped. Enterprise/experimental configuration was already enabled, and both PostGIS and postgis_raster 3.4.3 were installed. The new ImageServer paths did not require installing raster support or obtaining a new license. These runtime facts do not imply that every optional protocol contract is implemented.

The installed ArcPy Basic Named User license is not by itself a versioning exclusion: current [Create Version documentation](https://doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/create-version.html) supports that combination for branch versioning. Esri's [Advanced Editing requirements](https://support.esri.com/en-us/knowledge-base/version-management-service-arcgis-advanced-editing-user-000037734) separately describe operation- and deployment-dependent extension licensing, including reconcile/post. Honua's Enterprise setting does not prove an Esri extension assignment. No retained native trace has yet established that extension licensing causes the current workspace-initialization failure; discovery and identity defects remain separately evidenced.

Fresh PyQGIS run j on the current source `8b7aea9f6` candidate passes the existing three-case SDK profile: WCS1 and WCS2 discovery, pixel/grid assertions, project reload and independent full/subset TIFF controls, plus configured OGC API Tiles reads. New SQL grid/corner controls agree, both TIFF hashes match the prior repaired image, and worker exit 0 and receipt integrity are verified. The configured tile result retains its explicit projected bounds and does not close default discovery. These are regression results, with no additional checklist or GUI credit.

New GetPropertyValue run a uses stock OGR with exact WFS request URLs and `outputFormat=application/geo+json`. Numeric and text projections agree with independent SQL/HTTP controls and separate native project reloads. This resolves one PyQGIS operation and reopens its GUI exclusion without awarding a GUI pass; it does not prove stock WFS-provider request generation.

ListStoredQueries run c also passes through stock GMLAS/PyQGIS with the official WFS 2.0 schema, explicit non-feature metadata configuration, verified TLS and a private schema cache. Three native tables expose the actual query ID, title and eight return-feature types; parent-child keys, independent XML before/after and separate project reload agree. This resolves one further PyQGIS operation and reopens the corresponding GUI exclusion. It does not prove the dedicated WFS provider generates the request. Attempt a found that provider sublayer discovery ignored the URI options; attempt b produced valid tables but failed an incorrect parent-field title expectation. Both remain retained, with no server bug or pass attributed to those attempts.

New geographic OGC API Tiles run c selects the advertised WorldCRS84Quad at zoom 10 through the stock OGCAPI driver, without manually supplied bounds. Collection-based tileset discovery, CRS and extent, all nine non-null-geometry fixture IDs/names, and native project reload agree with independent SQL before/after. All point errors are within the half-cell diagonal calculated from the retained tiles' encoded extent 4096: maximum error 0.000019538 degrees, bound 0.000030346 degrees. The two qualified tileset/tile operations pass; a top-level catalog chooser and WebMercator default discovery remain unproven. Failed receipt-writing attempt a and preliminary diagnostic b are preserved. Native logs show OGCAPI/MVT opens, not a complete wire trace; the interpreter and image-source hash inspection is explicitly post-run. Neither new Python result adds UI credit.

Further WCS1 traces retain complete native XML/TIFF HTTP 200 responses with no receive drops. Native ArcPy can read the exact returned TIFF locally; MakeWCSLayer still fails with both the bundled and Pro-selected Conda environments. A fresh owned TEMP/TMP directory causes a new GetCapabilities request but still fails after eight successful responses. Localhost diagnostics construct layers with the same description/TIFF bodies, including gzip/chunked XML and a separate full-original-path control; they change the authority, transport and capabilities links and earn no native service credit. The original path ancestry alone does not reproduce the failure. Neither a cache cause nor a server transport defect is proven. No new WCS exclusion or pass was introduced.

A separate HTTPS responder using the existing matching certificate reproduces the WCS failure without Honua or Caddy serving its responses. The corresponding HTTP control succeeds. An original-service replay with process-local CA-bundle variables still fails; whether the failing ArcPy path consumes those variables is unproven. These diagnostics narrow the connection investigation without establishing a server fix or a new exclusion.

A separate owned-branch REST/SQL diagnostic confirms [#5044](https://github.com/honua-io/honua-server/issues/5044): a successful branch edit appears in the SQL overlay, but the storage-mapped FeatureServer query returns DEFAULT despite the exact branch GUID. The owned branch/delta were removed and DEFAULT remained unchanged. This is a newly tested query variant, not a retroactive failure of the historical DEFAULT-query receipt. The fresh repaired-image diagnostic now returns branch value 7920 while DEFAULT remains 1, agrees with the stored overlay, and removes its owned version/delta with DEFAULT unchanged. This proves the targeted server diagnostic, with no native-client credit.

Eight isolated localhost metadata variants then identified two discovery omissions: FeatureServer `hasBranchVersionedData` and MapServer `supportedExtensions`. Together they make native ArcPy request the VersionManagementServer root without adding `currentVersion`. VMS root POST routing is repaired in the subsequent source slice; response initialization still needs a successful native replay. These altered localhost responses earn no native service pass; neither changing the version string alone nor later response-shape/GUID controls completed creation.

Repair work now has 58 passing mapped-provider/version-query tests covering grouped ordering, branch overlays and raw numeric filters, five final versioning metadata endpoint tests, and 41 passing raster tests after three new optimizer regressions reproduced repeated expressions. All 41 selected GeoServices endpoint tests pass, including 23 new statistics field-schema cases for layer/service POSTs, empty/null results, dates and restricted fields. These source/test results do not replace client replays.

The first compiled branch-provider run passed 55 of 56 tests and exposed a further raw-filter defect, [#5046](https://github.com/honua-io/honua-server/issues/5046): a numeric JSONB field was compared as text against a numeric literal, producing PostgreSQL 42883. A separate two-case run reproduced the same error on both DEFAULT and branch reads. The repair preserves those assertions; the final 58-case run passes with no failures or skips. This provider result adds no native-client pass or exclusion.

The [#5047](https://github.com/honua-io/honua-server/issues/5047) repair now derives versioning metadata from each accessible publication's resolved reader and binding, sharing provider eligibility with actual branch reads. Commit `0e9ef71d200355850d230a1a2fb9ad7657603992` passes 41 provider tests and 16 FeatureServer/Admin endpoint tests, including explicit publication overrides, managed connections, rejected external mappings, access filtering, licensing and the experimental gate. Earlier failed test-fixture attempts are preserved; the final endpoint fixture uses an unambiguous owned service route and retains its positive and negative assertions. This change has not been activated in the current runtime and gives no additional native-client credit; arbitrary external mappings remain uncertified.

The subsequent discovery/attachment source slice, commit `6fc088346e303caade6841024d8c90eaa5aabd4e`, adds FeatureServer `hasBranchVersionedData`, MapServer `supportedExtensions` and read-only VMS root POST routing. Discovery follows canonical publication eligibility, access checks, licensing and the experimental gate. Attachment advertisement now respects the existing typed `editing.supportsAttachments` flag: explicit false overrides stale legacy annotations. The corrected endpoint run c passes 14 tests; six unchanged Admin/attachment tests passed in run a against the same production hashes. Run a's eight assertion failures and run b's analyzer failure remain retained. Sixteen denied GET/POST responses were independently checked as canonical error envelopes, including GeoServices HTTP 200 with error codes 501 or 402. These repairs are not yet active and add no native credit. Persistent DEFAULT identity, the VMS capability response shape and supported attachment authoring remain further work. All 35 selected architecture/governance checks pass with zero failures/skips, covering registry drift, API surface coverage, test attributes, feature-catalog/parity drift and authorization guards; both canonical emitters and the no-ArcGIS-version gate also pass. This is a targeted result, not a full architecture-suite or CI claim. The emitted feature catalog is an embedded Server resource, so the next candidate build must incorporate the newly generated file before activation.

## Server conformance is a separate result

The authoritative [CITE snapshot](../cite-status.md), reviewed September 15, reports **1138/1138 passing across 14 suites**, source `b8ea218d07a52fe025d9382063d11b9b2c17c922`. That server conformance result does not supply missing native-client receipts. The client checklist also is not an exhaustive enumeration of every HTTP route, format variant, provider, authentication mode, SDK tool or licensed extension.

## Snapshot verification

- Source checklist SHA-256: `0fc39d4db2491b1ed920a2ad5448a8b8ba8dea9ecc986af8ca79648f58f0829e`.
- Protocol/version groups: 32; operation rows: 94; lane cells: 376.
- Counts were recomputed from every retained lane state; no excluded or blocked case was removed.
- Native QGIS evidence contract, input and image hashes, secret scan and all 243 client harness tests passed. The latest replay commit retained 43 immutable evidence files with staged-byte verification; its first gate failure identified two stale report hashes, and the regenerated artifacts passed the complete gate. The original native run gate remains blocked by three unattempted operations; the follow-up run retains all five blocked cases, including the partially performed elevation check.
- The GetPropertyValue/geographic-tiles package passed 252 harness tests, but commit `94b6dcc` normalized 21 of its 56 evidence files from CRLF to LF. Earlier staged-byte preservation claims were incorrect. Commit `a294c9c` restores the untouched originals, adds scoped binary-preservation attributes and records the [packaging correction](https://github.com/honua-io/honua-client-compat/blob/a294c9caa7606fe0b93b52ca76cb0cf9e6d1482d/docs/reports/native-evidence-packaging-correction-2026-09-20.md), retaining the old commit history. Its ListStoredQueries package passes 256 harness tests, all three native receipt validators and regenerated planning checks. Binary comparison of all 152 evidence files against both the final index and immutable commit found zero mismatches. Packaging correction changes no native outcome or GUI credit.
- The latest Esri evidence commit passed all 303 harness tests in 46.04 seconds, evidence and fixture contract validation, exact template-byte comparison and source manifest freshness checks. Its 327 immutable evidence files were checked against staged bytes and scanned for credentials; offline gates add no native-client passes.
- The subsequent Esri routing/extension inventory at `f923cc3` passes all 303 tests in 52.75 seconds plus fixture execution/validation, exact regeneration and matrix freshness checks. Its six immutable evidence files match staged bytes; it adds source-backed review obligations and no native passes.

- Ten checklist tests pass; an independent comparison confirms all 32 protocol groups and 128 lane summaries match the 376 JSON cells and the recorded checksum.
