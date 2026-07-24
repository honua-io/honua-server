# CITE conformance runbook

How to run the OGC CITE conformance suites against Honua locally and in CI.

This is the operational guide. The authoritative records live elsewhere:

- [OGC Certification Path](ogc-certification-path.md) — decision record, gaps, and re-entry criteria.
- [OGC CITE Conformance Evidence](ogc-cite-conformance-evidence.md) — current pass-state evidence linked from the public site.

For WMS 1.1.1 / WFS 1.0.0 / WFS 1.1.0 (manual-only legacy suites), see [Legacy OGC CITE](../../archive/contributor/cite-legacy-ogc-conformance-testing.md) in the archive — those are not part of the automated runbook.

## Suites covered

| Suite | Kind | Script | Workflow |
|---|---|---|---|
| OGC API Features 1.0 | service | `run-cite-tests.sh` | `cite-conformance.yml` |
| OGC API Tiles | service | `run-cite-tiles-tests.sh` | `cite-tiles-conformance.yml` |
| OGC API Maps | service | `run-ogc-maps-conformance-tests.sh` (integration suite — not TeamEngine) | `ogc-maps-conformance.yml` |
| WMS 1.3 | service | `run-cite-wms-tests.sh` | `cite-wms-conformance.yml` |
| WMTS 1.0 | service | `run-cite-wmts-tests.sh` | `cite-wmts-conformance.yml` |
| WFS 2.0 | service | `run-cite-wfs20-tests.sh` | `cite-wfs20-conformance.yml` |
| WPS 2.0.2 | service | `run-cite-wps20-tests.sh` | `cite-wps20-conformance.yml` |
| WCS 2.0.1 | service | `run-cite-wcs20-tests.sh` | `cite-wcs20-conformance.yml` |
| KML 2.2 | format | `run-cite-kml22-tests.sh` | `cite-kml22-conformance.yml` |
| GML 3.2 | format | `run-cite-gml32-tests.sh` | `cite-gml32-conformance.yml` |
| GeoPackage 1.2 | format | `run-cite-gpkg12-tests.sh` | `cite-gpkg12-conformance.yml` |

**Service-level** suites exercise an HTTP API end-to-end via TeamEngine. **Format-level** suites validate a document or file produced by Honua against the spec's schema.

## Running any suite locally

All scripts live under `scripts/conformance/cite/` (and `scripts/conformance/ogc/` for the Maps integration suite) and share a common flag surface:

```bash
# Default profile, clean exit
./scripts/conformance/cite/run-cite-<suite>-tests.sh

# Pick a non-default profile (see per-suite tables below)
./scripts/conformance/cite/run-cite-<suite>-tests.sh --profile <name>

# Keep containers up for log inspection
./scripts/conformance/cite/run-cite-<suite>-tests.sh --no-cleanup --verbose

# Leave services running so you can drive TeamEngine manually
./scripts/conformance/cite/run-cite-<suite>-tests.sh --interactive
```

The scripts build `honua-server:latest` unless `HONUA_CITE_SKIP_BUILD=true` is set, start the suite's Docker Compose stack (Honua + Postgres + TeamEngine where applicable), wait for healthchecks, run the suite, and write artifacts to `cite-<suite>-results/`.

Interactive mode exposes:
- Honua server: <http://localhost:8080>
- TeamEngine UI (service-level suites): <http://localhost:8081/teamengine>
- Postgres: `localhost:5433`

## CI execution

Each suite has a dedicated workflow (see table above) on a weekly schedule plus `workflow_dispatch`. None of these are PR-blocking — see [CI gate model](../ci/gate-model.md) for the tier rationale.

CI baseline for every suite:
- `failed_tests` must be `0`.
- `total_tests` must be greater than `0`.
- For format-level suites: `skipped_tests` and `canttell_tests` must also be `0` (these run a strict no-skip `applicable` profile by default).

Artifacts (markdown summary + raw TeamEngine outputs or TRX) are uploaded with 30-day retention. Preserve anything you need for release or certification evidence outside the normal workflow artifact store.

## Common troubleshooting

When a run fails:

```bash
# Honua app logs
docker compose -f docker/cite/<suite>/compose.yml logs honua-server

# TeamEngine logs (service-level suites)
docker compose -f docker/cite/<suite>/compose.yml logs cite-runner
```

If `--interactive` was used, open the relevant capabilities or landing endpoint listed below in a browser before invoking TeamEngine — most failures show up there first.

For format-level suites, the upstream OGC ETS images are pinned to `:latest` (KML 2.2, GML 3.2, GeoPackage 1.2). If validation behavior changes unexpectedly, check for an image refresh upstream.

## Per-suite details

### OGC API Features 1.0

- **Scope:** Core, OpenAPI 3.0, GeoJSON, HTML. CRS is optional.
- **Profiles:** `full` (default — all classes + CRS + advanced), `default` (core conformance classes only), `minimal` (Core only).
- **Test params:** `docker/cite/ogc-api-features/config/test-params.xml`.
- **Sanity check:** open `http://localhost:8080/ogc/features/` in a browser.

### OGC API Tiles

- **Scope:** Core, Tileset, Tilesets List, Dataset Tilesets, Geodata Tilesets, MVT, OpenAPI 3.0.
- **Profiles:** `full` (default), `default` (core classes), `minimal`.
- **Test params:** `docker/cite/ogc-api-tiles/config/test-params.xml`.

### OGC API Maps

This suite does **not** use TeamEngine — upstream `ets-ogcapi-maps10` images were not consistently published as of 2026-02. Honua enforces Maps conformance with an in-tree integration test suite:

- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/Ogc*Tests.cs`
- Runner: `./scripts/conformance/ogc/run-ogc-maps-conformance-tests.sh --configuration Release`
- Artifacts: `ogc-maps-results/ogc-maps-summary.md`, `.log`, `.trx`.
- Production-audit integration: included in `scripts/conformance/run-production-audit.sh --phase 2 --agents protocol`.

Conformance classes advertised: `core`, `collection-map`, `dataset-map`, `collections-selection`, `datetime` (temporal raster mosaic — gated by the `raster.temporal-mosaic` entitlement; returns 402 without it), `crs`, `png`, `jpeg`, `tiff`, `scaling`.

### WMS 1.3

- **Scope:** `GetCapabilities`, `GetMap`.
- **Profiles:** `minimal`, `default`, `full`.
- **Test params:** `docker/cite/wms13/config/test-params.xml`.
- **Sanity check:** open `http://localhost:8080/rest/services/cite/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0` in a browser.

### WMTS 1.0

- **Scope:** `GetCapabilities`, `GetTile` (KVP).
- **Profiles:** label-override via `--profile` (default: standard WMTS profile).
- **Test params:** `docker/cite/wmts10/config/test-params.xml`.
- **Sanity check:** open `http://localhost:8080/rest/services/cite/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0` in a browser.
- **Open issue:** lane is not certification-ready until `WebMercatorQuad`'s `WellKnownScaleSet` CRS URN is corrected. Tracked by [#870](https://github.com/honua-io/honua-server/issues/870).

### WPS 2.0.2

- **Scope:** official `ets-wps20` 1.1 Basic, Synchronous, and Asynchronous conformance classes.
- **Profiles:** `basic-async` (default certification path), `basic-sync`, and `all`.
- **Test params:** the runner generates `cite-wps20-results/test-run-props.xml` from the template under `docker/cite/wps20/config/`.
- **Sanity check:** open `http://localhost:8100/wps?service=WPS&request=GetCapabilities&version=2.0.0` in a browser.
- **Upstream pin:** tag `1.1`, commit `e2acc691440fad98d32e873a6b7237c9d759b8df`, built as the official all-in-one Java 17 JAR.
- **Evidence status:** pending the first all-pass run. This lane is not yet part of the public aggregate or an OGC certification claim.

The upstream ETS always executes all three class groups. Honua's parser reports
the selected certification path in the standard summary while retaining raw
totals, per-class counts, the TestNG tree, ETS exit status, image inspection,
and source provenance. A failure or skip in a selected class fails the lane.

```bash
./scripts/conformance/cite/run-cite-wps20-tests.sh --profile basic-async --verbose
```

WPS result bundles include the checked-out Honua commit, build mode, requested
prebuilt reference when applicable, registry digests, the immutable image ID
from the running Compose container, and a full Docker image inspection. CI
supplies and requires a checkout SHA that exactly matches `git rev-parse HEAD`.
Local runs derive it from Git; outside a Git checkout they continue with an
explicit `unknown` warning. Local `--skip-build` runs are marked
`local-existing` rather than claiming a source-to-image build relationship.

### WFS 2.0

- **Scope:** Basic WFS, XML/KVP encoding, FES filter encoding; transactional WFS optional.
- **Profiles:** `basic` (default), `transactional`, `full`.
- **Test params:** `docker/cite/wfs20/config/test-params.xml`.
- **Sanity check:** open `http://localhost:8080/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities` in a browser.
- **Open issue:** lane is not certification-ready until the bounded service-behavior fixes from [#870](https://github.com/honua-io/honua-server/issues/870), [#871](https://github.com/honua-io/honua-server/issues/871), [#872](https://github.com/honua-io/honua-server/issues/872), [#873](https://github.com/honua-io/honua-server/issues/873) are re-validated against a clean retained artifact.

### WCS 2.0.1

- **Profiles:** `core` (default), `crs` (core + CRS extension), `extensions` (adds POST, processing, scaling, interpolation, range subsetting, CRS), `full` (adds EO-WCS).
- **TeamEngine image:** `ogccite/ets-wcs20:1.22-teamengine-6.0.0-RC2`.
- **Passing criteria:** the core profile is eligible for public evidence only when the run reports nonzero tests with `failed = skipped = CantTell = 0`.

### KML 2.2 (format-level)

- **Scope:** validates a Honua-generated KML document produced by `MapServer/generateKml` against the OGC KML 2.2 schema. KML output is always EPSG:4326.
- **Profiles:** `applicable` (default — strict Level 1 no-skip evidence profile), `default` (raw ETS, may report skips for Level 2/3 checks).
- **Sanity check:** open `http://localhost:8080/rest/services/cite/MapServer/generateKml` in a browser.

### GML 3.2 (format-level)

- **Scope:** validates a GML document fetched from the `cite:BasicPolygons` collection via OGC API Features content negotiation (`Accept: application/gml+xml; version=3.2`).
- **Profiles:** `applicable` (default), `default` (raw ETS).
- **Sanity check:** use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /ogc/features/collections/cite:BasicPolygons/items` and select `application/gml+xml; version=3.2`.

### GeoPackage 1.2 (format-level)

- **Scope:** validates a GeoPackage exported from layer 0 of the cite service via the admin layer-export endpoint.
- **Auth:** admin export requires `X-API-Key`. In the CITE environment this is hardcoded to `CiteAdminPassword123!` — no secrets management needed for this test infrastructure.
- **Profiles:** `applicable` (default — core + features), `default` (raw ETS).
- **Sanity check:** use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/services/cite/layers/0/export?format=gpkg` with the documented test key.

## External references

- [OGC API Features specification](https://docs.ogc.org/is/17-069r3/17-069r3.html)
- [CITE TeamEngine](https://cite.opengeospatial.org/)
