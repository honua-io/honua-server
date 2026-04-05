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
  "client_lane": "<js|desktop-arcgis|desktop-qgis|cli|bi-powerbi|bi-excel|ci-desktop|ci-bi>",
  "client_version": "<client tool version>",
  "protocol": "<featureserver|mapserver|ogc-features|odata|mvt|wms|wmts|admin-api>",
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
| `client_lane` | string | Yes | One of: `js`, `desktop-arcgis`, `desktop-qgis`, `cli`, `bi-powerbi`, `bi-excel`, `ci-desktop`, `ci-bi` |
| `client_version` | string | Yes | Version of the client tool under test |
| `protocol` | string | Yes | One of: `featureserver`, `mapserver`, `ogc-features`, `odata`, `mvt`, `wms`, `wmts`, `admin-api` |
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
├── 20260316T1430Z-cli-featureserver.cert.json
├── 20260316T1430Z-cli-ogc-features.cert.json
├── 20260316T1430Z-cli-admin-api.cert.json
├── 20260316T1430Z-bi-powerbi-odata.cert.json
└── 20260316T1430Z-bi-excel-odata.cert.json
```

The JS lane covers FeatureServer, OGC API Features, WFS, and OGC Tiles protocols via Vitest, plus CERT-RNDR rendering via Playwright (MVT only). Additional protocols (OData, MapServer) will produce evidence files once automated suites are added.

The CLI lane lists FeatureServer, OGC API Features, and Admin API evidence files. FeatureServer and OGC API Features files will be produced once `@pytest.mark.cert` markers and xUnit `[Trait("CertId", …)]` attributes are added; the Admin API file covers CLI-EXT-01/CLI-EXT-02 extensions.

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

## Integration Mapping

This section describes how each evidence source will map to the evidence envelope. The repo currently enforces `[Protocol]`, `[Operation]`, and `[Endpoint]` attributes on integration tests (see `TestAttributeEnforcementTests`). CERT-ID-specific markers described below are proposed conventions to be added in a follow-up implementation ticket.

| Source | Lane | How It Feeds the Envelope |
|---|---|---|
| Vitest JSON reporter | JS | Automated: map `describe`/`it` blocks to CERT-\* IDs via test name convention |
| Playwright browser test | JS (MVT) | Automated: headless Chromium renders OGC Tiles via OpenLayers, records CERT-RNDR-01 and JS-EXT-02 |
| pytest markers | CLI | Planned: add `@pytest.mark.cert("CERT-CONN-01")` markers and map to result entries |
| xUnit attributes | CLI | Planned: add `[Trait("CertId", "CERT-CONN-01")]` alongside existing `[Protocol]` attributes |
| CITE testng-results XML | OGC conformance | Reference: link via `cite_results` field; CITE tests are protocol-scoped, not client-scoped |
| Manual runbook | Desktop, BI | Manual: operator fills a JSON template or markdown checklist, converted to `.cert.json` |
| Manual browser verification | JS (MVT) | Fallback: operator loads MapLibre GL JS against the server, records remaining manual-only results into a `js`/`mvt` evidence file (see [MapLibre MVT workflow](#maplibre-mvt-manual-workflow) below). CERT-RNDR-01 and JS-EXT-02 are now automated via Playwright |

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
    { "test_case_id": "CERT-RNDR-02", "status": "", "duration_ms": null, "measured_count": null, "measured_delta": null, "notes": "", "evidence_ref": "" }
  ],
  "summary": {
    "total": 18,
    "passed": 0,
    "failed": 0,
    "skipped": 0,
    "not_applicable": 0
  },
  "cite_results": null,
  "extensions": []
}
```

### MapLibre MVT Manual Workflow

MapLibre GL JS MVT certification is partially automated. CERT-RNDR-01 and JS-EXT-02 are covered by the Playwright headless browser test (`render.spec.ts`), and MVT tile metadata/discovery tests run via Vitest. This manual workflow remains useful for JS-EXT-01 (PBF decode fidelity) and any visual verification beyond pixel-diff assertions.

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
