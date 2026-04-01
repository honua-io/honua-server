# Client Template Version Matrix

This matrix is the evidence ledger for template compatibility used by the client template runbook.

Primary sources:
- manual smoke execution from [`CLIENT_TEMPLATE_RUNBOOK.md`](CLIENT_TEMPLATE_RUNBOOK.md)
- workflow smoke artifacts from [issue `#320`](https://github.com/honua-io/honua-server/issues/320)

Update this table on every release candidate.

| Client | Template Source | Protocol | Tested Version | Run Date (UTC) | `#320` Run / Artifact Link | Template Opens | Manual Smoke Result | Evidence File | Notes |
|---|---|---|---|---|---|---|---|---|---|
| ArcGIS Pro | `docs/gis/client-templates/arcgis-pro/Honua-Desktop-Smoke.aprx.template.md` | FeatureServer | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` |
| ArcGIS Pro | `docs/gis/client-templates/arcgis-pro/Honua-Desktop-Smoke.aprx.template.md` | MapServer | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` |
| QGIS | `docs/user/client-templates/qgis/Honua-Desktop-Smoke.qgs.template` | OGC API Features | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` |
| Power BI Desktop | `docs/gis/client-templates/power-bi/Honua-OData-Smoke.pq.template` | OData v4 | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` |
| Excel | `docs/gis/client-templates/excel/Honua-OData-Smoke.pq.template` | OData v4 | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` | `TBD` |
| MapLibre GL JS ‡ | N/A (manual browser verification) | MVT | `TBD` | `TBD` | `TBD` | N/A | `TBD` | `TBD` | `TBD` |

‡ MapLibre GL JS has no project template. Certification is manual (visual browser-based verification) and evidence rolls up under the JS lane (`client_lane: "js"`, `protocol: "mvt"`). See the [Evidence Specification — MapLibre MVT Manual Workflow](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md#maplibre-mvt-manual-workflow) for the procedure.

The **`#320` Run / Artifact Link** column should point to the uploaded smoke-artifact root (`artifacts/client-compat/<service>-<timestamp>/`) or the corresponding workflow artifact URL. The **Evidence File** column links to the final `.cert.json` file stored under `docs/user/certification-evidence/<run-id>/` after manual follow-through. The workflow does not emit those `.cert.json` files automatically. See the [Cross-Client Certification Evidence Specification](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) for the envelope format.
