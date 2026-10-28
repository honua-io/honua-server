---
type: reference
title: "Cloud-native formats"
description: "Honua's support for the cloud-native geospatial format family: what each format is used for, the endpoints involved, and an honest status per format."
resource: "honua://capability/raster.cloud-cog-serving"
resources:
  - "honua://capability/raster.multidim-coverage"
  - "honua://capability/raster.cloud-storage-config"
---
# Cloud-native formats

Honua's support for the cloud-native geospatial format family: what each format is used for, the endpoints involved, and an honest status per format. Formats fall into three roles — **registered sources** (data stays in object storage, Honua serves it), **produced artifacts** (Honua generates them), and **wire formats** (query output negotiation).

## Status summary

| Format | Role | Register / produce | Serve / consume | Status |
|---|---|---|---|---|
| COG / GeoTIFF | Registered source + import | `POST /api/v1/admin/cloud-rasters` (S3/Azure, in place) or file import `POST /api/v1/admin/import/raster` | Registered cloud objects: ImageServer WebMercatorQuad tiles only; imported GeoTIFFs use the normal raster pipeline | COG is a 2026.1 GA target; direct serving is limited to the documented tile workflow |
| PMTiles | Produced artifact | Tile-operations jobs (`archive`, `publish`) — see [Publish tiles](../../guides/publish/publish-tiles.md) | `GET`/`HEAD /api/v1/tiles/pmtiles/{artifactId}` (HTTP range requests) | Serving live |
| GeoParquet | Import + wire format | File import (`.parquet`, `.geoparquet`) | FeatureServer `f=parquet` (GeoParquet 1.1.0, shared response formatter) | Live |
| GeoArrow | Wire format | — | FeatureServer `f=arrow` (Arrow IPC stream, shared response formatter) | Live |
| FlatGeobuf | Import + wire format | File import (`.fgb`) | FeatureServer `f=fgb` (native store output, not the shared formatter) | Live |
| Zarr | Registered source | `POST /api/v1/admin/zarr-stores` (CRUD + `/refresh`) | OGC API Coverages pixel subsets (`ZarrCoverageService`) | Registration + serving live |
| Cloud-optimized HDF5 / NetCDF4 | Registered source | `POST /api/v1/admin/multidim-coverages` (CRUD; URL-registered, not file-imported); `/refresh` enqueues an async GDAL worker job (202 + jobId/statusUrl) | Metadata extracted + enriched, then auto-converted to Zarr and registered for OGC API Coverages serving | Registration + conversion live; pixel read via the derived Zarr (reader is build-optional) |
| GRIB (`.grib`/`.grb`/`.grb2`/`.grib2`) | Registered source | `POST /api/v1/admin/multidim-coverages` (same path as HDF5/NetCDF) | Same GDAL→Zarr conversion and OGC API Coverages serving | Registration + conversion live |

The [standards and evidence matrix](cloud-native-standards.md) records exact
format targets and qualification limits. A supported format does not imply every
encoding, cloud deployment or publication workflow has passed conformance.

## COG and cloud rasters

Register a raster that already lives in object storage — no copy, no conversion. Cloud tile serving
uses the ImageServer tile fallback:

In the authorized [API explorer](../openapi-and-explorer.md), run `POST /api/v1/admin/cloud-rasters` with `{"layerId":1,"name":"Imagery 2026","provider":"AwsS3","bucket":"my-rasters","objectKey":"imagery/2026.tif"}`.

The registered object is considered only by the ImageServer WebMercatorQuad tile fallback after the
PostGIS tile path. `exportImage`, `identify`, WCS 2.0.1, and OGC API Coverages do not read registered
cloud COGs. The tile object must be an EPSG:3857, GoogleMapsCompatible-aligned COG; other grids fail
closed rather than being reprojected. JPEG tiles serve with `format=jpg`: when the COG stores shared
JPEGTables (TIFF tag 347, GDAL's default `JPEGTABLESMODE=1`), the tables are spliced into each tile so every
response is a complete JPEG stream. 8-bit chunky grayscale, RGB and YCbCr (GDAL's default for three-band
JPEG COGs) tiles are supported; other JPEG layouts are not served. DEFLATE, LZW, ZSTD and uncompressed
chunky unsigned 8/16-bit grayscale or RGB samples serve as lossless PNG (`format=png`, the default), preserving sample depth and nodata transparency. For scientific data, `format=tiff` or `format=cog` preserves unsigned, signed and floating-point samples in a single-tile GeoTIFF with nodata and EPSG:3857 georeferencing. Palettes, separate planes and JPEG conversion from decoded samples are unsupported. The default
`format=png` does not transcode JPEG. Unsupported grids and output formats return GeoServices error code 404 from the tile fallback (an HTTP 200 error envelope); a successful metadata refresh does not guarantee tile delivery. Workflow detail: [Publish rasters](../../guides/publish/publish-rasters.md).

TIFF floating-point predictor 3 is unsupported: prepare floating-point sources with predictor 1.
Complex or undefined TIFF SampleFormat values and malformed JPEGTables are rejected
during metadata extraction, before any tile payload can be returned as an image.

Imported rasters can be deleted (`DELETE /api/v1/admin/import/raster/{rasterId}`) and have their descriptive metadata updated (`PATCH /api/v1/admin/import/raster/{rasterId}` — `name`/`description`/`acquisitionDate`); cloud-registered COGs use `DELETE /api/v1/admin/cloud-rasters/{id}`. These admin operations are the canonical equivalents of Esri ImageServer's `deleteRasters`/`updateRaster` — see the [ImageServer admin-op mapping](../compatibility/imageserver-admin-mapping.md).

Cloud COG `layerId` is the service-local publication index, not the backing storage layer ID.
It must identify one routable publication across the catalog; colliding indexes fail closed.
COG is a 2026.1 GA target through the documented direct tile workflow.
Separately, the COG artifact publication service requests `RasterFormat.COG` from
the raster store, publishes the result to file storage, and exposes authorized
GET/HEAD range access. Published-file layout and deployed delivery need their own
independent evidence; the tile-transcode conformance cell does not cover them.
COG export requires a writable native GDAL COG driver on the actual database connection;
an unavailable driver fails explicitly, without substituting GeoTIFF bytes. Empty canvases
use the same COG creation options as populated exports, including internal overviews.
Real PostGIS publication regressions cover a 1024×1024 all-nodata raster, stored-byte
GET/HEAD/range fidelity and unavailable-driver failure. Independent strict rio-cogeo
validation and Rasterio window reads qualify those fixture bytes; deployed HTTPS,
CORS and cloud transfer behavior require separate deployment evidence.

Optional per-raster **sensor metadata** (sensor name, camera model, interior/exterior orientation, RPC, DEM source) can be modeled in the `raster_sensor_metadata` companion table. When present it powers ImageServer DEM-backed height mensuration, orientation-ranked `find`, and RPC image-coordinate-system `project` warps; plain rasters with no sensor metadata serve normally and those features degrade gracefully.

## PMTiles

Tile-operations jobs can `archive` a layer's tiles into a single PMTiles file and `publish` it; the artifact is served with HTTP range support at `/api/v1/tiles/pmtiles/{artifactId}`, which makes it suitable for CDN fronting or serverless map hosting. Workflow detail and URL strategies: [Publish tiles](../../guides/publish/publish-tiles.md#pmtiles).

## Analytics wire formats

FeatureServer `f=parquet`, `f=arrow`, and `f=fgb` are query formats on the shared
FeatureServer output path. `f=parquet` (GeoParquet 1.1.0) and `f=arrow` (Arrow IPC
stream) are encoded by the shared response formatters from the canonical feature
stream, so they are not limited to one database provider. `f=fgb` is not encoded
by that formatter. FeatureServer serves it only when the resolved reader implements
native FlatGeobuf output. That implementation is the PostgreSQL/PostGIS feature
stores. Other providers, including Amazon Redshift, Snowflake, and Databricks, do
not, and `f=fgb` is rejected for those layers.

Verified coverage:

- GeoParquet and GeoArrow formatter tests encode payloads with no database provider.
- On the PostgreSQL/PostGIS integration host, FeatureServer query tests cover
  successful `f=parquet` and `f=fgb`, and the `f=arrow` rejection for `returnM=true`.
  MapServer query tests on that host cover successful `f=parquet` and `f=arrow`.
- Amazon Redshift, Snowflake, and Databricks have no `f=parquet`, `f=arrow`, or
  `f=fgb` tests. Their nightly lane, when credentials are configured, checks live
  query only.

Native provider exports (for example MVT) remain provider-specific. See [Export data](../../guides/query-analyze/export-data.md)
and the [data formats matrix](../data-formats.md).

## Zarr and multidimensional coverages (HDF5/NetCDF/GRIB)

NetCDF4, HDF5, and GRIB sources are registered via `POST /api/v1/admin/multidim-coverages` (URL-registered, not file-imported). Calling `/api/v1/admin/multidim-coverages/{id}/refresh` enqueues an async GDAL worker job (returns `202` with a `jobId`/`statusUrl`) that runs `gdalmdiminfo` + `gdalinfo` to extract and enrich structure and metadata — variables, dimensions, chunk layout, compression, CF attributes, nodata, spatial extent, cell resolution, and CF-decoded temporal/vertical bounds (best-effort, tolerant of missing fields) — and `gdalmdimtranslate -of Zarr` to convert the source to a derived Zarr written beside it in cloud storage. The derived Zarr is then registered as a sibling coverage and served through the existing Zarr coverage path (`ZarrCoverageService`) over **OGC API Coverages** (`GetCoverage`, including `datetime` temporal subsetting and coordinate-valued `subset=<axis>(...)` on declared additional vertical/elevation/named axes). The reader remains optional per build (`MultidimensionalCoverage` reader); when the feature is disabled, `/refresh` returns `501`. The end-to-end pixel read path runs against cloud object storage and the GDAL worker (`ubuntu-full` image for the NetCDF/HDF5/GRIB drivers). Per-slice multidimensional point sampling on GeoServices ImageServer `identify` and `getSamples` is supported through the bounded canonical point-slice reader. ImageServer `exportImage` also reads a coordinate-selected 2D window through the canonical Zarr subset planner and managed PNG renderer for the native-CRS grayscale PNG combination (`RSP_NearestNeighbor`); unsupported format, reprojection, interpolation, and raster-function combinations are rejected explicitly. Classic WCS `GetCoverage` uses the same bounded native-CRS PNG slice reader for single coordinate selections on registered additional axes. Layers without a readable Zarr store remain metadata-only and return an explicit `OperationNotSupported` response rather than sampling the dimension-collapsed raster.

Conversion can copy the source to worker scratch storage during metadata fallback;
it is not a guarantee of source-native bounded HDF5/NetCDF access. The optional
rehearsal in [CNG status](../../cng-status.md) is engineering evidence only.

The Zarr reader (`ZarrMetadataExtractor`/`ZarrSubsetReader`) reads both **Zarr v2** (`.zgroup`/`.zarray`/`.zattrs`) and **Zarr v3** (`zarr.json`, `node_type` group/array). For v3 it normalizes the `data_type` name (e.g. `float32` → numpy `<f4`), reads the `c/`-prefixed default chunk-key encoding (or the `v2` dotted encoding), and gates the codec pipeline — uncompressed and `gzip`-coded little-endian chunks are supported; `blosc`, `zstd`, sharding, `crc32c`, and big-endian are rejected cleanly.

### Zarr metadata, chunk integrity, and compatibility

The supported reader validates mandatory metadata at each discovered node against the [Zarr v2 specification](https://zarr-specs.readthedocs.io/en/latest/v2/v2.0.html) and [v3 core specification](https://zarr-specs.readthedocs.io/en/latest/v3/core/index.html). V2 array extension fields remain ignorable; v2 groups reject extra fields. V3 unknown required extensions are rejected, while explicitly ignorable extensions still need valid extension metadata. Built-in v3 data types use string identifiers. Supported v3.1 shorthands include `bytes` for one-byte numeric/boolean samples and `default`/`v2` chunk-key encodings; multi-byte samples require an explicit little-endian bytes configuration. The supported v3 pipeline remains one bytes codec followed by at most one gzip codec. Other codecs, general v3 hierarchy discovery, consolidated-only v3 discovery, scalar arrays, and empty coverage dimensions remain explicitly unsupported.

Missing chunks preserve boolean, signed/unsigned integer, and floating-point fills in the binary subset buffer, including exact 64-bit integers and hexadecimal floating-point bits. Numeric floating fills follow IEEE round-half-to-even. Existing empty objects are not treated as missing chunks. Uncompressed chunks must match their full declared chunk shape, including edge padding. Zlib and gzip reads validate complete streams and checksums; gzip also validates optional header CRCs, member sizes, and concatenated members. Truncation, excess decoded samples, and trailing junk fail explicitly. Previously accepted malformed metadata or inconsistent chunks must be corrected before they can be served.

Resource limits remain 4,096 requested chunks, 256 MiB of output and per decoded chunk, 257 MiB per compressed chunk, and eight concurrent chunk reads. Gzip decoding additionally permits at most 4,096 members per chunk, with cancellation checked during bounded decoding/checksum/fill work. These are serving resource limits. Binary subsets retain source dtype precision; point sampling and rendering still use doubles, and numeric schema metadata omits fill sentinels that cannot be represented exactly as finite doubles.

Registered Zarr coverages can also be rendered as PNG map tiles via `GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}` (optional `variable`, `datetime`, and `elevation` grid-index query parameters). This is a read-only serving surface that does not advertise an OGC tiles conformance class. The tile bbox is mapped to a half-open grid-index window, the time and vertical axes are resolved through the shared CF axis indexers, the bounded slice is read through the Zarr subset pipeline, and the dtype buffer is colour-mapped and encoded by the managed AOT-safe PNG encoder. Temporal parsing uses the neutral Core-level `Iso8601TemporalIntervalParser` so the datacube tile path takes no dependency on any OGC protocol family. The tile gridset CRS must match the coverage storage CRS today; cross-CRS reprojection of the tile window is a follow-up.

## Related

- [Data formats matrix](../data-formats.md) — full import/export format support
- [STAC](stac.md) — catalog discovery for these assets
- [OGC APIs](ogc-apis.md) — coverage routes for imported rasters
