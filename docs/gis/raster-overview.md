# Raster Overview

This page summarizes the current Honua raster surface for GIS operators and
client integrators. It is the durable status page for the raster roadmap tracked
by issue `#381`.

## Current status

| Area | Status | Notes |
| --- | --- | --- |
| Raster upload and import | Shipped (`#517`) | Admin import accepts GeoTIFF/COG files and PNG/JPEG rasters with world-file sidecars, then loads them into the PostGIS raster store. |
| COG registration, direct serving, and export support | Shipped (`#519`) | Admin COG registration is available for cloud-hosted COGs. ImageServer tile requests can fall back to registered COGs when PostGIS has no tile. Shared raster export/conversion paths can request COG output. |
| Terrain-RGB elevation tiles | Shipped (`#839`) | Registered single-band DEM/raster sources can be served through `/terrain/{datasetId}/tile.json` and `/terrain/{datasetId}/{z}/{x}/{y}.png` for MapLibre/Mapbox `raster-dem` clients. |
| Multi-raster mosaic and raster catalog completion | Remaining (`#522`) | MVP layer-level raster selection and simple PostGIS mosaic rendering exist, but full mosaic dataset/raster catalog behavior remains the remaining implementation child. |
| WCS protocol adapter | Shipped (`#377`) | WCS adapts to the shared raster backend for primary-raster `GetCapabilities`, `DescribeCoverage`, and `GetCoverage`. |
| OGC API Coverages protocol adapter | Shipped (`#521`) | OGC API Coverages adapts to the shared raster backend for REST/JSON coverage discovery, schema metadata, and GeoTIFF/PNG coverage retrieval. |

## Raster upload and import

Raster import is exposed through the admin API:

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/admin/import/raster` | Multipart raster upload into PostGIS. |
| `GET /api/v1/admin/import/raster/formats` | Lists supported raster extensions and descriptions. |

The primary raster file can be:

| Format | File extensions | Georeferencing expectations |
| --- | --- | --- |
| GeoTIFF / COG | `.tif`, `.tiff` | Embedded TIFF/GeoTIFF georeferencing is preferred. The import path validates TIFF/BigTIFF headers and uses the same PostGIS ingestion path for standard GeoTIFF and COG files. |
| PNG world-file raster | `.png` | Requires a `.pgw` or `.wld` sidecar. A `.prj` sidecar or explicit `srid` field should be supplied when the CRS is not otherwise known. |
| JPEG world-file raster | `.jpg`, `.jpeg` | Requires a `.jgw` or `.wld` sidecar. A `.prj` sidecar or explicit `srid` field should be supplied when the CRS is not otherwise known. |

The import request is synchronous today. It is bounded by
`Limits:Imports:MaxSyncImportSize` and does not yet enqueue a background raster
job for larger uploads. If async/background raster import is prioritized, track
that as a separate `honua-server` child ticket rather than adding it to `#522`.

Successful imports report progress through the universal progress store when it
is available and invalidate layer output cache entries after the import
commits, including Terrain-RGB metadata and tile entries tagged with `terrain`.
Imported rasters must remain homogeneous per layer for SRID and band count
because the shared mosaic paths depend on PostGIS `ST_Union`.

## Cloud raster and COG serving

Cloud-hosted COGs are registered outside the Esri ImageServer operation set:

| Endpoint | Purpose |
| --- | --- |
| `POST /api/v1/admin/cloud-rasters` | Register a cloud-hosted COG for a layer. |
| `GET /api/v1/admin/cloud-rasters?layerId={layerId}` | List COG registrations for a layer. |
| `GET /api/v1/admin/cloud-rasters/{id}` | Read one COG registration. |
| `DELETE /api/v1/admin/cloud-rasters/{id}` | Unregister a COG and evict its metadata cache entry. |
| `POST /api/v1/admin/cloud-rasters/{id}/refresh` | Re-scan COG metadata from cloud storage and evict stale cached metadata. |

Registered COGs currently support these providers:

| Provider | Status |
| --- | --- |
| `AwsS3` | Supported for direct COG range reads when the S3 range reader is configured. |
| `AzureBlob` | Supported for direct COG range reads when the Azure Blob range reader is configured. |
| `Local` | Not valid for COG registration. |
| Google Cloud Storage | Not implemented. Do not treat GCS as shipped support until a provider exists. |

ImageServer tile requests use the PostGIS raster tile path first. When no
PostGIS tile is produced and a COG resolver is configured, Honua attempts direct
COG tile serving from registered cloud rasters for the addressed layer.

Direct COG tile serving is Pro-gated by the canonical feature key
`raster.cloud-cog-serving`. COG import, registration, and export/conversion
support are Community-tier and ungated.

## COG compression and tile output

Direct COG tile serving reads tile byte ranges from cloud storage and serves the
native tile content when it can satisfy the requested ImageServer tile format.

| Compression | Direct tile-serving status |
| --- | --- |
| `JPEG` | Supported as JPEG passthrough. |
| `DEFLATE` | Supported through zlib-wrapped TIFF deflate decompression. |
| `NONE` / empty | Supported as uncompressed tile bytes. |
| `LZW`, `ZSTD`, `WEBP`, and other TIFF/COG modes | Not supported for direct tile serving today. Honua logs an unsupported-compression warning and tries the next registered COG for the layer. |

COG export support uses the raster store's GDAL output path. When the native
COG driver is available it can emit `COG`; otherwise it falls back to `GTiff`
with COG-compatible options such as internal tiling and `DEFLATE` compression.
The public ArcGIS ImageServer `exportImage` format parameter remains limited to
the documented ImageServer formats and does not expose `format=cog`.

## CRS and georeferencing assumptions

GeoTIFF and COG imports should carry embedded georeferencing. World-file rasters
depend on sidecar georeferencing and should include a `.prj` file or explicit
`srid` form value when the CRS cannot be inferred.

Direct COG tile resolution is designed for web-map tile alignment. EPSG:3857 is
the expected CRS for directly serving web tiles. EPSG:4326 COG metadata can be
read, but clients may need protocol-specific handling. Other SRIDs are logged
as potentially problematic for web clients.

## Terrain-RGB elevation tiles

Terrain-RGB is available as a server-owned elevation tile surface over the
registered PostGIS raster source for a layer:

| Endpoint | Purpose |
| --- | --- |
| `GET /terrain/{datasetId}/tile.json` | TileJSON 3.0 metadata with Honua source and no-data extensions. |
| `GET /terrain/{datasetId}/{z}/{x}/{y}.png` | 256x256 WebMercator XYZ Terrain-RGB PNG tile. |

Terrain v1 expects one numeric source elevation band, a usable CRS/SRID, and a
consistent source CRS across the dataset. Source no-data and uncovered pixels
are encoded as opaque Terrain-RGB `[0, 0, 0]` (`-10000m`), including tiles that
are entirely outside raster coverage. See [Terrain-RGB Elevation Tiles](terrain-tiles.md)
for the client contract.

## Cache and observability behavior

Raster import invalidates the affected layer's output-cache entries after a
successful commit, including the `terrain` tag used by Terrain-RGB TileJSON and
finite-grid tile policies. Admin service, collection, and all-cache invalidation
also evict the same terrain tag. It does not enable exact response caching for
arbitrary raster windows.

COG metadata uses an in-memory cache keyed as `cog:metadata:{id}` with a
30-minute sliding expiration. `DELETE /api/v1/admin/cloud-rasters/{id}` and
`POST /api/v1/admin/cloud-rasters/{id}/refresh` remove that cache entry.
Persisted metadata stores overview summaries today; tile offset arrays may
still require a cloud scan on cold cache.

The shipped paths preserve observable signals for raster import progress, COG
registration, metadata scans, direct COG tile serving, OGC API Coverages exports,
unsupported compression, and non-web-mercator CRS warnings. OGC API Coverages
collection list/detail, schema, and coverage byte routes are not output-cached;
only bounded metadata resources such as landing, conformance, and OpenAPI use
output-cache policies.

## Remaining roadmap

| Issue | Scope |
| --- | --- |
| `#522` | Complete true multi-raster mosaic dataset behavior and raster catalog workflows beyond the MVP layer-level selection/composition paths. |
| Future child if prioritized | Add async/background raster import queueing separate from `#522`. |

Protocol follow-ons must stay as thin adapters over the shared raster store,
rendering, metadata, validation, cache, authorization, and telemetry
infrastructure.
