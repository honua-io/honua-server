# Cross-Client Certification Matrix

This matrix defines the shared certification vocabulary for cross-client interoperability testing. It establishes a common core of test cases that all client lanes must address, plus lane-specific extensions.

**Scope boundary**: this matrix tracks _client interoperability_ — whether a given client can successfully consume Honua Server APIs. It does not replace the [FeatureServer Coverage Matrix](../reference/compatibility/geoservices-parity.md), [MapServer Coverage Matrix](../reference/compatibility/geoservices-parity.md), or OGC coverage docs, which track _server API parity_.

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

‡ **MVT rendering:** requires a visual web client (e.g., MapLibre GL JS, OpenLayers `OGCVectorTile`). Automated browser evidence now comes from the JS — MapLibre (Playwright) lane for CERT-CONN-01, CERT-RNDR-01, JS-EXT-01, and JS-EXT-02 (#464). The visual / style certification slice ([`visual-style-certification-slice.md`](../internal/evidence/visual-style-certification-slice.md)) tracks the additional `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` IDs.

The six `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` IDs above are the visual / style certification slice that ticket [`#478`](https://github.com/honua-io/honua-server/issues/478) introduces. They are append-only additions to the matrix — `CERT-RNDR-01` and `CERT-RNDR-02` are unchanged. The slice spec at [`visual-style-certification-slice.md`](../internal/evidence/visual-style-certification-slice.md) defines per-scenario fixtures, expected colors, pass criteria, and lane substantiation.

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
| STAC | STAC API 1.0 (core, collections, item-search) | `stac` |
| Admin API | Control-plane admin endpoints | `admin-api` |
| Portal | ArcGIS Portal/Sharing facade (`/sharing/rest`) | `portal` |
| All | All supported protocols | (all of the above) |

## Client Lane Coverage

Automated lane applicability is machine-readable in `docs/gis/data/client-certification-matrix.v1.json` and is gated in both directions against committed `tests/baselines/client-compat/**/*.cert.json` evidence by `DocumentationMatrixDriftTests`.

Each lane maps its coverage to the common core and declares lane-specific extensions.

### Execution tiers

| Tier | Required execution | Gate behavior |
|---|---|---|
| PR | Schema, generator, parser, marker, and fixture-contract tests; no full external-client matrix | Blocking |
| Nightly | Every active unlicensed real-client lane and any available licensed Windows lane | Missing, malformed, or regressed evidence fails the nightly; it does not block ordinary PRs |
| Release | Every release-required lane against the exact candidate image and source revision; licensed evidence must be fresh and entitlement-bound | Blocking |

### Canonical fixture and configuration policy

Canonical-client lanes converge on one versioned logical fixture and server-configuration revision, frozen by [#3393](https://github.com/honua-io/honua-server/issues/3393). Receipts must bind both revisions. Every applicable client operation maps to explicit fixture cases and scenario facets; unsupported client capabilities are `not-applicable` with a governed reason, while applicable but unexecuted cases fail closed.

The contract is **frozen-2026.1**. The machine-readable manifest is [`client-certification-fixture.v1.json`](data/client-certification-fixture.v1.json), paired with the prose [`CLIENT_CERTIFICATION_FIXTURE_CONTRACT.md`](CLIENT_CERTIFICATION_FIXTURE_CONTRACT.md). It content-addresses six inputs (`client-compat-v1.sql`, `browser-compat.yaml`, `portal-compat.yaml`, `apply-yaml-seed.sh`, the seed runner, and the server config) into `fixtureRevision` / `serverConfigRevision`, plus a declaration-digested `authPolicyRevision`. `CanonicalFixtureManifestTests` recomputes every digest from the real files, and `scripts/certification/verify-fixture-manifest.py` is the PR-tier verifier.

Frozen does not mean complete: the manifest records **21 governed gaps** with tracking issues rather than claiming coverage the seed does not have — notably absent multipart and hole geometry, no non-ASCII attribute values, an uncertified edit path, and four unrealized auth profiles (expired credential, insufficient role, cross-tenant denial, separate proposer/approver). Depth remains [#3435](https://github.com/honua-io/honua-server/issues/3435).

Two active lanes do **not** yet bind the canonical vector fixture: `js` and `arcgis-stub` run against `browser_compat` layer 2000 (3 point features, `objectid`/`name`/`shape` only), so they exercise none of the canonical attribute spread, null geometry, or ten-feature pagination. That is recorded as a gap against [#3392](https://github.com/honua-io/honua-server/issues/3392), not as equivalence.

OGC CITE is the explicit exception. Each CITE suite retains its specification-mandated custom seed and setup procedure. CITE shares exact candidate identity, image digest, authentication policy, and capability mapping with release certification, but it is never relabeled as having used the canonical client fixture.

### Planned canonical-client lanes

Planned lanes do not enter the active `lanes` array or `expected-pairs.json` until their real, non-placeholder envelopes land in the same change.

| Lane | Canonical client | Protocols | Required tier | Owner |
|---|---|---|---|---|
| `desktop-arcgis` | ArcGIS Pro and arcpy | Esri REST plus applicable OGC services | Release, licensed | [honua-esri-compat#75](https://github.com/honua-io/honua-esri-compat/issues/75) |
| `bi-excel` | Microsoft Excel Power Query | OData | Release, licensed | [#3390](https://github.com/honua-io/honua-server/issues/3390) |
| `bi-powerbi` | Power BI Desktop | OData | Release, licensed | [#3390](https://github.com/honua-io/honua-server/issues/3390) |
| `bi-tableau` | Tableau Desktop | OData, OGC API Features | Release, licensed | [#3390](https://github.com/honua-io/honua-server/issues/3390) |

### Governed exclusions

| Client | Exclusion rationale |
|---|---|
| Mapbox GL JS v2+ | Proprietary license and mandatory telemetry; MapLibre covers the applicable protocol surface |
| ArcGIS Earth | No distinct server surface beyond the governed MapServer and KML lanes |
| Golden Surfer | File-import workflow rather than a server protocol client |
| Avenza Maps | File-import workflow rather than a server protocol client |
| Looker Studio | No direct geospatial connector; warehouse access is certified separately |

| Lane | Automation | Core Coverage | Extensions |
|---|---|---|---|
| **JS** (Vitest + Playwright) | Automated ‡‡ | All CERT-\* | JS-EXT-01, JS-EXT-02, JS-EXT-OL-\*, JS-EXT-TILES-\* |
| **JS — MapLibre** (Playwright) | Automated | CERT-CONN-01, CERT-RNDR-01 (browser render) | JS-EXT-01, JS-EXT-02 |
| **JS — Esri Leaflet** (Playwright) | Automated §§ | FeatureServer + MapServer browser subset | EL-EXT-01 … EL-EXT-04 |
| **JS — Cesium** (Playwright) | Automated ¶¶ | WMS, WMTS, OGC API Tiles, OGC API Maps imagery subset | JS-CES-IMG-01, JS-CES-TILE-01 |
| **Desktop — ArcGIS Pro** | Stub (REST) + manual/scheduled licensed runner scaffold | REST common-core via `arcgis-stub`; Portal/Sharing facade discovery via the `arcgis-stub` `portal` protocol (CERT-PRTL-\*); licensed `desktop-arcgis` workflow emits FeatureServer + MapServer envelopes when an explicitly enabled self-hosted Windows ArcGIS Pro runner is available | DSK-EXT-01, DSK-EXT-02, CERT-PRTL-\* |
| **Desktop — QGIS** | Automated (PyQGIS) + manual per runbook | Core CERT-\* through RNDR-01/02/SYM/LIN/FIL; RNDR-LBL/SPR/URL remain manual | DSK-EXT-01, DSK-EXT-02 (manual) |
| **CLI / SDK** (admin SDK, pytest, Microsoft.OData.Client) | Automated | All CERT-\* except CERT-RNDR (OData via Microsoft.OData.Client xUnit suite) | CLI-EXT-01, CLI-EXT-02 |
| **BI — Power BI** | Manual per runbook | CERT-CONN, AUTH, DISC, SCHM, QFLT, PAGE, ERRH, RNDR † | BI-EXT-01, BI-EXT-02 |
| **BI — Excel** | Manual per runbook | CERT-CONN, AUTH, DISC, SCHM, QFLT, PAGE, ERRH, RNDR † | BI-EXT-01, BI-EXT-02 |
| **Analyst — GeoPandas** (pyogrio/Fiona) | Automated | All CERT-\* except CERT-RNDR (data client) | NB-GPD-\* (42) |
| **Analyst — OWSLib** | Automated | All CERT-\* except CERT-RNDR-02/SYM/LIN/FIL/LBL/SPR/URL; CERT-RNDR-01 on WMS/WMTS imagery | NB-OWS-\* (65) |
| **Analyst — DuckDB Spatial** | Automated | All CERT-\* except CERT-RNDR (analytical SQL client) | NB-DDB-\* (28) |
| **Analyst — R sf / ows4R** | Automated | All CERT-\* except CERT-RNDR (data client) | NB-RSF-\* (50) |
| **Analyst — pystac-client** | Automated | All CERT-\* except CERT-RNDR (catalog client) | NB-STAC-\* (39) |
| **Licensed desktop clients** | Planned; not active until real evidence exists | See the planned canonical-client roster above | Entitlement-bound, content-addressed release evidence |

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

#### ArcGIS Portal Facade Lane (`arcgis-stub` `portal` protocol)

The Portal/Sharing facade (epic #1240) lets packaged Esri clients (ArcGIS Pro "Add Portal", Field Maps) bind through Portal items + ArcGIS tokens instead of raw `/rest/services` URLs. The `arcgis-stub` lane emits a third `portal`-protocol envelope exercising the request sequence those clients issue, keyed on the following append-only IDs. The seeded fixture is `tests/seed/portal-compat.yaml` (public/org/private tiers); the licensed ArcGIS Pro / Field Maps runs substantiate the same IDs against the real clients.

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| CERT-PRTL-INFO-01 | `/sharing/rest/info` advertises token-based security + `tokenServicesUrl` | Portal | pass/fail |
| CERT-PRTL-SELF-01 | `/sharing/rest/portals/self` returns portal identity (`isPortal`/`name`) | Portal | pass/fail |
| CERT-PRTL-SRCH-01 | `/sharing/rest/search` returns the Esri paging shape (`total`/`start`/`num`/`nextStart`/`results`) | Portal | pass/fail+count |
| CERT-PRTL-ITEM-01 | `/sharing/rest/content/items/{id}` resolves to a `/rest/services` URL with an access tier | Portal | pass/fail |
| CERT-PRTL-RBAC-01 | Anonymous search projects only public-tier items (RBAC access projection) | Portal | pass/fail |
| CERT-PRTL-TOKN-01 | `/sharing/rest/generateToken` issues a token whose `expires` is Unix milliseconds | Portal | pass/fail/skip † |
| CERT-PRTL-AUTH-01 | A named-user token widens discovery to org-tier items (authenticated projection) | Portal | pass/fail/skip † |
| CERT-PRTL-OAUTH-01 | `/sharing/rest/oauth2/token` returns an RFC 6749 error envelope for an invalid grant | Portal | pass/fail/skip † |
| CERT-PRTL-SELF-02 | `portals/self` with a named-user token populates the `user` block | Portal | pass/fail/skip † |
| CERT-PRTL-COMM-01 | Anonymous `/sharing/rest/community/self` returns the Esri error envelope (401/499), never a user document | Portal | pass/fail |
| CERT-PRTL-COMM-02 | `community/self` with a named-user token returns the signed-in user document (`username`) | Portal | pass/fail/skip † |
| CERT-PRTL-TOKN-02 | `generateToken` with invalid credentials returns the Esri error envelope (401/499) without leaking a token | Portal | pass/fail/skip † |
| CERT-PRTL-OAUTH-02 | `oauth2/token` `refresh_token` grant returns `invalid_grant` for an unknown/expired refresh token | Portal | pass/fail/skip † |

† **Token / OAuth2 slice substantiation.** The token-surface IDs (CERT-PRTL-TOKN-\*, -AUTH-01, -SELF-02, -COMM-02, -OAUTH-\*) require the token surface to be reachable (admin credentials configured on the lane and `Authentication:PortalToken:RequireHttps=false` for the HTTP-only docker network, both set in `docker/client-compat/compose.yml`). The stub emits `skip` **only** for the genuinely-unconfigured cases (no admin credentials / no token issued / the https-only guard rejecting the transport / the OAuth2 bridge returning 404-disabled); once the surface is reachable a broken projection, wrong token shape, or non-RFC-6749 error body emits `fail`, which the strict diff catches via the `new-fail` path even against a `skip` baseline. All 13 CERT-PRTL IDs are baselined `pass` from a containerized-lane capture (`scripts/client-compat/refresh-baselines.sh arcgis-stub`), which promoted the formerly-`skip` token/OAuth2 rows; the endpoint behaviors are independently verified by `PortalFacadeDiscoveryContractTests`. The anonymous read-surface IDs (INFO/SELF/SRCH/ITEM/RBAC, plus COMM-01 and TOKN-02) need no credentials at all.


<a id="canonical-analyst-lanes"></a>

### Canonical analyst-client extensions (`NB-*`)

The five canonical analyst lanes ([#3392](https://github.com/honua-io/honua-server/issues/3392)) cover the breadth of each client library's API against the server, beyond the shared common core. Each ID is emitted into the `extensions` array of that lane's `.cert.json` envelope; `DocumentationMatrixDriftTests` joins these rows, `client-certification-matrix.v1.json`, and the committed evidence in both directions, so an ID cannot exist in only one of the three.

#### `py-geopandas` — GeoPandas via pyogrio/Fiona (42 cases)

| Test Case ID | Category | Description | Protocol(s) | Evidence |
|---|---|---|---|---|
| NB-GPD-AUTH-01 | NB-GPD | An invalid X-API-Key produced 401 (not 403, which would imply an authenticated-but-forbidden principal, and not 500) | ogc-features | pass/fail |
| NB-GPD-CRS-01 | NB-GPD | total_bounds=(np.float64(-122.49), np.float64(37.71), np.float64(-122.37), np.float64(37.79)) matches the canonical lon/lat extent, proving the default GeoJSON output uses CRS84 axis... | ogc-features | pass/fail |
| NB-GPD-CRS-02 | NB-GPD | Opened with the OAPIF driver's CRS=EPSG:3857 open option; the server returned EPSG:3857 and the anchor landed 5.587935447692871e-09 m from the pyproj reference (limit 0.01 m) | ogc-features | pass/fail |
| NB-GPD-CRS-03 | NB-GPD | Compared 9 features; worst server-vs-pyproj EPSG:3857 deviation 5.587935447692871e-09 m | ogc-features | pass/fail |
| NB-GPD-CRS-04 | NB-GPD | bbox=-13636081.024722047,4537835.6179989865,-13631628.245090313,4542057.542774347 with bbox-crs=http://www.opengis.net/def/crs/EPSG/0/3857 returned 3 rows, matching the EPSG:4326 bbox result | ogc-features | pass/fail |
| NB-GPD-ENG-01 | NB-GPD | geopandas.read_file with engine='fiona' and engine='pyogrio' (two independently vendored GDAL builds) agreed on row count, ordering, CRS and geometry to within 0.0 deg | ogc-features | pass/fail |
| NB-GPD-ERR-01 | NB-GPD | Status codes observed by the client: {'unknown-collection': 404, 'malformed-crs': 400, 'malformed-cql2': 400}. A missing resource is 404 while malformed CRS and CQL2 inputs are 400, so a... | ogc-features | pass/fail |
| NB-GPD-FLT-01 | NB-GPD | datetime=2024-01-03T00:00:00Z/2024-01-05T23:59:59Z selected ['delta', 'epsilon', 'gamma'], i.e. exactly the three features whose created_at falls inside the interval | ogc-features | pass/fail |
| NB-GPD-FLT-02 | NB-GPD | filter='count > 5' returned 5 rows ([6, 7, 8, 9, 10]); the conjunction with status='active' returned 2 rows, so the server evaluates CQL2-text predicates including AND | ogc-features | pass/fail |
| NB-GPD-FLT-03 | NB-GPD | Both an unmatched OGR attribute filter and an unmatched CQL2 predicate produced empty GeoDataFrames with the schema intact rather than an HTTP error or a malformed payload | ogc-features | pass/fail |
| NB-GPD-FLT-04 | NB-GPD | An Antarctic bbox in EPSG:4326 (lat/lon axis order) returned an empty FeatureCollection, confirming the server treats a disjoint-but-valid envelope as a zero-result query | ogc-features | pass/fail |
| NB-GPD-GEO-01 | NB-GPD | 9 geometries round-tripped through shapely WKB and WKT with a worst-case deviation of 0.0 deg | ogc-features | pass/fail |
| NB-GPD-GEO-02 | NB-GPD | declared collection extent (-122.5, 37.7, -122.35, 37.84) contains the materialized data bounds (-122.49, 37.71, -122.37, 37.79), which equal the canonical fixture extent | ogc-features | pass/fail |
| NB-GPD-IO-01 | NB-GPD | Round-tripped the server response through {'GPKG': 10, 'FlatGeobuf': 10}; worst coordinate deviation 0.0 deg and the null-geometry row stayed null in both formats | ogc-features | pass/fail |
| NB-GPD-IO-02 | NB-GPD | GeoParquet round trip preserved 10 rows, EPSG:4326 and the datetime64[ms, UTC] timestamp dtype | ogc-features | pass/fail |
| NB-GPD-NUL-01 | NB-GPD | 4 of 10 rows carried a null description; the remainder are non-empty strings, so JSON null was not coerced to an empty string | ogc-features | pass/fail |
| NB-GPD-NUL-02 | NB-GPD | 10 rows returned with 1 null geometry (['lambda']); the geometry-less feature is neither dropped nor given a placeholder geometry | ogc-features | pass/fail |
| NB-GPD-PAG-01 | NB-GPD | 4 pages of limit=3 produced 10 distinct features with no repeats and no gaps, so limit/offset paging is stable and complete | ogc-features | pass/fail |
| NB-GPD-PAG-02 | NB-GPD | limit=1000000 was clamped and returned 10 rows; offset=10000 returned an empty FeatureCollection rather than an error | ogc-features | pass/fail |
| NB-GPD-SCH-01 | NB-GPD | The declared queryable 'eo:cloud_cover' is present in the feature properties and filterable (3 rows matched > 50); ?properties= answered HTTP 200 with columns ['eo:cloud_cover',... | ogc-features | pass/fail |
| NB-GPD-SRT-01 | NB-GPD | sortby=-count produced [10, 9, 8, 7, 6, 5, 4, 3, 2, 1]; the first two pages match the corresponding slices of the full ordering, so the sort is applied before paging | ogc-features | pass/fail |
| NB-GPD-TYP-01 | NB-GPD | count dtype=int32, ratio dtype=float64; anchor count=1, ratio=1.25 round-tripped exactly | ogc-features | pass/fail |
| NB-GPD-TYP-02 | NB-GPD | active dtype=bool with 5 true rows, consistent with status='active' on every row | ogc-features | pass/fail |
| NB-GPD-TYP-03 | NB-GPD | created_at dtype=datetime64[ms, UTC] (anchor 2024-01-01 12:00:00+00:00), event_date dtype=datetime64[ms] (anchor 2024-02-01 00:00:00); the RFC 3339 timestamps the server emits are parsed... | ogc-features | pass/fail |
| NB-GPD-TYP-04 | NB-GPD | Through pyogrio's Arrow path: event_time=datetime.time(12, 34, 56) (a real time value, not a string), tags=['red', 'blue'], numbers=[np.int32(0), np.int32(1), np.int32(2)],... | ogc-features | pass/fail |
| NB-GPD-WFS-BBX-01 | NB-GPD | BBOX in urn:ogc:def:crs:EPSG::4326 lat/lon order matched 3 features; the same numbers supplied in lon/lat order yielded HTTP 400, proving the server applies the CRS-declared axis order... | wfs | pass/fail |
| NB-GPD-WFS-CAP-01 | NB-GPD | All 5 feature types advertised by GetCapabilities resolved through DescribeFeatureType and reported a geometry type: ['honua:test_layer', 'honua:browser_points', 'honua:browser_lines',... | wfs | pass/fail |
| NB-GPD-WFS-CRS-01 | NB-GPD | total_bounds=(np.float64(-122.49), np.float64(37.71), np.float64(-122.37), np.float64(37.79)) after GDAL applied the axis swap implied by srsName=urn:ogc:def:crs:EPSG::4326; the server's... | wfs | pass/fail |
| NB-GPD-WFS-CRS-02 | NB-GPD | SRSNAME=urn:ogc:def:crs:EPSG::3857 returned EPSG:3857; the anchor landed 2.7939677238464355e-09 m from the pyproj reference (limit 0.01 m) | wfs | pass/fail |
| NB-GPD-WFS-ERR-01 | NB-GPD | Client-observed statuses: {'unknown-typename': '400', 'malformed-srsname': '400'}. The unknown-typename response body was a well-formed ows:ExceptionReport, so a GeoPandas caller... | wfs | pass/fail |
| NB-GPD-WFS-FLT-01 | NB-GPD | fes:PropertyIsEqualTo(status, 'active') returned 5 features, all with status='active', so the server evaluates OGC Filter Encoding 2.0 predicates | wfs | pass/fail |
| NB-GPD-WFS-HIT-01 | NB-GPD | GDAL satisfies GetFeatureCount with GetFeature&RESULTTYPE=hits (fast_feature_count=True) and the server reported numberMatched=10, matching the seeded row count without transferring features | wfs | pass/fail |
| NB-GPD-WFS-IDN-01 | NB-GPD | 10 unique gml:id values (e.g. ['test_layer.1', 'test_layer.2', 'test_layer.3']), identical across two requests and each suffixed with the feature's objectid, so a client can key on them... | wfs | pass/fail |
| NB-GPD-WFS-IO-01 | NB-GPD | GeoPackage round trip preserved 10 features, EPSG:4326, the objectid ordering and the null geometry, with a worst deviation of 0.0 deg | wfs | pass/fail |
| NB-GPD-WFS-NS-01 | NB-GPD | TYPENAMES='honua:test_layer' and TYPENAMES='test_layer' returned the same 10 features in the same order, so the unprefixed name used by the server's own paging links and... | wfs | pass/fail |
| NB-GPD-WFS-NUL-01 | NB-GPD | 10 features returned with 1 nil geometry; the server emits the nillable geometry property rather than dropping the feature or writing an empty gml:Point | wfs | pass/fail |
| NB-GPD-WFS-PAG-01 | NB-GPD | 4 pages of COUNT=3 produced 10 distinct features with no repeats and no gaps | wfs | pass/fail |
| NB-GPD-WFS-PAG-02 | NB-GPD | COUNT=1000000 returned 10 features (clamped, not rejected); STARTINDEX=10000 returned an empty FeatureCollection rather than a service exception | wfs | pass/fail |
| NB-GPD-WFS-PRP-01 | NB-GPD | PROPERTYNAME=name,status produced exactly ['name', 'status'] on 3 features; no unrequested property was serialized | wfs | pass/fail |
| NB-GPD-WFS-SCH-01 | NB-GPD | WFS exposed the namespaced field as 'eo_x003A_cloud_cover' (GML escapes ':' as _x003A_) with 9 populated values, e.g. [5.0, 8.0, 25.0] | wfs | pass/fail |
| NB-GPD-WFS-TYP-01 | NB-GPD | objectid dtype=int32, count=int32, ratio=float64, active=bool; xsd:int/xsd:double/xsd:boolean from DescribeFeatureType survived into pandas dtypes | wfs | pass/fail |
| NB-GPD-WFS-TYP-02 | NB-GPD | created_at='2024-01-01T12:00:00+00:00', event_date='2024-02-01', event_time='12:34:56.0000000' - all parse to the seeded instants | wfs | pass/fail |

#### `py-owslib` — OWSLib (68 cases)

| Test Case ID | Category | Description | Protocol(s) | Evidence |
|---|---|---|---|---|
| NB-OWS-WFS-100-01 | NB-OWS | OWSLib negotiated WFS 1.0.0, parsed capabilities, discovered the canonical layer, and executed GetFeature with longitude/latitude axis order | wfs | pass/fail |
| NB-OWS-WFS-110-01 | NB-OWS | OWSLib negotiated WFS 1.1.0, parsed capabilities, discovered the canonical layer, and executed GetFeature with latitude/longitude axis order | wfs | pass/fail |
| NB-OWS-OAF-AUTH-03 | NB-OWS | A syntactically valid but incorrect X-API-Key returns 401, not 403 or 500 | ogc-features | pass/fail |
| NB-OWS-OAF-COLL-01 | NB-OWS | collection.extent.spatial.bbox=[-122.5, 37.7, -122.35, 37.84] encloses the seeded feature envelope [-122.49, 37.71, -122.37, 37.79] and is declared in CRS84; temporal interval... | ogc-features | pass/fail |
| NB-OWS-OAF-COLL-02 | NB-OWS | Features.feature_collections() -> ['2000', '2001', '2002', '3000', '0']; every collection declares itemType=feature | ogc-features | pass/fail |
| NB-OWS-OAF-CONF-01 | NB-OWS | /conformance declares 26 classes including Features 1.0 core/geojson/oas30 | ogc-features | pass/fail |
| NB-OWS-OAF-CONF-02 | NB-OWS | Declared conformance classes were exercised rather than trusted: ['crs', 'queryables', 'cql2-text'] all behaved as advertised | ogc-features | pass/fail |
| NB-OWS-OAF-CRS-01 | NB-OWS | CRS84 -> lon/lat, EPSG:4326 -> lat/lon (axis order honoured, not just echoed), EPSG:3857 -> (-13635524.42726808, 4538539.15341347) which is within 3.725290298461914e-09 m of the... | ogc-features | pass/fail |
| NB-OWS-OAF-CRS-02 | NB-OWS | All 3 CRSs advertised on the collection were accepted by /items and echoed back in Content-Crs: ['http://www.opengis.net/def/crs/EPSG/0/3857',... | ogc-features | pass/fail |
| NB-OWS-OAF-DATE-01 | NB-OWS | A closed RFC 3339 interval on the seeded created_at column selects exactly the three features inside it (alpha/beta/gamma) | ogc-features | pass/fail |
| NB-OWS-OAF-ERR-01 | NB-OWS | All 5 deliberate client errors (unknown collection, unknown item, bad CRS, negative offset, short bbox) returned RFC 7807 problem+json with matching status, title, detail and instance | ogc-features | pass/fail |
| NB-OWS-OAF-ITEM-01 | NB-OWS | /items/1 returned the identical Feature the collection listing carried, with self and collection link relations | ogc-features | pass/fail |
| NB-OWS-OAF-LAND-01 | NB-OWS | Landing page carries typed self/conformance/data/service-desc links plus an alternate representation, which is what OWSLib navigates from | ogc-features | pass/fail |
| NB-OWS-OAF-LAND-02 | NB-OWS | Features.api() resolved the service-desc link to an OpenAPI 3.0.3 document describing 15 paths, including the landing page, collections, single collection and collection items resources | ogc-features | pass/fail |
| NB-OWS-OAF-LINK-01 | NB-OWS | Paged responses advertise self+next and no prev on page 1; a response that already covers numberMatched advertises no next link | ogc-features | pass/fail |
| NB-OWS-OAF-LINK-02 | NB-OWS | Followed the advertised next href verbatim (http://honua:5000/ogc/features/collections/0/items?limit=3&offset=3&f=geojson); it returned a disjoint GeoJSON page, so OWSLib-style link... | ogc-features | pass/fail |
| NB-OWS-OAF-PAGE-03 | NB-OWS | A limit=3 walk over the collection yielded 10 distinct feature ids, exactly matching numberMatched=10: no gaps, no repeats, stable ordering | ogc-features | pass/fail |
| NB-OWS-OAF-QFLT-03 | NB-OWS | CQL2-text `count > 7` compares numerically (3 rows: theta/iota/lambda); a lexical comparison would also return rows 8 and 9 or drop 10 | ogc-features | pass/fail |
| NB-OWS-OAF-QRYB-01 | NB-OWS | Queryables is a 2020-12 JSON Schema with $id, every property typed, and the non-queryable JSON array columns correctly excluded | ogc-features | pass/fail |
| NB-OWS-OAF-SCHM-03 | NB-OWS | All 12 seeded attributes round-trip with their JSON types preserved (bool/int/double/array), not stringified | ogc-features | pass/fail |
| NB-OWS-OAF-SORT-01 | NB-OWS | OWSLib's (property, direction) sortby tuple maps to the server's `-name` convention; desc is the exact reverse of asc over all 10 features | ogc-features | pass/fail |
| NB-OWS-WFS-BBOX-01 | NB-OWS | The 4-element BBOX (default CRS, longitude/latitude) and the 5-element CRS84 BBOX select the identical feature set ['alpha', 'beta', 'gamma'], so the server's bbox axis-order handling... | wfs | pass/fail |
| NB-OWS-WFS-CAP-01 | NB-OWS | OperationsMetadata advertises 27 entries covering every mandatory WFS 2.0 operation, and GetFeature offers both ['get', 'post'] DCP bindings | wfs | pass/fail |
| NB-OWS-WFS-CAP-02 | NB-OWS | Capabilities declare the WFS 2.0 conformance constraint set including ImplementsBasicWFS, ImplementsResultPaging, KVPEncoding and XMLEncoding; the paging and encoding claims are... | wfs | pass/fail |
| NB-OWS-WFS-CAP-03 | NB-OWS | All 5 advertised GetFeature output formats returned real payloads: application/gml+xml; version=3.2=1707B, GML3.2=1671B, application/geo+json=518B, text/csv=375B, application/json=518B | wfs | pass/fail |
| NB-OWS-WFS-CRS-02 | NB-OWS | 4 (CRS, spelling) combinations from the advertised crsOptions were served, each labelled with a matching srsName and reprojected within tolerance | wfs | pass/fail |
| NB-OWS-WFS-CRS-03 | NB-OWS | All three CRS84 spellings (URN, CRS:84, OGC URI) return longitude/latitude ordinates labelled with the CRS84 URN, so srsName and axis order agree | wfs | pass/fail |
| NB-OWS-WFS-DFT-01 | NB-OWS | DescribeFeatureType returned a well-formed XSD declaring 'test_layer' in the gml:AbstractFeature substitution group and importing GML 3.2 | wfs | pass/fail |
| NB-OWS-WFS-ERR-02 | NB-OWS | Every deliberate client error produced an ows:ExceptionReport OWSLib could parse: bad-srsname=InvalidParameterValue, structurally-invalid-filter=InvalidParameterValue,... | wfs | pass/fail |
| NB-OWS-WFS-FILT-02 | NB-OWS | fes:PropertyIsGreaterThan on the integer `count` column returns exactly the three rows above 7; a lexical comparison would mis-order 10 | wfs | pass/fail |
| NB-OWS-WFS-HITS-01 | NB-OWS | RESULTTYPE=hits reported numberMatched=10 with numberReturned=0 and no wfs:member elements, which is what a client uses to size a query before fetching | wfs | pass/fail |
| NB-OWS-WFS-PAGE-03 | NB-OWS | A COUNT=3 walk produced 10 distinct gml:id values summing exactly to numberMatched: no gaps, no repeats, stable ordering across pages | wfs | pass/fail |
| NB-OWS-WFS-PROP-01 | NB-OWS | propertyname=['name','status'] narrowed the payload to exactly those two columns, and the PROPERTYNAME=* wildcard widened it back to all 13 properties | wfs | pass/fail |
| NB-OWS-WFS-SORT-01 | NB-OWS | SORTBY=name returned all 10 features in ascending name order | wfs | pass/fail |
| NB-OWS-WFS-STQ-01 | NB-OWS | ListStoredQueries advertises 1 queries including the mandatory urn:ogc:def:query:OGC-WFS::GetFeatureById; invoking it through OWSLib's storedQueryID/storedQueryParams returned the... | wfs | pass/fail |
| NB-OWS-WFS-T-DEL-01 | NB-OWS | OWSLib posts a WFS 2.0 Delete to a dedicated scratch layer; the transaction summary reports one deletion and a follow-up OWSLib GetFeature query observes an empty layer | wfs | pass/fail |
| NB-OWS-WFS-T-INS-01 | NB-OWS | OWSLib posts a WFS 2.0 Insert to a dedicated scratch layer; the transaction summary reports one insertion and a follow-up OWSLib GetFeature query observes the new feature | wfs | pass/fail |
| NB-OWS-WFS-T-UPD-01 | NB-OWS | OWSLib posts a WFS 2.0 Update to a dedicated scratch layer; the transaction summary reports one update and a follow-up OWSLib GetFeature query observes only the new value | wfs | pass/fail |
| NB-OWS-WFS-VER-01 | NB-OWS | OWSLib's bare getfeature() (which sends PROPERTYNAME=*) works on both legacy versions | wfs | pass/fail |
| NB-OWS-WFS-XPRO-01 | NB-OWS | WFS and OGC API - Features agree on the same layer: extent (-122.5, 37.7, -122.35, 37.84), numberMatched 10, and every WFS-advertised EPSG code [3857, 4326] is also offered by the OGC... | wfs | pass/fail |
| NB-OWS-WMS-111-01 | NB-OWS | WMS 1.1.1 identifies as OGC:WMS, advertises SRS (not CRS:84) and the 1.1.1 exception MIME type, and its longitude-first EPSG:4326 GetMap is pixel-identical to the 1.3.0 CRS:84 render of... | wms | pass/fail |
| NB-OWS-WMS-111-WITNESS-01 | NB-OWS | OWSLib negotiated WMS 1.1.1, parsed capabilities, discovered the canonical raster layer, and executed a non-empty GetMap using the 1.1.1 SRS request shape | wms | pass/fail |
| NB-OWS-WMS-CAP-01 | NB-OWS | Service block: Name=WMS, Title='browser_compat', Abstract='Honua WMS service', 3 keywords ['WMS', 'OGC', 'browser_compat'], OnlineResource... | wms | pass/fail |
| NB-OWS-WMS-CAP-02 | NB-OWS | OWSLib parsed 8 ContactInformation fields: {'name': 'Honua Support', 'organization': 'Honua', 'position': 'Support Engineer', 'address': '1 Honua Way', 'city': 'Honolulu', 'region':... | wms | pass/fail |
| NB-OWS-WMS-CAP-03 | NB-OWS | EX_GeographicBoundingBox (-122.44, 37.76, -122.4, 37.79); on the wire the BoundingBox for CRS:84 is (-122.44, 37.76, -122.4, 37.79) (longitude first) and for EPSG:4326 is (37.76,... | wms | pass/fail |
| NB-OWS-WMS-CAP-04 | NB-OWS | The advertised LegendURL for style 'default' returned a decodable 122x32 PNG | wms | pass/fail |
| NB-OWS-WMS-CAP-05 | NB-OWS | GetMap formats ['image/png', 'image/jpeg']; GetFeatureInfo formats ['text/plain', 'application/vnd.ogc.gml', 'application/json']; exception formats ['XML']; every operation carries an... | wms | pass/fail |
| NB-OWS-WMS-ERR-01 | NB-OWS | All 5 deliberate GetMap errors (unsupported-format, unknown-crs, inverted-bbox, degenerate-bbox, oversize-width) raised owslib ServiceException from an ogc:ServiceExceptionReport served... | wms | pass/fail |
| NB-OWS-WMS-GFI-01 | NB-OWS | GetFeatureInfo at I/J (111, 107) -- the pixel the seeded pt-alpha point projects to in the requested view -- returned that feature with its attributes, so the server's pixel-to-world... | wms | pass/fail |
| NB-OWS-WMS-GFI-02 | NB-OWS | All 3 advertised GetFeatureInfo formats identified the same feature with a body matching the declared media type: text/plain=46B, application/vnd.ogc.gml=570B, application/json=117B | wms | pass/fail |
| NB-OWS-WMS-GFI-03 | NB-OWS | A GetFeatureInfo aimed at empty space returns a well-formed response with an empty feature list rather than an exception, which is what identify tools rely on | wms | pass/fail |
| NB-OWS-WMS-MAP-01 | NB-OWS | Every advertised GetMap format decoded at the requested size: image/png -> PNG/RGBA, image/jpeg -> JPEG/RGB | wms | pass/fail |
| NB-OWS-WMS-MAP-02 | NB-OWS | CRS:84 and EPSG:4326 requests for the same ground area are pixel-identical even though OWSLib sends latitude-first ordinates for EPSG:4326, and the EPSG:3857 reprojection of the same... | wms | pass/fail |
| NB-OWS-WMS-MAP-03 | NB-OWS | TRANSPARENT=TRUE -> fully transparent background; TRANSPARENT=FALSE -> opaque white; BGCOLOR=0xFF0000 -> opaque red, with the features still drawn on top (103 non-background pixels) | wms | pass/fail |
| NB-OWS-WMS-MAP-04 | NB-OWS | STYLES=default matches the implicit default style pixel for pixel, and a 3-layer LAYERS request composites strictly more content than the single layer alone | wms | pass/fail |
| NB-OWS-WMS-MAP-05 | NB-OWS | A well-formed bbox outside the layer extent returns a correctly sized, fully transparent PNG rather than a ServiceException, so tiled clients degrade to empty tiles instead of error tiles | wms | pass/fail |
| NB-OWS-WMS-VER-01 | NB-OWS | Version negotiation: {'1.3.0': '1.3.0', '1.1.1': '1.1.1', '1.1.0': '1.1.1', '1.0.0': '1.1.1', '9.9.9': '1.3.0', '<absent>': '1.3.0'}. A request above the supported range degrades to the... | wms | pass/fail |
| NB-OWS-WMS-XPRO-01 | NB-OWS | Layer 'Browser Points' has the same title and the same WGS84 extent (-122.44, 37.76, -122.4, 37.79) through WMS capabilities and the OGC API - Features collection, so the two adapters... | wms | pass/fail |
| NB-OWS-WMTS-CAP-01 | NB-OWS | OperationsMetadata declares ['GetCapabilities', 'GetFeatureInfo', 'GetTile']; GetTile offers both ['KVP', 'RESTFUL'] GetEncoding constraints and OWSLib selects the KVP binding from them | wmts | pass/fail |
| NB-OWS-WMTS-CAP-02 | NB-OWS | Both tile matrix sets are internally consistent: contiguous zoom identifiers, 256px tiles, a fixed TopLeftCorner, power-of-two matrix growth and halving scale denominators | wmts | pass/fail |
| NB-OWS-WMTS-CAP-03 | NB-OWS | 38 TileMatrixLimits entries across both grids stay within the row/column range of the tile matrix they constrain, so a limits-aware client cannot be steered at a tile that does not exist | wmts | pass/fail |
| NB-OWS-WMTS-CAP-04 | NB-OWS | 3 ResourceURL entries (['FeatureInfo', 'tile']); OWSLib's own buildTileResource substituted the tile template to... | wmts | pass/fail |
| NB-OWS-WMTS-CAP-05 | NB-OWS | The default style's LegendURL returned a decodable 256x256 PNG matching the declared LegendURL width/height (256, 256) | wmts | pass/fail |
| NB-OWS-WMTS-ERR-01 | NB-OWS | Every out-of-contract GetTile returned a coded ows:ExceptionReport: row-out-of-range=TileOutOfRange, column-out-of-range=TileOutOfRange, unknown-tilematrixset=InvalidParameterValue,... | wmts | pass/fail |
| NB-OWS-WMTS-GFI-01 | NB-OWS | The advertised FeatureInfo ResourceURL, substituted for tile 14/6332/2620 at pixel I/J (142, 194) -- derived from the capabilities tile geometry -- identified the seeded pt-alpha... | wmts | pass/fail |
| NB-OWS-WMTS-TILE-01 | NB-OWS | Tile indices derived from the capabilities scale denominators with the WMTS pixel-span formula produced in-range tiles at every sampled level and all decoded as 256x256 PNGs: {'0': (0,... | wmts | pass/fail |
| NB-OWS-WMTS-TILE-02 | NB-OWS | WorldCRS84Quad declares the OGC 2x1 level-0 grid with its origin at (-180, 90) in CRS84 longitude/latitude order, and GetTile(WorldCRS84Quad/4/4/5) -- derived from that geometry --... | wmts | pass/fail |
| NB-OWS-WMTS-TILE-03 | NB-OWS | The advertised RESTful ResourceURL (http://honua:5000/rest/services/browser_compat/MapServer/WMTS/2000/default/WebMercatorQuad/5/12/5.png) and the KVP GetTile binding return... | wmts | pass/fail |
| NB-OWS-WMTS-TILE-04 | NB-OWS | Two identical GetTile requests returned byte-identical 558-byte payloads, so the tile stream is cacheable and reproducible | wmts | pass/fail |
| NB-OWS-WMTS-TILE-05 | NB-OWS | GetTile(WebMercatorQuad/5/18/24) -- a valid index far from the seeded extent -- returned a fully transparent 256x256 PNG rather than a ServiceException | wmts | pass/fail |
| NB-OWS-WMTS-XPRO-01 | NB-OWS | Layer 'Browser Points' carries the same title and the same WGS84 bounding box (-122.44, 37.76, -122.4, 37.79) through the WMTS capabilities and the OGC API - Features collection for the... | wmts | pass/fail |

#### `duckdb` — DuckDB Spatial (28 cases)

| Test Case ID | Category | Description | Protocol(s) | Evidence |
|---|---|---|---|---|
| NB-DDB-AUTH-03 | NB-DDB | GET /api/v1/admin/services with an invalid X-API-Key -> 401 (not 403, not 500) | ogc-features | pass/fail |
| NB-DDB-AUTH-04 | NB-DDB | DuckDB httpfs authenticated the same control-plane read via CREATE SECRET (TYPE http, EXTRA_HTTP_HEADERS MAP) on duckdb=1.5.5;spatial=eb1e57c; the header survives into the HTTP GET | ogc-features | pass/fail |
| NB-DDB-CRS-01 | NB-DDB | Content-Crs echoes the negotiated CRS for every supported value and defaults to CRS84: {'<default>': '<http://www.opengis.net/def/crs/OGC/1.3/CRS84>',... | ogc-features | pass/fail |
| NB-DDB-CRS-02 | NB-DDB | crs=CRS84 -> (-122.49, 37.71) = (lon, lat); crs=EPSG:4326 -> (37.71, -122.49) = (lat, lon) | ogc-features | pass/fail |
| NB-DDB-CRS-03 | NB-DDB | bbox-crs=EPSG:3857 with the DuckDB-reprojected envelope selected [1, 2, 3], identical to the CRS84 bbox | ogc-features | pass/fail |
| NB-DDB-ERR-01 | NB-DDB | Unknown collection -> 404 application/problem+json; malformed bbox -> 400 application/problem+json | ogc-features | pass/fail |
| NB-DDB-ERR-02 | NB-DDB | Unsupported format/CRS/bbox-crs each answered 400 application/problem+json rather than 500 or a hang: {'f=nosuchformat': "Unsupported format 'nosuchformat'", 'crs=bogus': "Unsupported... | ogc-features | pass/fail |
| NB-DDB-ERR-04 | NB-DDB | Unparseable paging parameters answered structured problem+json 400s: {'limit=abc': (400, 'application/problem+json'), 'limit=1.5': (400, 'application/problem+json'), 'offset=abc': (400,... | ogc-features | pass/fail |
| NB-DDB-FMT-01 | NB-DDB | COPY ... TO PARQUET then read_parquet preserved 10 rows, 9 WKB geometries and the anchor within 0.0 degrees | ogc-features | pass/fail |
| NB-DDB-FMT-02 | NB-DDB | COPY ... (FORMAT GDAL, DRIVER 'GeoJSON') then ST_Read preserved 9 geometries and the anchor within 0.0 degrees of the server's coordinates | ogc-features | pass/fail |
| NB-DDB-GEOM-03 | NB-DDB | ST_AsText(geom) = POINT (-122.49 37.71); WKB round-trip is bit-identical (deviation 0.0), so the server's coordinates survive DuckDB's binary encoding without precision loss | ogc-features | pass/fail |
| NB-DDB-GEOM-04 | NB-DDB | ST_IsValid returned true for all 9 emitted geometries | ogc-features | pass/fail |
| NB-DDB-GEOM-05 | NB-DDB | Collection 2001: 2 LINESTRINGs, lengths [2634.4, 2634.5] m in EPSG:3857 | ogc-features | pass/fail |
| NB-DDB-GEOM-06 | NB-DDB | Server crs=EPSG:3857 -> (-13635524.42726808, 4538539.15341347); DuckDB ST_Transform(always_xy) -> (-13635524.427268079, 4538539.1534134755); max deviation 5.587935447692871e-09 m <= 0.01... | ogc-features | pass/fail |
| NB-DDB-NULL-01 | NB-DDB | 4 NULL and 6 populated description values across 10 rows — the server emits JSON null (not an omitted key or an empty string) and no row was dropped | ogc-features | pass/fail |
| NB-DDB-NULL-02 | NB-DDB | 1 null-geometry row ('lambda', status 'inactive') and 9 geometry rows totalling 10 | ogc-features | pass/fail |
| NB-DDB-PAGE-03 | NB-DDB | 5 pages of limit=3 produced [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] — exactly the full set, no duplicates, no gaps, and the walk terminated on an empty page | ogc-features | pass/fail |
| NB-DDB-PAGE-04 | NB-DDB | limit=100000 -> numberMatched/numberReturned 10/10; bbox subset with limit=2 -> 3/2 (numberMatched counts the filtered set, not the page); offset=100000 -> 0 rows via DuckDB; limit=0 ->... | ogc-features | pass/fail |
| NB-DDB-PUSH-01 | NB-DDB | SERVER-SIDE bbox pushdown and CLIENT-SIDE ST_Intersects over a full fetch both selected [1, 2, 3] | ogc-features | pass/fail |
| NB-DDB-PUSH-02 | NB-DDB | SERVER-SIDE cql2-text filter status='active' and the CLIENT-SIDE DuckDB predicate both selected [1, 3, 5, 7, 9] | ogc-features | pass/fail |
| NB-DDB-PUSH-03 | NB-DDB | SERVER-SIDE datetime=2024-01-01T00:00:00Z/2024-01-03T23:59:59Z and the CLIENT-SIDE DuckDB TIMESTAMPTZ BETWEEN predicate both selected [1, 2, 3], so the server's temporal field binding... | ogc-features | pass/fail |
| NB-DDB-QRY-01 | NB-DDB | A zero-match cql2-text filter returned an empty FeatureCollection (HTTP 200) that GDAL opened without error, so DuckDB sees 0 rows rather than an exception | ogc-features | pass/fail |
| NB-DDB-QRY-02 | NB-DDB | min/max/sum over 'count' = 1/10/55, avg(ratio) = 6.875, GROUP BY status = {'active': 5, 'inactive': 5}. Every value matches the canonical fixture, so the server delivered a complete,... | ogc-features | pass/fail |
| NB-DDB-QRY-03 | NB-DDB | Spatial join of collections 0 and 2000 via ST_DWithin produced 27 pairs; row_number() OVER (PARTITION BY status) picked [('active', 'alpha', 1), ('inactive', 'beta', 1)] | ogc-features | pass/fail |
| NB-DDB-TYPE-01 | NB-DDB | All 13 columns kept their natural type through the server -> GeoJSON -> GDAL -> DuckDB path; nothing was silently coerced to VARCHAR | ogc-features | pass/fail |
| NB-DDB-TYPE-02 | NB-DDB | Cross-checked 11 fields from http://honua:5000/ogc/features/collections/0/queryables against the DuckDB types ST_Read produced | ogc-features | pass/fail |
| NB-DDB-TYPE-03 | NB-DDB | The server declares uid with JSON-Schema format 'uuid'; GeoJSON has no UUID type so GDAL/DuckDB materialize it as VARCHAR | ogc-features | pass/fail |
| NB-DDB-TYPE-04 | NB-DDB | tags -> VARCHAR[] ['red', 'blue'], numbers -> INTEGER[] [0, 1, 2]; list indexing and len() work, so the server emitted real JSON arrays rather than JSON-encoded strings | ogc-features | pass/fail |

#### `r-sf` — R sf and ows4R (50 cases)

| Test Case ID | Category | Description | Protocol(s) | Evidence |
|---|---|---|---|---|
| NB-RSF-AUT-01 | NB-RSF | 401 challenge shape on /api/v1/admin/services: WWW-Authenticate: ApiKey realm="Honua Admin", header="X-API-Key" | wfs | pass/fail |
| NB-RSF-AUT-02 | NB-RSF | A wrong X-API-Key value returned HTTP 401 on /api/v1/admin/services; it must be 401 (bad credential), not 403 (authenticated but forbidden) and never 500 | wfs | pass/fail |
| NB-RSF-AUT-03 | NB-RSF | GDAL_HTTP_HEADERS carried 'X-API-Key: <admin key>' through sf::st_layers() on the WFS DSN: 7 layer(s) listed and the certification target 'honua:test_layer' is present | wfs | pass/fail |
| NB-RSF-CRS-01 | NB-RSF | WFS advertises CRS list {4326, 3857} for the certification layer; both the storage CRS (EPSG:4326) and the Web Mercator alternative (EPSG:3857) must be offered or R users cannot request... | wfs | pass/fail |
| NB-RSF-CRS-02 | NB-RSF | Server-side reprojection to EPSG:3857 (urn:ogc:def:crs:EPSG::3857) returned (-13635524.4273, 4538539.1534); PROJ 9.4.0 via sf::st_transform() computes (-13635524.4273, 4538539.1534); max... | wfs | pass/fail |
| NB-RSF-CRS-03 | NB-RSF | Axis order with srsName=urn:ogc:def:crs:EPSG::4326: raw GML gml:pos is '37.71 -122.49' (spec requires lat lon for the urn form) and sf/GDAL recovered (-122.4900, 37.7100) as lon/lat | wfs | pass/fail |
| NB-RSF-CRS-04 | NB-RSF | bbox axis-order contract for WFS: the specified order (the feature type's default CRS, lat,lon for urn EPSG::4326) selected 3 feature(s) (expected 3), and the reversed order returned... | wfs | pass/fail |
| NB-RSF-ERR-01 | NB-RSF | WFS error shape: unknown typeName returned 400 with an ows:ExceptionReport (exceptionCode present); an unsupported REQUEST returned 501 | wfs | pass/fail |
| NB-RSF-ERR-02 | NB-RSF | Malformed CRS 'urn:ogc:def:crs:BOGUS::9999' returned HTTP 400 with a structured error body (problem+json / ExceptionReport) | wfs | pass/fail |
| NB-RSF-ERR-03 | NB-RSF | Unsupported output format returned HTTP 400 (expected a 4xx) | wfs | pass/fail |
| NB-RSF-ERR-04 | NB-RSF | Truncated protocol filter ("status = 'active' AND") returned HTTP 400 (expected a 4xx structured error, never a 500 and never a silent full result set) | wfs | pass/fail |
| NB-RSF-FMT-01 | NB-RSF | End-to-end GeoPackage fidelity: the WFS response was written with sf::st_write() and read back with sf::st_read() — 10/10 rows, names match, empty geometries match, max coordinate... | wfs | pass/fail |
| NB-RSF-FMT-02 | NB-RSF | End-to-end GeoJSON fidelity: the WFS response was written with sf::st_write() and read back with sf::st_read() — 10/10 rows, names match, empty geometries match, max coordinate deviation... | wfs | pass/fail |
| NB-RSF-FMT-03 | NB-RSF | Advertised output formats all serve the complete feature set: application/geo+json -> HTTP 200, 4059 byte(s), anchor present, last feature present; text/csv -> HTTP 200, 2516 byte(s),... | wfs | pass/fail |
| NB-RSF-GEO-01 | NB-RSF | st_bbox() of the returned features (-122.49000 37.71000 -122.37000 37.79000) against the WFS declared extent (-122.50000 37.70000 -122.35000 37.84000): the data extent must lie inside... | wfs | pass/fail |
| NB-RSF-GEO-02 | NB-RSF | WKB (st_as_binary) and WKT (st_as_text) round-trip of 9 server geometries: max coordinate deviation 0.000e+00 (WKB 0.000e+00, WKT 0.000e+00), threshold 1e-06 | wfs | pass/fail |
| NB-RSF-GEO-03 | NB-RSF | sf::st_is_valid() over 9 server geometries: 9 valid, 0 invalid, 0 indeterminate | wfs | pass/fail |
| NB-RSF-NUL-01 | NB-RSF | Nullable `description`: 4 of 10 rows are NA (fixture seeds 4 NULLs); 'alpha' is NA and 'beta' is 'description_1'. A server that emitted "" or the string "null" would fail this | wfs | pass/fail |
| NB-RSF-NUL-02 | NB-RSF | Null-geometry handling: 10 row(s) returned with 1 empty geometry/geometries (fixture seeds 10 features, 9 with geometry); the null-geometry row 'lambda' is present | wfs | pass/fail |
| NB-RSF-OAF-01 | NB-RSF | Landing page http://honua:5000/ogc/features returned HTTP 200 with link relations {self, alternate, service-desc, conformance, data, https://www.opengis.net/def/rel/ogc/1.0/map,... | ogc-features | pass/fail |
| NB-RSF-OAF-02 | NB-RSF | Declared-vs-honoured conformance: 7 of 26 declared classes were probed and all held | ogc-features | pass/fail |
| NB-RSF-OAF-03 | NB-RSF | Link relations: self=http://honua:5000/ogc/features/collections/0/items?limit=3; following `next` returned HTTP 200 with 3 feature(s) disjoint from page one (overlap 0); the final page... | ogc-features | pass/fail |
| NB-RSF-OAF-04 | NB-RSF | Single-item retrieval /items/1 returned HTTP 200 as a GeoJSON Feature with name 'alpha' (expected the 'alpha' anchor) | ogc-features | pass/fail |
| NB-RSF-OAF-05 | NB-RSF | datetime=2024-01-01T00:00:00Z/2024-01-03T00:00:00Z returned HTTP 200 with 2 of 10 feature(s); every returned created_at is inside the interval | ogc-features | pass/fail |
| NB-RSF-OAF-06 | NB-RSF | CQL2-text `filter=status = 'active'` returned HTTP 200 with 5 feature(s) (expected 5) and status values {active}. This is the protocol filter parameter, not a client-side filter | ogc-features | pass/fail |
| NB-RSF-OWS-01 | NB-RSF | ows4R parsed ows:ServiceIdentification: title='Honua WFS 2.0', serviceType='WFS', serviceTypeVersion={2.0.0} | wfs | pass/fail |
| NB-RSF-OWS-02 | NB-RSF | ows4R parsed ows:OperationsMetadata with 9 operation(s): {GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction, ListStoredQueries, DescribeStoredQueries,... | wfs | pass/fail |
| NB-RSF-OWS-03 | NB-RSF | ows4R parsed 5 feature type(s) from the WFS 2.0.0 capabilities: {honua:test_layer, honua:browser_points, honua:browser_lines, honua:browser_polygons, honua:portal_public_points}; the... | wfs | pass/fail |
| NB-RSF-OWS-04 | NB-RSF | ows4R per-feature-type metadata: DefaultCRS parsed to epsg:4326 (expected EPSG:4326) and ows:WGS84BoundingBox parsed | wfs | pass/fail |
| NB-RSF-OWS-05 | NB-RSF | ows4R DescribeFeatureType returned 15 element(s); 12 of 12 canonical attribute fields present | wfs | pass/fail |
| NB-RSF-OWS-06 | NB-RSF | ows4R WFSFeatureType$getFeatures() returned a sf/data.frame with 10 row(s); expected 10 | wfs | pass/fail |
| NB-RSF-OWS-07 | NB-RSF | ows4R getFeatures(count=3) then getFeatures(count=3, startIndex=3) returned 3 and 3 row(s) with overlap 0: {alpha,beta,gamma} then {delta,epsilon,zeta} | wfs | pass/fail |
| NB-RSF-OWS-08 | NB-RSF | RESULTTYPE=hits (httr transport-shape check) returned HTTP 200 with numberMatched=10, numberReturned=0 and 0 wfs:member element(s) | wfs | pass/fail |
| NB-RSF-OWS-09 | NB-RSF | PROPERTYNAME=name,status returned HTTP 200 with properties {name, status}; expected exactly {name, status}. Property subsetting that silently returns everything wastes the bandwidth the... | wfs | pass/fail |
| NB-RSF-OWS-10 | NB-RSF | OGC Filter Encoding 2.0 fes:PropertyIsEqualTo(status='active') returned HTTP 200 with 5 feature(s) (expected 5) and status values {active} | wfs | pass/fail |
| NB-RSF-OWS-11 | NB-RSF | SORTBY=name A produced {alpha,beta,delta,epsilon,eta,gamma,iota,lambda,theta,zeta} (sorted) and SORTBY=name D produced {zeta,theta,lambda,iota,gamma,eta,epsilon,delta,beta,alpha} (sorted) | wfs | pass/fail |
| NB-RSF-OWS-12 | NB-RSF | ows4R against the advertised legacy WFS versions: 1.1.0 -> 5 feature type(s), target present; 1.0.0 -> 5 feature type(s), target present | wfs | pass/fail |
| NB-RSF-PAG-01 | NB-RSF | Full paginated walk in pages of 3 over 5 request(s) collected 10 unique feature name(s) with 0 duplicate(s); the fixture seeds 10 | wfs | pass/fail |
| NB-RSF-PAG-02 | NB-RSF | Oversized page request (limit/COUNT=100000) returned HTTP 400 with n/a row(s) | wfs | pass/fail |
| NB-RSF-PAG-03 | NB-RSF | Paging counters on a 2-feature page (httr transport-shape check): numberMatched=10 (expected 10), numberReturned=2, actual features=2. numberMatched must be the unpaged total, not the... | wfs | pass/fail |
| NB-RSF-PAG-04 | NB-RSF | Offset/startIndex past the end returned HTTP 200 with 0 feature(s) and numberMatched=10 (expected 200, 0 features, 10 matched) | wfs | pass/fail |
| NB-RSF-PAG-05 | NB-RSF | Zero-size page request (limit/COUNT=0) returned HTTP 200 with 0 feature(s): a structured 4xx or an empty 200 are both defensible; a 5xx or a full result set is not | wfs | pass/fail |
| NB-RSF-TYP-01 | NB-RSF | Numeric/boolean typing through WFS: count=integer, ratio=numeric, active=logical | wfs | pass/fail |
| NB-RSF-TYP-02 | NB-RSF | Temporal typing through WFS: created_at as character, event_date as character, event_time as character | wfs | pass/fail |
| NB-RSF-TYP-03 | NB-RSF | JSON array columns through WFS/GML: tags as character, numbers as character; values tags=[red\|blue] numbers=[0\|1\|2] | wfs | pass/fail |
| NB-RSF-TYP-04 | NB-RSF | uuid column materialised as character with value '00000000-0000-0000-0000-000000000001' (expected '00000000-0000-0000-0000-000000000001') | wfs | pass/fail |
| NB-RSF-XPR-01 | NB-RSF | Cross-protocol extent agreement: OGC API Features collection extent [-122.50000 37.70000 -122.35000 37.84000] vs WFS ows:WGS84BoundingBox [-122.50000 37.70000 -122.35000 37.84000]; max... | wfs | pass/fail |
| NB-RSF-XPR-02 | NB-RSF | Cross-protocol count agreement: OGC API Features numberMatched=10, WFS resultType=hits numberMatched=10, fixture total=10 | wfs | pass/fail |
| NB-RSF-XPR-03 | NB-RSF | Cross-protocol CRS agreement: OGC API Features offers {3857, 4326}, WFS offers {3857, 4326}. A CRS available on one protocol but not the other is a metadata bug, not a capability difference | wfs | pass/fail |
| NB-RSF-XPR-04 | NB-RSF | Cross-protocol attribute agreement: 13 field(s) in OGC API Features items vs 13 in WFS DescribeFeatureType | wfs | pass/fail |

#### `py-pystac` — pystac-client (39 cases)

| Test Case ID | Category | Description | Protocol(s) | Evidence |
|---|---|---|---|---|
| NB-STAC-COLL-01 | NB-STAC | Declared temporal extent [2024-01-01 12:00:00+00:00, 2024-01-10 12:00:00+00:00] covers all 10 item datetimes (observed [2024-01-01 12:00:00+00:00, 2024-01-10 12:00:00+00:00]) | stac | pass/fail |
| NB-STAC-COLL-02 | NB-STAC | Required members present (license='proprietary', stac_version=1.0.0, links ['alternate', 'http://www.opengis.net/def/rel/ogc/1.0/queryables', 'items', 'parent', 'root', 'self']);... | stac | pass/fail |
| NB-STAC-COLL-03 | NB-STAC | /stac/collections returned 5 entries, each rehydrating under pystac.Collection with a spatial extent | stac | pass/fail |
| NB-STAC-CONF-01 | NB-STAC | core honored: landing page carries ['child', 'conformance', 'data', 'http://www.opengis.net/def/rel/ogc/1.0/queryables', 'root', 'search', 'self', 'service-desc', 'service-doc'] and... | stac | pass/fail |
| NB-STAC-CONF-02 | NB-STAC | collections honored: all 5 listed collections (0, 2000, 2001, 2002, 3000) round-tripped through get_collection(id) | stac | pass/fail |
| NB-STAC-CONF-03 | NB-STAC | ogcapi-features honored: /items honored limit and bbox and every returned feature rehydrated under pystac.Item.from_dict | stac | pass/fail |
| NB-STAC-CONF-04 | NB-STAC | item-search honored: landing page advertises ['GET', 'POST'] search links and both methods returned 10 items | stac | pass/fail |
| NB-STAC-CONF-05 | NB-STAC | item-search#fields honored: include narrowed properties to the requested set and exclude removed only properties.tags | stac | pass/fail |
| NB-STAC-CONF-06 | NB-STAC | item-search#sort honored: +properties.name and -properties.name produced exactly reversed orderings over 10 items | stac | pass/fail |
| NB-STAC-CONF-07 | NB-STAC | item-search#filter honored: queryables publish 16 properties (dialect https://json-schema.org/draft/2019-09/schema) and a CQL2-JSON comparison on eo:cloud_cover narrowed to 5 items | stac | pass/fail |
| NB-STAC-CONF-08 | NB-STAC | oas30 honored: service-desc served OpenAPI 3.0.3 with 9 paths; service-doc link present | stac | pass/fail |
| NB-STAC-CONF-09 | NB-STAC | basic-cql2 honored in both dialects: an AND of an equality and a numeric comparison returned the same 3 items via cql2-json and cql2-text | stac | pass/fail |
| NB-STAC-ERR-01 | NB-STAC | GET an unknown item -> status=404 title='Not Found' detail="Item '999999' not found in collection '0'."; pystac-client surfaced it as APIError(status_code=404) | stac | pass/fail |
| NB-STAC-ERR-02 | NB-STAC | 3-value bbox -> status=400 title='Bad Request' detail='bbox must contain four or six numeric values.'; inverted bbox -> status=400 title='Bad Request' detail='bbox latitude values are... | stac | pass/fail |
| NB-STAC-ERR-03 | NB-STAC | datetime=not-a-date -> status=400 title='Bad Request' detail='Invalid datetime parameter.'; reversed interval -> status=400 title='Bad Request' detail='Invalid datetime parameter.' | stac | pass/fail |
| NB-STAC-ERR-04 | NB-STAC | filter-lang=bogus-lang -> status=400 title='Bad Request' detail="Invalid filter-lang 'bogus-lang'."; filter-lang without filter -> status=400 title='Bad Request' detail='filter-lang and... | stac | pass/fail |
| NB-STAC-ERR-05 | NB-STAC | GET /api/v1/admin/services with an incorrect X-API-Key -> 401 | stac | pass/fail |
| NB-STAC-ERR-06 | NB-STAC | An unknown search query parameter -> status=400 title='Bad Request' detail='Unknown query parameter: not-a-real-parameter' | stac | pass/fail |
| NB-STAC-ITEM-01 | NB-STAC | All 1 assets on item 1 (geojson) carried href/type/roles and resolved with a matching content type | stac | pass/fail |
| NB-STAC-ITEM-02 | NB-STAC | 9 items had a bbox containing their geometry; the 1 null-geometry item correctly omitted bbox | stac | pass/fail |
| NB-STAC-ITEM-03 | NB-STAC | Item 1 carried ['collection', 'parent', 'root', 'self'] links; the self link re-fetched the same item and the collection link resolved to the owning collection | stac | pass/fail |
| NB-STAC-ITEM-04 | NB-STAC | All 10 items carried an RFC 3339 UTC datetime that pystac parsed into a timezone-aware value with zero offset | stac | pass/fail |
| NB-STAC-ITEM-05 | NB-STAC | stac_extensions declared: none; eo:cloud_cover present on items: True | stac | pass/fail |
| NB-STAC-ITEM-06 | NB-STAC | Catalog: validated against the published STAC JSON Schemas; Collection: validated against the published STAC JSON Schemas; Item: validated against the published STAC JSON Schemas | stac | pass/fail |
| NB-STAC-PAGE-01 | NB-STAC | pages() walked to exhaustion in page sizes [3, 3, 3, 1], collecting all 10 seeded items with no duplicates and terminating without a next link | stac | pass/fail |
| NB-STAC-PAGE-02 | NB-STAC | ItemSearch.matched()=10 agrees with numberMatched=10 and numberReturned=3 equals the actual feature count on the page | stac | pass/fail |
| NB-STAC-PAGE-03 | NB-STAC | GET next uses a token query parameter; POST next is a body-bearing method=POST merge=true link whose token advanced the cursor to a disjoint page | stac | pass/fail |
| NB-STAC-PAGE-04 | NB-STAC | limit=1000000 clamped to the server maximum and still answered 200; limit=0 was rejected with status=400 title='Bad Request' detail='limit must be greater than or equal to 1.'. Raw httpx... | stac | pass/fail |
| NB-STAC-PAGE-05 | NB-STAC | A token past the end returned an empty FeatureCollection with no next link; a malformed token was rejected with status=400 title='Bad Request' detail='Invalid pagination token.' | stac | pass/fail |
| NB-STAC-SEARCH-01 | NB-STAC | intersects(Polygon) and the equivalent bbox both returned ['1', '2', '3'] | stac | pass/fail |
| NB-STAC-SEARCH-02 | NB-STAC | intersects(Point) at the anchor coordinate matched exactly the anchor item; a degenerate point geometry is the classic spatial-predicate edge case | stac | pass/fail |
| NB-STAC-SEARCH-03 | NB-STAC | datetime=2024-01-03T12:00:00Z matched exactly the one seeded item at that instant | stac | pass/fail |
| NB-STAC-SEARCH-04 | NB-STAC | A closed RFC 3339 interval selected the three items inside it, with both endpoints treated inclusively | stac | pass/fail |
| NB-STAC-SEARCH-05 | NB-STAC | Open-start ../T returned ['1', '2', '3'] and open-end T/.. returned ['10', '8', '9']; the two halves are disjoint | stac | pass/fail |
| NB-STAC-SEARCH-06 | NB-STAC | ids=['2', '4'] returned exactly those items; an unknown id returned an empty FeatureCollection rather than an error or the whole collection | stac | pass/fail |
| NB-STAC-SEARCH-07 | NB-STAC | collections=['0'] returned 10 items all bearing that collection back-reference; the unscoped search spanned ['0', '2000', '2001', '2002', '3000']; an unknown collection matched nothing | stac | pass/fail |
| NB-STAC-SEARCH-08 | NB-STAC | An identical bbox + datetime + sortby search returned the same ordered ids (['1', '2', '3']) over both GET and POST | stac | pass/fail |
| NB-STAC-SEARCH-09 | NB-STAC | limit=3 with max_items=4 crossed a page boundary and yielded four distinct items | stac | pass/fail |
| NB-STAC-VALID-01 | NB-STAC | stac-api-validator validated core, collections, features, item-search, filter against http://honua:5000/stac with exit code 0 | stac | pass/fail |

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
| 1.3.0 | 2026-07-09 | Register the ArcGIS Portal/Sharing facade lane: add the `portal` protocol abbreviation and the CERT-PRTL-\* extension slice exercised by the `arcgis-stub` `portal` envelope against the `portal-compat` fixture (epic #1240 / #1372) |
| 1.4.0 | 2026-07-27 | Extend the Portal facade slice with the identity + auth-failure shapes packaged Esri clients depend on (CERT-PRTL-SELF-02, -COMM-01/-02, -TOKN-02, -OAUTH-02: `community/self`, authenticated `portals/self` user block, generateToken bad-credential envelope, `refresh_token` `invalid_grant`); promote the token/OAuth2 baselines to `pass` from the first containerized-lane capture (#1372) |
