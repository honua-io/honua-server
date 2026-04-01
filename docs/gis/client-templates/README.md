# Client Template Sources

These sources feed the Windows client compatibility pack and the manual smoke runbook.
Generate native client artifacts (`.aprx`, `.qgz`, `.pbix`, `.xlsx`) from these sources and attach them to release/certification evidence.

Most source files live in this directory. The QGIS project template currently lives in [`docs/user/client-templates/qgis`](../../user/client-templates/qgis/). The `windows-client-compat-nightly.yml` workflow assembles both locations into one canonical pack under:

```text
artifacts/client-compat/<service>-<timestamp>/pack/templates/
```

## Files

- `./.env.example`: placeholder values for endpoint and credential substitution.
- `./arcgis-pro/Honua-Desktop-Smoke.aprx.template.md`: ArcGIS Pro project template instructions.
- `../../user/client-templates/qgis/Honua-Desktop-Smoke.qgs.template`: QGIS project template file with placeholders.
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

Current contract note: `HONUA_COLLECTION_ID` is the numeric OGC `collectionId`, which currently matches the layer id (`0` in the ticket `#320` certification seed).

See [`../CLIENT_TEMPLATE_RUNBOOK.md`](../CLIENT_TEMPLATE_RUNBOOK.md) for full smoke execution steps and the workflow artifact contract.
