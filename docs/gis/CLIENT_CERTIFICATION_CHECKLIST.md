# Client certification checklist — four lanes

The [first exclusion audit](client-exclusion-audit-2026-09-20.md) and
[deeper follow-up](client-exclusion-followup-2026-09-20.md) preserve disputed
historical exclusions and their intervening review states. Native SDK and UI
receipts resolve individual operations; unsupported historical passes are also
reopened when their evidence does not prove the named server operation.
The generated tables below contain the current totals for all 376 cells; the
[protocol status report](CLIENT_PROTOCOL_STATUS_2026-09-20.md) explains the latest
findings. The original baseline narrative below is historical. Missing fixtures,
licenses, flags or a failed connection method cannot establish `n/a-no-client`.

## Goal

Every protocol Honua serves is exercised by every client lane that can reach it, at
operation level, with a receipt — and every lane × operation that **cannot** be reached
is closed as `n/a` citing vendor documentation or a provider probe.

A complete certification needs every reachable operation resolved and every
excluded operation supported by evidence for the native paths in scope.
A blanket claim about a missing client is insufficient when it rests only on
one provider name, one unsuccessful method or a disabled fixture.

## The four lanes

| Lane | Client | Build | Runs |
|---|---|---|---|
| `pro-ui` | ArcGIS Pro application | 3.7.1.1904 | Operator, computer-use receipts |
| `arcpy` | ArcPy | ships with Pro | Self-hosted licensed Windows runner |
| `qgis-ui` | QGIS application | **3.44.14 LTR only** | Operator, computer-use receipts |
| `pyqgis` | PyQGIS | 3.44.14 LTR | Headless, CI |

QGIS 4.2.2 is **out of scope**. Consequences, accepted deliberately:

- the 8 `*-latest` profile files retire, and the three scope ids currently shared between
  an LTR file and its `-latest` twin stop colliding
- ImageServer and SceneServer/i3s were initially excluded because dedicated provider
  keys were absent. Those claims are now open for operation-specific evidence review,
  including shared GDAL/OGR paths; an absent dedicated key alone is insufficient.
- the one pass bound to 4.2.2 (`UI-XYZ-RASTER`) no longer counts toward the goal

## Cell states

| State | Meaning | Evidence required |
|---|---|---|
| `pass` | Operation exercised through the client, correct result | Receipt or `.cert.json` naming the client build |
| `fail` | Exercised, wrong result | Receipt + filed issue |
| `blocked` | Execution or evidence review cannot yet close the cell | Named cause + citation |
| `not-started` | Reachable, never attempted | — |
| `n/a-no-client` | The client cannot issue this operation | Operation-specific vendor/source/request evidence |
| `n/a-superseded` | The client negotiates a different version we also serve | Vendor doc |

A protocol is certified for a lane when every operation cell is `pass` or `n/a-*`.
`blocked` and `not-started` do not count. A `pass` bound to a build other than the two
above does not count — `docs/certification-master-plan.md:18-19`: *"Version changes
create a new target revision."*

## Verified baseline

Enumerated from the three repositories, not estimated.

**`qgis-ui`** — 127 authored case ids. **10 pass on 3.44.14**
(`honua-client-compat/evidence/native-qgis-ltr-20260916-a/`), all FeatureServer core plus
basic feature operations. ~20 more pass only on 3.44.3 or 3.40.15 and need re-execution.
39 have never been attempted once. 61 are blocked with a named cause.

**`pyqgis`** — 5 protocols certified, 54 passing cases: OGC API Features (18), WFS 2.0
(12), **WCS 1.0.0 (8)**, **WMS 1.3.0 (9)**, **WMTS 1.0.0 (7)**. All five envelopes are
registered in `expected-pairs.json` with committed baselines, so a regression in any of
them is detectable.

**`pro-ui`** — 94 authored case ids. Certified: FeatureServer, MapServer, WMS, WMTS,
WFS 2.0, VectorTileServer (`honua-esri-compat/evidence/native-pro-matrix-20260917-a/`).
GPServer has 13 authored cases and **no receipt anywhere**. WCS (3) and the catalog and
catalog-layer cases (8) are all `pending`. STAC and OGC API Tiles have no coverage of any
kind, despite Pro supporting both.

**`arcpy`** — no operation-level checklist exists. `honua-esri-compat/src/honua_esri_compat/licensed.py`
gates 9 `(lane, protocol)` pairs as required arcpy evidence, and omits
`("desktop-arcgis-wf-scene", "scene-vectortile")`.

## Work groups

**A — Certified; hold and re-verify per candidate.** The six `pro-ui` protocols, the
five `pyqgis` protocols, FeatureServer core on `qgis-ui`.

**B — Re-execute on the target build.** The ~20 `qgis-ui` cases passing only on 3.44.3 or
3.40.15. No new authoring; re-runs.

**C — Ready but never run.** Highest value per hour, because the fixture and tooling
already exist:
- **QGIS STAC (4 cases)** — server live at `/stac` advertising 8 child collections (5 seeded:
  `0`, `2000`, `2001`, `2002`, `3000`; plus `10`, `11`, `12`, which are per-test layers the
  OWSLib lane creates, so no cell may assert a fixed collection count), connection prepared,
  fixture captured, and **no blocking cause ever recorded**
- **QGIS SensorThings (5 of 6)** — fixture plus nine scripts complete, provider present;
  gated only by the server root 404 (#4202)
- **Pro GPServer (13)** — authored, ~30 diagnostic directories, zero receipts
- **Pro WCS and catalog (11)** — all `pending`
- QGIS: WFS-T (2), OGC API Tiles/Maps (2), TileJSON (1), WFS stored query (1), catalog
  REST/version (2), FeatureServer statistics/topFeatures/domains (3), the edit cluster (7),
  offline (1), and 9 auth cases

**D — Unauthored but the client supports it.** Pro STAC; Pro OGC API Features and Tiles
(*"only the OGC API Features and OGC API Tiles (map tiles) standards are supported in
ArcGIS Pro"*); Pro UI cases for ImageServer, GeometryServer, VersionManagementServer,
GeocodeServer, SceneServer; QGIS 3D Tiles and Quantized Mesh (`cesiumtiles`,
`tiledscene`, `quantizedmesh` all present on 3.44.14, no case exercises them); the entire
`arcpy` operation checklist.

**E — Blocked on a fixture or artifact; client already capable.** COG (2), object store
(2), PMTiles (1), OGC API Styles (1), a published scene, an elevation service. Each opens
the moment the artifact exists.

**F — Blocked on infrastructure or config.** NAServer routing (no pgRouting in
`postgis/postgis:16-3.4`); SensorThings (`serve.sensorthings` experimental);
`versioning.branch` experimental; OIDC (no IdP); `exportImage` (PostGIS GDAL GUC plus a
server restart, because the driver check is cached for the process lifetime).

**G — `n/a` closed with a citation; never revisit.**
- `pro-ui` / `arcpy`: SensorThings, OGC API Coverages/Maps/Records/Processes/Styles/EDR
- `qgis-ui` / `pyqgis`: GPServer (0 of 440 algorithms reference arcgis, esri or gpserver),
  VersionManagementServer, GeometryServer, GeocodeServer, OData, OGC API
  EDR/Coverages/Records/Processes, Esri SOAP catalog, ImageServer, SceneServer/i3s
- `qgis-*` WCS 2.0.1 — the provider is 1.0/1.1; moot now the server serves 1.0.0

## Prerequisites — the evidence chain

A checklist is worthless while passing work is discarded. Five links were broken; two are
repaired:

- [x] **WCS envelope registered** — was written and thrown away, with no baseline and no
      `expected-pairs` row, so `diff-baselines.py --strict` never saw 8 passing cases
- [x] **`esri_client_compat` wired in** — was referenced by no file in the repository and
      never ran. Now in `testpaths`; 5 of 7 probes pass, 2 skip on environment
- [x] **`ogc_features` (135) and `feature_server` (163) classified as conformance, not
      client lanes** — they emit no envelope, and should not. Both are driven by raw
      `httpx`, so an envelope would assert that our own test code can talk to our own
      server, which certifies nothing about client compatibility and would inflate the
      matrix with circular evidence. They are also not invisible: `ci.yml` runs
      `pytest --tb=short` with no `|| true` and no `continue-on-error`, so a failure fails
      the PR. The real gap they expose is a different one — see below
- [ ] **Pro UI receipts carry `cert_ids: []`** — receipts and `.cert.json` cells are not
      cross-linked in either direction; `licensed.py` is also missing the wf-scene pair
- [x] **A lane can fail wholesale and report success** — repaired. `run.sh` masked the
      pytest exit code with `|| true` and an unmapped collector recorded nothing, which
      hid 6 of 7 failing WCS cases behind `exit=0`. An unmapped collector is now a hard
      error, and the pyqgis lane decides from `--junitxml` rather than the exit code.
      That distinction is load-bearing rather than pedantic: QGIS segfaults inside
      `QgsApplication.exitQgis()` during interpreter teardown, so a fully green run
      exits 139, which is why the mask was there. The report is written before teardown,
      so 139 is forgiven only when it records zero failures. Verified both ways — an
      injected failing case exits 1, a clean run exits 0

### The gap the conformance suites actually expose

FeatureServer, GeometryServer, ImageServer and GPServer have deep *conformance* coverage
in `tests/python/feature_server` and none of it is client evidence. Real client coverage
for those protocols does exist — `arcgis-python` (the license-free `arcgis` PyPI package),
`esri-dotnet`, and the Pro UI lane — but it lives in `honua-esri-compat`, so this
repository's matrix cannot see it. The fix is cross-repo evidence federation, not new
envelopes on httpx suites: the checklist must be able to resolve a cell from an
`honua-esri-compat` `.cert.json` the same way it resolves one produced here.

## Measuring it

`scripts/certification/build-client-checklist.py` generates this document's tables from
`docs/gis/data/client-certification-checklist.v1.json` and validates fail-closed:

- every cell has a state; none is `unknown`
- every `n/a-*` and `blocked` cell carries a citation
- every `pass` cell names ArcGIS Pro 3.7.1.1904 or QGIS 3.44.14
- every lane in `testpaths` produces an envelope; a lane that runs and emits nothing is a
  build failure

`capability-matrix-aggregation.yml` gates PRs to trunk and runs
`build-client-checklist.py --check`, so a cell with no state, an uncited `n/a` or
`blocked`, or a `pass` bound to a build that is not under certification fails the PR. It
also reports drift between the committed projection and what the generator produces, so
the tables cannot be edited away from the data.

Not yet gated, and therefore still open work: failing on a cell that *regresses* from
`pass` (which needs the previous state, not just the current one), and failing when a new
lane × protocol pair appears with no checklist row.

<!-- BEGIN GENERATED TABLES -->

<!-- Generated by scripts/certification/build-client-checklist.py. Do not edit by hand. -->

### Totals

| Lane | Client build | Closed | Open | Breakdown |
|---|---|---|---|---|
| `pro-ui` | ArcGIS Pro 3.7.1.1904 | 24/94 | 70 | blocked 16, fail 2, n/a-no-client 4, not-started 52, pass 20 |
| `arcpy` | ArcPy (ships with ArcGIS Pro 3.7.1.1904) | 37/94 | 57 | blocked 55, fail 2, n/a-no-client 2, pass 35 |
| `qgis-ui` | QGIS 3.44.14 LTR | 58/94 | 36 | blocked 36, n/a-no-client 5, pass 53 |
| `pyqgis` | QGIS 3.44.14 LTR | 63/94 | 31 | blocked 30, fail 1, n/a-no-client 5, pass 58 |

**182 of 376 cells closed; 194 open.**

### Cells

A cell closes as `pass`, `n/a-no-client` or `n/a-superseded`. Every other
value is open work. The full evidence reference or citation for each cell is
in `docs/gis/data/client-certification-checklist.v1.json`.

#### wms 1.3.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| GetCapabilities | pass | pass | pass | pass |
| GetMap | pass | pass | pass | pass |
| GetFeatureInfo | pass | blocked | pass | pass |
| GetLegendGraphic | not-started | blocked | pass | pass |
| styles | not-started | blocked | pass | pass |
| time-dimension | not-started | blocked | pass | pass |

#### wmts 1.0.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| GetCapabilities | pass | blocked | pass | pass |
| GetTile | pass | blocked | pass | pass |
| GetFeatureInfo | not-started | blocked | pass | pass |
| RESTful-tile-path | not-started | blocked | pass | pass |

#### wfs 2.0.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| GetCapabilities | pass | pass | pass | pass |
| DescribeFeatureType | pass | pass | pass | pass |
| GetFeature | pass | pass | pass | pass |
| GetPropertyValue | not-started | blocked | n/a-no-client | n/a-no-client |
| Transaction-Insert | n/a-no-client | blocked | pass | pass |
| Transaction-Update | n/a-no-client | blocked | pass | pass |
| Transaction-Delete | n/a-no-client | blocked | pass | pass |
| ListStoredQueries | not-started | blocked | n/a-no-client | n/a-no-client |

#### wcs 1.0.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| GetCapabilities | blocked | blocked | pass | pass |
| DescribeCoverage | blocked | blocked | pass | pass |
| GetCoverage | blocked | blocked | pass | pass |

#### wcs 2.0.1

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| GetCapabilities | not-started | blocked | blocked | pass |
| DescribeCoverage | not-started | blocked | blocked | pass |
| GetCoverage | not-started | blocked | blocked | pass |

#### ogc-api-features 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| landing-page | not-started | blocked | pass | pass |
| conformance | not-started | blocked | pass | pass |
| collections | not-started | blocked | pass | pass |
| items | not-started | blocked | pass | pass |
| item | not-started | blocked | pass | pass |
| bbox-datetime-filter | not-started | blocked | pass | pass |
| crs-negotiation | not-started | blocked | pass | pass |
| transactions-part4 | n/a-no-client | blocked | pass | pass |

#### ogc-api-tiles 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| landing-tilesets | blocked | blocked | blocked | blocked |
| tile | blocked | blocked | blocked | blocked |

#### stac 1.0.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| catalog-landing | not-started | pass | pass | pass |
| collections | not-started | pass | pass | pass |
| item-search | not-started | pass | pass | pass |
| asset-download | not-started | blocked | pass | pass |

#### sensorthings 1.1

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| entity-sets | blocked | blocked | pass | pass |
| expand | blocked | blocked | pass | pass |
| filter-paging | blocked | blocked | pass | pass |

#### featureserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| service-info | pass | pass | pass | pass |
| layer-metadata | pass | pass | pass | pass |
| query | pass | pass | pass | pass |
| identify | pass | pass | pass | pass |
| applyEdits | pass | pass | pass | pass |
| attachments | fail | blocked | n/a-no-client | n/a-no-client |
| relatedRecords | fail | blocked | n/a-no-client | n/a-no-client |
| statistics | not-started | blocked | blocked | fail |
| domains | not-started | pass | pass | pass |
| replica-sync | not-started | blocked | n/a-no-client | n/a-no-client |

#### mapserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| service-info | pass | pass | pass | pass |
| export | pass | pass | pass | pass |
| identify | pass | blocked | pass | pass |
| legend | pass | blocked | pass | pass |

#### imageserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| service-info | not-started | pass | pass | pass |
| exportImage | not-started | pass | pass | pass |
| identify | not-started | pass | blocked | blocked |

#### vectortileserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| service-info | pass | pass | pass | pass |
| tile | pass | pass | pass | pass |
| style | pass | pass | pass | pass |

#### gpserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| service-info | not-started | pass | blocked | blocked |
| task-info | not-started | pass | blocked | blocked |
| submitJob | not-started | pass | blocked | blocked |
| job-status | not-started | pass | blocked | blocked |
| results | not-started | pass | blocked | blocked |
| cancel | not-started | pass | blocked | blocked |

#### geocodeserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| findAddressCandidates | not-started | pass | blocked | blocked |
| suggest | not-started | pass | blocked | blocked |
| reverseGeocode | not-started | pass | blocked | blocked |
| geocodeAddresses | not-started | pass | blocked | blocked |

#### geometryserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| project | not-started | blocked | blocked | blocked |
| buffer | not-started | blocked | blocked | blocked |
| areasAndLengths | not-started | blocked | blocked | blocked |
| relation | not-started | blocked | blocked | blocked |

#### naserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| route-solve | not-started | n/a-no-client | blocked | blocked |
| service-area | not-started | n/a-no-client | blocked | blocked |

#### versionmanagementserver GeoServices REST

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| create-version | not-started | fail | blocked | blocked |
| reconcile-post | not-started | fail | blocked | blocked |

#### geoservices-soap GeoServices SOAP

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| catalog-discovery | not-started | blocked | blocked | blocked |

#### odata v4

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| metadata | blocked | blocked | blocked | blocked |
| entity-query | blocked | blocked | blocked | blocked |

#### ogc-api-maps 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| map | blocked | blocked | blocked | blocked |

#### ogc-api-coverages 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| coverage | blocked | blocked | blocked | blocked |

#### ogc-api-records 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| records | blocked | blocked | blocked | blocked |

#### ogc-api-processes 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| processes-execute | blocked | blocked | blocked | blocked |

#### ogc-api-styles 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| styles | blocked | blocked | pass | pass |

#### ogc-api-edr 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| edr-query | blocked | blocked | blocked | blocked |

#### pmtiles 3

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| archive-read | not-started | blocked | pass | pass |

#### tilejson 3.0.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| descriptor | not-started | blocked | blocked | pass |

#### cog GeoTIFF

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| range-read | not-started | pass | pass | pass |

#### i3s-sceneserver 1.x

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| scene-layer | not-started | pass | blocked | blocked |

#### 3d-tiles 1.0

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| tileset | not-started | blocked | pass | pass |

#### elevation Esri

| Operation | `pro-ui` | `arcpy` | `qgis-ui` | `pyqgis` |
|---|---|---|---|---|
| point-query | not-started | pass | blocked | pass |

<!-- END GENERATED TABLES -->
