# Export data

Pull a layer's features out of Honua as GeoJSON, CSV, GeoParquet, GeoArrow, FlatGeobuf, GeoPackage, or Shapefile — straight from the query endpoints or as a bulk admin export.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). The bulk export in step 4 needs an admin API key ([authentication](../secure/authentication.md)).

Which formats are available depends on the surface: OGC API Features items serve `geojson`, `json`, `gml`, `csv`, and `html` via the `f` parameter; FeatureServer `query` serves `json`, `pjson`, `geojson`, and `pbf` everywhere, plus `fgb` (FlatGeobuf), `geobuf`, `parquet` (GeoParquet), and `arrow` (GeoArrow / Arrow IPC stream) on layers whose feature store supports encoded output (PostGIS-backed layers) — the layer metadata's `supportedQueryFormats` field is authoritative.

## Steps

1. Export GeoJSON or CSV from OGC API Features with the `f` parameter (an `Accept` header works too):

   Open `/ogc/features/collections/{collectionId}/items?limit=1000&f=geojson` or the same URL with `f=csv` at the deployment origin. Save the response as `features.geojson` or `features.csv`.

2. Check which encoded formats your layer advertises, then export GeoParquet or GeoArrow from the FeatureServer query endpoint. Any `where`/`outFields`/geometry filter from [query features](query-features.md) applies:

   Open `/rest/services/{service}/FeatureServer/0?f=json` and inspect the response's `supportedQueryFormats`. To download encoded data, open `/rest/services/{service}/FeatureServer/0/query?where=1%3D1&outFields=*&f=parquet` or the same URL with `f=arrow`.

   GeoParquet output follows GeoParquet 1.1.0 (XY and XYZ geometries; `returnM=true` is rejected). The Arrow output is an IPC stream (`application/vnd.apache.arrow.stream`) with GeoArrow metadata.

3. Load it straight into the Python analyst stack:

   ```python
   import io, urllib.request, geopandas as gpd
   url = "http://localhost:8080/rest/services/my_service/FeatureServer/0/query?where=1%3D1&outFields=*&f=parquet"
   gdf = gpd.read_parquet(io.BytesIO(urllib.request.urlopen(url).read()))
   ```

   For the Arrow stream, use `pyarrow.ipc.open_stream(...)` on the response bytes instead.

4. Export FlatGeobuf the same way (PostGIS-backed layers):

   Open `/rest/services/{service}/FeatureServer/0/query?where=1%3D1&f=fgb` at the deployment origin and save the response as `layer.fgb`.

5. Use the admin bulk export for Shapefile or GeoPackage. It accepts optional `where`, `bbox`, `outFields`, and `outSR` parameters:

   In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `GET /api/v1/admin/services/{service}/layers/{layerId}/export?format=gpkg` and save the response as `layer.gpkg`.

   Valid formats are `csv`, `shapefile`, and `gpkg`. Small exports stream back directly; large exports return `202 Accepted` with an `operationId` and a `statusUrl` (`/api/v1/admin/operations/{id}`) to poll — the async path requires cloud storage to be configured.

## Verify

Save `/rest/services/{service}/FeatureServer/0/query?where=1%3D1&resultRecordCount=5&f=parquet` as `sample.parquet`, then inspect it:

```bash
python -c "import geopandas; print(geopandas.read_parquet('sample.parquet').shape)"
```

Expected: a row/column tuple such as `(5, 12)` with a populated `geometry` column.

## Troubleshoot

- **400 `not supported by the configured feature store` on `f=fgb`/`f=geobuf`** — the layer's backing store cannot emit encoded output; check `supportedQueryFormats` on the layer metadata and fall back to `f=geojson`.
- **400 on `f=parquet` with `returnM=true`** — GeoParquet 1.1.0 supports only XY/XYZ; drop `returnM`.
- **400 `Shapefile format does not support mixed geometry types`** — bulk-export mixed-geometry layers as `gpkg` or `csv` instead.
- **503 `Large exports require cloud storage`** — the async export path needs a configured cloud storage provider; reduce the export scope with `where`/`bbox` or configure storage.
- **CSV is missing geometry detail** — CSV serializes geometry to a text column; use GeoJSON/GeoParquet for full-fidelity geometry. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Query features](query-features.md)
- [Run geoprocessing](run-geoprocessing.md)
- [Connect Excel and Power BI](../connect/excel-power-bi.md)
