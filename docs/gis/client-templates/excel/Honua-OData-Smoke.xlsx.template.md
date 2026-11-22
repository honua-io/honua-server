# Excel `.xlsx` Template Instructions

This template produces `Honua-OData-Smoke.xlsx`.

## Build Steps

1. Open Excel.
2. Go to `Data -> Get Data -> Launch Power Query Editor`.
3. Create a blank query and paste the generated `Honua-OData-Smoke.pq` script.
4. Confirm the features of layer `${HONUA_ODATA_LAYER_ID}` load in preview. Expand the **More Columns** column to show the layer attributes.
5. Load query output to a worksheet table.
6. Save as `Honua-OData-Smoke.xlsx`.

## Auth Notes

- API key auth: leave the query header block enabled.
- OIDC/Basic auth: remove the header block and use the built-in credential prompt.
