# Publish rasters

You'll have raster data imported into PostGIS (or registered from cloud storage) and served through ImageServer, WCS, and OGC API Coverages in about 10 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)), admin credentials ([authentication](../secure/authentication.md)), and a target layer id in the catalog.

Honua serves rasters from two sources: rasters imported into the PostGIS raster store, and cloud-hosted COGs registered for direct range-read serving. Both surface through the same protocol adapters.

> Also available in Honua Console — UI guide coming soon.

## Steps

### 1. Check supported raster formats

```bash
HONUA_URL=http://localhost:8080
HONUA_API_KEY=your-admin-api-key
curl -H "X-API-Key: $HONUA_API_KEY" "$HONUA_URL/api/v1/admin/import/raster/formats"
```

GeoTIFF/COG (`.tif`, `.tiff`) carry embedded georeferencing; PNG (`.png`) and JPEG (`.jpg`, `.jpeg`) require a world-file sidecar.

### 2. Import a raster into PostGIS

```bash
LAYER_ID=1
curl -X POST -H "X-API-Key: $HONUA_API_KEY" \
  -F "file=@dem.tif" \
  -F "layerId=$LAYER_ID" \
  -F "name=City DEM 2026" \
  "$HONUA_URL/api/v1/admin/import/raster"
```

Optional form fields: `description`, `srid` (overrides CRS detection), `acquisitionDate` (ISO 8601, used by temporal mosaic selection), `tileZoomLevels` (comma-separated 0–24, default `0-8` pre-generated). For PNG/JPEG, attach the world file (`-F "file=@dem.pgw"`) and optionally a `.prj` sidecar. The import runs synchronously and is bounded by `Limits:Imports:MaxSyncImportSize`.

Every raster uploaded to the same layer must match the layer's first raster in SRID and band count; mismatches return `400` with a structured homogeneity message.

### 3. Register a cloud-hosted COG (alternative to import)

```bash
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{"layerId":1,"name":"ortho-2026","provider":"AwsS3","bucket":"my-rasters","objectKey":"ortho/2026.tif"}' \
  "$HONUA_URL/api/v1/admin/cloud-rasters"
```

Providers: `AwsS3` and `AzureBlob` (a matching range reader must be configured). Manage registrations with `GET /api/v1/admin/cloud-rasters?layerId=1`, `GET|DELETE /api/v1/admin/cloud-rasters/{id}`, and `POST /api/v1/admin/cloud-rasters/{id}/refresh` to re-scan metadata. ImageServer tile requests use PostGIS first and fall back to registered COGs; direct COG tile serving supports JPEG, DEFLATE, and uncompressed tiles, and is Pro-gated (`raster.cloud-cog-serving`).

## Verify

ImageServer export:

```bash
curl "$HONUA_URL/rest/services/$LAYER_ID/ImageServer/exportImage?bbox=-122.5,37.7,-122.3,37.9&f=json"
```

Other protocol surfaces over the same raster backend:

```bash
curl "$HONUA_URL/ogc/coverages/collections"
curl "$HONUA_URL/rest/services/$LAYER_ID/ImageServer/WCS?service=WCS&request=GetCapabilities"
```

```json
{"collections": [{"id": "…", …}], …}
```

## HDF5 / NetCDF

Cloud-optimized HDF5 (`.h5`, `.hdf5`) and NetCDF-4 (`.nc`, `.nc4`) sources can be registered against a layer today; metadata extraction and subset reads ship in a follow-up reader:

```bash
curl -X POST -H "X-API-Key: $HONUA_API_KEY" -H "Content-Type: application/json" \
  -d '{
    "layerId": 1,
    "name": "ghrsst-l4-daily",
    "format": "NetCdf4",
    "provider": "AwsS3",
    "bucket": "noaa-sst",
    "objectKey": "ghrsst/2026/05/18.nc4",
    "variables": ["analysed_sst", "analysis_error"]
  }' \
  "$HONUA_URL/api/v1/admin/multidim-coverages"
```

- `provider` must be `AwsS3` or `AzureBlob`; local paths are rejected. `variables` empty means "all CF data variables".
- Manage with `GET /api/v1/admin/multidim-coverages?layerId=1`, `GET|DELETE .../multidim-coverages/{id}`.
- `POST .../multidim-coverages/{id}/refresh` currently returns `501 Not Implemented` (`HONUA-COV-HDF-READER-NOT-ENABLED`) — registrations are kept and activate when the metadata reader ships.
- Keep objects in a hot storage class (`STANDARD`); archive tiers time out on range reads.

## Troubleshoot

- **`'layerId' is required and must be a valid integer.`** — the multipart request is missing the `layerId` form field; rasters attach to an existing catalog layer.
- **`Raster import request is invalid.` for PNG/JPEG** — those formats need a `.pgw`/`.jgw`/`.wld` world file; add the sidecar or supply `srid` plus a `.prj`.
- **Homogeneity error on upload** — the new raster's SRID or band count differs from the layer's first raster; use a separate layer or reproject/restack the source.
- **COG tiles not served from cloud** — check the registration with `GET /api/v1/admin/cloud-rasters/{id}`, confirm a range reader is configured for the provider, and note that LZW/ZSTD/WEBP-compressed COGs are not supported for direct tile serving.
- **Web tiles misaligned** — direct COG tile serving expects EPSG:3857 sources; other SRIDs are logged as potentially problematic for web clients.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Publish terrain and elevation](publish-terrain-and-elevation.md) — serve a DEM as Terrain-RGB tiles and elevation queries.
- [Publish tiles](publish-tiles.md) — tile cache operations.
- [Publish layers](publish-layers.md) — vector layer publishing.
