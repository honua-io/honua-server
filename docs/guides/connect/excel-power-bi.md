# Connect Excel and Power BI to Honua

Pull Honua feature data into Excel or Power BI through the OData v4 feed at `/odata`, with server-side filtering — including spatial filters — and one-click refresh.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)).

The feed exposes two entity sets: **Layers** (one row per published layer) and **Features** (feature rows; address a single layer as `Features({layerId})`).

## Steps

### Excel

1. Open Excel and go to **Data → Get Data → From Other Sources → From OData Feed**.
2. Enter the feed URL `http://localhost:8080/odata/` and click **OK**.
3. In the credential dialog pick **Anonymous** (or the credentials your deployment requires) and click **Connect**.
4. In the Navigator, select the **Features** (or **Layers**) entity, then click **Load** — or **Transform Data** to filter columns and rows in Power Query first.
5. To update the worksheet later, use **Data → Refresh All**.

### Power BI Desktop

1. Go to **Home → Get Data → OData feed**.
2. Enter `http://localhost:8080/odata/` and click **OK**, then choose credentials and **Connect**.
3. Select the entity in the Navigator and click **Load** (or **Transform Data**).
4. Refresh with **Home → Refresh**; published reports refresh through the standard Power BI gateway/schedule mechanisms.

### API keys (Power Query)

The built-in OData credential dialog has no header field. If your server requires an `X-API-Key` header, create a blank query and use the Advanced Editor:

```text
let
    Source = OData.Feed(
        "http://localhost:8080/odata/",
        null,
        [Implementation = "2.0", Headers = [#"x-api-key" = "YOUR_KEY"]])
in
    Source
```

### Server-side query options

Power Query folds many transforms to OData automatically; you can also query the feed directly. Supported system options on `Features` include `$filter`, `$select`, `$orderby`, `$top`, `$skip`, and `$count`. Two spatial functions are available in `$filter`: `geo.distance` and `geo.intersects` (the geometry property is named `Geometry`).

Use these relative OData URLs in Power Query or another OData client, substituting the published layer id:

```text
/odata/Features({layerId})?$top=5&$select=ObjectId
/odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.85, -122.5 37.85, -122.5 37.7))')
```

## Verify

Open `http://localhost:8080/odata`, `http://localhost:8080/odata/$metadata`, and `http://localhost:8080/odata/Layers` in a browser. The responses should show the service document, EDM schema, and one entry per published layer, respectively.

In the client, the Navigator should list the entity sets, and the loaded table's column names and types should match `$metadata`.

## Troubleshoot

- **"Unable to connect" / connection refused** — open `http://localhost:8080/healthz/ready` in a browser and confirm the connector URL ends in `/odata/`. See [troubleshooting](../deploy/troubleshooting.md).
- **401 Unauthorized** — the deployment requires auth; use the Power Query `Headers` snippet above for API keys, or Basic credentials in the credential dialog.
- **Empty Navigator** — no layers are published; `http://localhost:8080/odata/Layers` should return at least one entry. You can also confirm the catalog with `honua services` and `honua layers <service-id>`.
- **Geometry column unusable in Excel** — Excel has no spatial type; `$select` it away in Power Query, or keep it for export-only purposes. Use `geo.intersects` in `$filter` to do the spatial work server-side.
- **Slow loads on large layers** — filter before loading (`$filter`, `$top`) rather than pulling the full table; Power Query's row/column filters fold to the server in most cases.

## Next steps

- [Query features](../query-analyze/query-features.md)
- [Client compatibility matrix](../../reference/compatibility/clients.md)
- [Protocol overview](../../concepts/protocols.md)
