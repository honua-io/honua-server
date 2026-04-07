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
| Query | `/rest/services/{serviceName}/ImageServer/query` | GET, POST | Partial | `GET\|POST /rest/services/{id}/ImageServer/query` | Returns the layer's raster catalog as Esri-compatible features with `OBJECTID`, footprint geometry, and pixel-size attributes. Supports `where`, `objectIds`, `outSR`, `resultOffset`, `resultRecordCount`, and the `returnGeometry`/`returnIdsOnly`/`returnCountOnly`/`returnExtentOnly` shaping flags. The MVP applies WHERE filters via the GeoServices SQL parser in-memory and does not reproject footprint geometry — `outSR` is honoured for the response envelope only. |
| Compute Statistics and Histograms | `/rest/services/{serviceName}/ImageServer/computeStatisticsHistograms` | GET, POST | Partial | `GET\|POST /rest/services/{id}/ImageServer/computeStatisticsHistograms` | Returns per-band statistics and histograms for one or more rasters in the catalog. Supports `rasterIds` (catalog object IDs), the Honua-specific `bandIds` selector, and `histogramParameters.size` for bin count. AOI clipping (`geometry`/`mosaicRule`/`renderingRule`) is not yet honoured — analysis always covers the full raster. |

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
| Compute Tie Points | `/rest/services/{serviceName}/ImageServer/computeTiePoints` | GET, POST | Not implemented | |
| Delete Rasters | `/rest/services/{serviceName}/ImageServer/deleteRasters` | POST | Not implemented | |
| Download Rasters | `/rest/services/{serviceName}/ImageServer/downloadRasters` | GET, POST | Not implemented | |
| Export Tiles | `/rest/services/{serviceName}/ImageServer/exportTiles` | POST | Not implemented | |
| Find | `/rest/services/{serviceName}/ImageServer/find` | GET, POST | Not implemented | |
| Get Samples | `/rest/services/{serviceName}/ImageServer/getSamples` | GET, POST | Not implemented | |
| Measure | `/rest/services/{serviceName}/ImageServer/measure` | GET, POST | Not implemented | |
| Project | `/rest/services/{serviceName}/ImageServer/project` | GET, POST | Not implemented | |
| Query Boundary | `/rest/services/{serviceName}/ImageServer/queryBoundary` | GET, POST | Not implemented | |
| Update Raster | `/rest/services/{serviceName}/ImageServer/updateRaster` | POST | Not implemented | |
| Uploads | `/rest/services/{serviceName}/ImageServer/uploads` | GET, POST | Not implemented | |
| Validate | `/rest/services/{serviceName}/ImageServer/validate` | GET, POST | Not implemented | |

## Image Service child resources

### Partial

| Esri child resource | Esri path | Honua status | Notes |
| --- | --- | --- | --- |
| Legend | `.../ImageServer/legend` | Partial | `GET /rest/services/{id}/ImageServer/legend` returns Esri-shaped `layers[].legend[]` swatches as base64 PNGs (`image/png`). The MVP renders a fixed 5-class equal-interval ramp keyed off the primary raster's band-1 statistics (`min`, `max`); per-layer renderer persistence and classification overrides via `renderingRule` are not yet honoured. Only `f=json`/`f=pjson` is accepted. |

### Not implemented

| Esri child resource | Esri path | Honua status | Notes |
| --- | --- | --- | --- |
| Colormap | `.../ImageServer/colormap` | Not implemented | |
| Histograms | `.../ImageServer/histograms` | Not implemented | |
| Image Service Info (`iteminfo`, `metadata`, `thumbnail`) | `.../ImageServer/info/*` | Not implemented | |
| Image Support Data | `.../ImageServer/imageSupportData` | Not implemented | |
| Key Properties | `.../ImageServer/keyProperties` | Not implemented | |
| KML Image | `.../ImageServer/kml` | Not implemented | |
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
| `imageSR` | Implemented | Accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), and `CRS84` aliases. |
| `bboxSR` | Implemented | Accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), and `CRS84` aliases. |
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
| `sr` | Implemented | Accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), and `CRS84` aliases. |
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

### Query (`GET|POST .../ImageServer/query`)

#### Implemented

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Only `json` and `pjson` are supported. |
| `where` | Partial | Parsed via the shared GeoServices SQL filter parser and applied in-memory against catalog metadata fields (`OBJECTID`, `Name`, `MinPS`, `MaxPS`, `LowPS`, `HighPS`, `CenterX`, `CenterY`, `ZOrder`, `Shape_Length`, `Shape_Area`, `AcquisitionDate`). Limited to 2000 characters. |
| `objectIds` | Implemented | Accepts CSV (`1,3,5`) or JSON array (`[1,3,5]`) form. |
| `outSR` | Partial | Stamped onto the response `spatialReference`. Footprint geometry is NOT reprojected — clients must inspect each feature's geometry-level `spatialReference` to detect that the rings remain in the raster's native SRID. |
| `resultOffset` | Implemented | Non-negative integer offset, defaults to `0`. |
| `resultRecordCount` | Implemented | Defaults to `100`, capped at `1000`. |
| `returnGeometry` | Implemented | When `false`, suppresses the `geometry` envelope on each feature. |
| `returnIdsOnly` | Implemented | Returns `objectIdFieldName` + `objectIds[]` shape. |
| `returnCountOnly` | Implemented | Returns `count`-only response. |
| `returnExtentOnly` | Implemented | Returns the aggregate extent (computed after `where`/`objectIds` filters but before pagination). |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `geometry` / `geometryType` / `inSR` / `spatialRel` | Spatial filtering against arbitrary client geometries is not yet supported by the catalog reader. |
| `outFields` | Catalog responses always include the full attribute set; per-field projection is not yet honoured. |
| `orderByFields` | Ordering is currently the catalog's natural order. |
| `time` | Temporal filtering is not yet honoured. |
| `pixelSize` | Not honoured. |

### Compute Statistics and Histograms (`GET|POST .../ImageServer/computeStatisticsHistograms`)

#### Implemented

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Only `json` and `pjson` are supported. |
| `rasterIds` | Implemented | Accepts CSV (`1,3`) or JSON array (`[1,3]`) of catalog object IDs. When omitted, defaults to the layer's primary raster. Unknown IDs return `400 Bad Request`. |
| `bandIds` | Implemented (Honua extension) | Accepts CSV or JSON array of 1-based band indices. Forwarded to the raster store as the band selector. |
| `histogramParameters.size` | Implemented | Bin count, capped at `1024`. Default is `256` when omitted. |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `geometry` / `geometryType` / `mosaicRule` | AOI clipping is not yet honoured — analysis always covers the full raster. |
| `renderingRule` | Not honoured. |
| `pixelSize` | Not honoured. |

### Legend (`GET .../ImageServer/legend`)

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Only `json` and `pjson` are supported. Other formats return `400 Bad Request`. |
| `renderingRule` | Not honoured | Classification overrides through a custom rendering rule are ignored — swatches are always rendered from a fixed 5-class equal-interval viridis ramp keyed off the primary raster's band-1 statistics (`min`, `max`). |

## Honua extensions

These endpoints are exposed under the ImageServer route prefix for parity with Esri client SDKs but do not have an exact Esri counterpart.

| Honua endpoint | Methods | Notes |
| --- | --- | --- |
| `/rest/services/{id}/ImageServer/computeClass` | GET, POST | Validates a raster function chain document supplied via `renderingRule` (or the legacy `rasterFunction` alias) and returns the planned execution metadata (`rasterFunction`, `chainDepth`, `executedFunctions`, `outputPixelType`, `status`). Closest in spirit to Esri's `validate` operation; the route name aligns with the Esri `computeClass`/analyze contract used by ArcGIS Pro. The MVP planner walks the canonical ArcGIS Pro stretch-and-clip chain — `Identity`, `Stretch`, and `Clip` — with a maximum chain depth of `8` and rejects unknown function names with a `400` naming the offending node. `Stretch` requires an integer `StretchType` (Esri `esriRasterStretchType`); `Clip` requires either `ClippingGeometry` or `Extent`. The output pixel type defaults to `U8` when the chain includes `Stretch` and `F32` otherwise unless the document overrides `outputPixelType`. The endpoint currently validates and plans the chain only — the executor is not yet wired into `exportImage` / `computeStatisticsHistograms`. |

### Computed parameter coverage (`POST .../ImageServer/computeClass`)

| Esri/Honua parameter | Honua status | Notes |
| --- | --- | --- |
| `renderingRule` (or `rasterFunction` alias) | Implemented | JSON-encoded raster function chain document. Required. |
| `f` | Partial | Only `json` and `pjson` are supported. |

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
| `capabilities` | Implemented | Advertised as `Catalog,Image,Metadata,Pixels,Statistics,Tilemap`. `Mensuration` is intentionally omitted until the `/measure` endpoint ships so the capability list stays in lockstep with routed operations. |
| `maxImageHeight`, `maxImageWidth`, `maxRecordCount` | Implemented | Static Honua metadata limits. |
| `singleFusedMapCache`, `cacheType` | Implemented | Always reports `true` / `Map` to advertise the rendered tile cache surface. |
| `tileInfo` | Implemented | Generated from a fixed Web Mercator (EPSG:3857) LOD grid (256×256 tiles, 96 DPI, PNG) sized for `MaxTileZoom`. |
| `hasHistograms` | Implemented | Always `true`; ImageServer exposes `computeStatisticsHistograms` for the catalog. |
| `timeInfo` | Partial | Surfaced only when the layer metadata declares any of `startTimeField`/`endTimeField`/`trackIdField`. The temporal extent is intentionally omitted because raster catalog items do not yet carry per-item timestamps. |
| `hasMultidimensions` | Implemented | Always emitted; defaults to `false` until cube ingestion ships. |
| `multidimensionalInfo` | Partial | Skeleton type exists in the response model but is omitted (`JsonIgnoreCondition.WhenWritingNull`) until multidimensional ingestion lands. |

### Not implemented or currently omitted

| Property or resource family | Notes |
| --- | --- |
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
- Catalog query implementation: [ImageServerCatalogQueryHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerCatalogQueryHandler.cs), [ImageServerCatalogReader](../../src/Honua.Server/Features/ImageServer/Services/ImageServerCatalogReader.cs)
- Statistics/histograms implementation: [ImageServerStatisticsHistogramsHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerStatisticsHistogramsHandler.cs)
- Legend implementation: [ImageServerLegendHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerLegendHandler.cs)
- Raster function chain analysis (`computeClass`): [ImageServerAnalyzeHandler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerAnalyzeHandler.cs), [ImageServerRasterFunctionPlanner](../../src/Honua.Server/Features/ImageServer/Services/ImageServerRasterFunctionPlanner.cs)
- Request/response models: [ImageServerModels](../../src/Honua.Server/Features/ImageServer/Models/ImageServerModels.cs)
- Integration tests: [ImageServerBasicTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerBasicTests.cs), [ImageServerParameterValidationTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerParameterValidationTests.cs), [ImageServerErrorHandlingTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerErrorHandlingTests.cs), [ImageServerEndpointsTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerEndpointsTests.cs)
- Handler unit tests: [ImageServerCatalogQueryHandlerTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerCatalogQueryHandlerTests.cs), [ImageServerStatisticsHistogramsHandlerTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerStatisticsHistogramsHandlerTests.cs), [ImageServerLegendHandlerTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerLegendHandlerTests.cs), [ImageServerAnalyzeHandlerTests](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerAnalyzeHandlerTests.cs)
