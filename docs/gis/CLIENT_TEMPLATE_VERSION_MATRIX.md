---
type: reference
title: "Client template version matrix"
description: "Which client and template versions each certification template is built and verified against."
---
# Client Template Version Matrix

This matrix is the evidence ledger for template compatibility used by the client template runbook.

Primary sources:
- manual smoke execution from [`CLIENT_TEMPLATE_RUNBOOK.md`](CLIENT_TEMPLATE_RUNBOOK.md)
- workflow smoke artifacts from [issue `#320`](https://github.com/honua-io/honua-server/issues/320)
- historical non-certifying schema examples in [`docs/gis/certification-evidence/20260402T000000Z/`](certification-evidence/20260402T000000Z/README.md) which are excluded from certification collectors

Update this table on every release candidate.

| Client | Template Source | Protocol | Tested Version | Run Date (UTC) | `#320` Run / Artifact Link | Template Opens | Manual Smoke Result | Evidence File | Notes |
|---|---|---|---|---|---|---|---|---|---|
| QGIS | `docs/user/client-templates/qgis/Honua-Desktop-Smoke.qgs.template` | OGC API Features | `3.40.0` | `2026-04-02` | [`20260402T000000Z`](certification-evidence/20260402T000000Z/README.md) | Example only | Not certified | [`20260402T000000Z-desktop-qgis-ogc-features.cert.example.json`](certification-evidence/20260402T000000Z/20260402T000000Z-desktop-qgis-ogc-features.cert.example.json) | Historical schema example with empty evidence references; excluded from certification gates. |
| QGIS (PyQGIS) | N/A (automated nightly) | OGC API Features | *nightly* | *nightly* | PyQGIS nightly CI | N/A | *automated* | [`pyqgis-client-compat-nightly.yml`](../../.github/workflows/pyqgis-client-compat-nightly.yml) (workflow outputs `*-desktop-qgis-ogc-features.cert.json`) | Automated via `pyqgis-client-compat-nightly.yml`; QGIS version captured from runtime. |
| QGIS (PyQGIS) | N/A (automated nightly) | WFS | *nightly* | *nightly* | PyQGIS nightly CI | N/A | *automated* | [`pyqgis-client-compat-nightly.yml`](../../.github/workflows/pyqgis-client-compat-nightly.yml) (workflow outputs `*-desktop-qgis-wfs.cert.json`) | Automated via `pyqgis-client-compat-nightly.yml`; QGIS version captured from runtime. |
| Power BI Desktop | `docs/gis/client-templates/power-bi/Honua-OData-Smoke.pq.template` | OData v4 | `2.142.1053.0` | `2026-04-02` | [`20260402T000000Z`](certification-evidence/20260402T000000Z/README.md) | Example only | Not certified | [`20260402T000000Z-bi-powerbi-odata.cert.example.json`](certification-evidence/20260402T000000Z/20260402T000000Z-bi-powerbi-odata.cert.example.json) | Historical schema example with empty evidence references; excluded from certification gates. |
| Excel | `docs/gis/client-templates/excel/Honua-OData-Smoke.pq.template` | OData v4 | `2402 (Build 17328.20174)` | `2026-04-02` | [`20260402T000000Z`](certification-evidence/20260402T000000Z/README.md) | Example only | Not certified | [`20260402T000000Z-bi-excel-odata.cert.example.json`](certification-evidence/20260402T000000Z/20260402T000000Z-bi-excel-odata.cert.example.json) | Historical schema example with empty evidence references; excluded from certification gates. |
| MapLibre GL JS ‡ | N/A (automated Playwright suite) | MVT | `5.22.0` | `2026-05-07` | `maplibre-compat` CI (`#464`) | N/A | Automated | [`js-mvt.cert.json`](../../tests/baselines/client-compat/openlayers/js-mvt.cert.json) | Committed certifying envelope produced by the MapLibre Playwright lane; CI refreshes evidence at the installed `maplibre-gl` version. |

‡ MapLibre GL JS has no project template. Certification is automated via the `maplibre-compat` CI job (Playwright + Chromium). Evidence rolls up under the JS lane (`client_lane: "js"`, `protocol: "mvt"`). See the [Evidence Specification — MapLibre MVT Automated Workflow](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md#maplibre-mvt-automated-workflow) for details.

The **`#320` Run / Artifact Link** column should point to the uploaded smoke-artifact root (`artifacts/client-compat/<service>-<timestamp>/`) or the corresponding workflow artifact URL. Evidence files should point to the final per-client/protocol `.cert.json` produced during certification follow-through. Since ticket `#415`, the workflow automatically emits `ci-desktop` and `ci-bi` certification envelopes under `certification/` (see [Evidence Specification — Workflow Output](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md#windows-client-compatibility-workflow-output)). Manual client lanes still require operator-produced evidence per the [Runbook](CLIENT_TEMPLATE_RUNBOOK.md).
