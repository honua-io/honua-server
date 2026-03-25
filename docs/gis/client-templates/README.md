# Client Template Pack

This directory contains parameterized template starters for desktop and BI smoke validation.
Generate native client artifacts (`.aprx`, `.qgz`, `.pbix`, `.xlsx`) from these sources and attach them to release/certification evidence.

## Files

- `./.env.example`: placeholder values for endpoint and credential substitution.
- `./arcgis-pro/Honua-Desktop-Smoke.aprx.template.md`: ArcGIS Pro project template instructions.
- `./qgis/Honua-Desktop-Smoke.qgs.template`: QGIS project template file with placeholders.
- `./power-bi/Honua-OData-Smoke.pq.template`: Power Query script template for Power BI.
- `./power-bi/Honua-OData-Smoke.pbix.template.md`: Power BI save/package instructions.
- `./excel/Honua-OData-Smoke.pq.template`: Power Query script template for Excel.
- `./excel/Honua-OData-Smoke.xlsx.template.md`: Excel save/package instructions.

## Placeholder Variables

Use these variables across templates:

- `HONUA_BASE_URL`
- `HONUA_SERVICE_ID`
- `HONUA_COLLECTION_ID`
- `HONUA_ODATA_ENTITY_SET`
- `HONUA_API_KEY`

See [`../CLIENT_TEMPLATE_RUNBOOK.md`](../CLIENT_TEMPLATE_RUNBOOK.md) for full smoke execution steps.
