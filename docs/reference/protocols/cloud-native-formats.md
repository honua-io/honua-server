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
| Zarr | Registered source | `POST /api/v1/admin/zarr-stores` (CRUD + `/refresh`) | Catalog/metadata only | Registration live; protocol serving not yet exposed |
| Cloud-optimized HDF5 / NetCDF4 | Registered source | `POST /api/v1/admin/multidim-coverages` (CRUD; URL-registered, not file-imported) | Catalog/metadata only; reader is build-optional | Registration live; protocol serving not yet exposed |

## COG and cloud rasters

Register a raster that already lives in object storage — no copy, no conversion:

```bash
HONUA_URL=http://localhost:8080
curl -s -H "X-API-Key: $ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"layerId":"imagery","name":"Imagery 2026","provider":"AwsS3","bucket":"my-rasters","objectKey":"imagery/2026.tif"}' \
  $HONUA_URL/api/v1/admin/cloud-rasters
```

The registered raster serves through the same pipeline as imported rasters: GeoServices ImageServer, WCS 2.0.1, and OGC API Coverages. Workflow detail: [Publish rasters](../../guides/publish/publish-rasters.md).

## PMTiles

Tile-operations jobs can `archive` a layer's tiles into a single PMTiles file and `publish` it; the artifact is served with HTTP range support at `/api/v1/tiles/pmtiles/{artifactId}`, which makes it suitable for CDN fronting or serverless map hosting. Workflow detail and URL strategies: [Publish tiles](../../guides/publish/publish-tiles.md#pmtiles).

## Analytics wire formats

PostGIS-backed layers negotiate columnar/binary outputs on FeatureServer query (`f=parquet`, `f=arrow`, `f=fgb`, `f=geobuf`) for notebook and pipeline consumption — see [Export data](../../guides/query-analyze/export-data.md) and the [data formats matrix](../data-formats.md) for which surfaces serve which formats.

## Zarr and multidimensional coverages (HDF5/NetCDF)

Both are **registration and catalog surfaces today**: Honua stores the source descriptor and metadata and exposes them through the admin API, but does not yet serve them through a query/coverage protocol. The HDF5/NetCDF reader is optional per build (`MultidimensionalCoverage` reader), and `/api/v1/admin/multidim-coverages/{id}/refresh` returns `501` until it is enabled. Treat protocol exposure for both as roadmap, not shipped.

## Related

- [Data formats matrix](../data-formats.md) — full import/export format support
- [STAC](stac.md) — catalog discovery for these assets
- [OGC APIs](ogc-apis.md) — the Coverages surface COG serving rides on
