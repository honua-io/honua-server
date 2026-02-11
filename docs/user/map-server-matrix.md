# MapServer API Matrix (Esri Enterprise vs Honua)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service/
- https://developers.arcgis.com/rest/services-reference/enterprise/map-service-layer/

Legend: **Implemented** | **Partial** | **Stubbed** | **Not implemented**

---

## Operations

### Implemented

| Operation | Esri path | Methods | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- |
| Service metadata | `.../MapServer` | GET | `GET .../MapServer` | Includes `maxRecordCount`, `supportedQueryFormats`. |
| Layer metadata | `.../MapServer/{layerId}` | GET | `GET .../MapServer/{layerId}` | Includes `drawingInfo`, query capability flags, `parentLayerId`/`subLayerIds`. |
| Export map | `.../MapServer/export` | GET/POST | `GET/POST .../MapServer/export` | `dynamicLayers`, `time`, `layerTimeOptions`, `layerDefs`, `backgroundColor`. Default `transparent=false` per spec. |
| Identify | `.../MapServer/identify` | GET/POST | `GET/POST .../MapServer/identify` | All geometry types, `dynamicLayers`, `time`/`timeRelation`, `layerDefs`. Returns `displayFieldName`. |
| Find | `.../MapServer/find` | GET/POST | `GET/POST .../MapServer/find` | Cross-layer text search: `searchText`, `layers`, `contains`, `searchFields`, `sr`, `layerDefs`, `returnGeometry`. |
| Legend | `.../MapServer/legend` | GET | `GET .../MapServer/legend` | Swatch images for visible layers. Supports `size` and `dynamicLayers`. |
| Layer query | `.../MapServer/{layerId}/query` | GET/POST | `GET/POST .../MapServer/{layerId}/query` | Delegates to FeatureServer query handler. See [FeatureServer matrix](feature-server-matrix.md). |

### Not implemented

| Operation | Esri path | Methods | Notes |
| --- | --- | --- | --- |
| Generate KML | `.../MapServer/generateKml` | GET | |
| Service-level query | `.../MapServer/query` | GET | Only layer-level query exists. |
| Tile | `.../MapServer/tile/{z}/{y}/{x}` | GET | Use OGC Tiles protocol. |
| WMTS | `.../MapServer/WMTS` | GET | Use OGC Tiles protocol. |
| WMS | `.../MapServer/WMS` | GET | |

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
| `supportedQueryFormats` | Optional | Implemented | From service `SupportedFormats`. |
| `documentInfo` | Optional | Not implemented | |
| `tileInfo` | Optional | Not implemented | No tile cache. |

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
| `supportedQueryFormats` | Optional | Implemented | From service `SupportedFormats`. |
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
- The `find` operation uses SQL `LIKE` for `contains=true` and equality for `contains=false`, searching string fields only.
- Export defaults `transparent` to `false` per ArcGIS spec (configurable via `MapServer.DefaultTransparent` in service metadata).
