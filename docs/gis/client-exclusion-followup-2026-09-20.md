# Deeper client exclusion audit, September 20, 2026

Historical SDK-only stage: the later [QGIS ImageServer UI diagnostic](qgis-imageserver-ui-2026-09-20.md) adds two inspected GUI operations and carries current totals.

The follow-up overturns more unsupported exclusion claims and proves six more
native Python-lane operations. Of the 82 exclusions left after the first audit,
64 now reopen for review, leaving 18 provisional exclusions. This does not assert
that all reopened paths work. It restores unresolved work to the open count.

The authoritative checklist now has **165 passes, 4 failures, 137 blocked,
52 not started and 18 exclusions** across **94 operations / 376 cells**:
183 closed, 193 open. All 149 disputed historical exclusions are preserved in
`previous_exclusion`; 12 now also have a preserved `previous_review` and a native
SDK receipt. GUI passes remain unchanged: Pro 20, QGIS 51. No GUI was operated.
Totals combine historical and fresh receipts, not one complete image rerun.

## Six fresh native operation passes

| Lane / operation | Verified behavior | Qualification |
| --- | --- | --- |
| ArcPy GeocodeServer findAddressCandidates | Native Locator.geocode returns ten candidates matching independent HTTP count, ordering, addresses, scores and WGS84 XY, with expected street/city and geographic bounds | Uses configured World locator and /arcgis alias, forStorage=False. Storage-request variant returned authorization error and remains separate |
| ArcPy GeocodeServer suggest | Native Locator.suggest returns five suggestions matching every independent text, magic key and collection flag | The old claim that ArcPy had no such methods was false |
| PyQGIS TileJSON descriptor | Stock updateUriSources consumes a remote descriptor referenced by a standard Mapbox GL style; native tile decoder returns the expected feature within actual MVT grid tolerance | Direct descriptor-as-XYZ still fails; not a claim about every TileJSON field |
| PyQGIS ImageServer service-info | Stock arcgismapserver constructor loads CRS and extent matching independent service metadata | No dedicated arcgisimageserver provider is required |
| PyQGIS ImageServer exportImage | Native raster block returns ARGB corners matching independent PNG pixels | Dynamic provider dimensions of 0x0 do not prevent rendering; display colors are not elevations |
| PyQGIS elevation point-query | Stock GDAL AGS numeric sampling returns 10 at [-122.498828125,37.83890625], matching independent ImageServer identify at exactly that point; project reload reproduces it | Same sampling-plus-control criterion used by the existing ArcPy elevation pass; native ImageServer identify remains open |

These use stock ArcPy 3.7.1 (the same installed Pro executable is version
3.7.1.1904 with a retained SHA-256), QGIS 3.44.14 and bundled GDAL 3.13.3.
Source `25fa17d9cfa72340c9de4a33a743f19ff0911800`, image
`sha256:0ad6f6c9d81ead9772cf0f0a2e282321811bc5d059c53dc78570a4f6f07f7780`
stayed unchanged. These are Development/JIT receipts. No adapter, plugin,
license checkout or feature-flag change was needed for these six successes.

## Review of all 82 remaining exclusions

| Prior category | Cells | Decision and reason |
| --- | ---: | --- |
| ArcPy geocode/suggest | 2 | Reopen and resolve with native receipts; Locator exposes both methods |
| ArcPy attachments/relatedRecords/MapServer identify/legend | 4 | Reopen. Old citation repeated the harness's own exclusion rule; installed attachment tools and factory-returned mapping classes need operation-specific probes |
| ArcPy FeatureServer replica/sync | 1 | Reopen. CreateReplica and CreateReplicaFromServer input contracts do not exhaust native offline paths; the latter targets GeoDataServer, not FeatureServer |
| ArcPy WFS property-value, stored queries and transactions | 5 | Reopen. WFSToFeatureClass parameters bound one conversion tool, not every native connection/layer or licensed extension |
| ArcPy WMS identify | 1 | Reopen. Module-name absence does not cover factory-returned mapping objects |
| ArcPy GeometryServer | 4 | Reopen. Local geometry methods and a module-name search are not an exhaustive remote-operation inventory |
| Pro SensorThings | 3 | Reopen. An OGC API connection menu list is not an exhaustive SensorThings client review |
| Pro other OGC API families | 6 | Reopen. The Features/Tiles menu restriction does not review every alternative native representation, saved layer or extension |
| QGIS/PyQGIS TileJSON | 2 | Reopen; PyQGIS descriptor consumption resolves with the qualified style-source path |
| QGIS/PyQGIS elevation | 2 | Reopen; PyQGIS numeric point sampling resolves. Existing ImageServer metadata/export exclusions were already reopened and now resolve for PyQGIS |
| QGIS/PyQGIS GPServer | 12 | Reopen for citation review. No specialized path found, but processing registry absence does not justify the universal claim across six operations |
| QGIS/PyQGIS GeocodeServer | 8 | Reopen for citation review. Google/Nominatim geocoders are not Esri clients, and no Esri path was found; the old general absence claim still needs a complete scoped inventory |
| QGIS/PyQGIS GeometryServer | 8 | Reopen for citation review. Local GEOS/GDAL results do not certify remote operations or exhaust SDK paths |
| QGIS/PyQGIS VersionManagementServer | 4 | Reopen for citation review. No specialized class found; source/API review must bound the create/reconcile/post claims |
| QGIS/PyQGIS SOAP catalog | 2 | Reopen for citation review. Observed REST discovery does not exhaust native SOAP catalog paths; generic HTTP/XML is not a native protocol pass |
| Pro WFS transactions | 3 | Retain provisionally: current vendor documentation explicitly says native WFS layers are read-only |
| Pro OGC API Features editing | 1 | Retain provisionally: current vendor documentation explicitly says the native layer is not editable |
| ArcPy synchronous NAServer solve/service-area | 2 | Retain provisionally for the exact synchronous transport: the existing native nax receipt instead requests asynchronous GP web tools. Honua routing itself exists |
| QGIS/PyQGIS WFS GetPropertyValue/ListStoredQueries | 4 | Retain provisionally. Earlier URI controls/request-source audit plus selected pinned GDAL WFS source found no matching request builders |
| QGIS/PyQGIS FeatureServer attachments/relatedRecords/statistics/replica-sync | 8 | Retain provisionally. Specific prior wire observations and selected pinned native request-source evidence support these narrower provider gaps |

The last five rows account for the remaining 18 exclusions. They remain visible
in the denominator and can be challenged with a concrete alternative native
path. A license, capability flag, generic HTTP call or a locally calculated
answer is not by itself proof of native support for a protocol operation.

The current point fixture advertises `hasAttachments=false`,
`supportsQueryAttachments=false` and an empty relationships list. Empty client
results on that fixture are **not** negative capability evidence. Attachment and
relationship investigations need a dedicated enabled fixture before execution.

## Server availability versus actual implementation gaps

The Esri detailed manifest generator contained a separate classification bug:
three VectorTileServer and two SceneServer operations were `not_implemented`
solely because a June fixture lacked per-service enablement or an Enterprise
grant. The correction retains all five as supported/partial with prerequisites.
Refreshing the source inventory adds four other supported cases. That separate
detailed manifest denominator rises from 278 to 287; it is not the four-lane
checklist denominator and does not create nine client passes.

The development fixture already has an Enterprise grant and the relevant
experimental flags. PostGIS **and postgis_raster 3.4.3** are installed with
GTiff/PNG/JPEG support. Their absence is not the current raster blocker. The
previous raster affine-grid defects needed code repairs, now in
[PR 5038](https://github.com/honua-io/honua-server/pull/5038).

Honua serves these protocol families, but that does not establish every optional
Esri contract. The current source parity inventory explicitly records JSON-only
replica output and rejection of runtime-geodatabase format, asynchronous replica
creation and attachment inclusion; some advanced applyEdits payloads are also
rejected. Current native ArcPy WCS still fails before pixel reads; branch-version
workspace authentication remains unresolved. QGIS ImageServer identify parses
the MapServer results shape and returns empty results for the ImageServer shape.
These conditions need their own fixes or scoped client-boundary evidence.

The authoritative [CITE snapshot](../cite-status.md) records **1138/1138 across
14 suites** on source b8ea218d, September 15, rather than the older 1137/1138
figure in repository instructions. That conformance profile is separate from
native-client coverage and does not prove every optional protocol parameter.

## Durable evidence and vendor sources

- Esri: `honua-esri-compat` commit **b691b03**, `docs/reports/arcpy-exclusion-followup-2026-09-20.md` and `evidence/arcpy-exclusion-followup-20260920-d/observations.json`; attempts a-c remain preserved. All 302 tests, fixture-contract validation, template freshness and source-manifest checks passed.
- QGIS: `honua-client-compat` commit **046a74c**, `docs/reports/pyqgis-deep-exclusion-audit-2026-09-20.md`, final run `evidence/pyqgis-deep-exclusions-20260920-d` and pinned source audit. All 240 tests, shared-snapshot verification and evidence/hash/secret checks passed.
- [Esri Locator](https://doc.esri.com/en/arcgis-pro/latest/arcpy/geocoding/locator-class.html) documents geocode and suggest.
- [Esri WFS restrictions](https://doc.esri.com/en/arcgis-pro/latest/help/data/services/use-wfs-services.html) and [OGC API layer restrictions](https://doc.esri.com/en/arcgis-pro/latest/help/data/services/use-ogc-api-services.html) support the retained edit boundaries.
- [Pinned QGIS ArcGIS provider](https://github.com/qgis/QGIS/blob/1a4cda5f2620e7374e5926fc955a7d2d06493e15/src/providers/arcgisrest/qgsamsprovider.cpp) and [vector tile utilities](https://github.com/qgis/QGIS/blob/1a4cda5f2620e7374e5926fc955a7d2d06493e15/src/core/vectortile/qgsvectortileutils.cpp) expose the previously overlooked paths.

The generated checklist carries the current operation-by-operation status. The
[first audit](client-exclusion-audit-2026-09-20.md) and [earlier Python report](python-lane-status-2026-09-20.md)
retain their original totals as historical stages of this investigation.

Server checklist validation: 158 tests and 43 subtests passed; generated JSON/Markdown checks and diff whitespace checks passed.
