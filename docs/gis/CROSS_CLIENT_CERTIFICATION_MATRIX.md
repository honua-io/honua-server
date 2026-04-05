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

‡ **MVT rendering:** requires a visual web client (e.g., MapLibre GL JS). The manual smoke runbook does not yet include an MVT lane; MVT render certification is tracked via the JS lane extensions (JS-EXT-01, JS-EXT-02) until a dedicated visual lane is added.

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
| OData | OData v4 | `odata` |
| MVT | Vector Tiles (Mapbox Vector Tiles) | `mvt` |
| WMS | WMS 1.3 | `wms` |
| WMTS | WMTS 1.0 | `wmts` |
| Admin API | Control-plane admin endpoints | `admin-api` |
| All | All supported protocols | (all of the above) |

## Client Lane Coverage

Each lane maps its coverage to the common core and declares lane-specific extensions.

| Lane | Automation | Core Coverage | Extensions |
|---|---|---|---|
| **JS** (Vitest) | Automated ‡‡ | All CERT-\* except CERT-RNDR (headless) | JS-EXT-01, JS-EXT-02 |
| **Desktop — ArcGIS Pro** | Manual per runbook | All CERT-\* (visual RNDR) | DSK-EXT-01, DSK-EXT-02 |
| **Desktop — QGIS** | Manual per runbook | All CERT-\* (visual RNDR) | DSK-EXT-01, DSK-EXT-02 |
| **CLI / SDK** (admin SDK, pytest) | Automated | All CERT-\* except CERT-RNDR | CLI-EXT-01, CLI-EXT-02 |
| **BI — Power BI** | Manual per runbook | CERT-CONN, AUTH, DISC, SCHM, QFLT, PAGE, ERRH, RNDR † | BI-EXT-01, BI-EXT-02 |
| **BI — Excel** | Manual per runbook | CERT-CONN, AUTH, DISC, SCHM, QFLT, PAGE, ERRH, RNDR † | BI-EXT-01, BI-EXT-02 |
| **Licensed** (future) | Placeholder | TBD | TBD |

† **BI lanes (OData-only):** CERT-GEOM-01, CERT-GEOM-02, CERT-SCHM-02, and CERT-QFLT-02 do not apply — these require geometry-capable protocols (FS, OGC). Record as `not-applicable` in the evidence envelope.

‡‡ **JS lane current automated scope:** Vitest covers FeatureServer protocol via JavaScript/TypeScript client tests. The Python pytest suite (FeatureServer + OGC API Features) provides independent server-side protocol validation; its results may inform certification confidence but are not JS-lane client evidence. OData and MapServer protocol automation is planned but not yet implemented. Until automated JS suites are added for those protocols, their CERT-\* results require manual evidence or are recorded as `skip` with a note referencing this gap.

### Lane-Specific Extensions

<a id="js-lane"></a>

#### JS Lane

| Extension ID | Description | Protocol(s) | Evidence |
|---|---|---|---|
| JS-EXT-01 | Binary format fidelity (PBF/MVT decode) | MVT | pass/fail |
| JS-EXT-02 | Streaming/MVT tile load pipeline | MVT | pass/fail |

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
