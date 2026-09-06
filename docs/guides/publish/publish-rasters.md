# Publish rasters

You'll have raster data imported into PostGIS and served through ImageServer, WCS, and OGC API Coverages in about 10 minutes. Direct cloud COG tile serving currently uses the ImageServer tile fallback only.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and a target layer id in the catalog.

Honua serves rasters from two sources: rasters imported into the PostGIS raster store, and cloud-hosted COGs registered for the ImageServer tile fallback. The cloud registration does not currently share the full protocol surface of imported rasters.

> Also available in Honua Console — UI guide coming soon.

## Steps

### 1. Check supported raster formats

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `GET /api/v1/admin/import/raster/formats`.

GeoTIFF/COG (`.tif`, `.tiff`) carry embedded georeferencing; PNG (`.png`) and JPEG (`.jpg`, `.jpeg`) require a world-file sidecar.

### 2. Import a raster into PostGIS

Run `POST /api/v1/admin/import/raster` with these form values:

| Field | Value |
| --- | --- |
| `file` | `dem.tif` |
| `layerId` | `1` |
| `name` | `City DEM 2026` |

Optional form fields: `description`, `srid` (overrides CRS detection), `acquisitionDate` (ISO 8601, used by temporal mosaic selection), `tileZoomLevels` (comma-separated 0–24, default `0-8` pre-generated). For PNG/JPEG, attach the world file as another `file` value and optionally add a `.prj` sidecar. The direct PostGIS import runs synchronously and accepts at most the lower of `Limits:Imports:MaxSyncImportSize` and the fixed 50 MiB serving-process safety ceiling. Larger rasters require the durable staged-import pipeline.

Every raster uploaded to the same layer must match the layer's first raster in SRID and band count; mismatches return `400` with a structured homogeneity message.

Imported rasters are stored with `EXTERNAL` TOAST storage (out-of-line and uncompressed) so tile, terrain, statistics, and export reads fetch only the pixels they need instead of detoasting and decompressing the entire raster on every request. Combined with the default `0-8` tile pre-generation, web tile requests resolve as indexed `raster_tiles` lookups rather than full-raster clips.

### 3. Register a cloud-hosted COG (alternative to import)

Run `POST /api/v1/admin/cloud-rasters` with this body:

```json
{
  "layerId": 1,
  "name": "ortho-2026",
  "provider": "AwsS3",
  "bucket": "my-rasters",
  "objectKey": "ortho/2026.tif"
}
```

Providers: `AwsS3` and `AzureBlob` (a matching range reader must be configured). Manage registrations with `GET /api/v1/admin/cloud-rasters?layerId=1`, `GET|DELETE /api/v1/admin/cloud-rasters/{id}`, and `POST /api/v1/admin/cloud-rasters/{id}/refresh` to re-scan metadata. ImageServer tile requests use PostGIS first and fall back to registered COGs; the fallback requires an EPSG:3857 GoogleMapsCompatible-aligned grid, can pass through standalone JPEG tiles for `format=jpg` (TIFF-JPEG tiles requiring shared JPEGTables are not assembled), and encodes DEFLATE/LZW/ZSTD/uncompressed chunky unsigned 8/16-bit grayscale or RGB samples as lossless PNG with nodata transparency, and is Pro-gated (`raster.cloud-cog-serving`). Registered cloud COGs are not read by `exportImage`, `identify`, WCS, or OGC API Coverages.

## Verify

For a registered cloud COG, use the authorized [API explorer](../../reference/openapi-and-explorer.md)
to request `GET /rest/services/{serviceId}/ImageServer/tile/{level}/{row}/{col}?format=png`.
Use the published ImageServer service name and an XYZ tile covered by the source at an available
overview resolution. Check that the response is a decodable `image/png` with the expected pixels;
an HTTP 200 GeoServices error envelope does not prove delivery. Use `format=tiff` for scientific
samples and `format=jpg` for standalone JPEG tiles. Register against a layer without imported
PostGIS tiles when verifying the cloud path, because PostGIS tiles take precedence.

For rasters imported into PostGIS, verify ImageServer export:

> Open `/rest/services/{layerId}/ImageServer/exportImage?bbox=-122.5,37.7,-122.3,37.9&f=json` in a browser.

Other protocol surfaces over the imported PostGIS raster backend:

> Open `/ogc/coverages/collections`, `/rest/services/{layerId}/ImageServer/WCS?service=WCS&request=GetCapabilities` in a browser.

```json
{"collections": [{"id": "…", …}], …}
```

## HDF5 / NetCDF

Cloud-optimized HDF5 (`.h5`, `.hdf5`) and NetCDF-4 (`.nc`, `.nc4`) sources can be registered against a layer today; metadata extraction and subset reads ship in a follow-up reader:

Run `POST /api/v1/admin/multidim-coverages` with this body:

```json
{
  "layerId": 1,
  "name": "ghrsst-l4-daily",
  "format": "NetCdf4",
  "provider": "AwsS3",
  "bucket": "noaa-sst",
  "objectKey": "ghrsst/2026/05/18.nc4",
  "variables": ["analysed_sst", "analysis_error"]
}
```

- `provider` must be `AwsS3` or `AzureBlob`; local paths are rejected. `variables` empty means "all CF data variables".
- Manage with `GET /api/v1/admin/multidim-coverages?layerId=1`, `GET|DELETE .../multidim-coverages/{id}`.
- `POST .../multidim-coverages/{id}/refresh` currently returns `501 Not Implemented` (`HONUA-COV-HDF-READER-NOT-ENABLED`) — registrations are kept and activate when the metadata reader ships.
- Keep objects in a hot storage class (`STANDARD`); archive tiers time out on range reads.

## Troubleshoot

- **`'layerId' is required and must be a valid integer.`** — the multipart request is missing the `layerId` form field; rasters attach to an existing catalog layer.
- **`Raster import request is invalid.` for PNG/JPEG** — those formats need a `.pgw`/`.jgw`/`.wld` world file; add the sidecar or supply `srid` plus a `.prj`.
- **Homogeneity error on upload** — the new raster's SRID or band count differs from the layer's first raster; use a separate layer or reproject/restack the source.
- **COG tiles not served from cloud** — check the registration with `GET /api/v1/admin/cloud-rasters/{id}`, confirm a range reader is configured for the provider, and verify that the object has an ETag and a supported sample layout. DEFLATE/LZW/ZSTD/uncompressed chunky unsigned 8/16-bit grayscale or RGB tiles serve as lossless PNG with nodata transparency; use `format=tiff` or `format=cog` for lossless unsigned, signed or floating-point samples with nodata and tile georeferencing; palettes, separate planes and JPEG conversion from decoded samples are unsupported; its default `format=png` does not transcode JPEG either.
- **Web tiles misaligned** — direct COG tile serving requires an EPSG:3857 GoogleMapsCompatible grid. Other SRIDs and grids return GeoServices error code 404 from the tile fallback (an HTTP 200 error envelope); use the imported PostGIS path for reprojection and general raster operations. A successful metadata refresh does not certify that a source is tile-servable. Rotated, sheared and invalid ModelTransformation grids are rejected during metadata extraction.
- **Floating-point or JPEG source rejected** — TIFF floating-point predictor 3 is unsupported; prepare floating-point sources without that predictor (predictor 1). Complex or undefined TIFF SampleFormat values and sources declaring shared JPEGTables are rejected during metadata extraction. Use imported rasters for these source layouts.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Publish terrain and elevation](publish-terrain-and-elevation.md) — serve a DEM as Terrain-RGB tiles and elevation queries.
- [Publish tiles](publish-tiles.md) — tile cache operations.
- [Publish layers](publish-layers.md) — vector layer publishing.

Cloud COG `layerId` is the service-local publication index, not the backing storage layer ID.
It must identify one routable publication across the catalog; colliding indexes fail closed.
COG is a 2026.1 GA target through the documented direct tile workflow.
