# Client Templates and Manual Smoke Runbook

This runbook provides first-party template starters and repeatable manual smoke checks for common desktop and BI clients:
- ArcGIS Pro (`.aprx`)
- QGIS (`.qgz`)
- Power BI Desktop (`.pbix`)
- Excel (`.xlsx`)

Use this runbook when validating a demo or customer environment.

## Template Pack

Template sources live in [`docs/user/client-templates`](client-templates/README.md).

| Client | Template Source | Saved Output Artifact |
|---|---|---|
| ArcGIS Pro | [`arcgis-pro/Honua-Desktop-Smoke.aprx.template.md`](client-templates/arcgis-pro/Honua-Desktop-Smoke.aprx.template.md) | `Honua-Desktop-Smoke.aprx` |
| QGIS | [`qgis/Honua-Desktop-Smoke.qgs.template`](client-templates/qgis/Honua-Desktop-Smoke.qgs.template) | `Honua-Desktop-Smoke.qgz` |
| Power BI Desktop | [`power-bi/Honua-OData-Smoke.pq.template`](client-templates/power-bi/Honua-OData-Smoke.pq.template) and [`power-bi/Honua-OData-Smoke.pbix.template.md`](client-templates/power-bi/Honua-OData-Smoke.pbix.template.md) | `Honua-OData-Smoke.pbix` |
| Excel | [`excel/Honua-OData-Smoke.pq.template`](client-templates/excel/Honua-OData-Smoke.pq.template) and [`excel/Honua-OData-Smoke.xlsx.template.md`](client-templates/excel/Honua-OData-Smoke.xlsx.template.md) | `Honua-OData-Smoke.xlsx` |

The repository keeps source templates and run instructions. Generated binary outputs (`.aprx`, `.qgz`, `.pbix`, `.xlsx`) should be attached to release evidence and `#320` workflow artifacts.

The `windows-client-compat-nightly.yml` workflow assembles these sources into a single reusable artifact pack under:

```text
artifacts/client-compat/<service>-<timestamp>/pack/
```

That pack includes the template sources, the smoke runbook, the evidence schema, and the certification matrix so manual Windows follow-through can start from one canonical layout.

## Environment Substitution

Use these placeholders across templates:

| Variable | Example | Notes |
|---|---|---|
| `HONUA_BASE_URL` | `https://demo.honua.example` | No trailing slash |
| `HONUA_SERVICE_ID` | `utilities` | Service id/name for `/rest/services/{id}` |
| `HONUA_COLLECTION_ID` | `parcels` | OGC API Features collection id |
| `HONUA_ODATA_ENTITY_SET` | `Parcels` | OData entity set name |
| `HONUA_API_KEY` | `demo-key-123` | Leave blank if not using API-key auth |

1. Copy [`docs/user/client-templates/.env.example`](client-templates/.env.example) to `.env` in the same directory.
2. Fill values for your target environment.
3. Substitute placeholders with `envsubst`:

```bash
cd docs/user/client-templates
set -a; source .env; set +a

envsubst < qgis/Honua-Desktop-Smoke.qgs.template > qgis/Honua-Desktop-Smoke.qgs
envsubst < power-bi/Honua-OData-Smoke.pq.template > power-bi/Honua-OData-Smoke.pq
envsubst < excel/Honua-OData-Smoke.pq.template > excel/Honua-OData-Smoke.pq

# Optional pre-packaging for QGIS (QGIS can also save `.qgz` directly from UI)
zip -j qgis/Honua-Desktop-Smoke.qgz qgis/Honua-Desktop-Smoke.qgs
```

If `envsubst` is unavailable, replace placeholder tokens manually in each template file.

4. Save final client-native files (`.aprx`, `.qgz`, `.pbix`, `.xlsx`) after applying each client section below.

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
| `featureserver` | `…/FeatureServer` | All (1–5 + cross-cutting) | All 18 common-core CERT-\* IDs |
| `mapserver` | `…/MapServer` | 1 (connect), 2 (discovery), 4 (render), 5 (refresh), cross-cutting | CERT-CONN, CERT-AUTH, CERT-DISC, CERT-SCHM, CERT-ERRH, CERT-RNDR |

Step 3 (Filter/query) targets the FeatureServer connection. CERT-QFLT, CERT-PAGE, and CERT-GEOM test cases should be recorded as `not-applicable` in the `mapserver` evidence file unless the client also exercises MapServer's layer query endpoint.

See the [Evidence Specification](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) for the envelope format and [naming convention](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md#file-naming-convention).

## QGIS Smoke Checklist

Connection target:
- OGC API Features root: `${HONUA_BASE_URL}/ogc/features`
- Collection for items checks: `${HONUA_BASE_URL}/ogc/features/collections/${HONUA_COLLECTION_ID}/items`

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
- QGIS: feasible with PyQGIS for headless connection/layer load and project save.
- Power BI/Excel: query-refresh automation is feasible, but end-to-end desktop UI automation is brittle and environment-specific.
