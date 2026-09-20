# QGIS ImageServer native UI diagnostic, September 20, 2026

Fresh inspected QGIS 3.44.14 LTR computer-use receipts close exactly two UI
operations: ImageServer service-info and exportImage. The operator used the
native ArcGIS REST connection tree, added the ImageServer, inspected layer
information and its rendered canvas, and compared the image to the independent
pre-execution PNG control. The UI showed an existing native authentication
configuration reference; this is not an anonymous-native claim. Separate server
request correlation records successful ApiKey authentication without retaining
credential values.

The current checklist has **167 passes, 4 failures, 135 blocked, 52 not started
and 18 provisional exclusions**, across **94 operations / 376 cells**: 185
closed and 191 open. QGIS UI passes rise from 51 to 53; Pro remains 20. All 149
disputed historical exclusions remain preserved; 14 now also have a preserved
review state and operation-specific SDK or UI evidence. These totals combine
historical and fresh receipts, not one complete candidate rerun.

The immutable five-operation plan is
`honua-client-compat/evidence/native-qgis-image-tilejson-20260920-a/plan.json`,
SHA256 `790e0e594128c2ccbb86c674d4735468671b4505d17207f3d2576548e8936f9f`.
Its `results.json` references the fresh inspected checkpoint receipts. Source
`25fa17d9cfa72340c9de4a33a743f19ff0911800` and image
`sha256:0ad6f6c9d81ead9772cf0f0a2e282321811bc5d059c53dc78570a4f6f07f7780`
are Development/JIT. This is native UI diagnostic evidence, with no shipping
NativeAOT importer claim.

The profile, independent controls, inspected receipts and supplemental request
correlation are committed in `honua-client-compat` at
`2312dd99970f07b7ca0d38657e1c94a9b632edd3`. Client validation passed all 240 tests
and shared-snapshot verification. Server validation passed all 155 discovered
certification unit tests, including the five focused checklist regressions,
plus generated-checklist and whitespace checks.

| Operation | Evidence |
|---|---|
| ImageServer service-info | Native service tree and layer-information checkpoints show the ImageServer URL, EPSG:4326 and extent `[-122.5,37.7,-122.35,37.84]`, matching independent pre-execution metadata. Dynamic 0x0 provider dimensions do not prevent rendering. |
| ImageServer exportImage | Native canvas visually matches the independent PNG control. Server logs corroborate the QGIS/34414 request at 09:37:31 UTC: HTTP200, PNG765x724, 2682bytes, 632ms; trace `6f136fe7c94227895d3b74a229d701ed`. |

Layer Properties subsequently became unresponsive. ImageServer identify,
elevation point-query and TileJSON descriptor remain open in the UI lane. The
earlier successful SDK operations do not substitute for these uncompleted UI
operations, and the hang does not create a no-client exclusion.

The supplemental `server-request-correlation.json` preserves four successful
GUI-origin ImageServer requests: two metadata requests, legend and exportImage.
All completed before the observed hang. Readiness returned200 Ready; database
health remained healthy. The repeated degraded-performance warning concerned
zero cache-hit ratio. QGIS remained unresponsive while its CPU time and working
set grew across read-only samples. This supports a client busy-computation or
allocation hypothesis; it does not establish an exact cause or a server defect.
No process restart, runtime change or server fix was performed by the diagnostic
agent.

Regression checks allow only these two specifically receipted GUI exclusion
resolutions and keep the three unperformed UI operations blocked. Historical
exclusion citations and review states are retained. The
[previous deeper audit](client-exclusion-followup-2026-09-20.md) describes the
earlier SDK-only stage and its provisional exclusions.
