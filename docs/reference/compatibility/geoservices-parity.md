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
| [MapServer](#mapserver--wms--wmts) | Partial | Export, identify, find, legend/queryLegends, query, tiles, storage-backed exportTiles, generateKml, WMS 1.3/1.1.1, WMTS 1.0 | Full Esri tile-package/job export semantics, dynamic layers, several child resources; WMTS is WebMercatorQuad-only |
| [ImageServer](#imageserver) | Partial | Service metadata, exportImage, identify, tile, catalog query, statistics/histograms, getSamples, queryBoundary, computePixelLocation, dynamic computeCacheInfo, legend, WCS 2.0.1 KVP | Catalog mutation, export tiles, measure/project, generated ImageServer tile caches, renderingRule execution, AOI clipping for stats, WMTS |
| [Geometry Service](#geometry-service) | Complete | Root metadata plus all 23 ArcGIS geometry operations | None at operation level; parameter-level caveats only |
| GeocodeServer | Partial | Service metadata, findAddressCandidates, reverseGeocode, suggest, geocodeAddresses | `outFields`, `magicKey` round-trip, `category` filtering, non-default `outSR` — see [GeocodeServer matrix](../../internal/spikes/geocode-server-matrix.md) |
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
| `where`, `objectIds`, spatial filters (`geometry`, `geometryType`, `spatialRel`, `distance`, `units`), `inSR`/`outSR`, pagination, `outFields`, `orderByFields`, output flags (`returnGeometry`, `returnIdsOnly`, `returnCountOnly`, `returnExtentOnly`, `returnZ`, `returnM`), `returnDistinctValues`, `outStatistics` + `groupByFieldsForStatistics` + `having`, `time`/`timeRelation`, `geometryPrecision`, `maxAllowableOffset`, `nearestCount`/`returnDistance` | Implemented | ArcGIS SQL `where` parser; KNN via `nearestCount`; statistics support COUNT/SUM/MIN/MAX/AVG/STDDEV/VAR with GROUP BY and post-aggregation HAVING. |
| Output formats `f=json/pjson/geojson/pbf/fgb/geobuf/parquet/arrow` | Implemented | GeoJSON/GeoParquet/GeoArrow require EPSG:4326 when geometry is present; `parquet`/`arrow` always strip M values; `fgb`/`geobuf` need native store support and ignore precision/simplification parameters; special query modes always return JSON. |
| `resultType`, `sqlFormat`, `gdbVersion`, `quantizationParameters`, `datumTransformation` | Partial | Accepted for client compatibility; `gdbVersion`/`quantizationParameters` (for `f=json`/`geojson`)/`datumTransformation` are ignored. Layer metadata honestly advertises `supportsCoordinatesQuantization=false`; `f=pbf` does return a quantized `transform`. |
| `returnCentroid`, `returnTrueCurves`, `returnExceededLimitFeatures` | Not implemented | Rejected with 400. |

### applyEdits parameters

`adds`/`updates`/`deletes` and `rollbackOnFailure` are implemented (object-ID-keyed).
`useGlobalIds`, `gdbVersion`, `returnEditMoment`, and `attachments` are rejected with
400; session/async/upload-style parameters (`assetMaps`, `sessionID`, `async`,
`editsUploadId`, ...) are silently ignored. queryRelatedRecords rejects
`returnTrueCurves`, `gdbVersion`, and `historicMoment` with 400.

## MapServer + WMS / WMTS

Esri spec: [Map Service](https://developers.arcgis.com/rest/services-reference/enterprise/map-service/).

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata, layer metadata, allLayersAndTables, queryDomains, feature child resource | Implemented | Includes `drawingInfo`, `tileInfo`, scale ranges; `parentLayerId`/`subLayerIds` are always flat (`-1`/`null`) — no group layers. |
| export | Implemented | `bbox`, `size`, `dpi`, `format` (png/png8/png24/png32/jpg/gif), `transparent`, `layers`, `bboxSR`/`imageSR`, `layerDefs`, `dynamicLayers`, `time`/`layerTimeOptions`, `backgroundColor`, `f=image\|json\|pjson`. `gdbVersion`, `maxAllowableOffset`, `geometryPrecision`, `returnZ`, `returnM` are accepted but ignored. |
| identify, find | Implemented | All geometry types, `mapExtent`, `imageDisplay`, `tolerance`, `layerDefs`, `dynamicLayers`, `time`/`timeRelation`. `find` searches string fields with SQL LIKE. |
| Layer query, service query, generateRenderer (per layer), queryRelatedRecords, queryAttachments | Implemented | Thin adapters delegating to the FeatureServer handlers — same parameter coverage as the [FeatureServer section](#featureserver). |
| legend, queryLegends, generateKml, tile | Implemented | Legend swatch images at both legend routes; `f=kml`/`f=kmz`; dynamic PNG tiles at `.../MapServer/tile/{z}/{y}/{x}`. |
| estimateExportTilesSize, exportTiles | Partial | Estimates and exports bounded WebMercatorQuad PNG tiles as a ZIP archive written through configured cloud file storage (`local`, S3, Azure Blob). This closes common storage-backed tile archive workflows, but does not yet emit Esri TPK/TPKX/compact cache packages or async job child resources. |
| WMS | Implemented | WMS 1.3.0 and 1.1.1 GetCapabilities/GetMap/GetFeatureInfo (KVP) at `.../MapServer/WMS` and `/ogc/services/{serviceId}/wms`. Time-aware layers advertise a `time` dimension. WMS 1.3 is CITE-certified (199/199); 1.1.1 has no CITE evidence yet. |
| WMTS | Partial | GetCapabilities/GetTile/GetFeatureInfo (KVP + RESTful) at `.../MapServer/WMTS` and `/ogc/services/{serviceId}/wmts`; WebMercatorQuad only. WMTS 1.0 is CITE-certified (60/60). |
| generateRenderer (service-level), queryAnalytic, dynamicLayer, image/KML-image/job child resources, `exts/*` | Not implemented | |

## ImageServer

Esri spec: [Image Service](https://developers.arcgis.com/rest/services-reference/enterprise/image-service/).
Routes are layer-scoped: `{id}` in `GET /rest/services/{id}/ImageServer` is the
raster layer identifier.

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata | Implemented | Aggregate mosaic extent/statistics, `timeInfo` when acquisition dates exist, `tileInfo`, output-cached. `objectIdField`/`fields`/`rasterFunctionInfos` root properties not yet populated. |
| tile, computeHistograms, getSamples | Implemented | PNG/JPEG/TIFF tiles, zoom 0–28, multi-raster mosaic; getSamples caps at 1000 samples. |
| exportImage | Partial | JSON envelope with temporary `href` by default; `f=image` streams bytes. `bandIds`, `noData`, mosaic + single-instant `time` supported; `pixelType` validated but not applied; non-empty `renderingRule` returns 501; `bmp`/`gif` rejected with 400. |
| identify | Partial | Point/envelope/polygon (area geometries identify at the envelope centroid); `returnCatalogItems`; `pixelSize` echoed but pyramid selection deferred; `renderingRule` not applied. |
| query (raster catalog) | Partial | Esri-compatible catalog features with `where`, spatial filter, `outSR`, `orderByFields`, `outFields`, paging, and shaping flags. Filters run in-memory at footprint-envelope granularity. |
| computeStatisticsHistograms | Partial | Per-band stats + histograms with `rasterIds`, `bandIds` (Honua extension), `mosaicRule`, `time`, `histogramParameters.size`. AOI clipping via `geometry` not yet honoured — analysis covers the full selected raster/mosaic. |
| queryBoundary | Implemented | Returns Esri `shape` + approximate `area` from the aggregate raster footprint extent, with `outSR` honoured when the shared CRS pipeline can transform the extent. NoData-trimmed boundaries are deferred. |
| computePixelLocation | Implemented | Converts point geometries to raster pixel column/row coordinates using the raster geotransform, with extent fallback and `rasterId`/input SR support. |
| computeCacheInfo | Partial | Returns a spec-shaped `cacheInfo` extent for dynamic ImageServer layers and intentionally omits `tileInfo`/`cacheType` until a real ImageServer raster tile cache is configured. |
| computeClassStatistics | Partial | Route validates input; computation returns 501 until the signature pipeline lands. |
| computeClass (Honua extension) | Implemented (validation only) | Validates `renderingRule` raster-function chains (`Identity`, `Stretch`, `Clip`, depth ≤ 8) and returns planned execution metadata; the executor is not wired into exportImage. |
| addRasters, deleteRasters, updateRaster, uploads, downloadRasters, exportTiles, find, measure, project, calculateVolume, computeMultidimensionalInfo, computeTiePoints, validate | Not implemented | Raster ingestion happens through the Honua admin API (`/api/v1/admin/import/raster`, `/api/v1/admin/cloud-rasters`) instead. |

### Child resources

| Resource(s) | Status | Notes |
| --- | --- | --- |
| keyProperties, multidimensionalInfo, statistics, histograms, rasterFunctionInfos, rasterAttributeTable | Implemented | Spec-shaped documents from the shared raster store; non-applicable cases return spec-correct empty documents (e.g. `{"variables": []}`), not 404s. |
| legend | Partial | Fixed 5-class equal-interval ramp from band-1 statistics; `renderingRule` overrides ignored. |
| WCS | Partial | WCS 2.0.1 KVP GetCapabilities/DescribeCoverage/GetCoverage over the primary raster (`image/tiff`/`png`/`jpeg`, `SUBSET`/`BBOX`, `OUTPUTCRS`). Range subset, scaling, temporal slicing not implemented. WCS 2.0 core is CITE-certified (82/82). |
| colormap, `info/*`, imageSupportData, KML image, raster catalog item resources (`{rasterId}/*`), rasterFile, slices, WMTS | Not implemented | |

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
