# Power BI Desktop `.pbix` Template Instructions

This template produces `Honua-OData-Smoke.pbix`.

## Build Steps

1. Open Power BI Desktop.
2. Go to `Home -> Transform data -> New Source -> Blank Query`.
3. Open `Advanced Editor` and paste the generated `Honua-OData-Smoke.pq` query.
4. Apply changes and confirm the features of layer `${HONUA_ODATA_LAYER_ID}` load. Expand the **More Columns** column to show the layer attributes.
5. Add one table visual and one map visual.
6. Save as `Honua-OData-Smoke.pbix`.

## Auth Notes

- API key auth: leave the query header block enabled.
- OIDC/Basic auth: remove the header block and use the built-in credential prompt.
