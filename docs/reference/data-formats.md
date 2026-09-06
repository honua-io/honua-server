# Data formats

Import and export format matrix: which formats Honua ingests, and which output formats each query surface serves. All lists below are verified against the running format registries; the live lists are also served at `GET /api/v1/admin/import/formats`, `GET /api/v1/admin/import/raster/formats`, and `GET /api/v1/admin/import/limits`.

## Vector import formats

Uploaded via `POST /api/v1/admin/import/upload` (or `upload-url`, with `preview`/`preview-url` for inspection first).

| Format | Extensions | Notes |
| --- | --- | --- |
| GeoJSON | `.geojson`, `.json` | FeatureCollection, Feature, or bare geometry. |
| Shapefile | `.zip` | Must be a zip containing `.shp`/`.dbf` (plus `.shx`/`.prj`); bare `.shp` uploads are rejected. |
| GeoPackage | `.gpkg` | OGC SQLite-based format. BOOLEAN, DATE and DATETIME column types are preserved on import/export. DATETIME output uses UTC timestamps ending in `Z` and retains fractional precision; timestamps without an offset are treated as UTC. GeoServices epoch-millisecond date values are converted to UTC without rounding fractional milliseconds. DATE values use `yyyy-MM-dd`; DATETIME input must include the ISO date and time components. Invalid logical or temporal values produce validation errors. Source attributes named `fid` or `geom` retain their names; generated structural columns use unused names. |
| GPX | `.gpx` | GPS exchange format. Track segment boundaries are preserved as separate lines; singleton segments remain points, with a geometry collection for mixed tracks. Track and route elevations are preserved as Z ordinates; absent elevation remains absent, and invalid elevation rejects the import. |
| KML / KMZ | `.kml`, `.kmz` | Keyhole Markup Language, plain or zipped. |
| WKT | `.wkt` | Well-known text geometries. |
| CSV | `.csv` | Needs lon/lat columns or a WKT geometry column. WKT geometry preserves XY, Z and M ordinates on import and bulk export. Bulk, OGC API Features and WFS CSV output share null/empty string escaping. |
| FlatGeobuf | `.fgb` | Compact binary format. |
| File Geodatabase | `.gdb.zip` | Zipped Esri `.gdb` directory. |
| GeoParquet | `.parquet`, `.geoparquet` | Apache Parquet with WKB geometry encoding. |

Format detection uses the extension plus content magic numbers (ZIP, SQLite, FlatGeobuf, Parquet signatures), so mislabeled files are caught early.

CSV preserves quoted empty strings (`""`) and whitespace-only attribute values. Unquoted empty and missing fields represent null. Leading blank padding before a header is ignored; whitespace-only data rows after it remain present.

### Import size limits

| Limit | Default | Variable |
| --- | --- | --- |
| Preview | 10 MiB | `Limits__Imports__MaxPreviewSize` |
| Synchronous import | 50 MiB | `Limits__Imports__MaxSyncImportSize` |
| Any import (async job) | 500 MiB | `Limits__Imports__MaxImportSize` |
| Preview feature count | 100 | `Limits__Imports__MaxPreviewFeatures` |

Files above the synchronous threshold run as background jobs (`/api/v1/admin/import/jobs`).

## Raster import formats

Uploaded via `POST /api/v1/admin/import/raster` (sync limit `Limits__Imports__MaxSyncImportSize`).

| Format | Extensions | Notes |
| --- | --- | --- |
| GeoTIFF / COG | `.tif`, `.tiff` | Embedded CRS and geotransform; cloud-optimized GeoTIFF accepted. |
| PNG + world file | `.png` (+ `.pgw`) | Requires sidecar georeferencing. |
| JPEG + world file | `.jpg`, `.jpeg` (+ `.jgw`) | Requires sidecar georeferencing. |

Cloud-optimized HDF5 (`.h5`/`.hdf5`) and NetCDF4 multidimensional coverages are not file-uploaded; they are registered by URL via `POST /api/v1/admin/multidim-coverages` and served through OGC API Coverages. Metadata extraction for them requires a build with the HDF/NetCDF reader enabled.

## Export and output formats by query surface

### GeoServices REST (`/rest/services/.../FeatureServer/{layer}/query`, `f=` parameter)

| `f` value | Media type | Notes |
| --- | --- | --- |
| `json`, `pjson` | `application/json` | Esri JSON feature sets (default). |
| `geojson` | `application/geo+json` | GeoJSON FeatureCollection. |
| `pbf` | `application/x-protobuf` | Esri FeatureCollection protocol buffers. |
| `fgb` | FlatGeobuf | Encoded export; PostGIS-backed layers, including source-backed (provider-routed) layers. |
| `geobuf` | `application/geobuf` | Encoded export; PostGIS-backed layers, including source-backed (provider-routed) layers. |
| `parquet` | `application/vnd.apache.parquet` | GeoParquet 1.1.0, WKB geometry + authoritative PROJJSON `geo` metadata. Honors resolved `outSR` values (EPSG:4326 returns OGC:CRS84); rejects unresolvable `outSR`. `returnGeometry=false` short-circuits feature re-projection. Requires a glibc-based runtime image (see [runtime requirement](#geoparquet-runtime-requirement)). |
| `arrow` | `application/vnd.apache.arrow.stream` | Arrow IPC stream with `geoarrow.wkb` extension type and GeoParquet-style `geo` schema metadata. Pure-managed encoder (Apache.Arrow); no native runtime dependency. |

`fgb`, `geobuf`, `parquet`, and `arrow` are advertised in `supportedQueryFormats` only when the backing feature store can emit encoded output (PostGIS); other providers return HTTP 400 for them. `fgb`/`geobuf` work on both natively-stored and source-backed (provider-routed) PostGIS layers — the storage-mapped reader builds `ST_AsFlatGeobuf`/`ST_AsGeobuf` over the mapped table. Content negotiation via `Accept` headers maps to the same formats. Binary formats carry no `exceededTransferLimit` flag — page with `resultOffset`/`resultRecordCount` for complete exports.

#### GeoParquet runtime requirement

`f=parquet` (and OData `$format=parquet`) is encoded by the native ParquetSharp Arrow library (`ParquetSharpNative`), which ships only a glibc (`linux-x64`) binary. On a glibc-based runtime image (the Debian/Ubuntu `mcr.microsoft.com/dotnet/aspnet:10.0` image, RID `linux-x64`) GeoParquet output works normally. On the Alpine/musl image (`...:10.0-alpine`, RID `linux-musl-x64`) the native library cannot load; rather than returning an unhandled HTTP 500, the server detects the native-load failure and returns a clean **HTTP 501 Not Implemented** capability response (`GeoParquet (f=parquet) output is unavailable on this runtime image...`). `f=arrow` is unaffected because GeoArrow is encoded with the pure-managed Apache.Arrow IPC writer. To serve GeoParquet, deploy a glibc-based runtime image.

### OGC API Features (`/ogc/features/collections/{id}/items`, `f=` parameter or `Accept`)

| `f` value | Media type |
| --- | --- |
| `geojson` (default) | `application/geo+json` |
| `json` | `application/json` |
| `gml` | GML 3.2 (`application/gml+xml`) |
| `csv` | `text/csv` |
| `html` | `text/html` |

### WFS 2.0 (`/wfs`, `outputFormat` parameter)

| Format | Notes |
| --- | --- |
| GML 3.2 | Default (`application/gml+xml; version=3.2`); the only format for the `GetFeatureById` stored query. |
| GeoJSON | `application/geo+json`. |
| JSON | `application/json`. |
| CSV | `text/csv` (or `csv`). Uses shared CSV escaping: null is an unquoted blank; an empty string is `""`. |


### OData v4 (`/odata`)

JSON (`application/json`) responses by default; geometry is emitted as GeoJSON-shaped values inside the JSON payload. `$format=parquet` exports GeoParquet via the shared writer (same [glibc runtime requirement](#geoparquet-runtime-requirement) — returns HTTP 501 on a musl image). See the [OData reference](protocols/odata.md).

### Admin layer export (`GET /api/v1/admin/services/{service}/layers/{layerId}/export?format=...`)

| Format | Notes |
| --- | --- |
| `csv` | Attribute + geometry text export. |
| `shapefile` | Zipped shapefile; rejected for mixed-geometry layers. |
| `gpkg` | GeoPackage. |

Large exports queue as background jobs. See [export data guide](../guides/query-analyze/export-data.md).

The capability registry distinguishes `format.read.<name>` (file import) from
`format.write.<name>` (built-in admin file export). Only `csv`, `shapefile`, and
`geopackage` have implemented file-write descriptors; the GeoPackage endpoint token
is `gpkg`. The other ten file-write descriptors are planned, known implementation
gaps and resolve disabled. Plugin formats are available only when their writers
are installed and enabled.

The original `format.<name>` identifiers describe shared codec and protocol format
support. They do not promise a writer at the admin export endpoint. For example,
GeoJSON query responses and GeoParquet query exports remain supported through the
protocol routes listed above even though those formats have no built-in admin
file-export writer.

### Tiles, maps, and coverages

| Surface | Output |
| --- | --- |
| Vector tiles (`/ogc/tiles`, TileJSON) | Mapbox Vector Tiles (protobuf), extent 4096, buffer 256. |
| MapServer `/export`, WMS `GetMap` | Rendered map images. |
| ImageServer `/exportImage`, OGC API Coverages, WCS 2.0.1 | Raster/coverage output. |
| MapServer KML (`generateKml`) | KML / KMZ. |

See the protocol pages for parameters: [vector tiles](protocols/vector-tiles.md), [WMS/WFS/WCS/WMTS](protocols/wms-wfs-wcs-wmts.md), [GeoServices REST](protocols/geoservices-rest.md).

### gRPC

`geospatial.v1.FeatureService` streams protobuf-encoded features; see the [gRPC reference](protocols/grpc.md).

## Related pages

- [Import files guide](../guides/publish/import-files.md)
- [Export data guide](../guides/query-analyze/export-data.md)
- [Environment variables — imports and limits](configuration/environment-variables.md#imports-and-limits)

New import tables retain source geometry ordinate dimensions while enforcing the requested SRID. Migration 111 enables this for GPX elevations and other dimensional input. Existing append/upsert tables keep their declared geometry constraints.
