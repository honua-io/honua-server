# Cross-Client Certification Evidence Specification

This document defines the standardized evidence output format for cross-client certification results. All client lanes produce evidence in this format so results are directly comparable.

For the test case definitions and client lane mappings, see [Cross-Client Certification Matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md).

## Evidence Envelope (JSON)

Each certification run produces one JSON file per client lane per protocol. The envelope schema uses simple types only (no polymorphism, no deep nesting) for compatibility with `System.Text.Json` source generation if server-side tooling later needs to parse or produce these files.

```json
{
  "schema_version": "1.0",
  "run_id": "<timestamp or CI run ID>",
  "run_date": "<ISO 8601>",
  "server_version": "<honua-server version/commit>",
  "client_lane": "<js|js-cesium|desktop-arcgis|desktop-qgis|cli|bi-powerbi|bi-excel|ci-desktop|ci-bi|arcgis-stub>",
  "client_version": "<client tool version>",
  "protocol": "<featureserver|mapserver|ogc-features|ogc-maps|ogc-tiles|odata|mvt|wfs|wms|wmts|admin-api>",
  "environment": "<local|ci|staging>",
  "results": [
    {
      "test_case_id": "CERT-CONN-01",
      "status": "pass",
      "duration_ms": 142,
      "measured_count": null,
      "measured_delta": null,
      "notes": "",
      "evidence_ref": ""
    }
  ],
  "summary": {
    "total": 18,
    "passed": 17,
    "failed": 0,
    "skipped": 1,
    "not_applicable": 0
  },
  "cite_results": null,
  "extensions": []
}
```

### Field Reference

| Field | Type | Required | Description |
|---|---|---|---|
| `schema_version` | string | Yes | Evidence schema version (currently `"1.0"`) |
| `run_id` | string | Yes | Unique run identifier — timestamp (`20260316T1430Z`) or CI run ID |
| `run_date` | string | Yes | ISO 8601 date/time of the run |
| `server_version` | string | Yes | Honua Server version or commit SHA |
| `client_lane` | string | Yes | One of: `js`, `js-cesium`, `desktop-arcgis`, `desktop-qgis`, `cli`, `bi-powerbi`, `bi-excel`, `ci-desktop`, `ci-bi`, `arcgis-stub`. The Cesium browser sub-lane uses `js-cesium` (rather than the umbrella `js`) so it is independently identifiable in the docker/client-compat baseline-diff; the ArcGIS Pro REST stub uses `arcgis-stub` to keep its evidence distinct from a future licensed `desktop-arcgis` runner. |
| `client_version` | string | Yes | Version of the client tool under test |
| `protocol` | string | Yes | One of: `featureserver`, `mapserver`, `ogc-features`, `ogc-maps`, `ogc-tiles`, `odata`, `mvt`, `wfs`, `wms`, `wmts`, `admin-api`. The `ogc-maps` and `ogc-tiles` values are emitted by the Cesium imagery lane (and by future OGC API Maps / Tiles producers) and align with the protocol abbreviations in [`CROSS_CLIENT_CERTIFICATION_MATRIX.md`](CROSS_CLIENT_CERTIFICATION_MATRIX.md#protocol-abbreviations). |
| `environment` | string | Yes | One of: `local`, `ci`, `staging` |
| `results` | array | Yes | Array of common-core CERT-\* test case result objects |
| `results[].test_case_id` | string | Yes | CERT-\* ID from the matrix |
| `results[].status` | string | Yes | One of: `pass`, `fail`, `skip`, `not-applicable` |
| `results[].duration_ms` | number \| null | No | Execution time in milliseconds (automated lanes). Null for manual lanes. |
| `results[].measured_count` | number \| null | No | Observed item count for count-based evidence (CERT-DISC-01, CERT-QFLT-01, CERT-QFLT-02, CERT-PAGE-01). Null when not applicable to the test case. |
| `results[].measured_delta` | number \| null | No | Maximum absolute coordinate deviation in the CRS's native unit — decimal degrees for geographic CRS, meters for projected CRS (CERT-GEOM-01). See the [Geometry Tolerance](CROSS_CLIENT_CERTIFICATION_MATRIX.md#geometry-tolerance-cert-geom-01) section for default pass thresholds. Null when not applicable to the test case. |
| `results[].notes` | string | No | Free-text notes (failure details, caveats) |
| `results[].evidence_ref` | string | No | Path or URL to supporting evidence (screenshot, log) |
| `summary` | object | Yes | Aggregated counts from the `results` array only; extension results are tracked separately in `extensions` |
| `summary.total` | number | Yes | Total test cases in this run |
| `summary.passed` | number | Yes | Count of `pass` results |
| `summary.failed` | number | Yes | Count of `fail` results |
| `summary.skipped` | number | Yes | Count of `skip` results |
| `summary.not_applicable` | number | Yes | Count of `not-applicable` results |
| `cite_results` | string \| null | No | Path to CITE testng-results XML, if applicable. Null when no CITE run was performed. |
| `extensions` | array | No | Array of lane-specific extension result objects (see below) |
| `extensions[].test_case_id` | string | Yes | Lane-extension ID from the matrix (e.g., `JS-EXT-01`, `DSK-EXT-01`, `CLI-EXT-01`, `BI-EXT-01`) — not restricted to the CERT-\* prefix |
| `extensions[].status` | string | Yes | One of: `pass`, `fail`, `skip`, `not-applicable` |
| `extensions[].duration_ms` | number \| null | No | Execution time in milliseconds |
| `extensions[].measured_count` | number \| null | No | Observed item count for count-based extension evidence (e.g., BI-EXT-02). Null when not applicable. |
| `extensions[].measured_delta` | number \| null | No | Measurement deviation for extension cases that require it. Null when not applicable. |
| `extensions[].notes` | string | No | Free-text notes |
| `extensions[].evidence_ref` | string | No | Path or URL to supporting evidence |

**Nullable field convention:** Fields typed `number | null` or `string | null` use JSON `null` when unavailable. Producers must include the key with a `null` value rather than omitting the key, so every result object has a consistent shape. This keeps the schema compatible with `System.Text.Json` source generation (which maps nullable value types to `Nullable<T>` and nullable reference types to `T?`).

### Status Values

| Status | Meaning |
|---|---|
| `pass` | Test case executed successfully and met expectations |
| `fail` | Test case executed but did not meet expectations |
| `skip` | Test case was intentionally skipped (with reason in `notes`) |
| `not-applicable` | Test case does not apply to this client lane or protocol |

## File Naming Convention

```
<run-id>-<client-lane>-<protocol>.cert.json
```

Examples:
- `20260316T1430Z-js-featureserver.cert.json`
- `20260316T1430Z-desktop-arcgis-featureserver.cert.json`
- `20260316T1430Z-desktop-arcgis-mapserver.cert.json`
- `20260316T1430Z-cli-admin-api.cert.json`
- `20260316T1430Z-cli-odata.cert.json`
- `20260316T1430Z-bi-powerbi-odata.cert.json`

## Storage Location

Evidence files are stored under:

```
docs/gis/certification-evidence/<run-id>/
```

Example directory for a common-core certification run:

```
docs/gis/certification-evidence/20260316T1430Z/
├── 20260316T1430Z-js-featureserver.cert.json
├── 20260316T1430Z-desktop-arcgis-featureserver.cert.json
├── 20260316T1430Z-desktop-arcgis-mapserver.cert.json
├── 20260316T1430Z-desktop-qgis-ogc-features.cert.json
├── 20260316T1430Z-desktop-qgis-wfs.cert.json
├── 20260316T1430Z-cli-featureserver.cert.json
├── 20260316T1430Z-cli-ogc-features.cert.json
├── 20260316T1430Z-cli-admin-api.cert.json
├── 20260316T1430Z-cli-odata.cert.json
├── 20260316T1430Z-bi-powerbi-odata.cert.json
└── 20260316T1430Z-bi-excel-odata.cert.json
```

The JS lane covers FeatureServer direct JS tests and OpenLayers protocol-client tests for OGC API Features, OGC API Maps, OGC Tiles/MVT, WFS 2.0, WMS 1.3, and WMTS 1.0 via Vitest, plus browser rendering via Playwright for MVT and an OGC API Maps image-source smoke. The OpenLayers lane emits `*-js-ogc-features.cert.json`, `*-js-ogc-maps.cert.json`, `*-js-mvt.cert.json`, `*-js-wfs.cert.json`, `*-js-wms.cert.json`, and `*-js-wmts.cert.json` evidence files. The Esri Leaflet sub-lane adds Playwright-generated `*-js-featureserver.cert.json` and `*-js-mapserver.cert.json` evidence (written to `tests/js-browser/evidence/`, not this curated directory). Additional protocols (OData) will produce evidence files once automated suites are added.

The CLI lane lists FeatureServer, OGC API Features, OData, and Admin API evidence files. The OData envelope (`*-cli-odata.cert.json`) is produced automatically by `tests/dotnet/Honua.Server.Tests/Features/Protocols/OData/ODataClientCertificationTests.cs` (Microsoft.OData.Client 8.4.3) on every `test-all` run; FeatureServer and OGC API Features files will follow once `@pytest.mark.cert` markers and xUnit `[Trait("CertId", …)]` attributes are added to those suites; the Admin API file covers CLI-EXT-01/CLI-EXT-02 extensions.

The automated PyQGIS nightly lane (`pyqgis-client-compat-nightly.yml`) produces `*-desktop-qgis-ogc-features.cert.json` and `*-desktop-qgis-wfs.cert.json` envelopes under `tests/TestResults/`. These are uploaded as CI artifacts and use the `desktop-qgis` client lane value.

When extension-protocol testing is active, additional per-protocol files are produced (e.g., `*-js-mvt.cert.json`, `*-desktop-qgis-wms.cert.json`, `*-desktop-qgis-wmts.cert.json`). See the [Certification Matrix — Lane-Specific Extensions](CROSS_CLIENT_CERTIFICATION_MATRIX.md#lane-specific-extensions) for the full list.

## Windows Client Compatibility Workflow Output

The `windows-client-compat-nightly.yml` workflow introduced in ticket `#320` uploads a deterministic smoke-evidence artifact under:

```text
artifacts/client-compat/<service>-<timestamp>/
```

Since ticket `#415`, that artifact includes automated `.cert.json` envelopes for the `ci-desktop` and `ci-bi` lanes under `certification/`. Manual lanes (desktop-arcgis, desktop-qgis, bi-powerbi, bi-excel) still require operator-produced per-client evidence. The artifact also captures stable server-response evidence and packages the reusable client templates needed for manual Windows follow-through.

| Artifact path | Format | Contract |
|---|---|---|
| `README.md` | Markdown | Human-readable overview of the artifact root, lane folders, metadata, and reusable pack |
| `overall-summary.json` / `overall-summary.md` | JSON / Markdown | Root summary with `generated_at`, `artifact_root`, `service_id`, `layer_id`, `seed_source`, `server_version`, overall `status`, and `lanes[] { lane, title, status, summary, summary_path }`, plus a human-readable lane table |
| `lanes/<lane>/checks.tsv` | TSV | Raw smoke-check rows with `check_id`, `status`, `http_status`, transcript path, and optional note |
| `lanes/<lane>/lane-summary.json` / `lane-summary.md` | JSON / Markdown | Lane summary with `lane`, `title`, lane `status`, `summary { total, passed, failed, skipped, not_applicable }`, and `checks[] { id, status, http_status, transcript, note }`, plus a human-readable check table |
| `lanes/<lane>/transcripts/<check-id>.txt` | Text | Exact request/response transcript for a single smoke check |
| `certification/<timestamp>-<client_lane>-<protocol>.cert.json` | JSON | Per-protocol `.cert.json` envelope (full profile only); see [envelope schema](#evidence-envelope-schema) above |
| `metadata/workflow-context.json` | JSON | Workflow provenance including timestamp, base URL, service id, layer id, seed source, server version, and GitHub run metadata when available |
| `metadata/<seed-file>.sql` | SQL | Exact versioned seed snapshot used by the run |
| `server/server.log` | Text | Honua server stdout/stderr captured during the workflow run |
| `pack/README.md` | Markdown | Human-readable guide to the normalized pack contents |
| `pack/` | Directory | Canonical manual follow-through pack with templates, runbook, matrix, version ledger, and this evidence specification |

Since ticket #415, the workflow runs the `full` profile and emits per-protocol `.cert.json` envelopes under the `certification/` subdirectory:

```text
artifacts/client-compat/<service>-<timestamp>/
  certification/
    <timestamp>-ci-desktop-featureserver.cert.json
    <timestamp>-ci-desktop-ogc-features.cert.json
    <timestamp>-ci-desktop-mapserver.cert.json
    <timestamp>-ci-bi-odata.cert.json
```

These automated envelopes use the `ci-desktop` and `ci-bi` client lane values to distinguish curl-based protocol validation from actual client interoperability evidence. The `environment` field is `"ci"`.

Manual lanes (desktop-arcgis, desktop-qgis, bi-powerbi, bi-excel) still require operator-produced evidence per the [Client Templates and Manual Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md).

## Real-Client Interop Matrix Workflow Output

The `client-interop-nightly.yml` workflow (added by ticket [`#806`](https://github.com/honua-io/honua-server/issues/806) and restored as release evidence by ticket [`#938`](https://github.com/honua-io/honua-server/issues/938)) runs the docker/client-compat matrix once per night and emits one `.cert.json` envelope per `(lane, protocol)` pair. Lane services are defined in `docker/client-compat/compose.yml` and write their envelopes into `docker/client-compat/output/<lane>/`:

```
docker/client-compat/output/
  cesium/        <run-id>-js-cesium-{wms,wmts,ogc-tiles,ogc-maps}.cert.json
  openlayers/    <run-id>-js-{ogc-features,ogc-maps,mvt,wfs,wms,wmts}.cert.json
  pyqgis/        <run-id>-desktop-qgis-{ogc-features,wfs}.cert.json
  gdal/          gdal-ogr-results.json
                 <run-id>-cli-gdal-{ogc-features,wfs}.cert.json
  arcgis-stub/   <run-id>-arcgis-stub-{featureserver,mapserver}.cert.json
```

The full scheduled matrix is contractually complete only when these 16
`(client_lane, protocol)` pairs have current envelopes and committed baselines:

| client_lane | Protocols |
|---|---|
| `js-cesium` | `wms`, `wmts`, `ogc-tiles`, `ogc-maps` |
| `js` | `ogc-features`, `ogc-maps`, `mvt`, `wfs`, `wms`, `wmts` |
| `desktop-qgis` | `ogc-features`, `wfs` |
| `cli` | `ogc-features`, `wfs` |
| `arcgis-stub` | `featureserver`, `mapserver` |

The lane → `client_lane` mapping is intentional:

- `cesium` lane → `client_lane: "js-cesium"` (separate from the umbrella `js` lane so the Docker matrix can be diff-baselined independently of the merge-blocking Esri Leaflet / MapLibre lanes that ride on `ci.yml`)
- `arcgis-stub` lane → `client_lane: "arcgis-stub"` (REST-only stub; the FeatureServer-applicable render IDs `CERT-RNDR-01`, `CERT-RNDR-02`, and the visual / style slice IDs `CERT-RNDR-{SYM,LIN,FIL,LBL,URL}-01` are recorded as `skip` with note `pending: licensed-arcgis-runner` rather than `pass`. `CERT-RNDR-SPR-01` is MVT-only per the [matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md#common-core-test-cases) and is recorded as `not-applicable` for this lane); both `featureserver` and `mapserver` envelopes are emitted from the same run
- `pyqgis` lane → `client_lane: "desktop-qgis"` (same lane as the standalone `pyqgis-client-compat-nightly.yml`)
- `openlayers` lane → `client_lane: "js"` (same lane as the in-tree OpenLayers Vitest tests)
- `gdal` lane → emits the raw `gdal-ogr-results.json` for human inspection plus per-protocol cert envelopes via `scripts/client-compat/convert-gdal-results.py` under `client_lane: "cli"` (`oapif`→`ogc-features`, `wfs`→`wfs`); the converter projects the GDAL test category labels many-to-one onto CERT-* IDs with worst-status aggregation (`fail > pass > skip > not-applicable`): `discovery` and `feature_count` → CERT-DISC-01; `schema_introspection` → CERT-SCHM-01; `feature_read`/`read` → CERT-CONN-01; `attribute_query`/`spatial_query`/`query` → CERT-QFLT-01. The `export_geojson`/`export_gpkg`/`export_csv`/`export` categories are intentionally **unmapped** — they exercise ogr2ogr serialization paths and the [matrix CLI/SDK lane row](CROSS_CLIENT_CERTIFICATION_MATRIX.md#client-lane-coverage) excludes CERT-RNDR for this lane, so a passing GDAL export does not certify CERT-RNDR-01 in the cert envelope (the raw `gdal-ogr-results.json` still records the export pass/fail status). The remaining 24-ID common-core IDs are seeded as `skip` (applicable but not exercised) or `not-applicable`; CERT-RNDR-01 / CERT-RNDR-02 and the visual / style slice IDs always appear as `not-applicable` for this lane. Unknown category labels emit a stderr warning so newly-added test categories stay visible until `CATEGORY_MAP` is updated.

A pre-honua **seed** service applies `tests/seed/client-compat-v1.sql` (schema + `test_service`/layer 0 — both the `pyqgis` and `gdal` lanes target this pair via `HONUA_PYQGIS_SERVICE_ID`/`HONUA_PYQGIS_COLLECTION_ID` and `HONUA_GDAL_SERVICE_ID`/`HONUA_GDAL_COLLECTION_ID` respectively) and `tests/seed/browser-compat.yaml` (`browser_compat`/layers 2000-2002 used by cesium/openlayers/arcgis-stub) before honua starts, so lane services always observe the seeded data their specs reference. The compose runtime also starts Redis and binds Honua HTTP/1 on `5000` plus h2c gRPC on `5001`, matching the split transport expected by container deployments while keeping client base URLs stable at `http://honua:5000`.

Baselines are committed as `tests/baselines/client-compat/<lane>/<lane>-<protocol>.cert.json` (with the `run_id` prefix stripped for stable diffing). `scripts/client-compat/diff-baselines.py` indexes envelopes by `(client_lane, protocol)` from the JSON body — directory names are documentary only — and refreshes [`docs/gis/gap-report.md`](gap-report.md) on every run. An accompanying [`expected-pairs.json`](../../tests/baselines/client-compat/expected-pairs.json) manifest enumerates every `(client_lane, protocol)` pair the matrix is required to emit. The workflow `--strict` flag fails on (1) any baseline `pass` that does not stay `pass` in the current run — `pass`→`fail`, `pass`→`skip`, and `pass`→`not-applicable` are all regressions, so an endpoint going from up to down (or a test silently being skipped) cannot hide; (2) baseline test cases missing from the current run; (3) baseline envelopes missing entirely (a crashed lane); (4) no current-run evidence at all; (5) any expected-pairs manifest entry absent from both baseline and current run; (6) any current `fail` against a missing baseline OR a non-fail baseline (`skip`/`not-applicable`/placeholder) — so a placeholder skip cannot mask a current failure; and (7) any expected-pairs manifest entry without a committed baseline at all (even when the current run produced evidence) — so a never-baselined lane cannot pass by emitting unreviewed evidence.

Lane jobs deliberately upload evidence and diagnostics even when the lane exits non-zero. A failed lane artifact includes `lane-exit-code.txt` and `compose.log`; the matrix job itself exits successfully so the `baseline-diff` job can always download available artifacts, regenerate the gap report, and make the final pass/fail decision from the baseline contract. Manual `workflow_dispatch` runs can select a lane subset with `lanes=...`; the workflow maps those lane names to `client_lane` values and passes `--client-lanes` so strict mode only evaluates the requested subset.

Use `scripts/client-compat/refresh-baselines.sh` to bump baselines after an intentional behavior change; the cadence is scheduled quarterly via `/schedule`. Placeholder skip baselines are not acceptable substitutes — commit real envelopes from a refresh run instead.

## Integration Mapping

This section describes how each evidence source will map to the evidence envelope. The repo currently enforces `[Protocol]`, `[Operation]`, and `[Endpoint]` attributes on integration tests (see `TestAttributeEnforcementTests`). CERT-ID-specific markers described below are proposed conventions to be added in a follow-up implementation ticket.

| Source | Lane | How It Feeds the Envelope |
|---|---|---|
| Vitest JSON reporter | JS | Automated: map `describe`/`it` blocks to CERT-\* IDs via test name convention |
| Playwright browser test | JS (MVT) | Automated: headless Chromium renders OGC Tiles via OpenLayers, records CERT-RNDR-01 and JS-EXT-02 |
| OpenLayers OGC API Maps test | JS (OGC Maps) | Automated: `tests/js/openlayers/maps/` configures `ol/source/ImageStatic` against `/ogc/maps`, records discovery/error handling, and records render as `skip` when the live collection has no raster fixture |
| Playwright cert reporter | JS (Esri Leaflet) | Automated: custom Playwright reporter extracts `[CERT-*]` and `[EL-EXT-*]` annotations from test titles and writes per-protocol envelopes for `featureserver` and `mapserver`. Uses `client_lane: "js"`. See [Esri Leaflet evidence note](#esri-leaflet-evidence-note) below. |
| pytest markers | CLI | Planned: add `@pytest.mark.cert("CERT-CONN-01")` markers and map to result entries |
| xUnit attributes | CLI | Live (OData lane): each test in `tests/dotnet/Honua.Server.Tests/Features/Protocols/OData/ODataClientCertificationTests.cs` carries `[Trait("CertId", "CERT-…")]` alongside `[Protocol]`, and the class fixture flushes a `<run-id>-cli-odata.cert.json` envelope to `tests/TestResults/`. Filter the lane in isolation with `dotnet test --filter "CertId~CERT-"` |
| CITE testng-results XML | OGC conformance | Reference: link via `cite_results` field; CITE tests are protocol-scoped, not client-scoped |
| Manual runbook | Desktop, BI | Manual: operator fills a JSON template or markdown checklist, converted to `.cert.json` |
| Playwright MapLibre suite | JS (MVT) | Automated: `tests/js-browser/maplibre/` renders MapLibre GL JS against live TileJSON/MVT endpoints and emits a `<run-id>-js-mvt.cert.json` envelope via the custom reporter. See [MapLibre MVT automated workflow](#maplibre-mvt-automated-workflow) below. A manual fallback remains documented for ad-hoc visual verification. |
| Playwright CesiumJS suite | JS — Cesium (`js-cesium`) | Automated: `tests/js-browser/cesium/` exercises CesiumJS imagery providers (WMS, WMTS, OGC API Tiles, OGC API Maps) under `docker/client-compat/cesium/` in `client-interop-nightly.yml`; emits one envelope per protocol (`<run-id>-js-cesium-{wms,wmts,ogc-tiles,ogc-maps}.cert.json`). Vector-feature CERT-\* IDs and the visual / style slice IDs are recorded as `not-applicable` because Cesium imagery providers consume server-rendered raster output. |
| ArcGIS Pro REST stub | `arcgis-stub` | Automated stub: `docker/client-compat/arcgis-stub/stub_runner.py` issues both the FeatureServer and MapServer REST sequences Pro itself emits and writes one envelope per protocol (`*-arcgis-stub-featureserver.cert.json` and `*-arcgis-stub-mapserver.cert.json`). The FeatureServer-applicable render IDs `CERT-RNDR-01`, `CERT-RNDR-02`, and the visual / style slice IDs `CERT-RNDR-{SYM,LIN,FIL,LBL,URL}-01` are recorded as `skip` with note `pending: licensed-arcgis-runner` until a licensed Windows runner is provisioned. `CERT-RNDR-SPR-01` is MVT-only per the matrix and is emitted as `not-applicable` for this lane. |
| Licensed ArcGIS Pro desktop runner | `desktop-arcgis` | Manual/scheduled: `.github/workflows/arcgis-pro-desktop-evidence.yml` runs only on an explicitly enabled self-hosted Windows ArcGIS Pro runner and invokes `scripts/client-compat/arcgis-pro/run-arcgis-pro-evidence.py`. The runner emits `*-desktop-arcgis-featureserver.cert.json` and `*-desktop-arcgis-mapserver.cert.json`, captures logs/screenshots/project artifacts, and keeps this evidence distinct from the REST-only `arcgis-stub` lane. Ordinary PR gates validate only the fixture/envelope contract; they do not require ArcGIS Pro. See [Licensed ArcGIS Pro Desktop Evidence](ARCGIS_PRO_LICENSED_EVIDENCE.md). |
| GDAL/OGR pytest suite | CLI (`cli` via converter) | Automated: `tests/python/gdal_ogr/conftest.py:EvidenceCollector` writes `gdal-ogr-results.json`; the `gdal` lane runner invokes `scripts/client-compat/convert-gdal-results.py` to emit one cert envelope per protocol (`<run-id>-cli-gdal-ogc-features.cert.json`, `<run-id>-cli-gdal-wfs.cert.json`). The converter maps the GDAL category labels many-to-one onto CERT-* IDs with worst-status aggregation (`fail > pass > skip > not-applicable`): `discovery`/`feature_count` → CERT-DISC-01, `schema_introspection` → CERT-SCHM-01, `feature_read`/`read` → CERT-CONN-01, `attribute_query`/`spatial_query`/`query` → CERT-QFLT-01. The `export_*` categories are intentionally unmapped per the matrix CLI/SDK lane row (which excludes CERT-RNDR), so CERT-RNDR-01 and CERT-RNDR-02 are recorded as `not-applicable` rather than certified by an ogr2ogr export. Unknown labels surface as `::warning::` so test-side additions stay visible. |

### Manual Lane Workflow

For desktop, BI, and JS/MVT lanes where automation is not available:

1. Copy the evidence template below.
2. Fill `status` for each test case during the smoke run.
3. Add `notes` for any failures or caveats.
4. Save as `<run-id>-<client-lane>-<protocol>.cert.json`.

### Evidence Template (Manual Lanes)

```json
{
  "schema_version": "1.0",
  "run_id": "",
  "run_date": "",
  "server_version": "",
  "client_lane": "",
  "client_version": "",
  "protocol": "",
  "environment": "",
  "results": [
    { "test_case_id": "CERT-CONN-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-CONN-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-AUTH-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-AUTH-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-DISC-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-DISC-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-SCHM-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-SCHM-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-QFLT-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-QFLT-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-PAGE-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-PAGE-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-GEOM-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-GEOM-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-ERRH-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-ERRH-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-SYM-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-LIN-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-FIL-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-LBL-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-SPR-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" },
    { "test_case_id": "CERT-RNDR-URL-01", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" }
  ],
  "summary": {
    "total": 24,
    "passed": 0,
    "failed": 0,
    "skipped": 0,
    "not_applicable": 0
  },
  "cite_results": null,
  "extensions": []
}
```

The visual / style certification slice (ticket [`#478`](https://github.com/honua-io/honua-server/issues/478)) adds the six `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` IDs to the common core. They are append-only — the original 18 IDs are unchanged. See [`visual-style-certification-slice.md`](visual-style-certification-slice.md) for the per-scenario fixtures, expected colors, and lane substantiation. Lanes that do not exercise a slice category emit `skip` (or `not-applicable` for protocols where the category does not apply) so the gap is visible in the rollup.

### Esri Leaflet Evidence Note

The Esri Leaflet Playwright suite (`tests/js-browser/`) emits `client_lane: "js"` evidence envelopes — the same lane value as the Vitest JS suite. This is intentional: Esri Leaflet is a sub-lane of the JS lane in the [Certification Matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md#esri-leaflet-browser-sub-lane), not a separate client tool.

The reporter resolves `client_version` from the installed `esri-leaflet` package in `node_modules` and falls back to the semver range in `tests/js-browser/package.json` only when dependencies have not yet been installed.

Evidence files are written to `tests/js-browser/evidence/` during test runs (not to the curated `docs/gis/certification-evidence/` directory). The Playwright cert reporter produces one envelope per protocol exercised:

- `<run-id>-js-featureserver.cert.json` — FeatureServer common-core results + EL-EXT-\* extensions
- `<run-id>-js-mapserver.cert.json` — MapServer common-core results (CERT-QFLT/PAGE/GEOM/ERRH-02 recorded as `not-applicable`) + EL-EXT-02/EL-EXT-04 extensions

The reporter seeds the full 24 common-core CERT-\* IDs (the original 18 base IDs plus the six visual / style slice IDs `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` introduced by ticket [`#478`](https://github.com/honua-io/honua-server/issues/478)) into each envelope. FeatureServer cases that the browser suite does not currently exercise are recorded as `skip`. On the `mapserver` envelope the six slice IDs are additionally recorded as `not-applicable` because drawingInfo per-category style assertions live on FeatureServer, not the MapServer export endpoint.

The reporter skips evidence emission when the run is interrupted, timed out, or when no tests passed or failed (setup abort guard).

To distinguish Esri Leaflet evidence from Vitest JS evidence in a shared evidence directory, check for the presence of `EL-EXT-*` entries in the `extensions` array. A future follow-on may introduce a dedicated `js-esri-leaflet` client lane value if disambiguation at the envelope level becomes necessary.

### MapLibre MVT Automated Workflow

The MapLibre GL JS MVT lane is certified by the Playwright + Chromium suite in `tests/js-browser/maplibre/` (ticket [`#464`](https://github.com/honua-io/honua-server/issues/464)), driven by `playwright.maplibre.config.ts` and the `maplibre/support/cert-reporter.ts` custom reporter. The `maplibre-compat` CI job runs `npm run test:maplibre` against a live Honua Server seeded with `tests/seed/browser-compat.yaml` and uploads the Playwright report plus the generated `<run-id>-js-mvt.cert.json` envelope as merge-blocking artifacts.

The reporter seeds the full 24-ID common-core matrix into every envelope it emits:

- Tests substantiating a CERT ID (`CERT-RNDR-01`, currently covered by `style-loading.spec.ts`, `layer-visibility.spec.ts`, and `feature-query.spec.ts`) are recorded at their test-case outcome using worst-status aggregation (fail > pass > skip), so a later passing test cannot mask an earlier failure when the same CERT ID is attached to multiple specs.
- `CERT-CONN-01`, `CERT-CONN-02`, `CERT-AUTH-01`, `CERT-AUTH-02`, and `CERT-ERRH-01` are recorded as `skip` with the note `"Covered by JS/featureserver automated tests."` — they apply to MVT but are not exercisable in the MapLibre rendering path.
- The six visual / style slice IDs (`CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01`) are recorded as `skip` with a `pending-fixture` note until the MapLibre MVT lane substantiates them. Tracked in [`visual-style-certification-slice.md`](visual-style-certification-slice.md).
- All remaining common-core IDs (DISC, SCHM, QFLT, PAGE, GEOM, ERRH-02, RNDR-02) are recorded as `not-applicable` because they do not list MVT in their protocol column.
- `JS-EXT-01` (PBF/MVT decode fidelity) and `JS-EXT-02` (tile load pipeline) are recorded in the `extensions` array from `tile-rendering.spec.ts`.

Envelopes are written to `tests/js-browser/test-results/<run-id>-js-mvt.cert.json`. Runbook and prerequisite details live in [`docs/contributor/testing-maplibre-browser.md`](../contributor/testing-maplibre-browser.md).

### MapLibre MVT Manual Workflow

This manual workflow predates the automated suite above and remains as a fallback for ad-hoc visual verification (e.g. JS-EXT-01 PBF decode inspection beyond pixel-diff assertions) when the `maplibre-compat` CI job is not available.

1. Copy the evidence template above.
2. Set `client_lane` to `"js"` and `protocol` to `"mvt"`.
3. Set `client_version` to the MapLibre GL JS library version (e.g., `"4.7.1"`).
4. Open a MapLibre GL JS map pointed at the server's TileJSON/MVT endpoint.
5. For common-core CERT-\* results:
   - CERT-RNDR-01 (map renders without client error): record normally (`pass`/`fail`). CERT-RNDR-02 may also apply if the page supports tile refresh.
   - CERT-CONN-01, CERT-CONN-02, CERT-AUTH-01, CERT-AUTH-02, CERT-ERRH-01: these apply to MVT per the matrix ("All" protocols) but are not exercisable in a browser visual workflow. Record as `skip` with notes: `"Covered by JS/featureserver automated tests"`.
   - All remaining IDs (DISC, SCHM, QFLT, PAGE, GEOM, ERRH-02, RNDR-02): record as `not-applicable` — these do not list MVT in their protocol column.
6. For JS lane extensions: record JS-EXT-01 (PBF/MVT decode fidelity) and JS-EXT-02 (tile load pipeline) in the `extensions` array.
7. Save as `<run-id>-js-mvt.cert.json`.

## Evidence Version

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-03-16 | Initial evidence envelope specification |
| 1.0.1 | 2026-03-16 | Relabel example directory as common-core; add MapLibre MVT manual workflow |
| 1.0.2 | 2026-03-16 | Add `measured_count` and `measured_delta` fields; make nullable types explicit; add nullable field convention note |
| 1.0.3 | 2026-03-16 | Clarify `measured_delta` unit convention and link to matrix geometry tolerance thresholds |
| 1.0.4 | 2026-03-16 | Add `admin-api` protocol; add `measured_count`/`measured_delta` to extensions; fix pytest lane mapping; align examples with current JS scope |
| 1.0.5 | 2026-03-17 | Add `cli-ogc-features.cert.json` to example directory; add CLI lane coverage note |
| 1.0.6 | 2026-03-18 | Fix MVT workflow status semantics: use `skip` for "All"-protocol tests not exercised in visual workflow, `not-applicable` only for tests that don't list MVT |
| 1.0.7 | 2026-03-31 | Document the `windows-client-compat-nightly.yml` smoke-evidence artifact contract and clarify that it is upstream of final `.cert.json` envelopes |
| 1.0.8 | 2026-04-02 | Add `ci-desktop` and `ci-bi` client lane values for automated CI certification evidence; document `certification/` output layout |
| 1.0.9 | 2026-04-03 | Add `wfs` to allowed protocol values; add `desktop-qgis-wfs.cert.json` to examples; document PyQGIS nightly evidence output |
| 1.0.10 | 2026-04-03 | Add Esri Leaflet Playwright reporter to integration mapping; document evidence output path and disambiguation note |
| 1.0.11 | 2026-04-03 | Clarify Esri Leaflet `client_version` resolution and that unexercised CERT-\* IDs are emitted as `skip` |
| 1.0.12 | 2026-04-06 | Mark xUnit `[Trait("CertId", …)]` mapping as live for the CLI/OData lane; add `cli-odata.cert.json` example produced by `ODataClientCertificationTests` |
| 1.0.13 | 2026-04-06 | Document the visual / style certification slice append-only IDs (`CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01`) and update the example envelope template to the new 24-case core total |
| 1.0.14 | 2026-04-07 | Update the Esri Leaflet evidence note to reference the 24-case common-core total and document slice-ID `not-applicable` on the mapserver envelope |
| 1.0.15 | 2026-04-08 | Add MapLibre MVT automated workflow section for the Playwright suite landed by ticket `#464`; retarget the version-matrix footnote and integration-mapping row at the new anchor |
| 1.0.16 | 2026-04-25 | Add OGC API Maps OpenLayers evidence envelope and MapLibre image-source smoke coverage note |
| 1.0.17 | 2026-04-26 | Add `js-cesium` and `arcgis-stub` `client_lane` values, `ogc-maps` and `ogc-tiles` `protocol` values, the Real-Client Interop Matrix Workflow Output section, and the Cesium / ArcGIS-stub / GDAL-converter integration mapping rows (#806) |
| 1.0.18 | 2026-04-26 | Tighten `client-interop-nightly` strict-mode gate: `pass`→`skip` and `pass`→`not-applicable` now count as regressions; add `expected-pairs.json` manifest gate; new `fail` in unbaselined test cases now blocks the gate (#806) |
| 1.0.19 | 2026-04-26 | Close two skip-baseline loopholes: `skip`→`fail` and `not-applicable`→`fail` are now classified as `new-fail` (placeholder skip baselines can no longer hide current failures); strict mode also fails when any `expected-pairs.json` entry has no committed baseline at all (so a never-baselined lane cannot pass by emitting unreviewed evidence). Drop the placeholder Cesium WMS baseline that triggered the original loophole (#806) |
| 1.0.20 | 2026-04-26 | `diff-baselines.py` accepts `--client-lanes` to scope strict mode to a workflow_dispatch lane subset; the `prepare` job emits a `client_lanes` filter that mirrors the matrix selection, so a manual subset dispatch no longer fails strict against the full-matrix manifest. GDAL converter recognises the actual category labels emitted by `tests/python/gdal_ogr/test_*.py` (`feature_read`, `attribute_query`, `spatial_query`, `export_geojson`/`export_gpkg`/`export_csv`, `schema_introspection`, `feature_count`) and aggregates many-to-one statuses with `fail > pass > skip > not-applicable`; previously the converter dropped passed evidence as `skip`. `scripts/client-compat/refresh-baselines.sh` iterates lanes sequentially with `--exit-code-from <lane>` instead of `--profile matrix --abort-on-container-exit` so partial baselines no longer slip through when the first lane completes (#806) |
| 1.0.21 | 2026-04-26 | Align GDAL converter with the matrix CLI/SDK lane contract (matrix line 99 — "All CERT-\* except CERT-RNDR"): `export_geojson`/`export_gpkg`/`export_csv`/`export` categories are no longer mapped to CERT-RNDR-01 (the raw `gdal-ogr-results.json` still records their pass/fail); CERT-RNDR-01 / CERT-RNDR-02 / visual-style slice IDs now appear as `not-applicable` for the GDAL lane. `schema_introspection` now maps to CERT-SCHM-01 (the schema-coverage ID) instead of CERT-DISC-01. `diff-baselines.py write_gap_report` no longer prints the "No deviations from baseline ✅" success section when expected-pair gap sections were also written, so a strict-mode failure for an absent or never-baselined expected pair is no longer contradicted by a green checkmark in the same gap report (#806) |
| 1.0.22 | 2026-04-26 | Stabilise the docker/client-compat `gdal` and `pyqgis` lanes against the read-only `tests/` bind mount. The GDAL lane now honours `HONUA_BASE_URL` to skip the local `honua_server`/`postgis` fixture chain (which cannot start inside the read-only image), targets the seeded `test_service`/layer 0 pair via `HONUA_GDAL_SERVICE_ID` / `HONUA_GDAL_COLLECTION_ID`, and writes its raw `gdal-ogr-results.json` to `HONUA_GDAL_RESULTS_PATH` (set to `/output/gdal-ogr-results.json` by `docker/client-compat/gdal/run.sh`). The PyQGIS lane writes its `.cert.json` envelopes to `HONUA_PYQGIS_OUTPUT_DIR` (set to `/output` by `docker/client-compat/pyqgis/run.sh`). The seed-data note now records that both lanes share `test_service`/layer 0; behaviour change is testing-only and the envelope schema is unchanged (#806) |
| 1.0.23 | 2026-04-26 | Narrow the `arcgis-stub` lane's render-coverage wording in the Real-Client Interop Matrix Workflow Output bullet, the Integration Mapping row, and `docs/gis/gap-report.md` to match the actual envelope: the FeatureServer-applicable render IDs (`CERT-RNDR-01`, `CERT-RNDR-02`, `CERT-RNDR-{SYM,LIN,FIL,LBL,URL}-01`) are recorded as `skip` with `pending: licensed-arcgis-runner`, while `CERT-RNDR-SPR-01` is MVT-only per the matrix and is emitted as `not-applicable`. No code change — `docker/client-compat/arcgis-stub/stub_runner.py` already emits this contract; only the docs were over-broad (#806) |
| 1.0.24 | 2026-05-07 | Record the restored #938 release-evidence contract for `client-interop-nightly.yml`: the full scheduled matrix requires 16 current envelopes with committed baselines, the compose runtime includes Redis and split HTTP/gRPC ports, lane failures upload `lane-exit-code.txt` and `compose.log`, and manual dispatch subsets are scoped by `--client-lanes`. |
| 1.0.25 | 2026-05-18 | Add the licensed ArcGIS Pro desktop evidence scaffold for `client_lane: "desktop-arcgis"`: manual/scheduled self-hosted Windows workflow, ArcPy runner, artifact/redaction guardrails, and fixture-only contract tests. This is distinct from `arcgis-stub` and does not require ArcGIS Pro in ordinary PR gates (#1019). |
