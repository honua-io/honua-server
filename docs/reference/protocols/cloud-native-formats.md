# Cloud-native formats

Honua's support for the cloud-native geospatial format family: what each format is used for, the endpoints involved, and an honest status per format. Formats fall into three roles — **registered sources** (data stays in object storage, Honua serves it), **produced artifacts** (Honua generates them), and **wire formats** (query output negotiation).

## Status summary

| Format | Role | Register / produce | Serve / consume | Status |
|---|---|---|---|---|
| COG / GeoTIFF | Registered source + import | `POST /api/v1/admin/cloud-rasters` (S3/Azure, in place) or file import `POST /api/v1/admin/import/raster` | ImageServer (`exportImage`, `identify`, tiles), WCS 2.0.1, OGC API Coverages | Serving live |
| PMTiles | Produced artifact | Tile-operations jobs (`archive`, `publish`) — see [Publish tiles](../../guides/publish/publish-tiles.md) | `GET`/`HEAD /api/v1/tiles/pmtiles/{artifactId}` (HTTP range requests) | Serving live |
| GeoParquet | Import + wire format | File import (`.parquet`, `.geoparquet`) | FeatureServer `f=parquet` (GeoParquet 1.1.0, PostGIS-backed layers) | Live |
| GeoArrow | Wire format | — | FeatureServer `f=arrow` (Arrow IPC stream, PostGIS-backed layers) | Live |
| FlatGeobuf | Import + wire format | File import (`.fgb`) | FeatureServer `f=fgb` (PostGIS-backed layers) | Live |
| Zarr | Registered source | `POST /api/v1/admin/zarr-stores` (CRUD + `/refresh`) | OGC API Coverages pixel subsets (`ZarrCoverageService`) | Registration + serving live |
| Cloud-optimized HDF5 / NetCDF4 | Registered source | `POST /api/v1/admin/multidim-coverages` (CRUD; URL-registered, not file-imported); `/refresh` enqueues an async GDAL worker job (202 + jobId/statusUrl) | Metadata extracted + enriched, then auto-converted to Zarr and registered for OGC API Coverages serving | Registration + conversion live; pixel read via the derived Zarr (reader is build-optional) |
| GRIB (`.grib`/`.grb`/`.grb2`/`.grib2`) | Registered source | `POST /api/v1/admin/multidim-coverages` (same path as HDF5/NetCDF) | Same GDAL→Zarr conversion and OGC API Coverages serving | Registration + conversion live |

## COG and cloud rasters

Register a raster that already lives in object storage — no copy, no conversion:

```bash
HONUA_URL=http://localhost:8080
curl -s -H "X-API-Key: $ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"layerId":"imagery","name":"Imagery 2026","provider":"AwsS3","bucket":"my-rasters","objectKey":"imagery/2026.tif"}' \
  $HONUA_URL/api/v1/admin/cloud-rasters
```

The registered raster serves through the same pipeline as imported rasters: GeoServices ImageServer, WCS 2.0.1, and OGC API Coverages. Workflow detail: [Publish rasters](../../guides/publish/publish-rasters.md).

Imported rasters can be deleted (`DELETE /api/v1/admin/import/raster/{rasterId}`) and have their descriptive metadata updated (`PATCH /api/v1/admin/import/raster/{rasterId}` — `name`/`description`/`acquisitionDate`); cloud-registered COGs use `DELETE /api/v1/admin/cloud-rasters/{id}`. These admin operations are the canonical equivalents of Esri ImageServer's `deleteRasters`/`updateRaster` — see the [ImageServer admin-op mapping](../compatibility/imageserver-admin-mapping.md).

Optional per-raster **sensor metadata** (sensor name, camera model, interior/exterior orientation, RPC, DEM source) can be modeled in the `raster_sensor_metadata` companion table. When present it powers ImageServer DEM-backed height mensuration, orientation-ranked `find`, and RPC image-coordinate-system `project` warps; plain rasters with no sensor metadata serve normally and those features degrade gracefully.

## PMTiles

Tile-operations jobs can `archive` a layer's tiles into a single PMTiles file and `publish` it; the artifact is served with HTTP range support at `/api/v1/tiles/pmtiles/{artifactId}`, which makes it suitable for CDN fronting or serverless map hosting. Workflow detail and URL strategies: [Publish tiles](../../guides/publish/publish-tiles.md#pmtiles).

## Analytics wire formats

PostGIS-backed layers negotiate columnar/binary outputs on FeatureServer query (`f=parquet`, `f=arrow`, `f=fgb`, `f=geobuf`) for notebook and pipeline consumption — see [Export data](../../guides/query-analyze/export-data.md) and the [data formats matrix](../data-formats.md) for which surfaces serve which formats.

## Zarr and multidimensional coverages (HDF5/NetCDF/GRIB)

NetCDF4, HDF5, and GRIB sources are registered via `POST /api/v1/admin/multidim-coverages` (URL-registered, not file-imported). Calling `/api/v1/admin/multidim-coverages/{id}/refresh` enqueues an async GDAL worker job (returns `202` with a `jobId`/`statusUrl`) that runs `gdalmdiminfo` + `gdalinfo` to extract and enrich structure and metadata — variables, dimensions, chunk layout, compression, CF attributes, nodata, spatial extent, cell resolution, and CF-decoded temporal/vertical bounds (best-effort, tolerant of missing fields) — and `gdal_translate -of Zarr` to convert the source to a derived Zarr written beside it in cloud storage. The derived Zarr is then registered as a sibling coverage and served through the existing Zarr coverage path (`ZarrCoverageService`) over **OGC API Coverages** (`GetCoverage`, including `datetime` temporal subsetting). The reader remains optional per build (`MultidimensionalCoverage` reader); when the feature is disabled, `/refresh` returns `501`. The end-to-end pixel read path runs against cloud object storage and the GDAL worker (`ubuntu-full` image for the NetCDF/HDF5/GRIB drivers). Per-slice multidimensional pixel subsetting on the GeoServices ImageServer surface (`multidimensionalDefinition`) is still deferred.

The Zarr reader (`ZarrMetadataExtractor`/`ZarrSubsetReader`) reads both **Zarr v2** (`.zgroup`/`.zarray`/`.zattrs`) and **Zarr v3** (`zarr.json`, `node_type` group/array). For v3 it normalizes the `data_type` name (e.g. `float32` → numpy `<f4`), reads the `c/`-prefixed default chunk-key encoding (or the `v2` dotted encoding), and gates the codec pipeline — uncompressed and `gzip`-coded little-endian chunks are supported; `blosc`, `zstd`, sharding, `crc32c`, and big-endian are rejected cleanly.

## Related

- [Data formats matrix](../data-formats.md) — full import/export format support
- [STAC](stac.md) — catalog discovery for these assets
- [OGC APIs](ogc-apis.md) — the Coverages surface COG serving rides on
