# Client Certification Roster

<!-- Generated projection. Paired with `docs/gis/data/client-certification-roster.v1.json`;
     `ClientCertificationRosterTests` fails if the two document different id sets. -->

This document is the human-readable projection of [`docs/gis/data/client-certification-roster.v1.json`](data/client-certification-roster.v1.json), which is **the authoritative client-certification roster for honua-server**.

The external Claude artifact that seeded this roster ([`https://claude.ai/code/artifact/0122304a-cbf1-46a0-bc33-61826665bc94`](https://claude.ai/code/artifact/0122304a-cbf1-46a0-bc33-61826665bc94)) is **historical and non-authoritative**. It must not be cited as the roster. Any divergence resolves in favour of this document and [`client-certification-matrix.v1.json`](data/client-certification-matrix.v1.json).

Owning issue: [#3395](https://github.com/honua-io/honua-server/issues/3395) - parent [#3389](https://github.com/honua-io/honua-server/issues/3389).

## What a status means

| Field | Meaning |
|---|---|
| `active` | The client has a governed evidence producer in this repository or a named sibling repository. |
| `planned` | The client is registered but has no governed evidence yet. It links an implementation issue and a `targetRelease`. |
| `excluded` | The client is deliberately out of scope, with a rationale and a covered alternative. |
| `activationState` | `activated` means real, non-placeholder evidence exists today. Everything else names what is missing. |
| `requiredTier` | Only `active` + `activated` rows may declare `nightly` or `release`. Every other row declares `none` and is structurally incapable of passing or blocking a gate. |
| `intendedTierOnActivation` | Planning metadata for the tier a row would take once activated. No gate reads it. |

### Denominator rules

- **eligible**: status == 'active' AND activationState == 'activated'
- **releaseBlocking**: status == 'active' AND activationState == 'activated' AND requiredTier == 'release'
- **nightlyRequired**: status == 'active' AND activationState == 'activated' AND requiredTier == 'nightly'
- **invariant**: Every row that is not both active and activated declares requiredTier 'none' and is therefore structurally incapable of passing or blocking any gate. `intendedTierOnActivation` records the tier such a row would take once activated; it is planning metadata and is never read by a gate.

### Relationship to the capability-applicability authority

#3387 remains the single capability-applicability authority. This roster classifies client identities only: which clients exist, their status, activation state, tier, runtime, licensing, and evidence producer. It deliberately declares no capability-level denominator. #3387 joins to this file by `id` whenever a capability decision names a canonical external client, so client identity has exactly one home and the capability denominator has exactly one home.

## 2026.1 bounded-roster qualification disposition

Audit: 2026-09-06, server `010a3c0196336eeac4eedd1fbdd1cab4129ff0db`,
[release requirements revision `2026-08-29-complete.11`](https://github.com/honua-io/honua-release/blob/ffc92bc348e155fbd80b6ac6d44721fb9e632561/certification/protocol-certification-requirements.v1.json).
This is the acceptance-criterion handoff for
[server #3434](https://github.com/honua-io/honua-server/issues/3434), not a certification receipt.
The release promise is that supported 2026.1 external-client operations execute
correctly against the exact released server bytes, with complete, identity-bound
positive and applicable negative evidence. Missing evidence still blocks that promise.

The release repository owns operation selection and the final verdict under
[release #157](https://github.com/honua-io/honua-release/issues/157) and
[release #158](https://github.com/honua-io/honua-release/issues/158).
Neither this roster's activation flag nor a green server unit-test run overrides
an operation required by that frozen profile. A planned client cannot count as a
pass; if the release profile requires it, its missing producer remains a blocking
cell until the governed profile resolves the discrepancy.

### Observed input mismatch

The [audited manifest](https://github.com/honua-io/honua-release/blob/ffc92bc348e155fbd80b6ac6d44721fb9e632561/platform-manifest.yaml)
labels itself a `candidate-manifest-snapshot`. It selects server
`4ca8326f37b3225315033ee58e1a652e231992df`, image
`sha256:571395718765a499d8e25e069ded7bb8d990dd784e9f1e6bef4fd2b8322f2508`,
and cut time `2026-08-30T22:01:33Z`. Its
[pinned ledger](https://github.com/honua-io/honua-evidence/blob/c595f9d6c01e9e31aa46d460506950378f14ad85/data/protocol-certification.v1.json)
selects server `e3ab87cebb7bf2d32c4e8cdb145f8d626b864d8e`, image
`sha256:d7a45c871bf318b4882ec8e1c32004803e6d0210246be30120751f05dee1a14d`,
and cut time `2026-08-21T15:13:36Z`. Those identities differ in all three fields.

The following counts are exact-name rows in those audited inputs, **not a new
2026.1 activation denominator**. Operation IDs and scenario facets remain in the
linked requirements JSON; version sets here do not imply interchangeable versions.

| Canonical client | Required version values in audited catalog | Catalog rows | Pinned ledger result |
|---|---|---:|---|
| QGIS | 3.40 | 23 | 23 skipped |
| GDAL | 3.8.4, 3.13.3, 3.14.0 | 8 | 8 skipped |
| GDAL/OGR | 3.8.4 | 11 | 11 skipped |
| MapLibre GL JS | 5.7, 6.5.0 | 10 | 10 skipped |
| OWSLib | 0.36.0 | 6 | 6 skipped |
| PySTAC-Client | 0.9.0 | 1 | 1 skipped |

### Acceptance criteria and remaining work

| #3434 criterion | Disposition |
|---|---|
| Frozen operation/client/version profile | The linked release catalog enumerates operations and versions. Reconciliation with the supported maturity profile remains pre-cut work: for example its QGIS 3.40 requirement differs from this roster's 3.44.13 runtime policy. Do not silently accept a different version or demote supported operations. |
| Public client APIs against immutable image and #3393 fixture/config/auth digests | Released to exact-candidate qualification: the final cut image and its bound fixture/config/auth tuple do not yet exist. The working-snapshot ledger above cannot satisfy this criterion. |
| Complete identity and durable receipt fields | Schema/producer preparation remains pre-cut work. Fresh execution receipts with cut ID, server/client integrity, fixture/config/auth revisions, operation ID, target, timestamps and durable URI must be produced after the final tuple exists. Historical catalog rows are not receipts. |
| Positive and applicable credential, role/scope, tenant-isolation, pagination/limit, metadata and media/schema cases | Required cells must execute against that same tuple. Candidate execution is released for the same reason; producer coverage gaps remain pre-cut obligations. Multi-tenancy Preview does not waive applicable isolation denial checks. |
| Reject missing/skipped/stale/mismatched/source-built required cells | Required at intake and final release validation. The 59 skipped rows and mismatched identity above are non-passing evidence. Do not rebind this ledger as a substitute for execution. |
| Explicit planned/excluded rows | Retained in the roster below with target/rationale. They neither pass nor enlarge the bounded release roster. A conflict with a required release operation must be resolved explicitly by the release-owned profile. |
| No duplicate OWSLib/PySTAC producers | Reuse `owslib` and `pystac` in `client-interop-nightly.yml`, their `docker/client-compat` harnesses and `py-owslib` / `py-pystac` bindings. #3386 and #3392 must not add parallel producers for the same cells. |

At qualification, use one immutable local Docker target for the full matrix;
cloud lanes consume the slim deployment-parity subset. Join licensed ArcGIS
receipts from [honua-esri-compat #74](https://github.com/honua-io/honua-esri-compat/issues/74)
and [#75](https://github.com/honua-io/honua-esri-compat/issues/75) by evidence reference.
Do not substitute the ArcGIS stub for a licensed client. Reaggregate fresh receipts
in honua-evidence, then atomically bind the reviewed ledger commit, requirements
revision and byte digest in honua-release. #3434 stays open until this handoff is
executed; releasing candidate-dependent criteria is not a pass or a waiver of
pre-cut producer/profile work.

## Count reconciliation

| Count | Value |
|---|---|
| Artifact headline tally (`declaredEntryCount`) | 41 |
| Distinct identities enumerated by the artifact's tier tables | 42 |
| Rows this repository registers that the artifact never named | 2 |
| **Reconciled roster entries (`reconciledEntryCount`)** | **44** |

The note deltas below sum to 3, which closes the gap between 41 and 44. No row was padded or dropped to hit a round number.

| Note | Delta | Entries | Explanation |
|---|---|---|---|
| `excluded-bullet-names-two-clients` | +1 | `golden-surfer`, `avenza-maps` | The artifact's Excluded section has four bullets, but the third bullet, 'Golden Surfer, Avenza Maps', names two distinct clients. Splitting it raises the artifact's 41 headline entries to 42 distinct client identities. |
| `headline-status-split-differs-from-tier-tables` | 0 | `desktop-arcgis`, `esri-dotnet` | The headline tally claims 4 'Built, unrun' and 22 'Proposed'. The tier tables mark only 2 rows 'Built, unrun' (desktop-arcgis, esri-dotnet) and enumerate 24 unbuilt rows (16 Tier 1 'Proposed' plus 8 Tier 2 'On demand'). The two differences offset exactly, so the 41 total is unchanged. The per-row tier tables are authoritative; the artifact gives no basis for identifying which two additional rows the headline counted as built. |
| `certified-live-headline-counts-partial-rows` | 0 | `esri-js-maps-sdk`, `py-pystac` | The headline '11 certified live' equals the 9 rows the tier tables label Live, Live nightly, or 1117/1117, plus the 2 rows labelled 'Partial'. This roster does not treat 'Partial' as activated at the source artifact's revision: esri-js-maps-sdk was active but awaiting-federation, and py-pystac was planned and awaiting-evidence. esri-js-maps-sdk still declares requiredTier `none`; py-pystac was promoted to `active` with requiredTier `nightly` in #3392, which committed its envelope. The 41 total is unchanged. |
| `registry-lane-absent-from-artifact` | +1 | `arcgis-stub` | client-certification-matrix.v1.json registers an active `arcgis-stub` lane with committed evidence for featureserver, mapserver, and portal. The artifact has no row for it because it folded the stub into the ArcGIS Pro harness column. It is a distinct identity here because it is a stub harness, not a shipped client, and must never be read as certifying ArcGIS Pro. |
| `registry-sublane-absent-from-artifact` | +1 | `js-esri-leaflet` | CROSS_CLIENT_CERTIFICATION_MATRIX.md registers an Esri Leaflet browser sub-lane with its own EL-EXT-01..04 extension IDs, and tests/js-browser/esri-leaflet implements it, but the artifact has no row for it. Including it makes the gap visible: no lanes-array row declares EL-EXT-* applicability and no envelope is committed under tests/baselines/client-compat. |

## Roster

### Tier 0 - migration-blocking

A customer cannot leave Esri without these. Regression in an activated Tier 0 lane is a release blocker.

| Client | Status | Activation | Target | Required tier | Runtime | Protocol surfaces | Owning issue |
|---|---|---|---|---|---|---|---|
| `desktop-qgis` QGIS Desktop LTR (PyQGIS) | active | activated | 2026.1 | nightly | linux-container | `ogc-features`, `wfs` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |
| `desktop-arcgis` ArcGIS Pro (arcpy + honua_gp shim parity) | planned | awaiting-runtime | 2026.2 | none | windows-license | `featureserver`, `mapserver`, `imageserver`, `geometryserver`, `version-management-server`, `portal`, `ogc-features`, `wfs`, `wms`, `wmts` | [honua-esri-compat#75](https://github.com/honua-io/honua-esri-compat/issues/75) |
| `js` OpenLayers | active | activated | 2026.1 | nightly | browser-playwright | `ogc-features`, `ogc-maps`, `mvt`, `wfs`, `wms`, `wmts` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |
| `js-maplibre` MapLibre GL JS | active | activated | 2026.1 | nightly | browser-playwright | `mvt`, `ogc-maps`, `ogc-tiles` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |
| `js-cesium` CesiumJS | active | activated | 2026.1 | nightly | browser-playwright | `wms`, `wmts`, `ogc-tiles`, `ogc-maps` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |
| `esri-js-maps-sdk` ArcGIS Maps SDK for JavaScript | active | awaiting-federation | 2026.2 | none | browser-playwright | `featureserver`, `mapserver`, `portal` | [honua-esri-compat#74](https://github.com/honua-io/honua-esri-compat/issues/74) |
| `cli` GDAL / OGR (ogr2ogr) | active | activated | 2026.1 | nightly | linux-container | `ogc-features`, `wfs`, `admin-api` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |
| `esri-arcgis-python` ArcGIS API for Python | active | awaiting-federation | 2026.2 | none | linux-container | `featureserver`, `portal` | [honua-esri-compat#74](https://github.com/honua-io/honua-esri-compat/issues/74) |
| `esri-dotnet` ArcGIS Maps SDK for .NET | planned | awaiting-runtime | 2026.2 | none | windows-license | `featureserver`, `mapserver` | [honua-esri-compat#75](https://github.com/honua-io/honua-esri-compat/issues/75) |
| `honua-sdks` Honua SDKs (JavaScript, Python, .NET) | active | activated | 2026.1 | nightly | linux-container | `featureserver`, `mapserver`, `ogc-features`, `ogc-maps`, `ogc-tiles`, `odata`, `mvt`, `wfs`, `wms`, `wmts`, `admin-api`, `stac` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |
| `ogc-cite` OGC CITE / TEAM Engine (14 suites) | active | activated | 2026.1 | release | linux-container | `wfs`, `wms`, `wmts`, `wcs`, `gml`, `kml`, `gpkg`, `ogc-tiles` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |
| `arcgis-stub` ArcGIS REST + Portal facade stub client | active | activated | 2026.1 | nightly | linux-container | `featureserver`, `mapserver`, `portal` | [honua-server#3434](https://github.com/honua-io/honua-server/issues/3434) |

### Tier 1 - high value

Where the roster grows next. The notebook block is headless Linux and nearly free; one Windows host unlocks the BI block.

| Client | Status | Activation | Target | Required tier | Runtime | Protocol surfaces | Owning issue |
|---|---|---|---|---|---|---|---|
| `py-geopandas` GeoPandas (via pyogrio / Fiona) | planned | awaiting-evidence | 2026.2 | none | linux-container | `ogc-features`, `wfs` | [honua-server#3392](https://github.com/honua-io/honua-server/issues/3392) |
| `py-owslib` OWSLib | planned | awaiting-evidence | 2026.1 | none | linux-container | `ogc-features`, `wfs`, `wms`, `wmts` | [honua-server#3392](https://github.com/honua-io/honua-server/issues/3392) |
| `duckdb` DuckDB Spatial | planned | awaiting-evidence | 2026.2 | none | linux-container | `ogc-features` | [honua-server#3392](https://github.com/honua-io/honua-server/issues/3392) |
| `r-sf` R sf + ows4R | planned | awaiting-evidence | 2026.2 | none | linux-container | `ogc-features`, `wfs` | [honua-server#3392](https://github.com/honua-io/honua-server/issues/3392) |
| `jupyter-papermill` Jupyter end-to-end notebook (papermill) | planned | not-activated | later | none | linux-container | `ogc-features`, `mvt` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `py-pystac` pystac-client | planned | awaiting-evidence | 2026.1 | none | linux-container | `stac` | [honua-server#3392](https://github.com/honua-io/honua-server/issues/3392) |
| `apache-sedona` Apache Sedona (Databricks / Spark) | planned | not-activated | later | none | linux-container | `ogc-features`, `geoparquet` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `bi-powerbi` Power BI (Desktop + Service) | planned | awaiting-license | 2026.2 | none | windows-license | `odata`, `wms`, `esri-integration` | [honua-server#3390](https://github.com/honua-io/honua-server/issues/3390) |
| `bi-excel` Microsoft Excel (Power Query + ArcGIS for Excel) | planned | awaiting-license | 2026.2 | none | windows-license | `odata`, `featureserver` | [honua-server#3390](https://github.com/honua-io/honua-server/issues/3390) |
| `bi-tableau` Tableau (Desktop + Server) | planned | awaiting-license | 2026.2 | none | windows-license | `odata`, `ogc-features`, `wms` | [honua-server#3390](https://github.com/honua-io/honua-server/issues/3390) |
| `grafana` Grafana (geomap panel) | planned | not-activated | later | none | browser-playwright | `ogc-features`, `mvt` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `fme` Safe Software FME | planned | awaiting-license | later | none | linux-license | `wfs`, `ogc-features`, `featureserver` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `geoserver-cascade` GeoServer cascade (Honua as upstream) | active | activated | 2026.1 | nightly | linux-container | `wms`, `wfs` | [honua-server#3391](https://github.com/honua-io/honua-server/issues/3391) |
| `mapserver-pygeoapi-cascade` MapServer / pygeoapi cascade | planned | not-activated | later | none | linux-container | `wms`, `wfs`, `ogc-features` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `leaflet` Leaflet | planned | not-activated | later | none | browser-playwright | `wms`, `wmts`, `geojson` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `deckgl` deck.gl | planned | not-activated | later | none | browser-playwright | `mvt`, `geojson` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `js-esri-leaflet` Esri Leaflet | active | awaiting-evidence | 2026.2 | none | browser-playwright | `featureserver`, `mapserver` | [honua-server#3391](https://github.com/honua-io/honua-server/issues/3391) (needs its own issue) |
| `arcgis-field-maps` ArcGIS Field Maps | planned | not-activated | later | none | linux-container | `featureserver`, `featureserver-sync`, `portal` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `qfield-mergin` QField / Mergin Maps | planned | not-activated | later | none | linux-container | `ogc-features`, `wfs`, `gpkg` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |

### Tier 2 - opportunistic

Certified on customer demand. Each needs a license and a seat, and none blocks a release.

| Client | Status | Activation | Target | Required tier | Runtime | Protocol surfaces | Owning issue |
|---|---|---|---|---|---|---|---|
| `arcmap-10x` ArcMap 10.x | planned | awaiting-license | later | none | windows-license | `featureserver`, `wfs`, `wms` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `mapinfo-pro` MapInfo Pro | planned | awaiting-license | later | none | windows-license | `wfs`, `wms` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `autocad-map-3d` AutoCAD Map 3D / Civil 3D | planned | awaiting-license | later | none | windows-license | `wms`, `wfs` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `bentley-microstation` Bentley MicroStation / OpenCities | planned | awaiting-license | later | none | windows-license | `wms`, `wfs` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `global-mapper` Global Mapper | planned | awaiting-license | later | none | windows-license | `wms`, `wmts`, `wfs` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `qlik-sense-geoanalytics` Qlik Sense GeoAnalytics | planned | awaiting-license | later | none | browser-playwright | `wms`, `geojson` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `google-earth-pro` Google Earth Pro | planned | awaiting-license | later | none | windows-desktop | `kml`, `wms` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |
| `terriajs` TerriaJS | planned | awaiting-license | later | none | browser-playwright | `wms`, `wmts`, `ogc-features` | [honua-server#3389](https://github.com/honua-io/honua-server/issues/3389) (needs its own issue) |

### Excluded - deliberately out of scope

Named so nobody re-litigates them, and so an absent lane never reads as an oversight.

| Client | Status | Activation | Target | Required tier | Runtime | Protocol surfaces | Owning issue |
|---|---|---|---|---|---|---|---|
| `mapbox-gl-js-v2-plus` Mapbox GL JS v2+ | excluded | not-activated | (none) | none | not-provisioned | `mvt`, `ogc-tiles` | [honua-server#3395](https://github.com/honua-io/honua-server/issues/3395) |
| `arcgis-earth` ArcGIS Earth | excluded | not-activated | (none) | none | not-provisioned | `kml`, `mapserver`, `wms` | [honua-server#3395](https://github.com/honua-io/honua-server/issues/3395) |
| `golden-surfer` Golden Software Surfer | excluded | not-activated | (none) | none | not-provisioned | (none) | [honua-server#3395](https://github.com/honua-io/honua-server/issues/3395) |
| `avenza-maps` Avenza Maps | excluded | not-activated | (none) | none | not-provisioned | (none) | [honua-server#3395](https://github.com/honua-io/honua-server/issues/3395) |
| `looker-studio` Looker Studio | excluded | not-activated | (none) | none | not-provisioned | (none) | [honua-server#3395](https://github.com/honua-io/honua-server/issues/3395) |

## Entry detail

### `desktop-qgis` - QGIS Desktop LTR (PyQGIS)

- **Family / tier**: desktop-gis / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live nightly
- **Lane binding**: desktop-qgis (lane)
- **Client version policy**: QGIS LTR pinned by the pyqgis lane container image tag; the runtime version is captured into every envelope (currently 3.44.13-Solothurn).
- **Protocol surfaces**: `ogc-features`, `wfs`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: Portal/Sharing discovery is substantiated by the arcgis-stub portal envelope, not by this lane.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: canonical-client-fixture - Converges on the single logical fixture + server-config revision tracked by #3393.
- **Evidence producer**: .github/workflows/pyqgis-client-compat-nightly.yml (docker/client-compat/pyqgis)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434

### `desktop-arcgis` - ArcGIS Pro (arcpy + honua_gp shim parity)

- **Family / tier**: esri-desktop / 0
- **Status**: planned (awaiting-runtime), target release 2026.2
- **Required tier**: none (intended on activation: release)
- **Roster origin**: artifact; artifact status Built, unrun
- **Lane binding**: desktop-arcgis (planned-lane)
- **Client version policy**: Licensed ArcGIS Pro build recorded per run from the self-hosted Windows runner; evidence is entitlement-bound and content-addressed.
- **Protocol surfaces**: `featureserver`, `mapserver`, `imageserver`, `geometryserver`, `version-management-server`, `portal`, `ogc-features`, `wfs`, `wms`, `wmts`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`, `CERT-PRTL`
- **Structurally not applicable**: (none)
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: entitlement-bound - Licensed evidence must be entitlement-bound and content-addressed at release.
- **Runtime**: windows-license
- **Fixture projection**: canonical-client-fixture - Esri-owned fixtures must project onto the #3393 canonical fixture before federation.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-esri-compat/issues/75
- **Notes**: Lane code exists and records honest skips because no Windows ArcGIS Pro runner is provisioned. The 2026.1 gate (#3434) joins honua-esri-compat evidence by reference rather than activating this lane, so activation targets 2026.2 via workstream A of #3389.

### `js` - OpenLayers

- **Family / tier**: web-js / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live nightly
- **Lane binding**: js (lane)
- **Client version policy**: npm lockfile-resolved `ol` version in docker/client-compat/openlayers (currently 10.8.0); refreshed by `npm ci`.
- **Protocol surfaces**: `ogc-features`, `ogc-maps`, `mvt`, `wfs`, `wms`, `wmts`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: canonical-client-fixture - Converges on the single logical fixture + server-config revision tracked by #3393.
- **Evidence producer**: docker/client-compat/openlayers
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434
- **Notes**: OpenLayers owns the `js` matrix lane id. MapLibre GL JS and Esri Leaflet are distinct client identities that emit under the same lane id, which is why they appear as separate roster rows bound to `js` as sub-lanes.

### `js-maplibre` - MapLibre GL JS

- **Family / tier**: web-js / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live nightly
- **Lane binding**: js (sub-lane)
- **Client version policy**: npm lockfile-resolved `maplibre-gl` version in tests/js-browser (currently 5.22.0).
- **Protocol surfaces**: `mvt`, `ogc-maps`, `ogc-tiles`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-SCHM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-QFLT`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PAGE`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-GEOM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: canonical-client-fixture - Converges on the single logical fixture + server-config revision tracked by #3393.
- **Evidence producer**: tests/js-browser/maplibre (playwright.maplibre.config.ts); envelopes land under client_lane `js`, protocol `mvt`
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434
- **Notes**: Named in the bounded 2026.1 roster (#3434) for the advertised map/tile/style paths. Has a committed non-placeholder envelope at tests/baselines/client-compat/openlayers/js-mvt.cert.json.

### `js-cesium` - CesiumJS

- **Family / tier**: web-js / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live nightly
- **Lane binding**: js-cesium (lane)
- **Client version policy**: npm lockfile-resolved `cesium` version in tests/js-browser (currently 1.140.0).
- **Protocol surfaces**: `wms`, `wmts`, `ogc-tiles`, `ogc-maps`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-SCHM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-QFLT`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PAGE`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-GEOM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: canonical-client-fixture - Converges on the single logical fixture + server-config revision tracked by #3393.
- **Evidence producer**: docker/client-compat/cesium (tests/js-browser/cesium)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434
- **Notes**: Cesium imagery providers consume server-rendered raster output; the matrix Cesium sub-lane footnote governs the not-applicable set.

### `esri-js-maps-sdk` - ArcGIS Maps SDK for JavaScript

- **Family / tier**: esri-web / 0
- **Status**: active (awaiting-federation), target release 2026.2
- **Required tier**: none (intended on activation: release)
- **Roster origin**: artifact; artifact status Partial
- **Lane binding**: (none) (external-producer)
- **Client version policy**: CDN/npm-pinned ArcGIS Maps SDK for JavaScript version recorded by the honua-esri-compat sdk_matrix/jsapi run.
- **Protocol surfaces**: `featureserver`, `mapserver`, `portal`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`, `CERT-PRTL`
- **Structurally not applicable**: (none)
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-no-cost - Vendor-licensed but installable without a paid seat or activation.
- **Runtime**: browser-playwright
- **Fixture projection**: canonical-client-fixture - Esri-owned fixtures must project onto the #3393 canonical fixture before federation.
- **Evidence producer**: honua-esri-compat (sdk_matrix/jsapi)
- **Owning issue**: https://github.com/honua-io/honua-esri-compat/issues/74
- **Notes**: This is the client customers port from, but its evidence is produced in honua-esri-compat and is not yet federated into this registry, so it declares requiredTier `none` and can neither pass nor block a honua-server gate.

### `cli` - GDAL / OGR (ogr2ogr)

- **Family / tier**: libraries-cli / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live nightly
- **Lane binding**: cli (lane)
- **Client version policy**: GDAL version pinned by the lane container image tag; the full build string is recorded in every envelope (currently GDAL 3.14.0dev-480c02a).
- **Protocol surfaces**: `ogc-features`, `wfs`, `admin-api`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-GEOM`: Declared not-applicable for the `cli` lane in client-certification-matrix.v1.json; coordinate tolerance is proven by the feature-client lanes.
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: canonical-client-fixture - Converges on the single logical fixture + server-config revision tracked by #3393.
- **Evidence producer**: docker/client-compat/gdal
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434
- **Notes**: The universal adapter, and the `cli` lane id also carries the CLI/SDK admin-API extensions CLI-EXT-01/02.

### `esri-arcgis-python` - ArcGIS API for Python

- **Family / tier**: esri-libraries / 0
- **Status**: active (awaiting-federation), target release 2026.2
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live
- **Lane binding**: (none) (external-producer)
- **Client version policy**: pip/conda pin of `arcgis` in the honua-esri-compat arcgis_python lane image.
- **Protocol surfaces**: `featureserver`, `portal`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`, `CERT-PRTL`
- **Structurally not applicable**: (none)
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-no-cost - Vendor-licensed but installable without a paid seat or activation.
- **Runtime**: linux-container
- **Fixture projection**: canonical-client-fixture - Esri-owned fixtures must project onto the #3393 canonical fixture before federation.
- **Evidence producer**: honua-esri-compat (arcgis_python)
- **Owning issue**: https://github.com/honua-io/honua-esri-compat/issues/74
- **Notes**: The license-free Esri lane. Live upstream, but not yet joined to this registry's expected pairs, so it declares requiredTier `none`.

### `esri-dotnet` - ArcGIS Maps SDK for .NET

- **Family / tier**: esri-libraries / 0
- **Status**: planned (awaiting-runtime), target release 2026.2
- **Required tier**: none (intended on activation: release)
- **Roster origin**: artifact; artifact status Built, unrun
- **Lane binding**: (none) (unregistered)
- **Client version policy**: NuGet-pinned Esri.ArcGISRuntime version plus the licensed runtime deployment key recorded per run.
- **Protocol surfaces**: `featureserver`, `mapserver`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: Portal/Sharing discovery is substantiated by the arcgis-stub portal envelope, not by this lane.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: entitlement-bound - Licensed evidence must be entitlement-bound and content-addressed at release.
- **Runtime**: windows-license
- **Fixture projection**: canonical-client-fixture - Esri-owned fixtures must project onto the #3393 canonical fixture before federation.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-esri-compat/issues/75
- **Notes**: The second of the two rows the tier tables mark 'Built, unrun'. Unblocked by the same Windows host that unblocks desktop-arcgis and the BI lanes, but it has no plannedLanes row in client-certification-matrix.v1.json yet.

### `honua-sdks` - Honua SDKs (JavaScript, Python, .NET)

- **Family / tier**: honua-sdk / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live nightly
- **Lane binding**: (none) (external-producer)
- **Client version policy**: Published SDK package versions pinned per language in sdk-server-compatibility.yml; the resolved version is recorded per run.
- **Protocol surfaces**: `featureserver`, `mapserver`, `ogc-features`, `ogc-maps`, `ogc-tiles`, `odata`, `mvt`, `wfs`, `wms`, `wmts`, `admin-api`, `stac`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: canonical-client-fixture - SDK fixtures must project onto the #3393 canonical fixture before federation.
- **Evidence producer**: .github/workflows/sdk-server-compatibility.yml
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434
- **Notes**: Official-SDK parity assignments are owned by #3387. This row records the client identity only, never a second capability denominator.

### `ogc-cite` - OGC CITE / TEAM Engine (14 suites)

- **Family / tier**: conformance / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: release (intended on activation: release)
- **Roster origin**: artifact; artifact status 1137 / 1138
- **Lane binding**: (none) (external-producer)
- **Client version policy**: TEAM Engine and per-suite ETS versions pinned by the container image tags in .github/workflows/cite-*.yml.
- **Protocol surfaces**: `wfs`, `wms`, `wmts`, `wcs`, `gml`, `kml`, `gpkg`, `ogc-tiles`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: No OGC conformance suite exercises the Esri Portal/Sharing facade.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: governed-exception - Registered in client-certification-matrix.v1.json fixturePolicy.exceptions. CITE shares candidate identity, image digest, auth policy, and capability mapping with release certification but is never relabelled as having used the canonical client fixture.
- **Evidence producer**: .github/workflows/cite-*.yml (aggregated by cite-evidence-report.yml)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434
- **Notes**: The compliance receipt is currently 1137/1138; WFS 2.0 `basic` is 166/167 because multi-layer `rollbackOnFailure=true` transactions are rejected. Keeps its specification-mandated fixtures under the governed fixturePolicy exception and is never relabelled as having used the canonical client fixture.

### `arcgis-stub` - ArcGIS REST + Portal facade stub client

- **Family / tier**: esri-harness / 0
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: repository-registry; artifact status not-in-artifact
- **Lane binding**: arcgis-stub (lane)
- **Client version policy**: Stub contract version `stub-1.0`, versioned with the honua-server repository; it is a harness, not a shipped client.
- **Protocol surfaces**: `featureserver`, `mapserver`, `portal`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`, `CERT-PRTL`
- **Structurally not applicable**: (none)
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: canonical-client-fixture - Converges on the single logical fixture + server-config revision tracked by #3393.
- **Evidence producer**: docker/client-compat/arcgis-stub
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3434
- **Notes**: A registered `lanes` entry in client-certification-matrix.v1.json with no row in the source artifact, which folded it into the ArcGIS Pro harness column. It substantiates the license-free Esri REST common core and the CERT-PRTL-* Portal facade slice. Being a stub, it never certifies a real Esri client.

### `py-geopandas` - GeoPandas (via pyogrio / Fiona)

- **Family / tier**: notebooks-datascience / 1
- **Status**: planned (awaiting-evidence), target release 2026.2
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: py-geopandas (planned-lane)
- **Client version policy**: pip pin of `geopandas` and `pyogrio` in the lane requirements file, resolved into a lockfile by #3392.
- **Protocol surfaces**: `ogc-features`, `wfs`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3392); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3392
- **Notes**: Promoted from `plannedLanes` to `lanes` in #3392. Not named in the bounded 2026.1 roster (#3434); it activates with workstream B of #3389.

### `py-owslib` - OWSLib

- **Family / tier**: notebooks-datascience / 1
- **Status**: planned (awaiting-evidence), target release 2026.1
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: py-owslib (planned-lane)
- **Client version policy**: pip pin of `OWSLib` in the lane requirements file, resolved into a lockfile by #3392.
- **Protocol surfaces**: `ogc-features`, `wfs`, `wms`, `wmts`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3392); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3392
- **Notes**: Named in the bounded 2026.1 roster (#3434), but only for the WMS/WFS/WMTS/OGC operations that remain `supported` in the frozen maturity profile. #3386 and #3392 must not create duplicate OWSLib producers.

### `duckdb` - DuckDB Spatial

- **Family / tier**: notebooks-datascience / 1
- **Status**: planned (awaiting-evidence), target release 2026.2
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: duckdb (planned-lane)
- **Client version policy**: DuckDB binary version plus the `spatial` extension version pinned in the lane Dockerfile.
- **Protocol surfaces**: `ogc-features`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3392); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3392
- **Notes**: Consumes OGC API Features through `ST_Read`; roughly one Dockerfile of marginal cost and now the analyst default.

### `r-sf` - R sf + ows4R

- **Family / tier**: notebooks-datascience / 1
- **Status**: planned (awaiting-evidence), target release 2026.2
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: r-sf (planned-lane)
- **Client version policy**: `rocker/geospatial` image tag, which pins a CRAN snapshot date for `sf` and `ows4R`.
- **Protocol surfaces**: `ogc-features`, `wfs`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3392); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3392
- **Notes**: The government and academic baseline. `docker/client-compat/r-sf` and `tests/r/certification` were filled in by #3392; the lane emits committed envelopes for OGC API Features and WFS.

### `jupyter-papermill` - Jupyter end-to-end notebook (papermill)

- **Family / tier**: notebooks-datascience / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: pip pin of `papermill` plus the notebook kernel image tag; defined when the lane is scoped.
- **Protocol surfaces**: `ogc-features`, `mvt`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: No implementation issue exists yet; parked on the parent epic so the gap is visible rather than hidden.

### `py-pystac` - pystac-client

- **Family / tier**: notebooks-datascience / 1
- **Status**: planned (awaiting-evidence), target release 2026.1
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Partial
- **Lane binding**: py-pystac (planned-lane)
- **Client version policy**: pip pin of `pystac-client` in the Python lane requirements file, refreshed with the lane lockfile.
- **Protocol surfaces**: `stac`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-GEOM`: STAC items expose GeoJSON bbox/geometry metadata in EPSG:4326 only; there is no requested-CRS projection to assert a coordinate tolerance against.
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3392); the canonical fixture contract is #3393.
- **Evidence producer**: tests/python/stac_client (no committed .cert.json envelope yet)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3392
- **Notes**: Promoted to `active` in #3392: the pystac-client lane now commits a certification envelope (`tests/baselines/client-compat/pystac/py-pystac-stac.cert.json`). Counts toward the nightly denominator only while STAC remains `supported` in the frozen maturity profile (#3434).

### `apache-sedona` - Apache Sedona (Databricks / Spark)

- **Family / tier**: notebooks-datascience / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Sedona and Spark versions pinned by the lane image tag; defined when the lane is scoped.
- **Protocol surfaces**: `ogc-features`, `geoparquet`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Databricks and Spark scale. No implementation issue exists yet; parked on the parent epic.

### `bi-powerbi` - Power BI (Desktop + Service)

- **Family / tier**: bi-office / 1
- **Status**: planned (awaiting-license), target release 2026.2
- **Required tier**: none (intended on activation: release)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: bi-powerbi (planned-lane)
- **Client version policy**: Licensed Power BI Desktop build number recorded per run from the Windows host; the Service tenant is recorded separately.
- **Protocol surfaces**: `odata`, `wms`, `esri-integration`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-GEOM`: OData-only surface returns no geometry-capable protocol payload (matrix BI footnote).
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3390); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3390
- **Notes**: Harness plan is XMLA/DAX headless assertions plus visual baselines. The matrix plannedLanes row lists `odata` only; this roster also records the WMS and Esri-integration surfaces the client actually speaks.

### `bi-excel` - Microsoft Excel (Power Query + ArcGIS for Excel)

- **Family / tier**: bi-office / 1
- **Status**: planned (awaiting-license), target release 2026.2
- **Required tier**: none (intended on activation: release)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: bi-excel (planned-lane)
- **Client version policy**: Licensed Office build number recorded per run from the Windows host; the ArcGIS for Excel add-in version is recorded alongside it.
- **Protocol surfaces**: `odata`, `featureserver`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: Portal/Sharing discovery is substantiated by the arcgis-stub portal envelope, not by this lane.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3390); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3390
- **Notes**: Harness plan is COM automation via pywin32. The matrix plannedLanes row lists `odata` only; the ArcGIS for Excel add-in also consumes FeatureServer, which is why CERT-GEOM is applicable here but not on the OData-only Power BI row.

### `bi-tableau` - Tableau (Desktop + Server)

- **Family / tier**: bi-office / 1
- **Status**: planned (awaiting-license), target release 2026.2
- **Required tier**: none (intended on activation: release)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: bi-tableau (planned-lane)
- **Client version policy**: Licensed Tableau Desktop build recorded per run; the Hyper API and Document API package versions are pinned alongside it.
- **Protocol surfaces**: `odata`, `ogc-features`, `wms`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3390); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3390
- **Notes**: Harness plan is Hyper API plus Document API headless assertions, with the UI proven by screenshot baseline.

### `grafana` - Grafana (geomap panel)

- **Family / tier**: observability-bi / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Grafana image tag plus the provisioned-dashboard revision; defined when the lane is scoped.
- **Protocol surfaces**: `ogc-features`, `mvt`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-SCHM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-QFLT`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PAGE`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-GEOM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: No implementation issue exists yet; parked on the parent epic.

### `fme` - Safe Software FME

- **Family / tier**: enterprise-etl / 1
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed FME Form/Flow build recorded per run. A Linux engine exists, so the constraint is the license, not the runtime.
- **Protocol surfaces**: `wfs`, `ogc-features`, `featureserver`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`
- **Structurally not applicable**:
  - `CERT-RNDR`: Headless data client with no map canvas or table surface; rendering fidelity is not observable.
  - `CERT-PRTL`: Portal/Sharing discovery is substantiated by the arcgis-stub portal envelope, not by this lane.
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: linux-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Large in Esri migrations. Harness plan is `fme` CLI workspace runs; the Linux engine means no Windows host is required. No implementation issue exists yet.

### `geoserver-cascade` - GeoServer cascade (Honua as upstream)

- **Family / tier**: server-to-server / 1
- **Status**: active (activated), target release 2026.1
- **Required tier**: nightly (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Live nightly
- **Lane binding**: (none) (external-producer)
- **Client version policy**: GeoServer version pinned by the cross-server-consume-nightly compose image tag.
- **Protocol surfaces**: `wms`, `wfs`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3393); the canonical fixture contract is #3393.
- **Evidence producer**: .github/workflows/cross-server-consume-nightly.yml
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3391
- **Notes**: A live nightly server-to-server producer that is not registered in the client-compat lanes array and emits no .cert.json envelope. Federating it into this registry is workflow-hygiene work (#3391).

### `mapserver-pygeoapi-cascade` - MapServer / pygeoapi cascade

- **Family / tier**: server-to-server / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: MapServer and pygeoapi image tags pinned in the extended cross-server-consume compose file.
- **Protocol surfaces**: `wms`, `wfs`, `ogc-features`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Extends the existing cross-server-consume harness. No implementation issue exists yet.

### `leaflet` - Leaflet

- **Family / tier**: web-js / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: npm lockfile-resolved `leaflet` version once the lane extends tests/js-browser.
- **Protocol surfaces**: `wms`, `wmts`, `geojson`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-SCHM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-QFLT`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PAGE`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-GEOM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Still the most-deployed map library. Distinct from the already-registered Esri Leaflet sub-lane: plain Leaflet consumes WMS/WMTS/GeoJSON, not the Esri REST surface.

### `deckgl` - deck.gl

- **Family / tier**: web-js / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: npm lockfile-resolved `deck.gl` version once the lane extends tests/js-browser.
- **Protocol surfaces**: `mvt`, `geojson`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-SCHM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-QFLT`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PAGE`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-GEOM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: No implementation issue exists yet; parked on the parent epic.

### `js-esri-leaflet` - Esri Leaflet

- **Family / tier**: esri-web / 1
- **Status**: active (awaiting-evidence), target release 2026.2
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: repository-registry; artifact status not-in-artifact
- **Lane binding**: js (sub-lane)
- **Client version policy**: Lockfile-resolved `esri-leaflet` version installed by `npm ci` in tests/js-browser (currently 3.0.19); the reporter falls back to the semver range only when node_modules is absent.
- **Protocol surfaces**: `featureserver`, `mapserver`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: Portal/Sharing discovery is substantiated by the arcgis-stub portal envelope, not by this lane.
- **Scenario facets**: `positive`, `invalid-credential`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: canonical-client-fixture - Converges on the single logical fixture + server-config revision tracked by #3393.
- **Evidence producer**: tests/js-browser/esri-leaflet (evidence emitted per run to tests/js-browser/evidence/, not committed under tests/baselines)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3391 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Registered as a sub-lane in CROSS_CLIENT_CERTIFICATION_MATRIX.md with its own EL-EXT-01..04 extension IDs and implemented in tests/js-browser/esri-leaflet, but no lanes-array row declares EL-EXT-* applicability and no envelope is committed under tests/baselines/client-compat. It has no row in the source artifact. It cannot be `activated` until real committed evidence lands, so it declares requiredTier `none`.

### `arcgis-field-maps` - ArcGIS Field Maps

- **Family / tier**: esri-mobile / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: release)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Field Maps app build recorded per run; the REST-level sync probes pin the protocol revision rather than the app binary.
- **Protocol surfaces**: `featureserver`, `featureserver-sync`, `portal`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`, `CERT-PRTL`
- **Structurally not applicable**: (none)
- **Scenario facets**: `positive`, `invalid-credential`, `insufficient-role`, `cross-tenant`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Offline sync is the hard part. Field Maps binds through the Portal facade (epic #1240), so the arcgis-stub CERT-PRTL-* slice partially de-risks it; the sync protocol itself is unproven. No implementation issue exists yet.

### `qfield-mergin` - QField / Mergin Maps

- **Family / tier**: mobile-field / 1
- **Status**: planned (not-activated), target release later
- **Required tier**: none (intended on activation: nightly)
- **Roster origin**: artifact; artifact status Proposed
- **Lane binding**: (none) (unregistered)
- **Client version policy**: QField and Mergin client versions pinned by the shared PyQGIS lane image tag.
- **Protocol surfaces**: `ogc-features`, `wfs`, `gpkg`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: linux-container
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Shares the PyQGIS harness, so its marginal cost is low once the desktop-qgis lane is stable. No implementation issue exists yet.

### `arcmap-10x` - ArcMap 10.x

- **Family / tier**: esri-desktop / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `featureserver`, `wfs`, `wms`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`, `CERT-PRTL`
- **Structurally not applicable**: (none)
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: End-of-life but still deployed; the customers most motivated to migrate are the ones still on it. Unblocked by the same Windows host as desktop-arcgis. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `mapinfo-pro` - MapInfo Pro

- **Family / tier**: desktop-gis / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `wfs`, `wms`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Entrenched in utilities and telco. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `autocad-map-3d` - AutoCAD Map 3D / Civil 3D

- **Family / tier**: aec-cad / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `wms`, `wfs`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: The AEC side of every municipal deal. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `bentley-microstation` - Bentley MicroStation / OpenCities

- **Family / tier**: aec-cad / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `wms`, `wfs`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Infrastructure owners: rail and utilities. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `global-mapper` - Global Mapper

- **Family / tier**: desktop-gis / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `wms`, `wmts`, `wfs`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: windows-license
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: GDAL-adjacent, so it likely passes for free once the cli/GDAL lane does. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `qlik-sense-geoanalytics` - Qlik Sense GeoAnalytics

- **Family / tier**: bi-office / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `wms`, `geojson`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-SCHM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-QFLT`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PAGE`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-GEOM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-seat - Requires a purchased seat, an activation, and a human sign-in on the runner host.
- **Runtime**: browser-playwright
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: BI competitor to Power BI in EU enterprises. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `google-earth-pro` - Google Earth Pro

- **Family / tier**: desktop-gis / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `kml`, `wms`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-SCHM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-QFLT`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PAGE`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-GEOM`: Client consumes pre-rendered tiles or server-rendered imagery, not queryable feature endpoints.
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: proprietary-no-cost - Vendor-licensed but installable without a paid seat or activation.
- **Runtime**: windows-desktop
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: KML 2.2 is already CITE-certified by the ogc-cite row, so the client itself adds little. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `terriajs` - TerriaJS

- **Family / tier**: web-js / 2
- **Status**: planned (awaiting-license), target release later
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status On demand
- **Lane binding**: (none) (unregistered)
- **Client version policy**: Licensed desktop build recorded per run; the seat is provisioned only on customer demand.
- **Protocol surfaces**: `wms`, `wmts`, `ogc-features`
- **Applicable operation families**: `CERT-CONN`, `CERT-AUTH`, `CERT-DISC`, `CERT-SCHM`, `CERT-QFLT`, `CERT-PAGE`, `CERT-GEOM`, `CERT-ERRH`, `CERT-RNDR`
- **Structurally not applicable**:
  - `CERT-PRTL`: The ArcGIS Portal/Sharing facade is an Esri-client binding; this client addresses service URLs directly.
- **Scenario facets**: `positive`, `invalid-credential`, `pagination-limit`, `metadata`, `media-schema`
- **Licensing**: open-source - Installs unattended in a container or npm lockfile; no entitlement required.
- **Runtime**: browser-playwright
- **Fixture projection**: not-yet-assigned - Fixture projection is assigned when the lane is implemented (#3389); the canonical fixture contract is #3393.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3389 (needsOwningIssue: no dedicated issue exists yet)
- **Notes**: Backbone of AU/NZ government data portals. Tier 2 is opportunistic: certified on customer demand, and never a release blocker.

### `mapbox-gl-js-v2-plus` - Mapbox GL JS v2+

- **Family / tier**: web-js / excluded
- **Status**: excluded (not-activated), target release (none)
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status Excluded
- **Lane binding**: mapbox-gl-js-v2-plus (exclusion)
- **Client version policy**: Not pinned; the client is deliberately out of scope and is never installed in a lane.
- **Protocol surfaces**: `mvt`, `ogc-tiles`
- **Applicable operation families**: (none)
- **Structurally not applicable**:
  - `CERT-CONN`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-AUTH`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-DISC`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-SCHM`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-QFLT`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-PAGE`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-GEOM`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-ERRH`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-RNDR`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
  - `CERT-PRTL`: Client is a governed exclusion: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
- **Scenario facets**: (none)
- **Licensing**: not-evaluated - Licensing is not evaluated for governed exclusions.
- **Runtime**: not-provisioned
- **Fixture projection**: not-applicable - Excluded clients consume no fixture.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3395
- **Exclusion rationale**: Proprietary license and mandatory telemetry make it unsuitable as a CI lane.
- **Covered alternative**: js-maplibre covers the same protocol surface (MVT, styles, OGC tiles) under an open license.

### `arcgis-earth` - ArcGIS Earth

- **Family / tier**: esri-desktop / excluded
- **Status**: excluded (not-activated), target release (none)
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status Excluded
- **Lane binding**: arcgis-earth (exclusion)
- **Client version policy**: Not pinned; the client is deliberately out of scope and is never installed in a lane.
- **Protocol surfaces**: `kml`, `mapserver`, `wms`
- **Applicable operation families**: (none)
- **Structurally not applicable**:
  - `CERT-CONN`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-AUTH`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-DISC`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-SCHM`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-QFLT`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-PAGE`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-GEOM`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-ERRH`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-RNDR`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
  - `CERT-PRTL`: Client is a governed exclusion: No distinct server surface beyond the governed MapServer and KML lanes.
- **Scenario facets**: (none)
- **Licensing**: not-evaluated - Licensing is not evaluated for governed exclusions.
- **Runtime**: not-provisioned
- **Fixture projection**: not-applicable - Excluded clients consume no fixture.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3395
- **Exclusion rationale**: No distinct server surface beyond the governed MapServer and KML lanes.
- **Covered alternative**: arcgis-stub (mapserver) and ogc-cite (KML 2.2) already prove everything it consumes.

### `golden-surfer` - Golden Software Surfer

- **Family / tier**: file-import / excluded
- **Status**: excluded (not-activated), target release (none)
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status Excluded
- **Lane binding**: golden-surfer (exclusion)
- **Client version policy**: Not pinned; the client is deliberately out of scope and is never installed in a lane.
- **Protocol surfaces**: (none)
- **Applicable operation families**: (none)
- **Structurally not applicable**:
  - `CERT-CONN`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-AUTH`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-DISC`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-SCHM`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-QFLT`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-PAGE`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-GEOM`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-ERRH`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-RNDR`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-PRTL`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
- **Scenario facets**: (none)
- **Licensing**: not-evaluated - Licensing is not evaluated for governed exclusions.
- **Runtime**: not-provisioned
- **Fixture projection**: not-applicable - Excluded clients consume no fixture.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3395
- **Exclusion rationale**: File-import workflow rather than a server protocol client.
- **Covered alternative**: Server-side format I/O tests; no client lane can add signal.
- **Notes**: Named jointly with Avenza Maps in a single artifact bullet; split here into two distinct identities.

### `avenza-maps` - Avenza Maps

- **Family / tier**: file-import / excluded
- **Status**: excluded (not-activated), target release (none)
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status Excluded
- **Lane binding**: avenza-maps (exclusion)
- **Client version policy**: Not pinned; the client is deliberately out of scope and is never installed in a lane.
- **Protocol surfaces**: (none)
- **Applicable operation families**: (none)
- **Structurally not applicable**:
  - `CERT-CONN`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-AUTH`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-DISC`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-SCHM`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-QFLT`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-PAGE`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-GEOM`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-ERRH`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-RNDR`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
  - `CERT-PRTL`: Client is a governed exclusion: File-import workflow rather than a server protocol client.
- **Scenario facets**: (none)
- **Licensing**: not-evaluated - Licensing is not evaluated for governed exclusions.
- **Runtime**: not-provisioned
- **Fixture projection**: not-applicable - Excluded clients consume no fixture.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3395
- **Exclusion rationale**: File-import workflow rather than a server protocol client.
- **Covered alternative**: Server-side format I/O tests; no client lane can add signal.
- **Notes**: Named jointly with Golden Surfer in a single artifact bullet; split here into two distinct identities.

### `looker-studio` - Looker Studio

- **Family / tier**: bi-office / excluded
- **Status**: excluded (not-activated), target release (none)
- **Required tier**: none (intended on activation: none)
- **Roster origin**: artifact; artifact status Excluded
- **Lane binding**: looker-studio (exclusion)
- **Client version policy**: Not pinned; the client is deliberately out of scope and is never installed in a lane.
- **Protocol surfaces**: (none)
- **Applicable operation families**: (none)
- **Structurally not applicable**:
  - `CERT-CONN`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-AUTH`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-DISC`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-SCHM`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-QFLT`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-PAGE`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-GEOM`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-ERRH`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-RNDR`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
  - `CERT-PRTL`: Client is a governed exclusion: No geospatial server connector; it only reaches Honua through a warehouse.
- **Scenario facets**: (none)
- **Licensing**: not-evaluated - Licensing is not evaluated for governed exclusions.
- **Runtime**: not-provisioned
- **Fixture projection**: not-applicable - Excluded clients consume no fixture.
- **Evidence producer**: (none)
- **Owning issue**: https://github.com/honua-io/honua-server/issues/3395
- **Exclusion rationale**: No geospatial server connector; it only reaches Honua through a warehouse.
- **Covered alternative**: The warehouse nightly already covers the only reachable path.

## Downstream projection

honua-release and honua-evidence consume this file. They do not maintain a second copy of the roster and they do not read the historical artifact.

### honua-io/honua-release (https://github.com/honua-io/honua-release/issues/157)

- **Consumes**: `honua-server:docs/gis/data/client-certification-roster.v1.json` at schema version 1.0.0
- **Purpose**: Client half of the release denominator.
- **Selection rule**: status == 'active' AND activationState == 'activated' AND requiredTier == 'release'
- **Fields**: `id`, `displayName`, `status`, `activationState`, `requiredTier`, `targetRelease`, `protocolSurfaces`, `operationApplicability`, `evidenceProducer`
- **Must never count**:
  - Any row with status != 'active'.
  - Any row with activationState != 'activated'.
  - Any row with requiredTier == 'none'.
- **Gap cells**: A row required by the frozen 2026.1 profile (honua-release#158) that is not both active and activated must render as a blocking gap cell, never as an omission.

### honua-io/honua-evidence (https://github.com/honua-io/honua-evidence/issues/30)

- **Consumes**: `honua-server:docs/gis/data/client-certification-roster.v1.json` at schema version 1.0.0
- **Purpose**: Producer-identity allowlist and receipt validation.
- **Selection rule**: Accept a client receipt only when its client_lane joins a row whose status == 'active'; verify the producer against evidenceProducer and the client pin against clientVersionPolicy.
- **Fields**: `id`, `evidenceProducer`, `clientVersionPolicy`, `fixtureProjection`, `runtime`, `licensing`, `requiredTier`
- **Must never count**:
  - A receipt whose client_lane resolves to a planned or excluded row.
  - A receipt from a producer other than the row's declared evidenceProducer.
  - A receipt for a fixtureProjection.kind of 'governed-exception' relabelled as canonical-fixture.
