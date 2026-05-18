# Cross-Client Certification Matrix

This matrix defines the shared certification vocabulary for cross-client interoperability testing. It establishes a common core of test cases that all client lanes must address, plus lane-specific extensions.

**Scope boundary**: this matrix tracks _client interoperability_ — whether a given client can successfully consume Honua Server APIs. It does not replace the [FeatureServer Coverage Matrix](feature-server-matrix.md), [MapServer Coverage Matrix](map-server-matrix.md), or OGC coverage docs, which track _server API parity_.

## Certification Categories

Nine certification categories with stable ID prefixes, mapped to the existing 5-step smoke pattern from the [Client Templates + Manual Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md):

| ID Prefix | Category | Smoke Step |
|-----------|----------|------------|
| CERT-CONN | Connection | 1 (Connect/auth) |
| CERT-AUTH | Auth | 1 (Connect/auth) |
| CERT-DISC | Discovery | 2 (Discovery) |
| CERT-SCHM | Schema | 2 (Discovery) |
| CERT-QFLT | Query / Filter | 3 (Filter/query) |
| CERT-PAGE | Pagination | 3 (Filter/query) |
| CERT-GEOM | Geometry Fidelity | 4 (Render/table) |
| CERT-ERRH | Error Handling | Cross-cutting |
| CERT-RNDR | Rendering / Style | 4+5 (Render+Reload) |

## Common Core Test Cases

These test cases form the shared certification baseline. Every client lane must produce results for each applicable case.

| Test Case ID | Category | Description | Protocol(s) | Evidence |
|---|---|---|---|---|
| CERT-CONN-01 | CONN | Establish HTTP connection to base URL | All | pass/fail |
| CERT-CONN-02 | CONN | TLS handshake completes | All | pass/fail |
| CERT-AUTH-01 | AUTH | Unauthenticated request returns 401/403 | All | pass/fail |
| CERT-AUTH-02 | AUTH | Valid credential grants access | All | pass/fail |
| CERT-DISC-01 | DISC | List available services/collections | FS, OGC, OData | pass/fail+count |
| CERT-DISC-02 | DISC | Retrieve single service/collection metadata | FS, OGC, OData | pass/fail |
| CERT-SCHM-01 | SCHM | Retrieve field/property schema | FS, OGC, OData | pass/fail |
| CERT-SCHM-02 | SCHM | Geometry type reported correctly | FS, OGC | pass/fail |
| CERT-QFLT-01 | QFLT | Attribute equality filter returns subset | FS, OGC, OData | pass/fail+count |
| CERT-QFLT-02 | QFLT | Spatial bbox/geometry filter returns subset | FS, OGC | pass/fail+count |
| CERT-PAGE-01 | PAGE | First page with limit returns expected count | FS, OGC, OData | pass/fail+count |
| CERT-PAGE-02 | PAGE | Second page offset returns different features | FS, OGC, OData | pass/fail |
| CERT-GEOM-01 | GEOM | Returned coordinates within tolerance | FS, OGC | pass/fail+delta |
| CERT-GEOM-02 | GEOM | Output CRS/SR matches request | FS, OGC | pass/fail |
| CERT-ERRH-01 | ERRH | Invalid endpoint returns structured error | All | pass/fail |
| CERT-ERRH-02 | ERRH | Malformed filter returns structured error | FS, OGC, OData | pass/fail |
| CERT-RNDR-01 | RNDR | Map/table renders without client error | FS, OGC, OData, MVT ‡ | pass/fail |
| CERT-RNDR-02 | RNDR | Data refresh preserves state | FS, OGC, OData | pass/fail |
| CERT-RNDR-SYM-01 | RNDR | Point symbol renders with declared color/size | FS, OGC, MVT | screenshot+pass/fail |
| CERT-RNDR-LIN-01 | RNDR | Line renders with declared stroke + width | FS, OGC, MVT | screenshot+pass/fail |
| CERT-RNDR-FIL-01 | RNDR | Polygon fill renders with declared color | FS, OGC, MVT | screenshot+pass/fail |
| CERT-RNDR-LBL-01 | RNDR | Label/text renders where supported | FS, OGC | screenshot+pass/fail |
| CERT-RNDR-SPR-01 | RNDR | Sprite/icon resolves and draws | MVT | screenshot+pass/fail |
| CERT-RNDR-URL-01 | RNDR | Style URL/metadata is consumed | FS, MVT | pass/fail |

‡ **MVT rendering:** requires a visual web client (e.g., MapLibre GL JS, OpenLayers `OGCVectorTile`). Automated browser evidence now comes from the JS — MapLibre (Playwright) lane for CERT-CONN-01, CERT-RNDR-01, JS-EXT-01, and JS-EXT-02 (#464). The visual / style certification slice ([`visual-style-certification-slice.md`](visual-style-certification-slice.md)) tracks the additional `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` IDs.

The six `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` IDs above are the visual / style certification slice that ticket [`#478`](https://github.com/honua-io/honua-server/issues/478) introduces. They are append-only additions to the matrix — `CERT-RNDR-01` and `CERT-RNDR-02` are unchanged. The slice spec at [`visual-style-certification-slice.md`](visual-style-certification-slice.md) defines per-scenario fixtures, expected colors, pass criteria, and lane substantiation.

§ **MapServer rendering lane:** The "FS" abbreviation covers both `featureserver` and `mapserver` evidence files. When MapServer is exercised as a rendering-only connection (e.g., ArcGIS Pro smoke test), the query-focused categories — CERT-QFLT, CERT-PAGE, CERT-GEOM, and CERT-ERRH-02 — are recorded as `not-applicable` in the `mapserver` evidence file. If the client also exercises MapServer's layer query endpoint (`/{layer-id}/query`), record those results normally. See the [runbook per-protocol evidence section](CLIENT_TEMPLATE_RUNBOOK.md#per-protocol-evidence-files) for the exact split.

### Geometry Tolerance (CERT-GEOM-01)

The `measured_delta` is the maximum absolute coordinate deviation between a known reference point and the value returned by the server, measured in the CRS's native unit.

| CRS class | Unit | Default pass threshold |
|---|---|---|
| Geographic (e.g., EPSG:4326) | decimal degrees | ≤ 1 × 10⁻⁶ (≈ 0.11 m at equator) |
| Projected (e.g., EPSG:3857) | meters | ≤ 0.01 |

The evidence envelope's `measured_delta` field records the observed deviation in the CRS's native unit. If the deviation exceeds the threshold for the CRS class, the status is `fail`. When a run uses a non-default threshold, record the chosen value in the result's `notes` field.

### Protocol abbreviations

| Abbreviation | Protocol | Evidence `protocol` value(s) |
|---|---|---|
| FS | GeoServices REST FeatureServer / MapServer | `featureserver`, `mapserver` |
| OGC | OGC API Features | `ogc-features` |
| OGC Maps | OGC API Maps | `ogc-maps` |
| OGC Tiles | OGC API Tiles | `ogc-tiles` |
| OData | OData v4 | `odata` |
| MVT | Vector Tiles (Mapbox Vector Tiles) | `mvt` |
| WFS | WFS 2.0 | `wfs` |
| WMS | WMS 1.3 | `wms` |
| WMTS | WMTS 1.0 | `wmts` |
| Admin API | Control-plane admin endpoints | `admin-api` |
| All | All supported protocols | (all of the above) |

## Client Lane Coverage

Each lane maps its coverage to the common core and declares lane-specific extensions.

| Lane | Automation | Core Coverage | Extensions |
|---|---|---|---|
| **JS** (Vitest + Playwright) | Automated ‡‡ | All CERT-\* | JS-EXT-01, JS-EXT-02, JS-EXT-OL-\*, JS-EXT-TILES-\* |
| **JS — MapLibre** (Playwright) | Automated | CERT-CONN-01, CERT-RNDR-01 (browser render) | JS-EXT-01, JS-EXT-02 |
| **JS — Esri Leaflet** (Playwright) | Automated §§ | FeatureServer + MapServer browser subset | EL-EXT-01 … EL-EXT-04 |
| **JS — Cesium** (Playwright) | Automated ¶¶ | WMS, WMTS, OGC API Tiles, OGC API Maps imagery subset | JS-CES-IMG-01, JS-CES-TILE-01 |
| **Desktop — ArcGIS Pro** | Stub (REST) + manual/scheduled licensed runner scaffold | REST common-core via `arcgis-stub`; licensed `desktop-arcgis` workflow emits FeatureServer + MapServer envelopes when an explicitly enabled self-hosted Windows ArcGIS Pro runner is available | DSK-EXT-01, DSK-EXT-02 |
| **Desktop — QGIS** | Automated (PyQGIS) + manual per runbook | All CERT-\* (OGC Features + WFS via PyQGIS; visual RNDR headless) | DSK-EXT-01, DSK-EXT-02 |
| **CLI / SDK** (admin SDK, pytest, Microsoft.OData.Client) | Automated | All CERT-\* except CERT-RNDR (OData via Microsoft.OData.Client xUnit suite) | CLI-EXT-01, CLI-EXT-02 |
| **BI — Power BI** | Manual per runbook | CERT-CONN, AUTH, DISC, SCHM, QFLT, PAGE, ERRH, RNDR † | BI-EXT-01, BI-EXT-02 |
| **BI — Excel** | Manual per runbook | CERT-CONN, AUTH, DISC, SCHM, QFLT, PAGE, ERRH, RNDR † | BI-EXT-01, BI-EXT-02 |
| **Licensed** (future) | Placeholder | TBD | TBD |

† **BI lanes (OData-only):** CERT-GEOM-01, CERT-GEOM-02, CERT-SCHM-02, and CERT-QFLT-02 do not apply — these require geometry-capable protocols (FS, OGC). Record as `not-applicable` in the evidence envelope.

‡‡ **JS lane current automated scope:** Vitest (Node.js) covers FeatureServer direct JS tests plus OpenLayers protocol-client tests for OGC API Features, OGC API Maps, OGC Tiles/MVT, WFS 2.0, WMS 1.3, and WMTS 1.0 (`*.test.ts`). The OpenLayers lane emits `.cert.json` envelopes for `ogc-features`, `ogc-maps`, `mvt`, `wfs`, `wms`, and `wmts`. Playwright (headless Chromium) covers MapLibre rendering tests for MVT and an OGC API Maps image-source smoke that is skipped when no raster fixture exists. The Python pytest suite (FeatureServer + OGC API Features + GPServer + ImageServer) provides independent server-side protocol validation; its results may inform certification confidence but are not JS-lane client evidence. JS-lane OData automation is planned but not yet implemented; CLI-lane OData automation is now closed via the Microsoft.OData.Client xUnit certification suite (`tests/dotnet/Honua.Server.Tests/Features/Protocols/OData/ODataClientCertificationTests.cs`). Until automated JS suites are added for those protocols, their CERT-\* results require manual evidence or are recorded as `skip` with a note referencing this gap.

§§ **Esri Leaflet sub-lane scope:** The Playwright suite currently exercises the browser-visible FeatureServer and MapServer subset: connection, metadata, schema, query/filter, paging, geometry fidelity, error handling, rendering, MapServer identify, and refresh. After the visual / style slice lands (ticket #478), the suite also substantiates `CERT-RNDR-SYM-01` and `CERT-RNDR-URL-01` on the FeatureServer surface via drawingInfo metadata and per-category style assertions, and the reporter emits a 24-case common-core envelope (18 base + 6 slice) by recording unexercised CERT-\* IDs as `skip` and the MapServer query-focused IDs as `not-applicable`. On the `mapserver` envelope the six `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` IDs are also recorded as `not-applicable` because drawingInfo per-category style assertions live on FeatureServer, not the MapServer export endpoint.

¶¶ **Cesium sub-lane scope:** The Playwright suite (`tests/js-browser/cesium/`, run via `docker/client-compat/cesium/`) exercises CesiumJS imagery providers — `WebMapServiceImageryProvider` (WMS), `WebMapTileServiceImageryProvider` (WMTS), `UrlTemplateImageryProvider` (OGC API Tiles), and `SingleTileImageryProvider` (OGC API Maps). Cesium does not consume vector-feature endpoints (FeatureServer, OGC API Features, WFS, OData) so the query/filter, schema, pagination, and geometry-fidelity categories are recorded as `not-applicable` in the `wms`, `wmts`, `ogc-tiles`, and `ogc-maps` envelopes. Per the contract for ticket #806, only CERT-CONN-01, CERT-CONN-02, CERT-AUTH-01, CERT-AUTH-02, CERT-DISC-01, CERT-DISC-02, CERT-ERRH-01, and CERT-RNDR-01 are applicable; the visual / style slice IDs (`CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01`) are `not-applicable` because Cesium imagery providers consume server-rendered raster output rather than per-feature drawing info.

### Lane-Specific Extensions

<a id="js-lane"></a>

#### JS Lane

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| JS-EXT-01 | Binary format fidelity (PBF/MVT decode) | MVT | pass/fail |
| JS-EXT-02 | Streaming/MVT tile load pipeline | MVT | pass/fail |
| JS-EXT-OL-COLL-01 | Collections list discovery | OGC Features | pass/fail+count |
| JS-EXT-OL-ITEMTYPE-01 | itemType field presence in collection metadata | OGC Features | pass/fail |
| JS-EXT-OL-ITEMS-01 | Feature items list and count | OGC Features | pass/fail+count |
| JS-EXT-OL-GEOJSON-01 | ol/format/GeoJSON feature parsing | OGC Features | pass/fail |
| JS-EXT-OL-GEOJSON-02 | ol/format/GeoJSON single-item parsing | OGC Features | pass/fail |
| JS-EXT-TILES-DISC-01 | OGC Tiles landing page | MVT | pass/fail |
| JS-EXT-TILES-DISC-02 | Collection tilesets listing | MVT | pass/fail |
| JS-EXT-TILES-SCHM-01 | Tileset metadata introspection | MVT | pass/fail |
| JS-EXT-OGC-MAPS-01 | OpenLayers ImageStatic targets OGC API Maps collection image endpoint | OGC Maps | pass/fail/skip |

#### Esri Leaflet Browser Sub-Lane

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| EL-EXT-01 | FeatureLayer symbology renders with drawingInfo | FS | pass/fail |
| EL-EXT-02 | DynamicMapLayer export image renders | FS (mapserver) | pass/fail |
| EL-EXT-03 | Feature attributes accessible via eachFeature | FS | pass/fail |
| EL-EXT-04 | MapServer identify returns attributes at point | FS (mapserver) | pass/fail |

#### Cesium Browser Sub-Lane

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| JS-CES-IMG-01 | WMS GetMap request parameters spec-compliant (CRS, BBOX, WIDTH, HEIGHT) | WMS | pass/fail |
| JS-CES-TILE-01 | OGC API Tiles URL template `{z}/{y}/{x}` substitution correct | OGC Tiles | pass/fail |

#### Desktop Lane

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| DSK-EXT-01 | Project save/reopen preserves layers | FS, OGC | pass/fail |
| DSK-EXT-02 | WMS/WMTS layer load and tile render | WMS, WMTS | pass/fail |

#### CLI / SDK Lane

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| CLI-EXT-01 | Admin publish workflow completes | Admin API | pass/fail |
| CLI-EXT-02 | Import lifecycle (upload → ready) | Admin API | pass/fail |

#### BI Lane

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| BI-EXT-01 | Power Query M expression executes | OData | pass/fail |
| BI-EXT-02 | OData `$apply` aggregation returns results | OData | pass/fail+count |

## ID Stability Policy

- CERT-\* IDs are **append-only**. New test cases receive the next available sequence number within their category.
- Deprecated IDs are **never reused**. Mark deprecated cases with a `[DEPRECATED]` prefix in the description column.
- Adding new IDs requires a minor version bump of this matrix document.

## Evidence Output

All certification results must follow the standardized evidence specification in [Cross-Client Certification Evidence](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md).

## Matrix Version

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-03-16 | Initial common core (18 test cases, 9 categories, 5 lane families) |
| 1.0.1 | 2026-03-16 | Fix OGC abbreviation, BI lane applicability footnotes, MVT render footnote |
| 1.0.2 | 2026-03-16 | Narrow OGC abbreviation to OGC API Features only; split WMS/WMTS into separate protocol entries |
| 1.0.3 | 2026-03-16 | Add MapServer rendering-lane footnote clarifying reduced CERT-* scope for mapserver evidence files |
| 1.0.4 | 2026-03-16 | Qualify JS lane automated scope to match current test inventory (FeatureServer + OGC Features) |
| 1.0.5 | 2026-03-16 | Add geometry tolerance thresholds for CERT-GEOM-01; scope JS lane to Vitest-only client evidence |
| 1.0.6 | 2026-03-16 | Add Admin API protocol abbreviation for CLI lane extensions |
| 1.0.7 | 2026-03-17 | Add stable HTML anchor for JS Lane heading to decouple cross-document links |
| 1.0.8 | 2026-04-03 | Add WFS protocol abbreviation; update Desktop — QGIS lane to reflect automated PyQGIS coverage |
| 1.0.9 | 2026-04-05 | Update JS lane to reflect hybrid Vitest + Playwright execution model and expanded protocol scope |
| 1.0.10 | 2026-04-03 | Register Esri Leaflet browser sub-lane (EL-EXT-01 … EL-EXT-04) |
| 1.0.11 | 2026-04-03 | Clarify Esri Leaflet automated scope and evidence skip/not-applicable behavior |
| 1.0.12 | 2026-04-06 | Note CLI/SDK OData automation via Microsoft.OData.Client xUnit suite; close OData automation gap for the CLI lane |
| 1.1.0 | 2026-04-06 | Add visual / style certification slice IDs (`CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01`) per ticket #478; link slice spec |
| 1.1.1 | 2026-04-07 | Update the `§§` Esri Leaflet sub-lane footnote to the post-#478 24-case common-core shape; document slice-ID `not-applicable` rationale on the mapserver envelope |
| 1.1.2 | 2026-04-08 | Add JS — MapLibre (Playwright) lane for automated MapLibre GL JS browser render certification (#464) |
| 1.1.3 | 2026-04-25 | Add OGC API Maps JS/OpenLayers evidence protocol and MapLibre image-source smoke coverage |
| 1.2.0 | 2026-04-26 | Add JS — Cesium (Playwright) lane and JS-CES-IMG-01 / JS-CES-TILE-01 extensions; add `ogc-tiles` protocol abbreviation; document ArcGIS Pro stub lane via `docker/client-compat/arcgis-stub` (#806) |
| 1.2.1 | 2026-05-18 | Add the licensed `desktop-arcgis` runner scaffold and clarify that ordinary PR gates validate the fixture/envelope contract without requiring ArcGIS Pro (#1019) |
