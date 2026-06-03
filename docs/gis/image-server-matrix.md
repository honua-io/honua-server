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
| Service metadata | `/rest/services/{serviceName}/ImageServer` | GET | Implemented | `GET /rest/services/{id}/ImageServer` | Returns metadata for the addressed layer mosaic, including aggregate extent/time metadata when multiple rasters are present. Metadata responses are cached with the `ImageServerMetadata` output-cache policy. |
| Image tile | `/rest/services/{serviceName}/ImageServer/tile/{level}/{row}/{col}` | GET | Implemented | `GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}` | Returns raster map tiles. Supports `png` (default), `jpeg`, and `tiff` output; zoom levels are limited to `0-28`. When multiple PostGIS rasters overlap the requested tile, Honua renders a mosaic using the resolved merge strategy. If no PostGIS tile is produced, instances with the active `raster.cloud-cog-serving` entitlement can fall back to a registered cloud-hosted COG for the layer. |
| Compute Histograms | `/rest/services/{serviceName}/ImageServer/computeHistograms` | GET, POST | Implemented | `GET\|POST /rest/services/{id}/ImageServer/computeHistograms` | Returns per-band histograms for the rasters intersecting the AOI. Requires `geometry` and `geometryType` (`esriGeometryEnvelope` or `esriGeometryPolygon`); shares the `computeStatisticsHistograms` core (`rasterIds`, `bandIds`, `mosaicRule`, single-instant `time`, `histogramParameters.size`). |
| Get Samples | `/rest/services/{serviceName}/ImageServer/getSamples` | GET, POST | Implemented | `GET\|POST /rest/services/{id}/ImageServer/getSamples` | Samples pixel values at the points of (or vertices along) the supplied geometry, reusing the shared raster identify/mosaic pipeline. Supports `sampleCount` (capped at 1000), `mosaicRule`, single-instant `time`, and per-point SRID. Each sample carries `location`, `value`, per-band `attributes`, and `resolution`. |

### Partial

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Compute Class Statistics | `/rest/services/{serviceName}/ImageServer/computeClassStatistics` | GET, POST | Partial | `GET\|POST /rest/services/{id}/ImageServer/computeClassStatistics` | Route is wired and validates `classDescriptions` (must be a JSON object with a `classes` array) and the response `f` format. Class-signature computation is not yet implemented — valid requests return `501 Not Implemented` until the signature pipeline lands. |
| Export Image | `/rest/services/{serviceName}/ImageServer/exportImage` | GET | Partial | `GET /rest/services/{id}/ImageServer/exportImage` | Default response is a JSON envelope with a temporary `href` to the rendered image. `f=image` returns the rendered bytes inline with the format-specific media type. Spatially intersecting rasters are exported as a mosaic, with optional `mosaicRule` and single-instant `time` support. |
| Identify | `/rest/services/{serviceName}/ImageServer/identify` | GET | Partial | `GET /rest/services/{id}/ImageServer/identify` | Supports point-only identify requests and JSON responses. When multiple rasters overlap the identify point, Honua returns the mosaic value and can include all participating catalog items. Several Esri response-shaping and raster-processing parameters remain unsupported. |
| Query | `/rest/services/{serviceName}/ImageServer/query` | GET, POST | Partial | `GET\|POST /rest/services/{id}/ImageServer/query` | Returns the layer's raster catalog as Esri-compatible features with `OBJECTID`, footprint geometry, pixel-size attributes, `BandCount`, `PixelType`, `AcquisitionDate`, and `CreatedAt`. Supports `where`, `objectIds`, `outSR`, `time`, `orderByFields`, `outFields`, `resultOffset`, `resultRecordCount`, and the `returnGeometry`/`returnIdsOnly`/`returnCountOnly`/`returnExtentOnly` shaping flags. The MVP applies WHERE filters via the GeoServices SQL parser in-memory and does not reproject footprint geometry — `outSR` is honoured for the response envelope only. |
| Compute Statistics and Histograms | `/rest/services/{serviceName}/ImageServer/computeStatisticsHistograms` | GET, POST | Partial | `GET\|POST /rest/services/{id}/ImageServer/computeStatisticsHistograms` | Returns per-band statistics and histograms for one or more rasters in the catalog. Supports `rasterIds` (catalog object IDs), the Honua-specific `bandIds` selector, `mosaicRule`, single-instant `time`, and `histogramParameters.size` for bin count. AOI clipping via `geometry`/`geometryType` is not yet honoured — analysis always covers the full selected raster or mosaic. |

### Not implemented

| Esri operation | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Add Rasters | `/rest/services/{serviceName}/ImageServer/addRasters` | POST | Not implemented | |
| Calculate Volume | `/rest/services/{serviceName}/ImageServer/calculateVolume` | GET, POST | Not implemented | |
| Compute Cache Info | `/rest/services/{serviceName}/ImageServer/computeCacheInfo` | GET, POST | Not implemented | |
| Compute Multidimensional Info | `/rest/services/{serviceName}/ImageServer/computeMultidimensionalInfo` | GET, POST | Not implemented | |
| Compute Pixel Location | `/rest/services/{serviceName}/ImageServer/computePixelLocation` | GET, POST | Not implemented | |
| Compute Tie Points | `/rest/services/{serviceName}/ImageServer/computeTiePoints` | GET, POST | Not implemented | |
| Delete Rasters | `/rest/services/{serviceName}/ImageServer/deleteRasters` | POST | Not implemented | |
| Download Rasters | `/rest/services/{serviceName}/ImageServer/downloadRasters` | GET, POST | Not implemented | |
| Export Tiles | `/rest/services/{serviceName}/ImageServer/exportTiles` | POST | Not implemented | |
| Find | `/rest/services/{serviceName}/ImageServer/find` | GET, POST | Not implemented | |
| Measure | `/rest/services/{serviceName}/ImageServer/measure` | GET, POST | Not implemented | |
| Project | `/rest/services/{serviceName}/ImageServer/project` | GET, POST | Not implemented | |
| Query Boundary | `/rest/services/{serviceName}/ImageServer/queryBoundary` | GET, POST | Not implemented | |
| Update Raster | `/rest/services/{serviceName}/ImageServer/updateRaster` | POST | Not implemented | |
| Uploads | `/rest/services/{serviceName}/ImageServer/uploads` | GET, POST | Not implemented | |
| Validate | `/rest/services/{serviceName}/ImageServer/validate` | GET, POST | Not implemented | |

## Image Service child resources

### Implemented

| Esri child resource | Esri path | Honua status | Notes |
| --- | --- | --- | --- |
| Key Properties | `.../ImageServer/keyProperties` | Implemented | `GET\|POST /rest/services/{id}/ImageServer/keyProperties` returns the canonical Esri raster key-properties document (per-band `BandProperties`, `DataType`, `BandCount`, `NoDataValue`, `LowCellSize`/`HighCellSize`/`MaxCellSize`) for the layer's primary raster, sourced from the shared raster store metadata. A POST mirror exists because the ArcGIS API for Python `ImageryLayer.key_properties()` issues an HTTP POST. |
| Multidimensional Info | `.../ImageServer/multidimensionalInfo` | Implemented | `GET\|POST /rest/services/{id}/ImageServer/multidimensionalInfo` returns the Esri `multidimensionalInfo` document (variables with dimensions). Non-multidimensional layers return a spec-correct empty document (`{ "variables": [] }`) rather than a 404, matching ArcGIS behaviour for non-cube rasters. |
| Statistics | `.../ImageServer/statistics` | Implemented | `GET\|POST /rest/services/{id}/ImageServer/statistics` returns the Esri `statistics[]` document (per-band `min`, `max`, `mean`, `standardDeviation`, `count`) for the layer's primary raster, or the resolved mosaic when multiple rasters are present. Sourced from the shared raster store statistics pipeline (the same path the legend renderer keys off). |
| Histograms | `.../ImageServer/histograms` | Implemented | `GET\|POST /rest/services/{id}/ImageServer/histograms` returns the Esri `histograms[]` document (per-band `size`, `min`, `max`, `counts[]`) for the primary raster or resolved mosaic, using the default 256-bin count. Shares the raster store histogram pipeline used by `computeStatisticsHistograms`. |
| Raster Function Info | `.../ImageServer/rasterFunctionInfos` | Implemented | `GET\|POST /rest/services/{id}/ImageServer/rasterFunctionInfos` returns the Esri `rasterFunctionInfos[]` document advertising the raster functions the service accepts through `renderingRule` (`None`, `Identity`, `Stretch`, `Clip`), mirroring the shared raster-function planner's supported set so the advertised list stays honest. |
| Raster Attribute Table | `.../ImageServer/rasterAttributeTable` | Implemented | `GET\|POST /rest/services/{id}/ImageServer/rasterAttributeTable` returns the Esri raster attribute table as a feature set (`objectIdFieldName`, `fields[]`, `features[]`). Honua rasters are continuous (non-thematic) and carry no value/attribute table, so the canonical `OBJECTID`/`Value`/`Count` column schema is returned with an empty `features[]` array rather than a 404, matching the document shape ArcGIS clients parse. |

### Partial

| Esri child resource | Esri path | Honua status | Notes |
| --- | --- | --- | --- |
| Legend | `.../ImageServer/legend` | Partial | `GET /rest/services/{id}/ImageServer/legend` returns Esri-shaped `layers[].legend[]` swatches as base64 PNGs (`image/png`). The MVP renders a fixed 5-class equal-interval ramp keyed off the resolved layer mosaic's band-1 statistics (`min`, `max`); per-layer renderer persistence and classification overrides via `renderingRule` are not yet honoured. Only `f=json`/`f=pjson` is accepted. |
| WCS | `.../ImageServer/WCS` | Partial | `GET /rest/services/{id}/ImageServer/WCS` and `GET /ogc/services/{serviceId}/wcs` expose WCS 2.0.1 KVP `GetCapabilities`, `DescribeCoverage`, and `GetCoverage` over the primary raster. Supports `image/tiff`, `image/png`, `image/jpeg`, `SUBSET`/`BBOX` trim, `SUBSETTINGCRS`/`BBOXCRS`, and `OUTPUTCRS`; capabilities advertise native CRSs for visible coverages, and one-axis `SUBSET` is native-CRS only. Range subset, scaling, temporal/multidimensional slicing, and WCS-specific mosaic selection are not yet implemented. |

### Not implemented

| Esri child resource | Esri path | Honua status | Notes |
| --- | --- | --- | --- |
| Colormap | `.../ImageServer/colormap` | Not implemented | |
| Image Service Info (`iteminfo`, `metadata`, `thumbnail`) | `.../ImageServer/info/*` | Not implemented | |
| Image Support Data | `.../ImageServer/imageSupportData` | Not implemented | |
| KML Image | `.../ImageServer/kml` | Not implemented | |
| Raster Catalog Item and nested raster resources | `.../ImageServer/{rasterId}/*` | Not implemented | |
| Raster File | `.../ImageServer/rasterFile` | Not implemented | |
| Slices | `.../ImageServer/slices` | Not implemented | |
| WMTS | `.../ImageServer/WMTS` | Not implemented | |

## Parameter coverage

### Export Image (`GET .../ImageServer/exportImage`)

#### Implemented

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `bbox` | Implemented | Envelope clipping region. When omitted, Honua uses the selected raster mosaic extent. |
| `imageSR` | Implemented | Accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), and `CRS84` aliases. |
| `bboxSR` | Implemented | Accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), and `CRS84` aliases. |
| `format` | Partial | Supports `png`, `jpg`, `jpeg`, `tif`, `tiff`. Esri formats such as `png8`, `png24`, `bmp`, and `gif` are not supported. |
| `interpolation` | Implemented | Parsed into raster resampling behavior. |
| `compressionQuality` | Implemented | Validated to `0-100`. Applied as JPEG `QUALITY` for JPEG output and as `JPEG_QUALITY` when TIFF compression is `JPEG`. |
| `f` | Partial | Supports `json`, `pjson`, and `image` (inline rendered bytes with the format-specific media type). `html` is not supported. |

#### Partial or behavior differences

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `size` | Implemented | Honua accepts Esri-style `width,height` dimensions from `1,1` through `4096,4096`; omitted values default to `400,400`. |
| `pixelType` | Partial | Input is validated, but the export handler does not apply pixel-type conversion. |
| `mosaicRule` | Partial | Accepts simple tokens (`newest`, `oldest`, `average`, `max`, `min`) or an Esri-style JSON object with `mergeStrategy`/`operation`. Applied only when the request intersects multiple rasters. |
| `time` | Partial | Accepts a single ISO 8601 instant. With the active `raster.temporal-mosaic` entitlement, Honua selects the newest layer-wide effective acquisition batch (`AcquisitionDate`, falling back to `CreatedAt`) at or before the timestamp, then applies the export bbox/window. |
| `compression` | Partial | Applied to TIFF output only. Supports `None`, `JPEG`, and `LZ77` (`LZ77` maps to GDAL `DEFLATE`); ignored for non-TIFF outputs. |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `bandIds` | Accepted by the request model but not applied by the export handler. |
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
| `returnCatalogItems` | Implemented | When `true`, returns the raster catalog items participating in the identify result in `catalogItems[]`. |
| `f` | Partial | Only `json` and `pjson` are supported. |

#### Partial or behavior differences

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `mosaicRule` | Partial | Accepts the same merge-strategy tokens as `exportImage` and is applied when the identify point intersects multiple rasters. |
| `time` | Partial | Accepts a single ISO 8601 instant. With the active `raster.temporal-mosaic` entitlement, Honua selects the newest layer-wide effective acquisition batch (`AcquisitionDate`, falling back to `CreatedAt`) at or before the timestamp, then applies the identify point. |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `renderingRule` | Accepted by the request model but not applied. |
| `pixelSize` | Accepted by the request model but not applied. |
| `returnGeometry` | Accepted by the request model but not applied; the response always includes `location`. |
| Non-point `geometryType` values | Rejected with `400 Bad Request`. |

### Tile (`GET .../ImageServer/tile/{level}/{row}/{col}`)

| Parameter | Honua status | Notes |
| --- | --- | --- |
| `format` | Implemented | Supports `png`, `jpg`, `jpeg`, `tif`, `tiff`; defaults to `png`. |
| `{level}` / `{row}` / `{col}` | Implemented | Validated as Web Mercator tile coordinates with zoom levels `0-28`. |
| `mosaicRule` | Partial | Accepts the same merge-strategy tokens as `exportImage` and is applied when multiple rasters overlap the requested tile. |
| `time` | Partial | Accepts a single ISO 8601 instant. With the active `raster.temporal-mosaic` entitlement, Honua selects the newest layer-wide effective acquisition batch (`AcquisitionDate`, falling back to `CreatedAt`) at or before the timestamp, then applies the tile envelope. |

### Query (`GET|POST .../ImageServer/query`)

#### Implemented

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Only `json` and `pjson` are supported. |
| `where` | Partial | Parsed via the shared GeoServices SQL filter parser and applied in-memory against catalog metadata fields (`OBJECTID`, `Name`, `MinPS`, `MaxPS`, `LowPS`, `HighPS`, `CenterX`, `CenterY`, `ZOrder`, `Shape_Length`, `Shape_Area`, `BandCount`, `PixelType`, `AcquisitionDate`, `CreatedAt`). Limited to 2000 characters. |
| `objectIds` | Implemented | Accepts CSV (`1,3,5`) or JSON array (`[1,3,5]`) form. |
| `outSR` | Partial | Stamped onto the response `spatialReference`. Footprint geometry is NOT reprojected — clients must inspect each feature's geometry-level `spatialReference` to detect that the rings remain in the raster's native SRID. |
| `resultOffset` | Implemented | Non-negative integer offset, defaults to `0`. |
| `resultRecordCount` | Implemented | Defaults to `100`, capped at `1000`. |
| `returnGeometry` | Implemented | When `false`, suppresses the `geometry` envelope on each feature. |
| `returnIdsOnly` | Implemented | Returns `objectIdFieldName` + `objectIds[]` shape. |
| `returnCountOnly` | Implemented | Returns `count`-only response. |
| `returnExtentOnly` | Implemented | Returns the aggregate extent (computed after `where`/`objectIds` filters but before pagination). |
| `orderByFields` | Implemented | Comma-separated `field [ASC\|DESC]` terms applied after filtering and before pagination, so paging walks the requested ordering. Multiple terms break ties left-to-right. Numbers and dates sort naturally; other fields use ordinal string comparison; nulls sort first. Unknown field names or sort directions return `400 Bad Request`. |
| `outFields` | Implemented | Comma-separated field selector (or `*` for all). Projects both the feature `attributes` and the `fields[]` schema to the requested set in canonical field order. `OBJECTID` is always retained so clients can correlate features. Unknown field names return `400 Bad Request`. |

#### Partial or behavior differences

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `time` | Partial | Accepts a single ISO 8601 instant. With the active `raster.temporal-mosaic` entitlement, Honua filters the catalog to rasters in the newest layer-wide effective acquisition batch (`AcquisitionDate`, falling back to `CreatedAt`) at or before the timestamp. |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `geometry` / `geometryType` / `inSR` / `spatialRel` | Spatial filtering against arbitrary client geometries is not yet supported by the catalog reader. |
| `pixelSize` | Not honoured. |

### Compute Statistics and Histograms (`GET|POST .../ImageServer/computeStatisticsHistograms`)

#### Implemented

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Only `json` and `pjson` are supported. |
| `rasterIds` | Implemented | Accepts CSV (`1,3`) or JSON array (`[1,3]`) of catalog object IDs. When omitted, defaults to the spatially/temporally selected layer mosaic rather than a single primary raster. Unknown IDs return `400 Bad Request`. |
| `bandIds` | Implemented (Honua extension) | Accepts CSV or JSON array of 1-based band indices. Forwarded to the raster store as the band selector. |
| `histogramParameters.size` | Implemented | Bin count, capped at `1024`. Default is `256` when omitted. |

#### Partial or behavior differences

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `mosaicRule` | Partial | Applied when `rasterIds` is omitted and multiple rasters participate in the selected mosaic. |
| `time` | Partial | Accepts a single ISO 8601 instant. With the active `raster.temporal-mosaic` entitlement, Honua selects the newest layer-wide effective acquisition batch (`AcquisitionDate`, falling back to `CreatedAt`) at or before the timestamp before computing statistics/histograms. |

#### Ignored or not implemented

| Esri parameter | Notes |
| --- | --- |
| `geometry` / `geometryType` | AOI clipping is not yet honoured — analysis always covers the full selected raster or mosaic. |
| `renderingRule` | Not honoured. |
| `pixelSize` | Not honoured. |

#### Response shape notes

The Esri `BandStatistic` and `BandHistogram` shapes do not carry a `band` field, so clients correlate the two parallel arrays positionally. Honua aligns them by band number before serialising: index `i` of `statistics[]` and index `i` of `histograms[]` always describe the same band. A band only appears in the response if at least one side returned data for it — bands the underlying store filtered out from **both** the statistics and histograms results (for example, asking for a band the raster does not have) are dropped from both arrays. When a band is present on only one side, the missing side is zero-filled (an all-zero `BandStatistic`, or a `BandHistogram` with empty `counts[]`) so the parallel arrays stay index-aligned. When multiple `rasterIds` are supplied the per-raster results are appended in the request order, with each raster's bands ordered by the caller-supplied `bandIds` (falling back to the store's natural order).

### Legend (`GET .../ImageServer/legend`)

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `f` | Partial | Only `json` and `pjson` are supported. Other formats return `400 Bad Request`. |
| `renderingRule` | Not honoured | Classification overrides through a custom rendering rule are ignored — swatches are always rendered from a fixed 5-class equal-interval viridis ramp keyed off the resolved layer mosaic's band-1 statistics (`min`, `max`). |

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

## Honua admin raster surfaces

These admin endpoints are shipped raster/COG capabilities, but they are not
Esri ImageServer operations. The Esri `Add Rasters`, `Uploads`, full mosaic
dataset operations, and raster catalog item child resources remain marked
according to their own ImageServer parity status above.

| Admin surface | Methods | Notes |
| --- | --- | --- |
| `/api/v1/admin/import/raster` | POST | Imports a GeoTIFF/COG file or a PNG/JPEG raster with world-file sidecars into PostGIS. The path is synchronous, bounded by `Limits:Imports:MaxSyncImportSize`, reports progress when the universal progress store is available, and invalidates layer output cache entries after a successful import. |
| `/api/v1/admin/import/raster/formats` | GET | Lists supported raster file extensions and sidecar expectations for GeoTIFF, PNG world-file, and JPEG world-file imports. |
| `/api/v1/admin/cloud-rasters` | POST, GET | Registers or lists cloud-hosted COGs for a layer. Direct range-read serving currently supports `AwsS3` and `AzureBlob`; `Local` and Google Cloud Storage are not valid shipped direct-serving providers. |
| `/api/v1/admin/cloud-rasters/{id}` | GET, DELETE | Reads or unregisters one COG registration. Delete evicts the registration's `cog:metadata:{id}` in-memory metadata cache entry. |
| `/api/v1/admin/cloud-rasters/{id}/refresh` | POST | Re-scans COG metadata from cloud storage, warns on unsupported direct-serving compression and non-web-map CRS cases, persists refreshed metadata, and evicts stale in-memory metadata. |

## Raster mosaic semantics

Honua selects rasters by intersecting the request geometry with raster footprints before rendering, identifying pixels, building tiles, or computing statistics. When a `time` filter is supplied, the temporal filter is resolved first at the layer level: Honua picks the newest effective acquisition batch (`AcquisitionDate`, falling back to `CreatedAt`) at or before the requested instant, then applies the request geometry/window to that batch. When no request geometry is supplied, the layer-level mosaic uses all rasters selected by any temporal filter. Single-raster selections use the existing single-raster path; multi-raster selections are composited with PostGIS `ST_Union`.

Merge strategy resolution is request `mosaicRule` first, then the layer's admin `rasterMosaic.mergeStrategy` default, then `newest`. `mosaicRule` accepts simple tokens or an Esri-style JSON object containing `mergeStrategy` or `operation`. Supported strategies are:

| Strategy | Effect |
| --- | --- |
| `newest` | Newer rasters win overlapping pixels (`ST_Union(..., 'LAST')` ordered by acquisition/creation time). |
| `oldest` | Older rasters win overlapping pixels (`ST_Union(..., 'FIRST')`). |
| `average` | Overlapping pixels are averaged (`ST_Union(..., 'MEAN')`). |
| `max` | Overlapping pixels keep the maximum value (`ST_Union(..., 'MAX')`). |
| `min` | Overlapping pixels keep the minimum value (`ST_Union(..., 'MIN')`). |

## Metadata response coverage (`GET .../ImageServer`)

### Implemented

| Property | Honua status | Notes |
| --- | --- | --- |
| `currentVersion` | Implemented | `10.81` |
| `serviceDescription`, `name`, `description` | Implemented | Derived from layer metadata. |
| `extent`, `spatialReference` | Implemented | Derived from the aggregate extent and SRID across the layer mosaic. |
| `pixelSizeX`, `pixelSizeY` | Implemented | Calculated from the aggregate mosaic extent using the finest pixel size observed across the selected rasters. |
| `bandCount`, `pixelType` | Implemented | Derived from the representative raster used to describe the layer mosaic. |
| `minValues`, `maxValues`, `meanValues`, `stdvValues` | Implemented | Derived from mosaic statistics when the layer contains multiple rasters. |
| `capabilities` | Implemented | Advertised as `Catalog,Image,Metadata,Pixels,Statistics,Tilemap`. `Mensuration` is intentionally omitted until the `/measure` endpoint ships so the capability list stays in lockstep with routed operations. |
| `maxImageHeight`, `maxImageWidth`, `maxRecordCount` | Implemented | Static Honua metadata limits. |
| `singleFusedMapCache`, `cacheType` | Implemented | Always reports `true` / `Map` to advertise the rendered tile cache surface. |
| `tileInfo` | Implemented | Generated from a fixed Web Mercator (EPSG:3857) LOD grid (256×256 tiles, 96 DPI, PNG) sized for `MaxTileZoom`. |
| `hasHistograms` | Implemented | Always `true`; ImageServer exposes `computeStatisticsHistograms` for the catalog. |
| `timeInfo` | Partial | Surfaced when the layer metadata declares temporal fields or when raster catalog items carry acquisition timestamps. When acquisition dates are available, Honua emits `AcquisitionDate` as the default start field and includes aggregate `timeExtent`. |
| `hasMultidimensions` | Implemented | Always emitted; defaults to `false` until cube ingestion ships. |
| `multidimensionalInfo` | Partial | Skeleton type exists in the response model but is omitted (`JsonIgnoreCondition.WhenWritingNull`) until multidimensional ingestion lands. |

### Not implemented or currently omitted

| Property or resource family | Notes |
| --- | --- |
| `objectIdField`, `fields` | Not currently populated. |
| `rasterFunctionInfos`, `rasterTypeInfos`, `mensurationCapabilities` | Not currently populated. |
| Root links to child resources such as legend, metadata, raster catalog items, and WMTS | Not surfaced because the corresponding child resources are not implemented. |

## Known limitations

- Temporal mosaic uses "newest batch" semantics: when `time` is supplied, Honua selects rasters whose effective acquisition (`AcquisitionDate`, falling back to `CreatedAt`) equals the single most-recent layer-wide acquisition at or before the requested instant, then applies request geometry/windowing. Rasters from earlier acquisitions are excluded — layers with mixed-date scenes can therefore produce spatial coverage gaps under a timestamp filter. Per-pixel temporal mosaicking (newest-per-area) is deferred follow-up scope.
- The current Honua route shape is layer-scoped: `GET /rest/services/{id}/ImageServer`, where `{id}` is the addressed raster layer identifier rather than a FeatureServer/MapServer-style `{serviceId}`.
- Raster imports are rejected with `400 Bad Request` when the upload's SRID or band count differs from the layer's existing rasters; ST_Union requires homogeneity, and the guard fires before commit so callers get a structured error rather than a query-time PostGIS failure.
- Default `exportImage` responses return JSON with a temporary file URL; `f=image` returns rendered bytes inline and does not create a temporary export envelope. Temporary exports are stored through `ITemporaryFileService`, expire after one hour, and use shared cloud file storage instead of node-local disk when the configured `FileStorage` provider is `AwsS3` or `AzureBlob`. Shared cloud-backed temporary files require Redis coordination so quota enforcement remains correct across replicas.
- Catalog filtering still happens in memory after the raster catalog is read; arbitrary geometry filters and `orderByFields` are not pushed to PostGIS yet.
- `exportImage` and `identify` accept more request fields than they currently honor. Unsupported fields are intentionally documented here so they are not mistaken for full parity.
- Rendered `exportImage`/`tile` byte output still depends on the PostGIS raster output drivers configured in the database.
- Tile access returns rendered raster tiles only. Honua does not expose ImageServer WMTS or offline tile-export workflows.

## Implementation evidence

- Endpoint mapping: [ImageServer endpoints](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/ImageServerEndpoints.cs)
- Metadata implementation: [ImageServerMetadataHandler](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Handlers/ImageServerMetadataHandler.cs)
- Export implementation: [ImageServerExportHandler](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Handlers/ImageServerExportHandler.cs)
- Identify implementation: [ImageServerIdentifyHandler](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Handlers/ImageServerIdentifyHandler.cs)
- Tile implementation: [ImageServerTileHandler](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Handlers/ImageServerTileHandler.cs)
- Catalog query implementation: [ImageServerCatalogQueryHandler](../../src/Honua.Protocols.GeoServices/ImageServer/Handlers/ImageServerCatalogQueryHandler.cs), [ImageServerCatalogReader](../../src/Honua.Protocols.GeoServices/ImageServer/Services/ImageServerCatalogReader.cs), [ImageServerCatalogFields](../../src/Honua.Protocols.GeoServices/ImageServer/Services/ImageServerCatalogFields.cs) (the shared field surface used by WHERE/`orderByFields`/`outFields`)
- Statistics/histograms implementation: [ImageServerStatisticsHistogramsHandler](../../src/Honua.Protocols.GeoServices/ImageServer/Handlers/ImageServerStatisticsHistogramsHandler.cs) (also serves `computeHistograms`)
- Get Samples implementation: [ImageServerSamplesHandler](../../src/Honua.Protocols.GeoServices/ImageServer/Handlers/ImageServerSamplesHandler.cs)
- Key Properties implementation: [ImageServerKeyPropertiesHandler](../../src/Honua.Protocols.GeoServices/ImageServer/Handlers/ImageServerKeyPropertiesHandler.cs)
- Raster metadata child resources (`statistics`, `histograms`, `rasterAttributeTable`, `rasterFunctionInfos`): [ImageServerRasterMetadataHandler](../../src/Honua.Protocols.GeoServices/ImageServer/Handlers/ImageServerRasterMetadataHandler.cs), [RasterMetadataModels](../../src/Honua.Protocols.GeoServices/ImageServer/Models/RasterMetadataModels.cs); integration tests: [ImageServerRasterMetadataTests](../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/ImageServer/ImageServerRasterMetadataTests.cs)
- Multidimensional Info implementation: [ImageServerMultidimensionalInfoHandler](../../src/Honua.Protocols.GeoServices/ImageServer/Handlers/ImageServerMultidimensionalInfoHandler.cs), [ImageServerMultidimensionalInfoBuilder](../../src/Honua.Protocols.GeoServices/ImageServer/Services/ImageServerMultidimensionalInfoBuilder.cs)
- Legend implementation: [ImageServerLegendHandler](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Handlers/ImageServerLegendHandler.cs)
- Raster function chain analysis (`computeClass`): [ImageServerAnalyzeHandler](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Handlers/ImageServerAnalyzeHandler.cs), [ImageServerRasterFunctionPlanner](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Services/ImageServerRasterFunctionPlanner.cs)
- Request/response models: [ImageServerModels](../../src/Honua.Server/Features/Protocols/GeoServices/ImageServer/Models/ImageServerModels.cs)
- Integration tests: [ImageServerBasicTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerBasicTests.cs), [ImageServerParameterValidationTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerParameterValidationTests.cs), [ImageServerErrorHandlingTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerErrorHandlingTests.cs), [ImageServerEndpointsTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerEndpointsTests.cs), [ImageServerMosaicIntegrationTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerMosaicIntegrationTests.cs)
- Handler unit tests: [ImageServerCatalogQueryHandlerTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerCatalogQueryHandlerTests.cs), [ImageServerStatisticsHistogramsHandlerTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerStatisticsHistogramsHandlerTests.cs), [ImageServerLegendHandlerTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerLegendHandlerTests.cs), [ImageServerAnalyzeHandlerTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/ImageServer/ImageServerAnalyzeHandlerTests.cs)
