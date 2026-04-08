# Client Templates and Manual Smoke Runbook

This runbook provides first-party template starters and repeatable manual smoke checks for common desktop and BI clients:
- ArcGIS Pro (`.aprx`)
- QGIS (`.qgz`)
- Power BI Desktop (`.pbix`)
- Excel (`.xlsx`)

Use this runbook when validating a demo or customer environment.

## Template Pack

Template sources are split between [`docs/gis/client-templates`](client-templates/README.md) and [`docs/user/client-templates/qgis`](../user/client-templates/qgis/).

| Client | Template Source | Saved Output Artifact |
|---|---|---|
| ArcGIS Pro | [`arcgis-pro/Honua-Desktop-Smoke.aprx.template.md`](client-templates/arcgis-pro/Honua-Desktop-Smoke.aprx.template.md) | `Honua-Desktop-Smoke.aprx` |
| QGIS | [`../user/client-templates/qgis/Honua-Desktop-Smoke.qgs.template`](../user/client-templates/qgis/Honua-Desktop-Smoke.qgs.template) | `Honua-Desktop-Smoke.qgz` |
| Power BI Desktop | [`power-bi/Honua-OData-Smoke.pq.template`](client-templates/power-bi/Honua-OData-Smoke.pq.template) and [`power-bi/Honua-OData-Smoke.pbix.template.md`](client-templates/power-bi/Honua-OData-Smoke.pbix.template.md) | `Honua-OData-Smoke.pbix` |
| Excel | [`excel/Honua-OData-Smoke.pq.template`](client-templates/excel/Honua-OData-Smoke.pq.template) and [`excel/Honua-OData-Smoke.xlsx.template.md`](client-templates/excel/Honua-OData-Smoke.xlsx.template.md) | `Honua-OData-Smoke.xlsx` |

The repository keeps source templates and run instructions. Generated binary outputs (`.aprx`, `.qgz`, `.pbix`, `.xlsx`) are not checked into the repo; attach them to release evidence or downstream certification records created from the `#320` workflow pack.

The `windows-client-compat-nightly.yml` workflow assembles these sources into a single reusable artifact pack under:

```text
artifacts/client-compat/<service>-<timestamp>/pack/
```

That pack includes the template sources, the smoke runbook, the evidence schema, the certification matrix, and the version ledger so manual Windows follow-through can start from one canonical layout.

The pack is the preferred operator entry point because it normalizes the current split repo layout into one directory tree:

```text
pack/
├── templates/
│   ├── .env.example
│   ├── desktop/arcgis-pro/
│   ├── desktop/qgis/
│   └── bi/{power-bi,excel}/
└── runbook/
```

## Workflow Artifact Contract

`windows-client-compat-nightly.yml` uploads a deterministic smoke-evidence artifact rooted at:

```text
artifacts/client-compat/<service>-<timestamp>/
```

The uploaded contract is:

| Path | Purpose |
|---|---|
| `README.md` | Human-readable overview of the artifact root, including the lane directories and metadata contents |
| `overall-summary.json` / `overall-summary.md` | Aggregate pass/fail for the `desktop` and `bi` lanes |
| `lanes/<lane>/checks.tsv` | Raw smoke-check rows used to build the lane summaries |
| `lanes/<lane>/lane-summary.json` / `lane-summary.md` | Per-lane check results with HTTP status, transcript path, and optional failure note |
| `lanes/<lane>/transcripts/<check-id>.txt` | Full request/response transcript captured by the smoke script |
| `certification/<timestamp>-<client_lane>-<protocol>.cert.json` | Per-protocol `.cert.json` envelope (full profile only); see [Evidence Specification](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) |
| `metadata/workflow-context.json` | Base URL, service/layer ids, seed source, timestamp, workflow metadata |
| `metadata/<seed-file>.sql` | Exact versioned SQL snapshot applied for the run |
| `server/server.log` | Honua server stdout/stderr captured during the run |
| `pack/README.md` | Human-readable guide to the normalized pack layout and source provenance |
| `pack/` | Reusable templates plus the runbook, matrix, evidence spec, and version ledger for manual follow-through |

Since ticket #415, the workflow defaults to the `full` profile and emits per-protocol `.cert.json` certification envelopes under a `certification/` subdirectory covering 18 CERT-\* test cases across 4 protocol lanes. Use `--profile smoke` for the original 11-check MVP subset.

Automated `.cert.json` envelopes use the `ci-desktop` and `ci-bi` client lane values. Manual lanes (desktop-arcgis, desktop-qgis, bi-powerbi, bi-excel) still require operator-produced evidence per the steps below.

The full profile covers:

| Protocol lane | Client lane | CERT-\* scope |
|---|---|---|
| FeatureServer | `ci-desktop` | 18 test cases (CERT-CONN through CERT-RNDR); CERT-CONN-02, AUTH-01/02, RNDR-01/02 skipped with reason |
| OGC API Features | `ci-desktop` | 18 test cases; same skip categories |
| MapServer | `ci-desktop` | 18 test cases; QFLT/PAGE/GEOM/ERRH-02 not-applicable for rendering-only lane |
| OData | `ci-bi` | 18 test cases; GEOM/SCHM-02/QFLT-02 not-applicable for OData-only lane |

The smoke profile retains the original narrow check set:

| Lane | Automated checks |
|---|---|
| `desktop` | `featureserver-service-metadata`, `featureserver-layer-metadata`, `featureserver-query-active-subset`, `mapserver-service-metadata`, `ogc-collections`, `ogc-items-first-page` |
| `bi` | `odata-service-document`, `odata-metadata`, `odata-layer-filter`, `odata-features-first-page`, `odata-features-second-page` |

## Environment Substitution

Use these placeholders across templates:

| Variable | Example | Notes |
|---|---|---|
| `HONUA_BASE_URL` | `https://demo.honua.example` | No trailing slash |
| `HONUA_SERVICE_ID` | `test_service` | Service id/name for `/rest/services/{id}`. The ticket `#320` certification seed uses `test_service`. |
| `HONUA_COLLECTION_ID` | `0` | OGC API Features collection id. Current server contract uses the numeric layer id as the collection id. |
| `HONUA_ODATA_ENTITY_SET` | `Features` | OData entity set name. The ticket `#320` certification pack defaults to `Features`. |
| `HONUA_API_KEY` | `demo-key-123` | Leave blank if not using API-key auth |

1. Prefer the workflow artifact pack when it is available. Copy `artifacts/client-compat/<service>-<timestamp>/pack/templates/.env.example` to `.env` in that same `templates/` directory.
2. If you are working directly from the repo instead of the artifact pack, copy [`docs/gis/client-templates/.env.example`](client-templates/.env.example) to a scratch `templates/` directory and also copy the QGIS template from [`docs/user/client-templates/qgis`](../user/client-templates/qgis/).
3. Fill values for your target environment.
4. Substitute placeholders with `envsubst` from the canonical pack layout:

```bash
cd artifacts/client-compat/<service>-<timestamp>/pack/templates
set -a; source .env; set +a

envsubst < desktop/qgis/Honua-Desktop-Smoke.qgs.template > desktop/qgis/Honua-Desktop-Smoke.qgs
envsubst < bi/power-bi/Honua-OData-Smoke.pq.template > bi/power-bi/Honua-OData-Smoke.pq
envsubst < bi/excel/Honua-OData-Smoke.pq.template > bi/excel/Honua-OData-Smoke.pq

# Optional pre-packaging for QGIS (QGIS can also save `.qgz` directly from UI)
zip -j desktop/qgis/Honua-Desktop-Smoke.qgz desktop/qgis/Honua-Desktop-Smoke.qgs
```

If you are generating files directly from repo sources, use the same directory structure as the pack (`desktop/qgis`, `bi/power-bi`, `bi/excel`) so the commands above still apply after you copy the sources into place.

If `envsubst` is unavailable, replace placeholder tokens manually in each template file.

5. Save final client-native files (`.aprx`, `.qgz`, `.pbix`, `.xlsx`) after applying each client section below.

## ArcGIS Pro Smoke Checklist

Connection target:
- Feature data: `${HONUA_BASE_URL}/rest/services/${HONUA_SERVICE_ID}/FeatureServer`
- Map rendering: `${HONUA_BASE_URL}/rest/services/${HONUA_SERVICE_ID}/MapServer`

Checklist:
- [ ] Connect/auth: Add the FeatureServer and MapServer URLs in ArcGIS Pro, then authenticate with API key/OIDC/Basic as required. Verify that an unauthenticated request is rejected.
- [ ] Discovery: Confirm layers and tables appear in Catalog and can be added to a map. Open layer properties and verify field names, types, and geometry type are reported correctly.
- [ ] Filter/query: Apply a layer definition query (for example `OBJECTID > 0`) and verify result count changes. Apply a spatial filter (e.g., Select by Location or set a map extent and enable spatial filtering) and verify a spatial subset is returned. Request a limited page size and confirm pagination returns different features on the second page.
- [ ] Render/table load: Confirm map draw and attribute table open without errors. Inspect a returned coordinate to verify it falls within expected bounds, and confirm the layer's spatial reference matches the requested CRS.
- [ ] Refresh/reload/export: Refresh the layer and export a filtered subset to file geodatabase or CSV.
- [ ] Error handling: Navigate to an invalid endpoint URL and confirm a structured error response. Submit a malformed query expression and verify a structured error is returned.

Save output artifact:
- [ ] Save project as `Honua-Desktop-Smoke.aprx`.

### Per-Protocol Evidence Files

ArcGIS Pro exercises two protocols. Produce one `.cert.json` evidence file for each:

| Evidence file protocol | Connection used | Applicable smoke steps | CERT-\* scope |
|---|---|---|---|
| `featureserver` | `…/FeatureServer` | All (1–5 + cross-cutting) | All 24 common-core CERT-\* IDs (18 base + the six `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` visual / style slice IDs from ticket `#478`). The slice IDs are substantiated by the automated OpenLayers, Esri Leaflet, and PyQGIS lanes; record them as `skip` with a `pending-slice-substantiation-in-another-lane` note in the manual ArcGIS Pro envelope unless the operator exercises per-category drawingInfo styling directly. See [`visual-style-certification-slice.md`](visual-style-certification-slice.md). |
| `mapserver` | `…/MapServer` | 1 (connect), 2 (discovery), 4 (render), 5 (refresh), cross-cutting | CERT-CONN, CERT-AUTH, CERT-DISC, CERT-SCHM, CERT-ERRH, CERT-RNDR. The six `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` slice IDs are `not-applicable` on `mapserver` evidence because drawingInfo per-category style assertions live on FeatureServer, not the MapServer export endpoint. |

Step 3 (Filter/query) targets the FeatureServer connection. CERT-QFLT, CERT-PAGE, CERT-GEOM, and CERT-ERRH-02 test cases should be recorded as `not-applicable` in the `mapserver` evidence file unless the client also exercises MapServer's layer query endpoint.

See the [Evidence Specification](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) for the envelope format and [naming convention](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md#file-naming-convention).

## QGIS Smoke Checklist

Connection target:
- OGC API Features root: `${HONUA_BASE_URL}/ogc/features`
- Collection for items checks: `${HONUA_BASE_URL}/ogc/features/collections/${HONUA_COLLECTION_ID}/items`

Current contract note:
- Honua currently uses the numeric layer id as the OGC `collectionId`. The ticket `#320` certification seed uses collection `0`.

Checklist:
- [ ] Connect/auth: Add an OGC API Features connection and authenticate with API key/OIDC/Basic as required. Verify that an unauthenticated request is rejected.
- [ ] Discovery: Confirm collections are listed and `${HONUA_COLLECTION_ID}` loads as a layer. Open layer properties and verify field names, types, and geometry type are reported correctly.
- [ ] Filter/query: Apply a subset string or query builder expression and verify feature count changes. Apply a spatial/bbox filter (e.g., set a bounding box in the query builder or use `bbox` parameter) and verify the returned features are spatially constrained. Request a limited page size and confirm pagination returns different features on the second page.
- [ ] Render/table load: Confirm map draw and attribute table load. Inspect a returned coordinate to verify it falls within expected bounds, and confirm the layer's CRS matches the requested output.
- [ ] Refresh/reload/export: Reload the layer and export a subset to GeoPackage or CSV.
- [ ] Error handling: Navigate to an invalid collection URL and confirm a structured error response. Submit a malformed filter expression and verify a structured error is returned.

Save output artifact:
- [ ] Save project as `Honua-Desktop-Smoke.qgz`.

## Power BI Desktop Smoke Checklist

Connection target:
- OData root: `${HONUA_BASE_URL}/odata/`

Checklist:
- [ ] Connect/auth: Use OData feed with API key header or standard credential prompt. Verify that an unauthenticated request is rejected.
- [ ] Discovery: Confirm `${HONUA_ODATA_ENTITY_SET}` is discoverable in navigator. Verify column names and data types are reported correctly.
- [ ] Filter/query: Apply a filter in Power Query and verify row reduction. Apply a page limit and confirm the returned row count; load a second page and verify different rows appear.
- [ ] Render/table load: Load data model and place at least one map/table visual.
- [ ] Refresh/reload/export: Trigger refresh and export filtered rows to CSV.
- [ ] Error handling: Request a non-existent entity set and confirm a structured error response. Submit a malformed `$filter` expression and verify a structured error is returned.

Save output artifact:
- [ ] Save report as `Honua-OData-Smoke.pbix`.

## Excel Smoke Checklist

Connection target:
- OData root: `${HONUA_BASE_URL}/odata/`

Checklist:
- [ ] Connect/auth: Use OData feed with API key header or standard credential prompt. Verify that an unauthenticated request is rejected.
- [ ] Discovery: Confirm `${HONUA_ODATA_ENTITY_SET}` is discoverable in Power Query. Verify column names and data types are reported correctly.
- [ ] Filter/query: Apply a Power Query filter and verify row reduction. Apply a page limit and confirm the returned row count; load a second page and verify different rows appear.
- [ ] Render/table load: Load query output to worksheet table.
- [ ] Refresh/reload/export: Trigger refresh and export a filtered subset to CSV.
- [ ] Error handling: Request a non-existent entity set and confirm a structured error response. Submit a malformed `$filter` expression and verify a structured error is returned.

Save output artifact:
- [ ] Save workbook as `Honua-OData-Smoke.xlsx`.

## Certification Core Mapping

Each smoke step maps to shared certification test cases from the [Cross-Client Certification Matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md). When executing this runbook, record results for the corresponding CERT-\* IDs.

| Smoke Step | Description | Certification IDs |
|---|---|---|
| 1 | Connect/auth | CERT-CONN-01, CERT-CONN-02, CERT-AUTH-01, CERT-AUTH-02 |
| 2 | Discovery | CERT-DISC-01, CERT-DISC-02, CERT-SCHM-01, CERT-SCHM-02 * |
| 3 | Filter/query | CERT-QFLT-01, CERT-QFLT-02 *, CERT-PAGE-01, CERT-PAGE-02 |
| 4 | Render/table load | CERT-GEOM-01 *, CERT-GEOM-02 *, CERT-RNDR-01 |
| 5 | Refresh/reload/export | CERT-RNDR-02 |
| Cross-cutting | Error handling | CERT-ERRH-01, CERT-ERRH-02 |

\* **BI lanes (Power BI, Excel):** CERT-GEOM-01, CERT-GEOM-02, CERT-SCHM-02, and CERT-QFLT-02 do not apply to OData-only clients (these require geometry-capable protocols). Record as `not-applicable` in the evidence envelope.

Each smoke run should produce one `.cert.json` evidence file per exercised protocol (e.g., ArcGIS Pro produces separate FeatureServer and MapServer files). See the [Evidence Specification](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) for the envelope format and naming convention.

## Tested-Version Evidence

Track and publish tested versions in [`CLIENT_TEMPLATE_VERSION_MATRIX.md`](CLIENT_TEMPLATE_VERSION_MATRIX.md).

For each client entry, include:
- workflow run evidence from [issue `#320`](https://github.com/honua-io/honua-server/issues/320)
- run date
- manual smoke pass/fail status
- caveats/workarounds

## Optional Automation Feasibility

Desktop automation can reduce manual effort but should be treated as non-blocking MVP support work:
- ArcGIS Pro: feasible with ArcPy on licensed Windows runners for scripted layer setup and project save.
- QGIS: **automated via PyQGIS** — the `pyqgis-client-compat-nightly.yml` workflow exercises OGC API Features and WFS programmatically with real QGIS providers, including headless rendering. The automated lane covers the 18 base CERT-\* cases (with documented skips for TLS/auth on the anonymous seed) plus the three substantiated visual / style slice IDs from ticket `#478` (`CERT-RNDR-SYM-01`, `CERT-RNDR-LIN-01`, `CERT-RNDR-FIL-01`) via [`tests/python/pyqgis/test_render_path.py`](../../tests/python/pyqgis/test_render_path.py). The `CERT-RNDR-{LBL,SPR,URL}-01` slice IDs ride the pending-fixture follow-on and are tracked in the slice spec rather than seeded into the PyQGIS envelope. The manual QGIS template flow remains OGC-first unless a later ticket broadens it.
- Power BI/Excel: query-refresh automation is feasible, but end-to-end desktop UI automation is brittle and environment-specific.
