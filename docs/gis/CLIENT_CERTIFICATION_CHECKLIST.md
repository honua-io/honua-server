# Client certification checklist — four lanes

## Goal

Every protocol Honua serves is exercised by every client lane that can reach it, at
operation level, with a receipt — and every lane × operation that **cannot** be reached
is closed as `n/a` citing vendor documentation or a provider probe.

"Certified" therefore means no cell is unknown. It does not mean every cell passes,
because some cells are unreachable by construction: ArcGIS Pro has no SensorThings
client and QGIS has no GPServer client, and no amount of work changes either.

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
- ImageServer and SceneServer/i3s close as `n/a-no-client` for both QGIS lanes, because
  `arcgisimageserver` and `esrii3s` are absent from the 3.44.14 provider registry.
  ArcGIS Pro certifies those protocols instead
- the one pass bound to 4.2.2 (`UI-XYZ-RASTER`) no longer counts toward the goal

## Cell states

| State | Meaning | Evidence required |
|---|---|---|
| `pass` | Operation exercised through the client, correct result | Receipt or `.cert.json` naming the client build |
| `fail` | Exercised, wrong result | Receipt + filed issue |
| `blocked` | Cannot be exercised yet | Named cause + citation |
| `not-started` | Reachable, never attempted | — |
| `n/a-no-client` | The client cannot issue this operation | Vendor doc or provider-registry probe |
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

**`pyqgis`** — 3 protocols certified, 38 passing cases: OGC API Features (18), WFS 2.0
(12), **WCS 1.0.0 (8)**. The WCS lane is new and its envelope is now registered in
`expected-pairs.json`, so a regression is detectable.

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
three `pyqgis` protocols, FeatureServer core on `qgis-ui`.

**B — Re-execute on the target build.** The ~20 `qgis-ui` cases passing only on 3.44.3 or
3.40.15. No new authoring; re-runs.

**C — Ready but never run.** Highest value per hour, because the fixture and tooling
already exist:
- **QGIS STAC (4 cases)** — server live at `/stac` with 9 collections, connection prepared,
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
- [ ] **A lane can fail wholesale and report success** — `run.sh` masks the pytest exit
      code and an unmapped collector records nothing. This already hid 6 of 7 failing WCS
      cases behind `exit=0`. Needs a hard error, not a comment

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

`capability-matrix-aggregation.yml` already gates PRs to trunk; it fails when a cell
regresses from `pass`, or when a new lane × protocol pair appears with no checklist row.
