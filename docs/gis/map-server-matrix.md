# MapServer API Matrix (Esri Enterprise vs Honua)

Canonical GeoServices entry point: [GeoServices REST Parity](geoservices-rest-parity.md)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service/
- https://developers.arcgis.com/rest/services-reference/enterprise/layer-table/

## Status vocabulary

- Implemented: endpoint exists and the documented operation is supported.
- Partial: endpoint exists, but Honua only supports a subset of the documented behavior or scope.
- Not implemented: the Esri operation or resource is not exposed by Honua.

## Operations

### Implemented

| Operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Service metadata | `.../MapServer` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer` | Includes `maxRecordCount`, `supportedQueryFormats`, `documentInfo`, `minScale`/`maxScale`. |
| Layer metadata | `.../MapServer/{layerId}` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer/{layerId}` | Includes `drawingInfo`, query capability flags, `parentLayerId`/`subLayerIds`. |
| Export map | `.../MapServer/export` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/MapServer/export` | Supports `bbox`, `size`, `dpi`, `format`, `transparent`, `layers`, `bboxSR`, `imageSR`, `dynamicLayers`, `time`, `layerTimeOptions`, `layerDefs`, `backgroundColor`, and `f=image|json|pjson`. `gdbVersion` is accepted but ignored. |
| Identify | `.../MapServer/identify` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/MapServer/identify` | Supports all geometry types, `mapExtent`, `imageDisplay`, `layers`, `tolerance`, `dynamicLayers`, `time`/`timeRelation`, and `layerDefs`. `gdbVersion` is accepted but ignored. |
| Find | `.../MapServer/find` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/MapServer/find` | Supports `searchText`, `layers`, `contains`, `searchFields`, `sr`, `layerDefs`, `dynamicLayers`, `returnGeometry`, and `f=json|pjson`. `gdbVersion` is accepted but ignored. |
| Generate KML | `.../MapServer/generateKml` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/MapServer/generateKml` | Supports `f=kml` and `f=kmz` plus `layers`, `layerDefs`, `dynamicLayers`, `time`, and `layerTimeOptions`. |
| Legend | `.../MapServer/legend` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer/legend` | Swatch images for visible layers. Supports `size` and `dynamicLayers`. |
| Layer query | `.../MapServer/{layerId}/query` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/MapServer/{layerId}/query` | Delegates to the FeatureServer query handler. See [FeatureServer Matrix](feature-server-matrix.md). |
| Service-level query | `.../MapServer/query` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/MapServer/query` | Delegates to the FeatureServer service-query handler using `layerId` or `layers`. |
| Tile | `.../MapServer/tile/{z}/{y}/{x}` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}` | Returns rendered PNG map tiles. |
| WMS | `.../MapServer/WMS` | GET | Implemented | `GET /rest/services/{serviceId}/MapServer/WMS`, `GET /ogc/services/{serviceId}/wms` | Supports WMS 1.3.0 and 1.1.1 `GetCapabilities`, `GetMap`, and `GetFeatureInfo` (KVP). |

### Partial

| Operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| WMTS | `.../MapServer/WMTS` | GET | Partial | `GET /rest/services/{serviceId}/MapServer/WMTS`, `GET /rest/services/{serviceId}/MapServer/WMTS/{**restPath}`, `GET /ogc/services/{serviceId}/wmts` | Supports `GetCapabilities`, `GetTile`, and `GetFeatureInfo`, but scope remains WebMercatorQuad-only. |

### Not implemented

| Esri operation or child resource | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Estimate Export Tile Size | `.../MapServer/estimateExportTilesSize` | GET, POST | Not implemented | |
| Export Tiles | `.../MapServer/exportTiles` | POST | Not implemented | |
| Generate Renderer | `.../MapServer/generateRenderer` or `.../MapServer/{layerId}/generateRenderer` | GET, POST | Not implemented | |
| Query Attachments | `.../MapServer/{layerId}/queryAttachments` | GET, POST | Not implemented | |
| Query Domains | `.../MapServer/queryDomains` | GET | Not implemented | |
| Query Legends | `.../MapServer/queryLegends` | GET, POST | Not implemented | |
| Query Related Records | `.../MapServer/{layerId}/queryRelatedRecords` | GET, POST | Not implemented | |
| Query Analytic | `.../MapServer/{layerId}/queryAnalytic` | GET, POST | Not implemented | |
| All Layers and Tables | `.../MapServer/allLayersAndTables` | GET | Not implemented | |
| Dynamic Layer / Table | `.../MapServer/dynamicLayer` | GET | Not implemented | |
| Feature child resource | `.../MapServer/{layerId}/{featureId}` | GET | Not implemented | |
| Image child resource | `.../MapServer/image` | GET | Not implemented | |
| KML Image child resource | `.../MapServer/kml/mapImage.kmz` | GET | Not implemented | |
| Job child resource | `.../MapServer/jobs/{jobId}` | GET | Not implemented | |
| Map Service Extension | `.../MapServer/exts/*` | GET | Not implemented | |

## Export parameter coverage

### Implemented

| Parameter | Status | Notes |
| --- | --- | --- |
| `bbox` | Implemented | Required. Format: `xmin,ymin,xmax,ymax`. |
| `size` | Implemented | Width and height pair (`width,height`). Invalid pairs return `400 Bad Request`. |
| `dpi` | Implemented | Validated integer value. |
| `format` | Implemented | Supports `png`, `png8`, `png24`, `png32`, `jpg`, `jpeg`, and `gif`. |
| `transparent` | Implemented | Accepts boolean text or numeric `0`/`1`. |
| `layers` | Implemented | Layer inclusion/exclusion mask. Empty or non-integer tokens are rejected. |
| `bboxSR` | Implemented | Accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), and `CRS84` aliases via shared CRS parser. |
| `imageSR` | Implemented | Accepts numeric WKID, `EPSG:####`, OGC CRS URI/URN, bracket-safe forms (`[EPSG:####]`), and `CRS84` aliases via shared CRS parser. Unsupported transforms return `400 Bad Request` with a generic spatial-reference error. |
| `layerDefs` | Implemented | JSON object keyed by layer id. Malformed JSON returns `400 Bad Request` without parser-detail leakage. |
| `dynamicLayers` | Implemented | JSON array. Malformed JSON returns `400 Bad Request` without parser-detail leakage. |
| `time` | Implemented | Supports instant or extent syntax used by the shared temporal query pipeline. |
| `timeRelation` | Implemented | Normalized through the shared time-relation parser. |
| `layerTimeOptions` | Implemented | JSON object keyed by layer id. Malformed JSON returns `400 Bad Request`. |
| `backgroundColor` | Implemented | `r,g,b` or `r,g,b,a`. Invalid tuples return `400 Bad Request`. |
| `f` | Implemented | `image` streams bytes directly; `json` and `pjson` return an Esri-style JSON envelope with `href`, `width`, `height`, `extent`, and `scale`. |

### Ignored or not applied

| Parameter | Status | Notes |
| --- | --- | --- |
| `gdbVersion` | Ignored | Accepted to preserve ArcGIS compatibility. No alternate geodatabase version routing occurs. |
| `maxAllowableOffset` | Ignored | Accepted but not applied. |
| `geometryPrecision` | Ignored | Accepted but not applied. |
| `returnZ` | Ignored | Accepted but not applied. |
| `returnM` | Ignored | Accepted but not applied. |

## Response Properties

### Service metadata (`GET .../MapServer`)

| Property | Spec | Status | Notes |
| --- | --- | --- | --- |
| `currentVersion` | Required | Implemented | 10.81 |
| `serviceDescription` | Required | Implemented | |
| `mapName` | Required | Implemented | |
| `description` | Required | Implemented | |
| `copyrightText` | Required | Implemented | |
| `spatialReference` | Required | Implemented | |
| `layers` | Required | Implemented | Each entry includes `parentLayerId`, `subLayerIds`. |
| `tables` | Required | Implemented | |
| `supportsDynamicLayers` | Required | Implemented | Always `true`. |
| `singleFusedMapCache` | Required | Implemented | Always `false`. |
| `supportedImageFormatTypes` | Required | Implemented | PNG, PNG8, PNG24, PNG32, JPG, GIF. |
| `capabilities` | Required | Implemented | Map, Query, Data, editing caps. |
| `fullExtent` | Required | Implemented | |
| `initialExtent` | Required | Implemented | |
| `units` | Required | Implemented | |
| `maxImageWidth` | Optional | Implemented | Configurable, default 4096. |
| `maxImageHeight` | Optional | Implemented | Configurable, default 4096. |
| `maxRecordCount` | Optional | Implemented | From `LimitsOptions.Query.MaxRecordCount`. |
| `supportedQueryFormats` | Optional | Implemented | Normalized to uppercase from service `SupportedFormats`. Unlike FeatureServer, MapServer does not augment with runtime binary formats; however, layer queries delegate to the FeatureServer handler and support its full format set (including `f=parquet` and `f=arrow`). |
| `minScale` | Optional | Implemented | Derived from max of layer `minScale` values. |
| `maxScale` | Optional | Implemented | Derived from min of layer `maxScale` values. |
| `documentInfo` | Optional | Implemented | Title, Author, Comments, Subject, Category, Keywords. |
| `tileInfo` | Optional | Implemented | Includes tile dimensions, DPI, origin, spatial reference, and level-of-detail entries for the dynamic tile route. |

### Layer metadata (`GET .../MapServer/{layerId}`)

| Property | Spec | Status | Notes |
| --- | --- | --- | --- |
| `currentVersion` | Required | Implemented | 10.81 |
| `id` | Required | Implemented | |
| `name` | Required | Implemented | |
| `type` | Required | Implemented | "Feature Layer" or "Table". |
| `description` | Required | Implemented | |
| `geometryType` | Required | Implemented | |
| `extent` | Required | Implemented | |
| `spatialReference` | Required | Implemented | |
| `displayField` | Required | Implemented | First string field or OID. |
| `objectIdField` | Required | Implemented | |
| `fields` | Required | Implemented | |
| `capabilities` | Required | Implemented | |
| `drawingInfo` | Required | Implemented | From `ILayerStyleService` (JSON passthrough). |
| `supportsAdvancedQueries` | Required | Implemented | |
| `hasAttachments` | Required | Implemented | |
| `minScale` / `maxScale` | Required | Implemented | |
| `defaultVisibility` | Required | Implemented | |
| `maxRecordCount` | Required | Implemented | |
| `supportedQueryFormats` | Optional | Implemented | Normalized to uppercase from service `SupportedFormats`. Layer queries delegate to FeatureServer and support its full format set. |
| `supportsStatistics` | Optional | Implemented | `false` (not yet supported). |
| `supportsOrderBy` | Optional | Implemented | `true`. |
| `supportsDistinct` | Optional | Implemented | `true`. |
| `supportsPagination` | Optional | Implemented | `true`. |
| `parentLayerId` | Optional | Implemented | Always `-1` (flat, no group layers). |
| `subLayerIds` | Optional | Implemented | Always `null` (leaf layers). |
| `relationships` | Optional | Not implemented | |
| `editingInfo` | Optional | Not implemented | |
| `templates` | Optional | Not implemented | |

### Identify response

| Property | Status | Notes |
| --- | --- | --- |
| `results[].layerId` | Implemented | |
| `results[].layerName` | Implemented | |
| `results[].displayFieldName` | Implemented | Resolved display field for the layer. |
| `results[].value` | Implemented | Display field value or feature ID fallback. |
| `results[].attributes` | Implemented | |
| `results[].geometryType` | Implemented | |
| `results[].geometry` | Implemented | Controlled by `returnGeometry`. |

### Find response

| Property | Status | Notes |
| --- | --- | --- |
| `results[].layerId` | Implemented | |
| `results[].layerName` | Implemented | |
| `results[].displayFieldName` | Implemented | |
| `results[].foundFieldName` | Implemented | Which field matched the search text. |
| `results[].value` | Implemented | Display field value. |
| `results[].attributes` | Implemented | |
| `results[].geometryType` | Implemented | |
| `results[].geometry` | Implemented | Controlled by `returnGeometry`. |

## Notes

- Layer queries delegate to the FeatureServer query contract. See the [FeatureServer Coverage Matrix](feature-server-matrix.md) for parameter details.
- `parentLayerId`/`subLayerIds` are always flat (`-1`/`null`) because the domain model does not support group layers.
- `drawingInfo` is loaded from `ILayerStyleService` and returned as-is (JSON passthrough).
- The `find` operation uses SQL `LIKE` with `ESCAPE '\'` for `contains=true` and equality for `contains=false`, searching string fields only. Field names are double-quoted in generated SQL.
- Export defaults `transparent` to `false` per ArcGIS spec (configurable via `MapServer.DefaultTransparent` in service metadata).
- Export returns bytes by default (`f=image`). When `f=json` or `f=pjson` is requested, Honua stores the rendered image temporarily and returns an Esri-style JSON envelope with `href`, `width`, `height`, `extent`, and `scale`.
- `gdbVersion` is accepted and ignored across Export, Identify, and Find operations.
- Identify result limit uses `LimitsOptions.Query.MaxRecordCount` (configurable) instead of a hard-coded value.
- `maxAllowableOffset`, `geometryPrecision`, `returnZ`, `returnM`, and other advanced params are silently accepted (no validation error) but not yet applied.
- Service metadata includes a `tileInfo` block even though map tiles are generated dynamically (not from a pre-built fused cache).

## Implementation evidence

- Endpoint mapping: [MapServerEndpoints](../../src/Honua.Server/Features/Protocols/GeoServices/MapServer/MapServerEndpoints.cs)
- Export/identify/find implementation: [MapServerRequestHandlers.Export](../../src/Honua.Server/Features/Protocols/GeoServices/MapServer/MapServerRequestHandlers.Export.cs), [MapServerRequestHandlers.Identify](../../src/Honua.Server/Features/Protocols/GeoServices/MapServer/MapServerRequestHandlers.Identify.cs), [MapServerRequestHandlers.Find](../../src/Honua.Server/Features/Protocols/GeoServices/MapServer/MapServerRequestHandlers.Find.cs)
- Query, tiles, and legends: [MapServerRequestHandlers.Query](../../src/Honua.Server/Features/Protocols/GeoServices/MapServer/MapServerRequestHandlers.Query.cs), [MapServerRequestHandlers.Tile](../../src/Honua.Server/Features/Protocols/GeoServices/MapServer/MapServerRequestHandlers.Tile.cs), [MapServerRequestHandlers.Legend](../../src/Honua.Server/Features/Protocols/GeoServices/MapServer/MapServerRequestHandlers.Legend.cs)
- Standards aliases: [OgcClassicRequestHandlers.Wms](../../src/Honua.Server/Features/Protocols/Ogc/Classic/OgcClassicRequestHandlers.Wms.cs), [OgcClassicRequestHandlers.Wmts](../../src/Honua.Server/Features/Protocols/Ogc/Classic/OgcClassicRequestHandlers.Wmts.cs)
- Integration tests: [MapServerEndpointTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/MapServer/MapServerEndpointTests.cs), [MapServerTileEndpointTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/GeoServices/MapServer/MapServerTileEndpointTests.cs), [OgcClassicWmsTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Classic/OgcClassicWmsTests.cs), [OgcClassicWmtsTests](../../tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Classic/OgcClassicWmtsTests.cs)
