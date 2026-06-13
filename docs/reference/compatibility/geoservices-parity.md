# GeoServices REST parity

Honua serves the Esri GeoServices REST API at compatible paths so ArcGIS Pro, the
ArcGIS SDKs, Esri Leaflet, and the ArcGIS API for Python can connect without
modification. This page summarizes endpoint-level parity per service; the
machine-readable source is
[`docs/gis/data/geoservices-rest-parity.json`](../../gis/data/geoservices-rest-parity.json).

Status vocabulary:

- **Implemented** — the Esri operation exists at a compatible path and the documented behavior is supported.
- **Partial** — the operation exists, but only a subset of documented parameters or behavior is supported.
- **Stub** — the route exists and returns the spec-shaped response, but the backing data model is deferred; read-style stubs return empty/`false` results and mutation stubs return HTTP 400 rather than fabricating success.
- **Not implemented** — the operation is not exposed.

## Service summary

| Service | Parity | Implemented surface | Headline gaps |
| --- | --- | --- | --- |
| [FeatureServer](#featureserver) | Partial | Query (7 output formats), edits, attachments, related records, domains, replication, estimates, calculate, validateSQL, append, bins/date bins/top features, generateRenderer, spatial analytics extensions (Pro) | Change tracking, contingent-value/shared-template/asset data models, 3D queries, true curves |
| [MapServer](#mapserver--wms--wmts) | Partial | Export, identify, find, legend/queryLegends, query, mapLayer dynamicLayer metadata, tiles with cloud-storage cache, storage-backed exportTiles, generateKml, WMS 1.3/1.1.1, WMTS 1.0 | Full Esri tile-package/job export semantics, workspace dynamic layers/tables, several child resources; WMTS is WebMercatorQuad-only |
| [ImageServer](#imageserver) | Partial | Service metadata, exportImage, identify, tile with cloud-storage cache, catalog query, find, measure (Basic), statistics/histograms, getSamples, queryBoundary, computePixelLocation, project, storage-backed exportTiles, dynamic computeCacheInfo, legend, WMTS 1.0 GetCapabilities/GetTile/GetFeatureInfo, WCS 2.0.1 KVP | Catalog mutation, full Esri tile-package/job export semantics, sensor/DEM height mensuration, Esri compact/generated tile-cache management, camera/orientation-ranked find, renderingRule identify rule (exportImage executes Stretch + Colormap + Clip; legend honours Colormap); WMTS is WebMercatorQuad only |
| [Geometry Service](#geometry-service) | Complete | Root metadata plus all 23 ArcGIS geometry operations | None at operation level; parameter-level caveats only |
| GeocodeServer | Partial | Service metadata, findAddressCandidates, reverseGeocode, suggest, geocodeAddresses, `outFields` projection, `outSR` reprojection | `magicKey` round-trip, `category` filtering, `forStorage`/`matchOutOfRange` — see [GeocodeServer matrix](../../internal/spikes/geocode-server-matrix.md) |
| GPServer | Partial | PrintingTools; generic adapter with catalog-backed task metadata, async submitJob, job status/cancel/results over 34 seeded processes | Async-only, no generic `execute` route; `env:*` rejected — see [run geoprocessing](../../guides/query-analyze/run-geoprocessing.md) |
| Portal Sharing | Partial (token slice) | `generateToken` issuing opaque tokens consumable on `/rest/services/*` | OAuth2 named-user flow, item/group/community surface — see [authentication](../../guides/secure/authentication.md) |

## FeatureServer

Esri spec: [Feature Service](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/).

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata; layer metadata | Implemented | Dynamic capabilities string; `editFieldsInfo`, `editingInfo`, `templates`, `timeInfo`, `allowGeometryUpdates`, `supportsStatistics`, normalized `supportedQueryFormats` incl. binary formats. |
| Query (service + layer), queryDomains, relationships, getEstimates (service + layer) | Implemented | Service-level query delegates to a target layer via `layerId`/`layers`. |
| applyEdits (service + layer), addFeatures, updateFeatures, deleteFeatures, append (service + layer), calculate, validateSQL | Implemented | Multi-layer batch edits; `rollbackOnFailure` defaults `false` for applyEdits, `true` for standalone endpoints; deleteFeatures supports `objectIds`, `where`, and spatial filters. |
| queryRelatedRecords, queryAttachments | Implemented | Full filter facets (`attachmentTypes`, `keywords`, `size`, `definitionExpression`); `globalIds` rejected with 400 (integer object IDs only). |
| addAttachment, updateAttachment, deleteAttachments, attachment download | Implemented | Form-data upload; binary download at `.../{featureId}/attachments/{attachmentId}`. |
| createReplica, extractChanges, synchronizeReplica, unRegisterReplica | Implemented (MVP semantics) | First sync reports full adds; later syncs lack DB-level incremental change tracking. See [known limitations](clients.md#known-limitations). |
| generateRenderer | Implemented | Simple renderer by default; `classificationDef` generates class-breaks (equal interval, quantile, natural breaks, standard deviation) or unique-value renderers. |
| queryBins, queryDateBins, queryTopFeatures | Implemented | queryDateBins requires the `temporal.histogram` entitlement. |
| queryClusters, spatialJoin, queryBufferAggregate, queryDensity, temporalExtent | Implemented (Honua extensions) | No Esri equivalent. Analytics ops are Pro-entitlement-gated (`analytics.*`, HTTP 402 when inactive), return GeoJSON in EPSG:4326, and are bounded by configurable limits. Return 501 on stores without an analytics reader (DuckDB, MySQL/MariaDB). |
| queryContingentValues, sharedTemplates (+ query/add/update/delete), htmlPopup, image | Stub | Routes exist and return spec-shaped documents, but Honua has no contingent-value, shared-template, popup, or feature-image store — reads return empty documents; mutations return 400. |
| hasAssets, queryAssets, cleanupAssets, uploadAssets, convert3D, query3D, metadata/update | Stub | No layer asset store or 3D pipeline; reads return empty/`false`, mutations/conversions return 400. |
| cleanupChangeTracking | Not implemented | No change-tracking tables exist to clean up. |

### Layer query parameters

| Area | Status | Notes |
| --- | --- | --- |
| `where`, `objectIds`, spatial filters (`geometry`, `geometryType`, `spatialRel`, `distance`, `units`), `inSR`/`outSR`, pagination, `outFields`, `orderByFields`, output flags (`returnGeometry`, `returnCentroid`, `returnIdsOnly`, `returnCountOnly`, `returnExtentOnly`, `returnZ`, `returnM`), `returnDistinctValues`, `outStatistics` + `groupByFieldsForStatistics` + `having`, `time`/`timeRelation`, `geometryPrecision`, `maxAllowableOffset`, `nearestCount`/`returnDistance` | Implemented | ArcGIS SQL `where` parser; KNN via `nearestCount`; statistics support COUNT/SUM/MIN/MAX/AVG/STDDEV/VAR with GROUP BY and post-aggregation HAVING. `returnCentroid` emits polygon centroids for GeoServices JSON responses and advertises `supportsReturningGeometryCentroid` on polygon layers. |
| Output formats `f=json/pjson/geojson/pbf/fgb/geobuf/parquet/arrow` | Implemented | GeoJSON/GeoParquet/GeoArrow require EPSG:4326 when geometry is present; `parquet`/`arrow` always strip M values; `fgb`/`geobuf` need native store support and ignore precision/simplification parameters; special query modes always return JSON. |
| `resultType`, `sqlFormat`, `gdbVersion`, `quantizationParameters`, `datumTransformation` | Partial | `quantizationParameters` is honored for `f=json`: the featureSet emits a `transform` (`originPosition`/`scale`/`translate`, `upperLeft` or `lowerLeft`) and geometry coordinates become integer grid deltas; layer metadata advertises `supportsCoordinatesQuantization=true`, and `f=pbf` likewise returns a quantized `transform`. Quantization is ignored for `f=geojson` (GeoJSON has no quantization). `gdbVersion`/`datumTransformation` are accepted for client compatibility and ignored. |
| `returnExceededLimitFeatures` | Implemented | Defaults to `true` (returns the truncated page plus `exceededTransferLimit`). When set `false` and the limit is exceeded, the `f=json`/`geojson` response omits the features while still flagging `exceededTransferLimit=true`; binary export formats (`pbf`/`fgb`/`geobuf`/`parquet`/`arrow`) always stream their rows. |
| `returnTrueCurves` | Partial | Accepted and ignored for client compatibility; Honua still advertises `supportsTrueCurve=false` and emits linearized geometries. |

### applyEdits parameters

`adds`/`updates`/`deletes` and `rollbackOnFailure` are implemented (object-ID-keyed).
`useGlobalIds`, `gdbVersion`, `returnEditMoment`, and `attachments` are rejected with
400; session/async/upload-style parameters (`assetMaps`, `sessionID`, `async`,
`editsUploadId`, ...) are silently ignored. queryRelatedRecords rejects
`gdbVersion` and `historicMoment` with 400 and accepts/ignores `returnTrueCurves`.

## MapServer + WMS / WMTS

Esri spec: [Map Service](https://developers.arcgis.com/rest/services-reference/enterprise/map-service/).

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata, layer metadata, allLayersAndTables, queryDomains, feature child resource | Implemented | Includes `drawingInfo`, `tileInfo`, scale ranges; `parentLayerId`/`subLayerIds` are always flat (`-1`/`null`) — no group layers. |
| dynamicLayer child resource | Partial | `GET .../MapServer/dynamicLayer` returns layer metadata for `source.type=mapLayer` definitions, including request-scoped `id`, `definitionExpression`, and `drawingInfo`; workspace layers, joins, and dynamic tables remain deferred. |
| export | Implemented | `bbox`, `size`, `dpi`, `format` (png/png8/png24/png32/jpg/gif), `transparent`, `layers`, `bboxSR`/`imageSR`, `layerDefs`, `dynamicLayers`, `time`/`layerTimeOptions`, `backgroundColor`, `f=image\|json\|pjson`. `dynamicLayers` supports `source.type=mapLayer`, request-scoped `definitionExpression`, and `drawingInfo.renderer` overrides for simple, unique-value, and class-breaks renderers. `gdbVersion`, `maxAllowableOffset`, `geometryPrecision`, `returnZ`, `returnM` are accepted but ignored. |
| identify, find | Implemented | All geometry types, `mapExtent`, `imageDisplay`, `tolerance`, `layerDefs`, `dynamicLayers`, `time`/`timeRelation`. `find` searches string fields with SQL LIKE. |
| Layer query, service query, generateRenderer (service + per layer), queryRelatedRecords, queryAttachments | Implemented | Thin adapters delegating to the FeatureServer handlers — same parameter coverage as the [FeatureServer section](#featureserver). Service-level `generateRenderer` selects the target layer via `layer` or `layerId`. |
| legend, queryLegends, generateKml, tile | Implemented | Legend swatch images at both legend routes, including dynamic layer `drawingInfo.renderer` overrides for simple, unique-value, and class-breaks renderers; `f=kml`/`f=kmz`; dynamic PNG tiles at `.../MapServer/tile/{z}/{y}/{x}` use configured cloud file storage (`local`, S3, Azure Blob) as a deterministic loose-object read-through/write-through cache. |
| estimateExportTilesSize, exportTiles | Partial | Estimates and exports bounded WebMercatorQuad PNG tiles as a ZIP archive written through configured cloud file storage (`local`, S3, Azure Blob). This closes common storage-backed tile archive workflows, but does not yet emit Esri TPK/TPKX/compact cache packages or async job child resources. |
| WMS | Implemented | WMS 1.3.0 and 1.1.1 GetCapabilities/GetMap/GetFeatureInfo (KVP) at `.../MapServer/WMS` and `/ogc/services/{serviceId}/wms`. Time-aware layers advertise a `time` dimension. WMS 1.3 is CITE-certified (199/199); 1.1.1 has no CITE evidence yet. |
| WMTS | Partial | GetCapabilities/GetTile/GetFeatureInfo (KVP + RESTful) at `.../MapServer/WMTS` and `/ogc/services/{serviceId}/wmts`; WebMercatorQuad only. WMTS 1.0 is CITE-certified (60/60). |
| queryAnalytic, image/KML-image/job child resources, `exts/*` | Not implemented | |

## ImageServer

Esri spec: [Image Service](https://developers.arcgis.com/rest/services-reference/enterprise/image-service/).
Routes are layer-scoped: `{id}` in `GET /rest/services/{id}/ImageServer` is the
raster layer identifier.

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata | Implemented | Aggregate mosaic extent/statistics, `timeInfo` when acquisition dates exist, `tileInfo`, output-cached. `objectIdField`/`fields`/`rasterFunctionInfos` root properties not yet populated. |
| tile, computeHistograms, getSamples | Implemented | PNG/JPEG/TIFF tiles, zoom 0–28, multi-raster mosaic. Dynamically generated tiles auto-apply a MinMax display stretch when the source raster is not 8-bit (elevation/analytic/float), so analytic layers are viewable; pre-rendered cached tiles are served as stored. Tile GETs use configured cloud file storage (`local`, S3, Azure Blob) as a deterministic loose-object read-through/write-through cache; getSamples caps at 1000 samples. |
| WMTS | Partial | OGC WMTS 1.0 GetCapabilities/GetTile (KVP + RESTful) at `.../ImageServer/WMTS` for numeric and service-name ImageServer routes. Uses the ImageServer tile/cloud-cache pipeline, advertises `WebMercatorQuad`, serves `image/png` and `image/jpeg` tiles, and answers GetFeatureInfo (application/json or text/xml) with the pixel band values at the requested tile pixel. Non-WebMercator matrix sets, dimensions, and other tile formats (TIFF) are deferred. |
| exportImage | Partial | JSON envelope with temporary `href` by default; `f=image` streams bytes. `bandIds`, `noData`, mosaic + single-instant `time` supported; `pixelType` validated but not applied. `renderingRule` executes a `Stretch` chain — types MinMax (5), StandardDeviation (3), and PercentClip (6), with optional per-band `Statistics` — linearly rescaling each band to 8-bit; `Identity` is a no-op pass-through. A `Colormap` raster function (explicit `[value, r, g, b]` stops) maps single-band values to an RGBA image via interpolation; it may wrap a `Stretch`. A `Clip` raster function masks the output to its `ClippingGeometry`/`Extent` (Esri envelope or polygon) as a second clip after the export `bbox`. Other stretch types and the histogram-equalize / colorramp-name / clip-inside variants return 501; unknown functions return 400. `bmp`/`gif` rejected with 400. |
| identify | Partial | Point/envelope/polygon (area geometries identify at the envelope centroid); `returnCatalogItems`; `pixelSize` echoed but pyramid selection deferred; `renderingRule` not applied. |
| query (raster catalog) | Partial | Esri-compatible catalog features with `where`, spatial filter, `outSR`, `orderByFields`, `outFields`, paging, and shaping flags. Filters run in-memory at footprint-envelope granularity. |
| find | Partial | Finds raster catalog images whose footprints contain `toGeometry`, with `objectIds`, `where`, `inSR`, `fromGeometry` validation, and `maxCount` support. Results use catalog center/pixel-size metadata; Esri camera/orientation-ranked image selection is deferred until sensor metadata is modeled. |
| measure | Partial | Supports Basic map-space point, distance/azimuth, area/perimeter, and centroid measurements over point, envelope, and polygon Esri JSON. Sensor/DEM-backed height and 3D mensuration return 501 until sensor/orientation metadata is modeled. |
| computeStatisticsHistograms | Partial | Per-band stats + histograms with `rasterIds`, `bandIds` (Honua extension), `mosaicRule`, `time`, `histogramParameters.size`. An AOI `geometry` clips the analysis to that area (envelope-scoped) for both single rasters and mosaics; without a geometry the full selected raster/mosaic is analysed. `renderingRule` is still not applied. |
| queryBoundary | Implemented | Returns Esri `shape` + approximate `area` from the aggregate raster footprint extent, with `outSR` honoured when the shared CRS pipeline can transform the extent. NoData-trimmed boundaries are deferred. |
| computePixelLocation | Implemented | Converts point geometries to raster pixel column/row coordinates using the raster geotransform, with extent fallback and `rasterId`/input SR support. |
| project | Partial | Reprojects Esri JSON geometry arrays between supported spatial references via the shared CRS pipeline and returns the Image Service `{ "geometries": [...] }` response shape. Envelope geometry remains envelope-shaped; `datumTransformation`/`transformation` and image coordinate systems are rejected until neutral support lands. |
| estimateExportTilesSize, exportTiles | Partial | Estimates and exports bounded WebMercatorQuad image tiles as a ZIP archive written through configured cloud file storage (`local`, S3, Azure Blob). Esri TPK/TPKX/compact cache packages and async job child resources are deferred. |
| computeCacheInfo | Partial | Returns a spec-shaped `cacheInfo` extent for dynamic ImageServer layers and intentionally omits `tileInfo`/`cacheType` until a formal Esri-style raster tile cache is configured. The loose-object tile GET cache is operational storage, not advertised cache metadata. |
| computeClassStatistics | Partial | Route validates input; computation returns 501 until the signature pipeline lands. |
| computeClass (Honua extension) | Implemented (validation only) | Validates `renderingRule` raster-function chains (`Identity`, `Stretch`, `Clip`, depth ≤ 8) and returns planned execution metadata. `exportImage` executes the `Stretch`, `Colormap`, and `Clip` portions of a chain. |
| addRasters, deleteRasters, updateRaster, uploads, downloadRasters, calculateVolume, computeMultidimensionalInfo, computeTiePoints, validate | Not implemented | Raster ingestion happens through the Honua admin API (`/api/v1/admin/import/raster`, `/api/v1/admin/cloud-rasters`) instead. |

### Child resources

| Resource(s) | Status | Notes |
| --- | --- | --- |
| keyProperties, multidimensionalInfo, statistics, histograms, rasterFunctionInfos, rasterAttributeTable | Implemented | Spec-shaped documents from the shared raster store; non-applicable cases return spec-correct empty documents (e.g. `{"variables": []}`), not 404s. |
| legend | Partial | Default 5-class equal-interval ramp from band-1 statistics. A `renderingRule` with an explicit `Colormap` is honoured: the legend emits one swatch per colour stop so it matches the rendered image. Other renderer overrides are ignored. |
| WCS | Partial | WCS 2.0.1 KVP GetCapabilities/DescribeCoverage/GetCoverage over the primary raster (`image/tiff`/`png`/`jpeg`, `SUBSET`/`BBOX`, `OUTPUTCRS`). `RANGESUBSET` selects coverage bands (`band1`..`bandN`, comma list and `band1:bandN` intervals); scaling and temporal slicing are not implemented. WCS 2.0 core is CITE-certified (82/82); the range-subsetting extension conformance class is not advertised pending CITE validation. |
| colormap, `info/*`, imageSupportData, KML image, raster catalog item resources (`{rasterId}/*`), rasterFile, slices | Not implemented | |

Mosaic semantics: rasters are selected by footprint intersection; merge strategy is
request `mosaicRule`, then the layer default, then `newest`
(`newest`/`oldest`/`average`/`max`/`min` via PostGIS `ST_Union`). Temporal `time`
filters use newest-batch semantics (see [known limitations](clients.md#known-limitations)).

## Geometry Service

Esri spec: [Geometry Service](https://developers.arcgis.com/rest/services-reference/enterprise/geometry-service/).
All operations are served at the canonical Esri route
`/rest/services/Utilities/Geometry/GeometryServer/<operation>` (GET + POST) over
NetTopologySuite, a PROJ-backed projection service, and a geography-based geodesic
measurement path. The live surface was verified end-to-end through the ArcGIS API
for Python (`arcgis.geometry`).

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Root `GeometryServer` metadata | Implemented | ArcGIS-style descriptor (`currentVersion`, `maxBufferCount`, ...) so ArcGIS Pro / SDK discovery handshakes complete. |
| buffer, simplify, project, intersect, union, clip, difference, areasAndLengths, lengths, distance, relation, densify, convexHull, generalize, labelPoints, cut, trimExtend, offset, autoComplete, reshape, findTransformations, toGeoCoordinateString, fromGeoCoordinateString | Implemented | Full ArcGIS operation set: geodesic buffer/area/length/distance, datum transforms (e.g. 4326 → 4267), DE-9IM relations, MGRS/USNG round-trips, the `bufferSR` cascade. No operation-level gaps. |

Parameter-level caveats (the complete list):

- `f` supports `json`/`pjson` only; `f=html` is rejected.
- `clip` uses the envelope of the clip geometry, not its full shape.
- `findTransformations` returns an empty list — CRS transformation runs through the
  PROJ pipeline rather than a discrete Esri transformation catalog; `project` still
  applies the correct datum transform directly.
- `toGeoCoordinateString`/`fromGeoCoordinateString` support `MGRS` and `USNG`;
  `UTM`, `GARS`, `GEOREF`, `DD`, `DDM`, and `DMS` return a clear 400.

## Sources and upkeep

- Machine-readable parity export: [`docs/gis/data/geoservices-rest-parity.json`](../../gis/data/geoservices-rest-parity.json) — update it in the same PR as any GeoServices route or behavior change.
- [GeocodeServer matrix](../../internal/spikes/geocode-server-matrix.md), [run geoprocessing](../../guides/query-analyze/run-geoprocessing.md), [authentication](../../guides/secure/authentication.md) — drill-downs for the services not detailed on this page.
- [Supported clients](clients.md) — which Esri clients are certified against this surface.
- Release owners verify this page during the [release checklist](../../internal/contributor/RELEASE_CHECKLIST.md).
