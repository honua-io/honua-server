# Imagery and raster: shipped state

This page is the 2026.1 truth map for imagery, raster, and multidimensional
coverage. It distinguishes working serving paths from registrations and
optional worker paths. The detailed workflow is in
[Publish rasters](publish/publish-rasters.md).

## What works now

| Surface | Shipped behavior | Edition and deployment limits |
|---|---|---|
| PostGIS raster import | GeoTIFF/COG import; PNG/JPEG with world-file georeferencing; ImageServer, WCS, and OGC API Coverages reads | Direct import is synchronous and bounded by the lower of `Limits:Imports:MaxSyncImportSize` and 50 MiB. Larger files require the configured staged-import/job infrastructure. |
| Cloud COG registration | S3 and Azure Blob registrations can serve range-readable COG tiles and imagery | Direct cloud COG serving is Pro (`raster.cloud-cog-serving`) and requires the matching range reader. JPEG, DEFLATE, and uncompressed tiles are supported; unsupported compression fails explicitly. |
| Raster protocols | ImageServer export/identify/tiles and selected analysis operations; WCS 2.0.1; OGC API Coverages; raster discovery through catalog/STAC paths | Each protocol exposes only its documented subset. WCS is not a complete implementation of every WCS extension. |
| Zarr v2/v3 | Register cloud stores, read metadata and supported little-endian chunks, and serve bounded subsets/tiles | The registration catalog is currently `InMemoryZarrStore`: it is process-local, not restart-durable or shared across replicas. Blosc, Zstd, sharding, CRC32C, and big-endian chunks are rejected. |
| NetCDF4/HDF5/GRIB | Register a multidimensional source; when the optional GDAL worker path is present, refresh extracts metadata and converts to a derived Zarr store | Registration alone does not make pixels readable. Builds without the multidimensional reader/worker return an explicit `501`; durable Zarr registration and broader worker coverage are 2026.2 work. |

Raster sources are represented as typed catalog/storage references. Request
payloads do not need to embed an entire raster as base64. The exact source and
reader prerequisites still apply: a typed reference is not proof that a
reader for every format is installed.

## Exercise the live read paths

After importing a raster as layer `1`, these are real read routes:

```bash
curl -fsS \
  'http://localhost:8080/rest/services/1/ImageServer/exportImage?bbox=-122.5,37.7,-122.3,37.9&f=json'

curl -fsS 'http://localhost:8080/ogc/coverages/collections'

curl -fsS \
  'http://localhost:8080/rest/services/1/ImageServer/WCS?service=WCS&request=GetCapabilities'
```

Admin import/registration calls require the configured admin authentication;
use the authorized [API explorer](../reference/openapi-and-explorer.md) for
`POST /api/v1/admin/import/raster`, `POST /api/v1/admin/cloud-rasters`,
`POST /api/v1/admin/zarr-stores`, and
`POST /api/v1/admin/multidim-coverages`.

## Not a 2026.1 claim

The following remain 2026.2 depth work and are not prerequisites for the
truthful 2026.1 page:

- a restart-durable, multi-replica Zarr registration store;
- broader file ingestion and compression/codec coverage;
- expanded sample, depth, and multidimensional rendering combinations;
- dedicated worker topology beyond the configured GDAL job path;
- certification for additional raster clients.

Unsupported format, reprojection, interpolation, compression, and missing
worker combinations must remain explicit errors; Honua does not silently
collapse dimensions or fall back to a different provider.

## Related

- [Publish rasters](publish/publish-rasters.md)
- [Cloud-native formats](../reference/protocols/cloud-native-formats.md)
- [Terrain and elevation](publish/publish-terrain-and-elevation.md)
- [ImageServer admin operation mapping](../reference/compatibility/imageserver-admin-mapping.md)
