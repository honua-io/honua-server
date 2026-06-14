# Connect QGIS to Honua

Add Honua layers to a QGIS project over OGC API Features, WMS/WMTS, or WFS, then filter them with the standard QGIS tools.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). QGIS 3.34 LTR or later is assumed for menu names.

## Steps

### Option A — OGC API Features (recommended)

1. Open the Data Source Manager (**Layer → Data Source Manager**, or `Ctrl+L`) and select the **WFS / OGC API - Features** tab.
2. Click **New** to create a connection.
3. Enter a name (for example `Honua`) and the URL `http://localhost:8080/ogc/features`, then click **OK**.
4. Click **Connect**. QGIS reads the landing page and lists the published collections.
5. Select a collection and click **Add**. The features load onto the map canvas.

> Collection ids are currently the numeric layer ids (for example `0`).

### Option B — WMS or WMTS (rendered maps and cached tiles)

Honua serves WMS 1.1.1/1.3 and WMTS 1.0 per service at `/ogc/services/{serviceId}/wms` and `/ogc/services/{serviceId}/wmts` (the ArcGIS-style aliases `/rest/services/{serviceId}/MapServer/WMS` and `.../MapServer/WMTS` work too).

1. In the Data Source Manager, select the **WMS/WMTS** tab and click **New**.
2. Enter a name and the URL for your service, for example `http://localhost:8080/ogc/services/my-service/wms` (or the `/wmts` URL for tiles), then click **OK**.
3. Click **Connect**, select a layer (WMS) or tile layer (WMTS), and click **Add**.

WMTS currently serves the WebMercatorQuad tile matrix set only.

### Option C — WFS 2.0 (legacy clients)

1. In the **WFS / OGC API - Features** tab, click **New**.
2. Enter the URL `http://localhost:8080/wfs` and click **OK**, then **Connect**.
3. Select a feature type and click **Add**.

QGIS negotiates the WFS version automatically. Honua also answers WFS 1.1.0 and 1.0.0 read-only requests on the same URL; append `VERSION=1.1.0` to the URL to pin a legacy version.

### Filter a layer

1. Right-click the layer → **Filter…**.
2. Enter an expression such as `"category" = 'park'` and click **OK**. The canvas updates to the matching subset; QGIS pushes the filter to the server where the provider supports it.
3. To inspect attributes, right-click the layer → **Open Attribute Table** (`F6`).

## Verify

Confirm the endpoints QGIS uses respond outside QGIS:

```bash
BASE=http://localhost:8080
curl -s "$BASE/ogc/features/collections" | head
curl -s "$BASE/wfs?service=WFS&request=GetCapabilities" | head
```

In QGIS, the layer should draw on the canvas and the attribute table should list features.

## Troubleshoot

- **Connection refused** — the server is not running or port 8080 is blocked. Check `curl http://localhost:8080/healthz/ready`. See [troubleshooting](../deploy/troubleshooting.md).
- **No collections listed** — no layers are published yet, or the layer's protocols exclude OGC API Features. Re-check [publish layers](../publish/publish-layers.md).
- **401/403 when connecting** — your deployment requires auth. Add credentials under **Authentication** in the QGIS connection dialog (API key header or Basic, matching your server config).
- **WMS/WMTS connection fails with 404** — the URL must include a service id: `/ogc/services/{serviceId}/wms`, not `/wms`.
- **Layer draws in the wrong place** — check the layer CRS; Honua serves EPSG:4326 by default and QGIS reprojects to the project CRS.

## Next steps

- [Query features from the command line](../query-analyze/query-features.md)
- [Style maps](../style/style-maps.md)
- [Client compatibility matrix](../../reference/compatibility/clients.md)
