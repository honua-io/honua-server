# Imports and jobs

Reference for importing vector and raster data (file upload, URL, GeoServer/GeoServices migration, raster registration) and for the job and operations endpoints used to track long-running work.

All endpoints require admin authentication — see [Authentication](../../guides/secure/authentication.md).

## File and URL import

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/import/formats` | List supported import file formats |
| POST | `/api/v1/admin/import/preview` | Preview uploaded file content before import |
| POST | `/api/v1/admin/import/preview-url` | Preview data from a supported public object URL |
| POST | `/api/v1/admin/import/upload` | Upload and import data |
| POST | `/api/v1/admin/import/upload-url` | Import data from a supported public object URL |
| GET | `/api/v1/admin/import/limits` | Get import size and concurrency limits |

```bash
HONUA_URL=https://honua.example.com
API_KEY=your-admin-key
curl -X POST "$HONUA_URL/api/v1/admin/import/upload" \
  -H "X-API-Key: $API_KEY" \
  -F "file=@parcels.geojson"
```

Esri File Geodatabases must be uploaded as a `.gdb.zip` archive that preserves the `.gdb` directory structure. FlatGeobuf and GeoParquet files upload directly; provide `sourceSrid` when the file does not embed a CRS. See [Import files](../../guides/publish/import-files.md) for format-specific behavior.

## Uploads and import jobs

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/import/uploads` | List active uploads |
| GET | `/api/v1/admin/import/uploads/{uploadId}/progress` | Get upload progress |
| POST | `/api/v1/admin/import/uploads/{uploadId}/cancel` | Cancel an upload |
| GET | `/api/v1/admin/import/jobs` | List active import jobs |
| GET | `/api/v1/admin/import/jobs/{jobId}` | Get import job status |
| POST | `/api/v1/admin/import/jobs/{jobId}/cancel` | Cancel an import job |

```bash
JOB_ID=8f3c2e9a
curl "$HONUA_URL/api/v1/admin/import/jobs/$JOB_ID" -H "X-API-Key: $API_KEY"
```

## Migration scan

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/import/scan` | Scan a GeoServer or ArcGIS GeoServices source and return a deterministic migration inventory artifact (`?export=json` returns it as a JSON attachment) |

`sourceKind` accepts `geoserver`, `geoserver-rest`, `geoservices`, and `arcgis-geoservices-rest`. Run a scan before any migration import job; use `scanCompleteness.status` and `overallCompatibility.level` as the planning gate.

```bash
curl -X POST "$HONUA_URL/api/v1/admin/import/scan" \
  -H "X-API-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"sourceKind":"geoservices","sourceUrl":"https://example.com/arcgis/rest/services/Parcels/FeatureServer"}'
```

## GeoServer migration import

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/import/geoserver/discover` | Discover GeoServer REST configuration and compatibility |
| POST | `/api/v1/admin/import/geoserver/start` | Queue a dry-run validation (`dryRun=true`) or bounded apply job |
| GET | `/api/v1/admin/import/geoserver/jobs` | List GeoServer import jobs |
| GET | `/api/v1/admin/import/geoserver/jobs/{jobId}` | Get GeoServer import job status |
| POST | `/api/v1/admin/import/geoserver/jobs/{jobId}/cancel` | Cancel a GeoServer import job |

Queued jobs persist request state before a worker runs, so credentials must use secret references (for example `"passwordSecretReference": "env:GEOSERVER_PASSWORD"`); plaintext passwords are rejected.

## GeoServices (ArcGIS) migration import

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/import/geoservices/discover` | Discover layers from an ArcGIS service URL |
| POST | `/api/v1/admin/import/geoservices/start` | Start an ArcGIS layer import job |
| GET | `/api/v1/admin/import/geoservices/jobs` | List GeoServices import jobs |
| GET | `/api/v1/admin/import/geoservices/jobs/{jobId}` | Get GeoServices import job status |
| POST | `/api/v1/admin/import/geoservices/jobs/{jobId}/cancel` | Cancel a GeoServices import job |

Authenticated sources send credentials in the request `credentials` object, never in the URL. Queued jobs must use `accessTokenSecretReference` or `passwordSecretReference`; plaintext token/password values are accepted only by the synchronous discover and scan endpoints.

```bash
curl -X POST "$HONUA_URL/api/v1/admin/import/geoservices/discover" \
  -H "X-API-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"serviceUrl":"https://example.com/arcgis/rest/services/Parcels/FeatureServer"}'
```

## Raster import and cloud raster registration

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/import/raster` | Import a raster file (GeoTIFF, PNG/JPEG world-file) into PostGIS |
| GET | `/api/v1/admin/import/raster/formats` | List supported raster formats and extensions |
| POST | `/api/v1/admin/cloud-rasters` | Register a cloud-optimized GeoTIFF (COG) by URL |
| GET | `/api/v1/admin/cloud-rasters` | List registered cloud rasters |
| GET | `/api/v1/admin/cloud-rasters/{id}` | Get a registered cloud raster |
| DELETE | `/api/v1/admin/cloud-rasters/{id}` | Remove a cloud raster registration |
| POST | `/api/v1/admin/cloud-rasters/{id}/refresh` | Refresh cached cloud raster metadata |
| POST | `/api/v1/admin/zarr-stores` | Register a Zarr store |
| GET | `/api/v1/admin/zarr-stores` | List Zarr stores |
| GET | `/api/v1/admin/zarr-stores/{id}` | Get a Zarr store |
| DELETE | `/api/v1/admin/zarr-stores/{id}` | Remove a Zarr store |
| POST | `/api/v1/admin/zarr-stores/{id}/refresh` | Refresh Zarr store metadata |
| POST | `/api/v1/admin/multidim-coverages` | Register a cloud-optimized HDF5/NetCDF4 multidimensional coverage |
| GET | `/api/v1/admin/multidim-coverages` | List multidimensional coverages |
| GET | `/api/v1/admin/multidim-coverages/{id}` | Get a multidimensional coverage |
| DELETE | `/api/v1/admin/multidim-coverages/{id}` | Remove a multidimensional coverage |
| POST | `/api/v1/admin/multidim-coverages/{id}/refresh` | Refresh multidimensional coverage metadata |

Raster import is multipart form-data with optional sidecars (`.pgw`/`.jgw`/`.tfw`/`.wld`, `.prj`). Subsequent uploads to a layer must match the layer's SRID and band count; mismatches return `400`.

```bash
curl -X POST "$HONUA_URL/api/v1/admin/import/raster" \
  -H "X-API-Key: $API_KEY" \
  -F "file=@ortho.tif" -F "layerName=ortho-2026"
```

## Tile operation jobs

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/tile-operations/jobs` | Start a tile operation job |
| GET | `/api/v1/admin/tile-operations/jobs` | List tile operation jobs |
| GET | `/api/v1/admin/tile-operations/jobs/{jobId}` | Get tile operation job status |
| POST | `/api/v1/admin/tile-operations/jobs/{jobId}/cancel` | Cancel a tile operation job |
| POST | `/api/v1/admin/tile-operations/jobs/{jobId}/retry` | Retry a tile operation job |

Supported `operation` values: `seed`, `warm`, `invalidate`, `purge`, `archive`, `publish`. The `publish` operation produces a durable PMTiles artifact described by `publishedArtifact` on the job-status response. See [Publish tiles](../../guides/publish/publish-tiles.md).

```bash
curl -X POST "$HONUA_URL/api/v1/admin/tile-operations/jobs" \
  -H "X-API-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"operation":"seed","layerId":42,"minZoom":0,"maxZoom":12}'
```

## Operations and durable jobs

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/operations/{operationId}` | Get operation progress and status |
| POST | `/api/v1/admin/operations/{operationId}/cancel` | Cancel an operation |
| GET | `/api/v1/admin/operations/active` | List active operations |
| GET | `/api/v1/admin/operations/type/{operationType}` | List operations by type |
| GET | `/api/v1/admin/jobs` | List durable execution jobs (cursor pagination, queue/status/resource filters) |
| GET | `/api/v1/admin/jobs/{jobId}` | Get durable job detail |
| GET | `/api/v1/admin/jobs/{jobId}/logs` | Page structured execution logs |
| GET | `/api/v1/admin/jobs/{jobId}/artifacts` | Page artifact references with availability state |
| GET | `/api/v1/admin/jobs/{jobId}/actions` | List available job control actions |
| POST | `/api/v1/admin/jobs/{jobId}/cancel` | Cancel a queued, provisioning, or running job |
| POST | `/api/v1/admin/jobs/{jobId}/retry` | Retry a failed or cancelled job when policy allows |

Supported `operationType` values: `Upload`, `Import`, `Ingest`, `ExternalImport`, `TileCache`, `PMTilesArchive`, `PMTilesPublish`, `Export`, `RasterImport`, `Print`, `Geoprocessing`, `Publishing`, `Orchestration`. Cancelling an operation already in a terminal state returns `409`; cancelling an already-cancelled operation returns `200` idempotently.

```bash
OP_ID=2b1f7c44
curl "$HONUA_URL/api/v1/admin/operations/$OP_ID" -H "X-API-Key: $API_KEY"
```

## Related guides

- [Import files](../../guides/publish/import-files.md)
- [Import from ArcGIS services](../../guides/publish/import-from-arcgis-services.md)
- [Migrate from GeoServer](../../guides/migrate/from-geoserver.md) and [from ArcGIS Server](../../guides/migrate/from-arcgis-server.md)
- [Publish rasters](../../guides/publish/publish-rasters.md)
- [Publish tiles](../../guides/publish/publish-tiles.md)
- [Operations](../../guides/deploy/backup-and-restore.md)
