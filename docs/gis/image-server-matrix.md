# ImageServer Matrix (Esri Enterprise vs Honua)

Canonical GeoServices entry point: [GeoServices REST Parity](geoservices-rest-parity.md)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/image-service/
- https://developers.arcgis.com/rest/services-reference/enterprise/raster-image/

## Status vocabulary

- Implemented: the Esri operation/resource exists in Honua and the documented behavior is supported.
- Partial: the Esri operation/resource exists, but only a subset of documented parameters or behavior is supported.
- Not implemented: the Esri operation/resource is not exposed by Honua.

## Image Service operations

### Implemented

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Service metadata | `/rest/services/{serviceName}/ImageServer` | GET | Implemented | `GET /rest/services/{id}/ImageServer` | Returns metadata for the primary raster in the addressed layer. Metadata responses are cached with the `ImageServerMetadata` output-cache policy. |
| Image tile | `/rest/services/{serviceName}/ImageServer/tile/{level}/{row}/{col}` | GET | Implemented | `GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}` | Returns raster map tiles. Supports `png` (default), `jpeg`, and `tiff` output; zoom levels are limited to `0-28`. |

### Partial

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Export Image | `/rest/services/{serviceName}/ImageServer/exportImage` | GET | Partial | `GET /rest/services/{id}/ImageServer/exportImage` | Returns a JSON envelope with a temporary `href` to the rendered image. Only a subset of Esri export parameters are applied, and `f=image` byte streaming is not supported. |
| Identify | `/rest/services/{serviceName}/ImageServer/identify` | GET | Partial | `GET /rest/services/{id}/ImageServer/identify` | Supports point-only identify requests and JSON responses. Several Esri response-shaping and raster-processing parameters are currently ignored. |

### Not implemented

| Esri operation | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Add Rasters | `/rest/services/{serviceName}/ImageServer/addRasters` | POST | Not implemented | |
| Calculate Volume | `/rest/services/{serviceName}/ImageServer/calculateVolume` | GET, POST | Not implemented | |
| Compute Cache Info | `/rest/services/{serviceName}/ImageServer/computeCacheInfo` | GET, POST | Not implemented | |
| Compute Class Statistics | `/rest/services/{serviceName}/ImageServer/computeClassStatistics` | GET, POST | Not implemented | |
| Compute Histograms | `/rest/services/{serviceName}/ImageServer/computeHistograms` | GET, POST | Not implemented | |
| Compute Multidimensional Info | `/rest/services/{serviceName}/ImageServer/computeMultidimensionalInfo` | GET, POST | Not implemented | |
| Compute Pixel Location | `/rest/services/{serviceName}/ImageServer/computePixelLocation` | GET, POST | Not implemented | |
| Compute Statistics and Histograms | `/rest/services/{serviceName}/ImageServer/computeStatisticsHistograms` | GET, POST | Not implemented | |
| Compute Tie Points | `/rest/services/{serviceName}/ImageServer/computeTiePoints` | GET, POST | Not implemented | |
| Delete Rasters | `/rest/services/{serviceName}/ImageServer/deleteRasters` | POST | Not implemented | |
| Download Rasters | `/rest/services/{serviceName}/ImageServer/downloadRasters` | GET, POST | Not implemented | |
| Export Tiles | `/rest/services/{serviceName}/ImageServer/exportTiles` | POST | Not implemented | |
| Find | `/rest/services/{serviceName}/ImageServer/find` | GET, POST | Not implemented | |
| Get Samples | `/rest/services/{serviceName}/ImageServer/getSamples` | GET, POST | Not implemented | |
| Measure | `/rest/services/{serviceName}/ImageServer/measure` | GET, POST | Not implemented | |
| Project | `/rest/services/{serviceName}/ImageServer/project` | GET, POST | Not implemented | |
| Query Boundary | `/rest/services/{serviceName}/ImageServer/queryBoundary` | GET, POST | Not implemented | |
| Query | `/rest/services/{serviceName}/ImageServer/query` | GET, POST | Not implemented | |
| Update Raster | `/rest/services/{serviceName}/ImageServer/updateRaster` | POST | Not implemented | |
| Uploads | `/rest/services/{serviceName}/ImageServer/uploads` | GET, POST | Not implemented | |
| Validate | `/rest/services/{serviceName}/ImageServer/validate` | GET, POST | Not implemented | |

## Image Service child resources

### Not implemented

| Esri child resource | Esri path | Honua status | Notes |
| --- | --- | --- | --- |
| Colormap | `.../ImageServer/colormap` | Not implemented | |
| Histograms | `.../ImageServer/histograms` | Not implemented | |
| Image Service Info (`iteminfo`, `metadata`, `thumbnail`) | `.../ImageServer/info/*` | Not implemented | |
| Image Support Data | `.../ImageServer/imageSupportData` | Not implemented | |
| Key Properties | `.../ImageServer/keyProperties` | Not implemented | |
| KML Image | `.../ImageServer/kml` | Not implemented | |
| Legend | `.../ImageServer/legend` | Not implemented | |
| Multidimensional Info | `.../ImageServer/multiDimensionalInfo` | Not implemented | |
| Raster Attribute Table | `.../ImageServer/rasterAttributeTable` | Not implemented | |
| Raster Catalog Item and nested raster resources | `.../ImageServer/{rasterId}/*` | Not implemented | |
| Raster File | `.../ImageServer/rasterFile` | Not implemented | |
| Raster Function Info | `.../ImageServer/rasterFunctionInfos` | Not implemented | |
| Slices | `.../ImageServer/slices` | Not implemented | |
| Statistics | `.../ImageServer/statistics` | Not implemented | |
| WMTS | `.../ImageServer/WMTS` | Not implemented | |

## Parameter coverage

### Export Image (`GET .../ImageServer/exportImage`)

#### Implemented

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `bbox` | Implemented | Envelope clipping region. When omitted, Honua uses the primary raster extent. |
| `imageSR` | Implemented | Accepts numeric WKID text only. |
| `bboxSR` | Implemented | Accepts numeric WKID text only. |
| `format` | Partial | Supports `png`, `jpg`, `jpeg`, `tif`, `tiff`. Esri formats such as `png8`, `png24`, `bmp`, and `gif` are not supported. |
| `interpolation` | Implemented | Parsed into raster resampling behavior. |
| `compressionQuality` | Implemented | Validated to `1-100`. |
| `f` | Partial | Only `json` and `pjson` are supported. `html` and `image` are not supported. |

#### Partial or behavior differences

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `size` | Partial | Honua accepts a single integer and treats it as output width, deriving proportional height from raster aspect ratio. Esri expects `width,height`. |
| `pixelType` | Partial | Input is validated, but the export handler does not apply pixel-type conversion. |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `compression` | Validated to `0-100`, but the export handler currently drops the value and does not map it to TIFF compression behavior. |
| `bandIds` | Accepted by the request model but not applied by the export handler. |
| `mosaicRule` | Accepted by the request model but not applied. |
| `renderingRule` | Accepted by the request model but not applied. |
| `noData` | Accepted by the request model but not applied. |
| `noDataInterpretation` | Accepted by the request model but not applied. |

### Identify (`GET .../ImageServer/identify`)

#### Implemented

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `geometry` | Implemented | Supports comma-separated `x,y` input or JSON point objects. |
| `geometryType` | Partial | Only `esriGeometryPoint` is supported. |
| `sr` | Implemented | Accepts numeric WKID text only. |
| `returnCatalogItems` | Implemented | When `true`, returns the primary raster catalog item in `catalogItems[]`. |
| `f` | Partial | Only `json` and `pjson` are supported. |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `mosaicRule` | Accepted by the request model but not applied. |
| `renderingRule` | Accepted by the request model but not applied. |
| `pixelSize` | Accepted by the request model but not applied. |
| `time` | Accepted by the request model but not applied. |
| `returnGeometry` | Accepted by the request model but not applied; the response always includes `location`. |
| Non-point `geometryType` values | Rejected with `400 Bad Request`. |

### Tile (`GET .../ImageServer/tile/{level}/{row}/{col}`)

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `format` | Implemented | Supports `png`, `jpg`, `jpeg`, `tif`, `tiff`; defaults to `png`. |
| `{level}` / `{row}` / `{col}` | Implemented | Validated as Web Mercator tile coordinates with zoom levels `0-28`. |

## Metadata response coverage (`GET .../ImageServer`)

### Implemented

| Property | Honua status | Notes |
| --- | --- | --- |
| `currentVersion` | Implemented | `10.81` |
| `serviceDescription`, `name`, `description` | Implemented | Derived from layer metadata. |
| `extent`, `spatialReference` | Implemented | Derived from the primary raster extent and SRID. |
| `pixelSizeX`, `pixelSizeY` | Implemented | Calculated from primary raster dimensions and extent. |
| `bandCount`, `pixelType` | Implemented | Derived from the primary raster. |
| `minValues`, `maxValues`, `meanValues`, `stdvValues` | Implemented | Derived from raster statistics for the primary raster. |
| `capabilities` | Implemented | Advertised as `Catalog,Image,Metadata,Pixels`. |
| `maxImageHeight`, `maxImageWidth`, `maxRecordCount` | Implemented | Static Honua metadata limits. |

### Not implemented or currently omitted

| Property or resource family | Notes |
| --- | --- |
| `tileInfo`, `cacheType` | Not currently populated in the metadata response. |
| `objectIdField`, `fields` | Not currently populated. |
| `rasterFunctionInfos`, `rasterTypeInfos`, `mensurationCapabilities` | Not currently populated. |
| Root links to child resources such as legend, metadata, raster catalog items, and WMTS | Not surfaced because the corresponding child resources are not implemented. |

## Known limitations

- The current Honua route shape is layer-scoped: `GET /rest/services/{id}/ImageServer`, where `{id}` is the addressed raster layer identifier rather than a FeatureServer/MapServer-style `{serviceId}`.
- Metadata, export, and identify all operate on the primary raster for the layer. Honua does not currently expose full mosaic-dataset catalog/query behavior.
- Export responses always return JSON with a temporary file URL. Temporary exports are stored through `ITemporaryFileService` and expire after one hour.
- `exportImage` and `identify` accept more request fields than they currently honor. Unsupported fields are intentionally documented here so they are not mistaken for full parity.
- Tile access returns rendered raster tiles only. Honua does not expose ImageServer WMTS or offline tile-export workflows.

## Implementation evidence

- Endpoint mapping: [ImageServer endpoints](../../src/Honua.Server/Features/ImageServer/ImageServerEndpoints.cs)
- Metadata implementation: [ImageServerMetadataHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerMetadataHandler.cs)
- Export implementation: [ImageServerExportHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerExportHandler.cs)
- Identify implementation: [ImageServerIdentifyHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerIdentifyHandler.cs)
- Tile implementation: [ImageServerTileHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerTileHandler.cs)
- Request/response models: [ImageServerModels](../../src/Honua.Server/Features/ImageServer/Models/ImageServerModels.cs)
- Integration tests: [ImageServerBasicTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerBasicTests.cs), [ImageServerParameterValidationTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerParameterValidationTests.cs), [ImageServerErrorHandlingTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerErrorHandlingTests.cs)
