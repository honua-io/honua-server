---
type: guide
title: "Connect Excel and Power BI to Honua"
description: "Load and refresh a published layer through its OData v4 feed."
---
# Connect Excel and Power BI to Honua

Load and refresh a published layer through its OData v4 feed.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)).

Find your layer id at `http://localhost:8080/odata/Layers`. Replace `{layerId}` below with that id. Use the scoped feed `http://localhost:8080/odata/Layers({layerId})/Features` to load feature rows.

The root feed exposes **Layers** and **Features**. Loading **Layers** shows catalog rows. The unfiltered **Features** entity requires a `LayerId` filter and cannot be loaded directly from the root Navigator.

Feature attributes are dynamic properties. In `/odata/$metadata` the `Feature` entity is an open type that declares only `LayerId`, `ObjectId` and `Geometry`; every other layer field arrives as a dynamic property in the JSON rows. Power Query builds the table's columns from `$metadata` and shows dynamic properties only when the `MoreColumns` option is on, so the dialog steps below load those three columns. To load the attributes, use the [Power Query source](#power-query-attributes-and-api-keys) below.

## Excel

1. Go to **Data → Get Data → From Other Sources → From OData Feed**.
2. Enter `http://localhost:8080/odata/Layers({layerId})/Features` and click **OK**.
3. Choose **Anonymous** or your deployment's credentials and click **Connect**.
4. Load the resulting feature table, or choose **Transform Data**, then **Close & Load**. If a Navigator appears, select the scoped feature table.
5. Use **Data → Refresh All** to refresh the worksheet.

## Power BI Desktop

1. Go to **Home → Get Data → OData feed**.
2. Enter `http://localhost:8080/odata/Layers({layerId})/Features`, choose credentials, and click **Connect**.
3. Load the resulting feature table, or choose **Transform Data**, then **Close & Apply**. If a Navigator appears, select the scoped feature table.
4. Refresh with **Home → Refresh**. Published reports use Power BI gateway and scheduled refresh settings.

## Power Query: attributes and API keys

For a reproducible source that includes the layer's attributes, create a blank query and paste this into the **Advanced Editor**. Replace `{layerId}` with your published id. `MoreColumns = true` adds a **More Columns** record column that holds the dynamic properties. The built-in credential dialog has no API-key header field, so add the `Headers` option when required:

```text
let
    Source = OData.Feed(
        "http://localhost:8080/odata/Layers({layerId})/Features",
        null,
        [Implementation = "2.0", MoreColumns = true, Headers = [#"x-api-key" = "YOUR_KEY"]])
in
    Source
```

For anonymous deployments, omit `Headers` and keep `[Implementation = "2.0", MoreColumns = true]`.

To turn the attributes into columns, click the expand icon in the **More Columns** header, choose the fields you need, and clear **Use original column name as prefix**. Power Query records this as a `Table.ExpandRecordColumn` step.

## Server-side query options

Power Query can fold transforms to OData. Supported options include `$filter`, `$select`, `$orderby`, `$top`, `$skip`, and `$count`. Spatial functions use the `Geometry` property:

```text
/odata/Layers({layerId})/Features?$top=5&$select=ObjectId
/odata/Layers({layerId})/Features?$filter=geo.intersects(Geometry, geography'SRID=4326;POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.85, -122.5 37.85, -122.5 37.7))')
```

## Verify

Complete **Load** and confirm that the table contains feature rows with `ObjectId`, `LayerId` and at least one of the layer's own attribute columns, then refresh it. If only `LayerId`, `ObjectId` and `Geometry` appear, the source was loaded without `MoreColumns`; use the Power Query source above. Seeing entity sets in the Navigator alone does not verify a successful feature load.

The service document at `/odata`, EDM schema at `/odata/$metadata`, and catalog at `/odata/Layers` can also be inspected in a browser.

## Troubleshoot

- **LayerId filter is required** — use `/odata/Layers({layerId})/Features` as the source instead of selecting the unfiltered root **Features** entity.
- **Connection refused** — check `http://localhost:8080/healthz/ready` and your server address. See [troubleshooting](../deploy/troubleshooting.md).
- **401 Unauthorized** — use the deployment's credentials or the API-key `Headers` snippet above. An anonymous request for a scoped feed returns `401`; a signed-in caller without read access to that layer gets `403`.
- **No layers** — `/odata/Layers` lists only the layers the caller can read, so an anonymous request to a deployment that requires authentication returns an empty catalog, not `401`. Add credentials first; if the list is still empty, publish a layer and confirm its id in `/odata/Layers`.
- **Only `LayerId`, `ObjectId` and `Geometry` columns** — the layer's attributes are dynamic properties; load them with `MoreColumns = true` and expand the **More Columns** record (see the Power Query source above).
- **Geometry column unusable in Excel** — Excel has no spatial type; use `$select` to load only the attributes you need.
- **Slow loads** — filter rows and select columns before loading large layers.

## Next steps

- [Query features](../query-analyze/query-features.md)
- [Client compatibility matrix](../../reference/compatibility/clients.md)
- [Protocol overview](../../concepts/protocols.md)
