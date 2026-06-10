# Data formats

Import and export format matrix: which formats Honua ingests, and which output formats each query surface serves. All lists below are verified against the running format registries; the live lists are also served at `GET /api/v1/admin/import/formats`, `GET /api/v1/admin/import/raster/formats`, and `GET /api/v1/admin/import/limits`.

## Vector import formats

Uploaded via `POST /api/v1/admin/import/upload` (or `upload-url`, with `preview`/`preview-url` for inspection first).

| Format | Extensions | Notes |
| --- | --- | --- |
| GeoJSON | `.geojson`, `.json` | FeatureCollection, Feature, or bare geometry. |
| Shapefile | `.zip` | Must be a zip containing `.shp`/`.dbf` (plus `.shx`/`.prj`); bare `.shp` uploads are rejected. |
| GeoPackage | `.gpkg` | OGC SQLite-based format. |
| GPX | `.gpx` | GPS exchange format. |
| KML / KMZ | `.kml`, `.kmz` | Keyhole Markup Language, plain or zipped. |
| WKT | `.wkt` | Well-known text geometries. |
| CSV | `.csv` | Needs lon/lat columns or a WKT geometry column. |
| FlatGeobuf | `.fgb` | Compact binary format. |
| File Geodatabase | `.gdb.zip` | Zipped Esri `.gdb` directory. |
| GeoParquet | `.parquet`, `.geoparquet` | Apache Parquet with WKB geometry encoding. |

Format detection uses the extension plus content magic numbers (ZIP, SQLite, FlatGeobuf, Parquet signatures), so mislabeled files are caught early.

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
| `fgb` | FlatGeobuf | Encoded export; PostGIS-backed layers only. |
| `geobuf` | `application/geobuf` | Encoded export; PostGIS-backed layers only. |
| `parquet` | `application/vnd.apache.parquet` | GeoParquet 1.1.0, WKB geometry + CRS metadata; output reprojected to EPSG:4326 unless `returnGeometry=false`. |
| `arrow` | `application/vnd.apache.arrow.stream` | Arrow IPC stream with `geoarrow.wkb` extension type and GeoParquet-style `geo` schema metadata. |

`fgb`, `geobuf`, `parquet`, and `arrow` are advertised in `supportedQueryFormats` only when the backing feature store can emit encoded output (PostGIS); other providers return HTTP 400 for them. Content negotiation via `Accept` headers maps to the same formats. Binary formats carry no `exceededTransferLimit` flag — page with `resultOffset`/`resultRecordCount` for complete exports.

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
| CSV | `text/csv`. |

### OData v4 (`/odata`)

JSON (`application/json`) responses only; geometry is emitted as GeoJSON-shaped values inside the JSON payload. See the [OData reference](protocols/odata.md).

### Admin layer export (`GET /api/v1/admin/services/{service}/layers/{layerId}/export?format=...`)

| Format | Notes |
| --- | --- |
| `csv` | Attribute + geometry text export. |
| `shapefile` | Zipped shapefile; rejected for mixed-geometry layers. |
| `gpkg` | GeoPackage. |

Large exports queue as background jobs. See [export data guide](../guides/query-analyze/export-data.md).

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
