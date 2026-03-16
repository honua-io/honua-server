# FeatureServer API Matrix (Esri Enterprise vs Honua)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/
- https://developers.arcgis.com/rest/services-reference/enterprise/layer-feature-service/

Legend:
- Implemented: endpoint exists and handles the operation.
- Partial: endpoint exists but only a subset of parameters or behavior is implemented.
- Not implemented: no endpoint or handler.

## Esri REST Feature Service coverage

This matrix tracks Honua coverage against the Esri REST Feature Service specification:
- https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/

MapServer coverage is tracked separately:
- [MapServer Coverage Matrix](map-server-matrix.md)

## Feature Service (root resource)

### Implemented

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Service metadata | `/rest/services/{serviceId}/FeatureServer` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer` | Service metadata + layer list. Dynamic capabilities string (Query, Create, Update, Delete, Editing, Extract, Uploads). Includes `allowGeometryUpdates`, `supportsStatistics`, `supportsAdvancedQueries`. |
| Apply Edits | `/rest/services/{serviceName}/FeatureServer/applyEdits` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/applyEdits` | Multi-layer batch edits. Request body is a JSON array of per-layer edits (`[{id, adds, updates, deletes}]`). Response aggregates per-layer results. |
| Append | `/rest/services/{serviceName}/FeatureServer/append` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/append` | Bulk append features to a target layer. Parses `edits` as a JSON array of GeoServices features; delegates to `applyEdits` internally. Returns `numFeaturesAppended` / `numFeaturesFailed`. |
| Query | `/rest/services/{serviceName}/FeatureServer/query` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/FeatureServer/query` | Service-level query that delegates to a target layer provided by `layerId` or `layers`. |
| Query Domains | `/rest/services/{serviceName}/FeatureServer/queryDomains` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/queryDomains` | Returns coded-value domain definitions by sampling feature values from the database. Supports `layers` parameter. |
| Relationships | `/rest/services/{serviceName}/FeatureServer/relationships` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/relationships` | Returns relationship definitions aggregated across service layers. |
| Create Replica | `/rest/services/{serviceName}/FeatureServer/createReplica` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/createReplica` | Creates a replica for offline synchronization. Parameters: `replicaName`, `layers`, `syncModel`. Distributed cache-backed replica registration with in-memory fallback. |
| Extract Changes | `/rest/services/{serviceName}/FeatureServer/extractChanges` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/extractChanges` | Returns changes since last synchronization. First sync reports all features as adds (real DB count); subsequent syncs report zero changes (no change tracking tables). |
| Synchronize Replica | `/rest/services/{serviceName}/FeatureServer/synchronizeReplica` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica` | Synchronizes a replica. Supports `download`, `upload`, and `bidirectional` sync directions. Incoming edits on upload/bidirectional syncs are applied via `applyEdits`. |
| Unregister Replica | `/rest/services/{serviceName}/FeatureServer/unRegisterReplica` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica` | Removes a registered replica. |

### Not implemented

| Esri operation | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Get Estimates | `/rest/services/{serviceName}/FeatureServer/getEstimates` | GET | Not implemented | |

## Feature Layer (resource + operations)

### Implemented

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Layer metadata | `/rest/services/{serviceId}/FeatureServer/{layerId}` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/{layerId}` | Includes `editFieldsInfo`, `editingInfo`, `templates`, `allowGeometryUpdates`, `supportsStatistics`. |
| Query | `/rest/services/{serviceName}/FeatureServer/{layerId}/query` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/FeatureServer/{layerId}/query` | Full query support including statistics, distinct, spatial, temporal. See parameter coverage below. |
| Apply Edits | `/rest/services/{serviceName}/FeatureServer/{layerId}/applyEdits` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits` | Supports adds/updates/deletes, rollbackOnFailure (default `false`), useGlobalIds. |
| Add Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/addFeatures` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures` | Standalone add endpoint. rollbackOnFailure defaults to `true`. |
| Update Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/updateFeatures` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures` | Standalone update endpoint. rollbackOnFailure defaults to `true`. |
| Delete Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/deleteFeatures` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures` | Supports `objectIds`, `where` clause, and `geometry`/`geometryType`/`spatialRel`/`inSR` for spatial deletes. rollbackOnFailure defaults to `true`. |
| Query Related Records | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryRelatedRecords` | GET, POST | Implemented | `GET/POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords` | Supports objectIds, relationshipId, where, outFields, returnGeometry, resultRecordCount. |
| Generate Renderer | `/rest/services/{serviceName}/FeatureServer/{layerId}/generateRenderer` | GET | Partial | `GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer` | Returns a simple renderer. `classificationDef` is rejected with 400 (classification-based renderers not yet supported). |
| Append | `/rest/services/{serviceName}/FeatureServer/{layerId}/append` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/append` | Bulk append features. Parses `edits` as a JSON array of GeoServices features; delegates to `applyEdits` internally. Returns `numFeaturesAppended` / `numFeaturesFailed`. |
| Calculate | `/rest/services/{serviceName}/FeatureServer/{layerId}/calculate` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate` | Calculates field values using expressions. Supports `where` filter, `calcExpression` as JSON array of `{field, sqlExpression}`. Applies string literals, numeric literals, NULL, and field references. |
| Validate SQL | `/rest/services/{serviceName}/FeatureServer/{layerId}/validateSQL` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/{layerId}/validateSQL` | Validates a SQL WHERE clause against the layer schema. Returns `isValidSQL` and `validationError`. |

### Partial

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Query Attachments | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryAttachments` | GET, POST | Partial | `GET/POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments` | Requires `objectId`; other Esri parameters not supported. |

### Not implemented

| Esri operation | Esri path | Methods | Honua status | Notes |
| --- | --- | --- | --- | --- |
| Cleanup Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/cleanupAssets` | GET | Not implemented | |
| Convert 3D | `/rest/services/{serviceName}/FeatureServer/{layerId}/convert3D` | GET | Not implemented | |
| Get Estimates | `/rest/services/{serviceName}/FeatureServer/{layerId}/getEstimates` | GET | Not implemented | |
| Has Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/hasAssets` | GET | Not implemented | |
| Query 3D | `/rest/services/{serviceName}/FeatureServer/{layerId}/query3D` | GET | Not implemented | |
| Query Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryAssets` | GET | Not implemented | |
| Query Bins | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryBins` | GET | Not implemented | |
| Query Date Bins | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryDateBins` | GET | Not implemented | |
| Query Top Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryTopFeatures` | GET | Not implemented | |
| Update Metadata | `/rest/services/{serviceName}/FeatureServer/{layerId}/metadata/update` | POST | Not implemented | |
| Upload Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/uploadAssets` | GET | Not implemented | |

## Attachments

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Add Attachment | `.../{layerId}/addAttachment` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment` | Form data: `objectId`, file, optional `keywords`. |
| Update Attachment | `.../{layerId}/updateAttachment` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateAttachment` | Form data: `objectId`, `attachmentId`, optional `keywords`. |
| Delete Attachments | `.../{layerId}/deleteAttachments` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteAttachments` | Form data: `objectId`, comma-separated `attachmentIds`. |
| Download Attachment | `.../{layerId}/{featureId}/attachments/{attachmentId}` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}` | Binary download. |

## Query parameter coverage (layer `/query`)

### Implemented

| Area | Esri parameters | Honua status | Notes |
| --- | --- | --- | --- |
| Filtering | `where`, `objectIds` | Implemented | ArcGIS SQL parser; objectIds bypass where. |
| Spatial filters | `geometry`, `geometryType`, `spatialRel`, `distance`, `units` | Implemented | Distance + KNN supported; geometry supports GeoServices JSON or point/envelope CSV. |
| Spatial reference | `inSR`, `outSR` | Implemented | GeoJSON output requires EPSG:4326; non-4326 `outSR` is rejected for non-special query modes. GeoParquet output requires EPSG:4326 when the response includes a geometry column; non-4326 `outSR` is rejected unless geometry is absent (`returnGeometry=false`, non-geometry layer, or a special query mode). Both formats reproject coordinates to EPSG:4326 when `outSR` is omitted. |
| Pagination | `resultOffset`, `resultRecordCount` | Implemented | Validated against limits. |
| Fields | `outFields` | Implemented | `*` returns all fields. |
| Sorting | `orderByFields` | Implemented | Validates against layer fields; supports any field with ASC/DESC. |
| Output flags | `returnGeometry`, `returnIdsOnly`, `returnCountOnly`, `returnExtentOnly`, `returnZ`, `returnM` | Implemented | Standard query outputs supported. `returnZ` and `returnM` are applied by `json`, `geojson`, and `pbf` formatters; binary formats `fgb`, `geobuf`, and `parquet` write raw geometry and do not filter dimensions. |
| Distinct | `returnDistinctValues` | Implemented | In-memory distinct over returned features; works best with explicit `outFields`. |
| Statistics | `outStatistics`, `groupByFieldsForStatistics` | Implemented | Aggregate queries with COUNT, SUM, MIN, MAX, AVG, STDDEV, VAR. Supports GROUP BY on any layer field. |
| KNN output | `nearestCount`, `returnDistance` | Partial | `returnDistance` only affects KNN queries. The computed `distance` attribute is included in `json`, `geojson`, and `parquet` output; `pbf`, `fgb`, and `geobuf` build their schema from layer fields only and omit runtime-computed attributes. |
| Temporal | `time`, `timeRelation` | Implemented | Uses layer timeInfo or first temporal field. |
| Output format | `f=json`, `f=geojson`, `f=pbf`, `f=fgb`, `f=geobuf`, `f=parquet` | Implemented | Six output formats supported. Binary formats (`fgb`, `geobuf`, `parquet`) also accept `Accept` header negotiation; `f=` takes precedence. See [Output format details](#output-format-details) below. |
| Geometry precision | `geometryPrecision` | Implemented | Rounds coordinates to specified decimal places. |
| Geometry simplification | `maxAllowableOffset` | Implemented | Simplifies geometry to the given tolerance. Applies to `json`, `geojson`, and `pbf` output only; `fgb`, `geobuf`, and `parquet` do not apply it. |

### Not implemented (explicitly rejected)

| Area | Esri parameters | Notes |
| --- | --- | --- |
| Having | `having` | Rejected; HAVING clause for statistics not yet supported. |
| Centroid | `returnCentroid` | Rejected. |
| True curves | `returnTrueCurves` | Rejected. |
| Exceeded limit | `returnExceededLimitFeatures` | Rejected. |
| Result type | `resultType` (non-standard) | Rejected. |
| SQL format | `sqlFormat` | Rejected. |
| GDB version | `gdbVersion` | Rejected. |
| Quantization | `quantizationParameters` | Rejected. |
| Datum transform | `datumTransformation` | Rejected. |

### Output format details

All non-JSON formats also accept `Accept` header negotiation (e.g. `Accept: application/vnd.apache.parquet`). When both `f=` and `Accept` are present, `f=` takes precedence.

**Special query modes**: `returnCountOnly`, `returnIdsOnly`, `returnExtentOnly`, and `outStatistics` queries always return JSON regardless of the requested format.

| Format | Content type | Notes |
| --- | --- | --- |
| `json` / `pjson` | `application/json` | Default GeoServices JSON. |
| `geojson` | `application/geo+json` | RFC 7946. Requires EPSG:4326. |
| `pbf` | `application/x-protobuf` | Esri-compatible Protocol Buffers. |
| `fgb` | `application/vnd.flatgeobuf` | FlatGeobuf binary. |
| `geobuf` | `application/geobuf` | Requires a store with native GeoBuf support. |
| `parquet` | `application/vnd.apache.parquet` | GeoParquet 1.1.0 with WKB-encoded geometry. Non-4326 `outSR` is rejected when the response includes a geometry column (CRS metadata cannot be written correctly; tracked as follow-up); allowed when `returnGeometry=false` or the layer has no geometry. When `outSR` is omitted, coordinates are reprojected to EPSG:4326 (matching GeoJSON behavior). CRS metadata omits the `crs` key for EPSG:4326 (spec-compliant OGC:CRS84 default). `bbox` is omitted because the spec defines it as the bounding box of geometries in the file, not the layer extent. |

**Binary format limitations** (`fgb`, `geobuf`, `parquet`):
- `geometryPrecision`, `maxAllowableOffset`, `returnZ`, and `returnM` are not applied — geometry is written as raw WKB.
- `exceededTransferLimit` is not conveyed. Clients should compare the returned feature count against `maxRecordCount` to detect truncation.

## ApplyEdits parameter coverage (layer `/applyEdits`)

### Implemented

| Esri parameter | Notes |
| --- | --- |
| `adds` | Accepts GeoServices features; geometry required for geometry layers. |
| `updates` | Requires `objectId` in attributes. |
| `deletes` | Expects object ID values; global/unique IDs not supported. |
| `rollbackOnFailure` | Default `false` for applyEdits, `true` for standalone add/update/delete endpoints. |

### Not implemented

| Esri parameter | Notes |
| --- | --- |
| `useGlobalIds` | Rejected; object IDs are required. |
| `gdbVersion` | Rejected (`400 Bad Request`). |
| `returnEditMoment` | Ignored. |
| `attachments` | Use dedicated attachment endpoints. |
| `assetMaps` | Ignored. |
| `trueCurveClient` | Ignored. |
| `sessionID` | Ignored. |
| `usePreviousEditMoment` | Ignored. |
| `datumTransformation` | Geometry must match layer SRID. |
| `timeReferenceUnknownClient` | Ignored. |
| `async` | Ignored. |
| `returnEditResults` | Results are always returned. |
| `editsUploadId` | Ignored. |
| `editsUploadFormat` | Ignored. |
| `useUniqueIds` | Ignored. |
| `f` | Response is always JSON. |

## QueryRelatedRecords parameter coverage (layer `/queryRelatedRecords`)

### Implemented

| Esri parameter | Notes |
| --- | --- |
| `objectIds` | Required. |
| `relationshipId` | Required. |
| `outFields` | Supports `*` for all fields. |
| `definitionExpression` | Aliased to `where` (combined with `where` if both present). |
| `returnGeometry` | Defaults to true. |
| `resultRecordCount` | Applies limit. |
| `resultOffset` | Applies offset. |

### Not implemented

| Esri parameter | Notes |
| --- | --- |
| `maxAllowableOffset` | Ignored. |
| `geometryPrecision` | Ignored. |
| `historicMoment` | Ignored. |
| `outSR` | Output SR always uses the related layer SR. |
| `returnZ` | Ignored. |
| `returnM` | Ignored. |
| `returnTrueCurves` | Ignored. |
| `gdbVersion` | Rejected (`400 Bad Request`). |
| `orderByFields` | Ignored. |
| `returnCountOnly` | Ignored. |
| `f` | Response is always JSON. |

## Service metadata properties

| Property | Honua status | Notes |
| --- | --- | --- |
| `capabilities` | Implemented | Dynamic: Query, Create, Update, Delete, Editing, Extract, Uploads based on service config. |
| `layers` | Implemented | Full layer list with id, name, geometryType, visibility, scale range. |
| `spatialReference` | Implemented | From service definition. |
| `initialExtent` / `fullExtent` | Implemented | From service effective extent. |
| `maxRecordCount` | Implemented | From query limits. |
| `supportedQueryFormats` | Implemented | Normalized to uppercase and augmented with runtime-supported binary formats (`PBF`, `FGB`, `PARQUET`, and `GEOBUF` when the backing store exposes native GeoBuf output). |
| `supportsAdvancedQueries` | Implemented | From service definition. |
| `supportsStatistics` | Implemented | Always true. |
| `objectIdField` | Implemented | Resolved from layer primary keys or defaults to `objectid`. |
| `fields` | Implemented | All fields across service layers. |
| `allowGeometryUpdates` | Implemented | Reflects `supportsEditing`. |

## Layer metadata properties

| Property | Honua status | Notes |
| --- | --- | --- |
| `id`, `name`, `description`, `type` | Implemented | Standard layer info. |
| `geometryType` | Implemented | Mapped to esriGeometry* types. |
| `spatialReference` | Implemented | From layer definition. |
| `extent` | Implemented | From layer extent. |
| `fields` | Implemented | Full field list with type, alias, length, nullable, editable. |
| `objectIdField` | Implemented | From primary key or default. |
| `drawingInfo` | Implemented | From layer metadata. |
| `capabilities` | Implemented | Dynamic: Query, Create, Update, Delete, Editing, Extract, Uploads. |
| `supportsStatistics` | Implemented | Always true. |
| `supportsAdvancedQueries` | Implemented | From service definition. |
| `supportsOrderBy`, `supportsDistinct`, `supportsPagination` | Implemented | All true. |
| `supportsRollbackOnFailureParameter` | Implemented | Reflects editing support. |
| `hasAttachments` | Implemented | From layer definition. |
| `supportsQueryRelated` | Implemented | True when layer has relationships. |
| `relationships` | Implemented | Relationship info array. |
| `allowGeometryUpdates` | Implemented | Reflects editing support. |
| `editFieldsInfo` | Implemented | Null (editor tracking not supported). |
| `editingInfo` | Implemented | Present for editable layers. |
| `templates` | Implemented | Empty array (no feature templates configured). |
| `timeInfo` | Implemented | Start/end time fields, time extent, track ID. |
| `maxRecordCount` | Implemented | From query limits. |
| `supportedQueryFormats` | Implemented | Normalized format list plus runtime-supported binary formats (`PBF`, `FGB`, `PARQUET`, and conditional `GEOBUF` when the backing store exposes native GeoBuf output). |
