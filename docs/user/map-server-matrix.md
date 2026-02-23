# MapServer API Matrix (Esri Enterprise vs Honua)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service/
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service-layer/

Legend: **Implemented** | **Partial** | **Not implemented**

---

## Operations

### Implemented

| Operation | Esri path | Methods | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- |
| Service metadata | `.../MapServer` | GET | `GET .../MapServer` | Includes `maxRecordCount`, `supportedQueryFormats`, `documentInfo`, `minScale`/`maxScale`. |
| Layer metadata | `.../MapServer/{layerId}` | GET | `GET .../MapServer/{layerId}` | Includes `drawingInfo`, query capability flags, `parentLayerId`/`subLayerIds`. |
| Export map | `.../MapServer/export` | GET/POST | `GET/POST .../MapServer/export` | `dynamicLayers`, `time`, `layerTimeOptions`, `layerDefs`, `backgroundColor`. Default `transparent=false` per spec. Unsupported `gdbVersion` is rejected (`400 Bad Request`). |
| Identify | `.../MapServer/identify` | GET/POST | `GET/POST .../MapServer/identify` | All geometry types, `dynamicLayers`, `time`/`timeRelation`, `layerDefs`. Returns `displayFieldName`. Unsupported `gdbVersion` is rejected (`400 Bad Request`). |
| Find | `.../MapServer/find` | GET/POST | `GET/POST .../MapServer/find` | Cross-layer text search: `searchText`, `layers`, `contains`, `searchFields`, `sr`, `layerDefs`, `dynamicLayers`, `returnGeometry`. Unsupported `gdbVersion` is rejected (`400 Bad Request`). |
| Legend | `.../MapServer/legend` | GET | `GET .../MapServer/legend` | Swatch images for visible layers. Supports `size` and `dynamicLayers`. |
| Layer query | `.../MapServer/{layerId}/query` | GET/POST | `GET/POST .../MapServer/{layerId}/query` | Delegates to FeatureServer query handler. See [FeatureServer matrix](feature-server-matrix.md). |
| Service-level query | `.../MapServer/query` | GET/POST | `GET/POST .../MapServer/query` | Delegates to FeatureServer query handler using `layerId` or `layers`. |
| Tile | `.../MapServer/tile/{z}/{y}/{x}` | GET | `GET .../MapServer/tile/{z}/{y}/{x}` | Returns rendered PNG map tiles. |
| WMTS | `.../MapServer/WMTS` | GET | `GET .../MapServer/WMTS` | Supports `GetCapabilities` and `GetTile` (KVP), `WebMercatorQuad` only. |
| WMS | `.../MapServer/WMS` | GET | `GET .../MapServer/WMS` | Supports `GetCapabilities` and `GetMap` (KVP). |

### Not implemented

| Operation | Esri path | Methods | Notes |
| --- | --- | --- | --- |
| Generate KML | `.../MapServer/generateKml` | GET | |

---

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
| `supportedQueryFormats` | Optional | Implemented | Comma-separated string from service `SupportedFormats`. |
| `minScale` | Optional | Implemented | Derived from max of layer `minScale` values. |
| `maxScale` | Optional | Implemented | Derived from min of layer `maxScale` values. |
| `documentInfo` | Optional | Implemented | Title, Author, Comments, Subject, Category, Keywords. |
| `tileInfo` | Optional | Not implemented | Tiles are served dynamically via `.../MapServer/tile/{z}/{y}/{x}` but the `tileInfo` metadata block is not included in the service metadata response. |

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
| `supportedQueryFormats` | Optional | Implemented | Comma-separated string from service `SupportedFormats`. |
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

---

## Notes

- Layer queries delegate to the FeatureServer query contract. See the [FeatureServer Coverage Matrix](feature-server-matrix.md) for parameter details.
- `parentLayerId`/`subLayerIds` are always flat (`-1`/`null`) because the domain model does not support group layers.
- `drawingInfo` is loaded from `ILayerStyleService` and returned as-is (JSON passthrough).
- The `find` operation uses SQL `LIKE` with `ESCAPE '\'` for `contains=true` and equality for `contains=false`, searching string fields only. Field names are double-quoted in generated SQL.
- Export defaults `transparent` to `false` per ArcGIS spec (configurable via `MapServer.DefaultTransparent` in service metadata).
- `gdbVersion` is consistently rejected with `400 Bad Request` across Export, Identify, and Find operations.
- Identify result limit uses `LimitsOptions.Query.MaxRecordCount` (configurable) instead of a hard-coded value.
- `maxAllowableOffset`, `geometryPrecision`, `returnZ`, `returnM`, and other advanced params are silently accepted (no validation error) but not yet applied.
