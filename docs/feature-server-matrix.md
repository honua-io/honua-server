# FeatureServer API Matrix (Esri Enterprise vs Honua)

Sources:
- https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/
- https://developers.arcgis.com/rest/services-reference/enterprise/layer-feature-service/

Legend:
- Implemented: endpoint exists and handles the operation.
- Partial: endpoint exists but only a subset of parameters or behavior is implemented.
- Stubbed: endpoint exists but returns "not implemented".
- Not implemented: no endpoint or handler.

## Esri REST Feature Service coverage

This matrix tracks Honua coverage against the Esri REST Feature Service specification:
- https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/

## Feature Service (root resource)

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Feature service metadata (resource) | `/rest/services/{serviceId}/FeatureServer` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer` | Service metadata + layer list. |
| Append | `/rest/services/{serviceName}/FeatureServer/append` | POST | Not implemented | None | No service-level append endpoint. |
| Apply Edits | `/rest/services/{serviceName}/FeatureServer/applyEdits` | POST | Not implemented | None | Layer-level applyEdits is implemented. |
| Create Replica | `/rest/services/{serviceName}/FeatureServer/createReplica` | POST | Not implemented | None | Replication APIs not present. |
| Extract Changes | `/rest/services/{serviceName}/FeatureServer/extractChanges` | POST | Not implemented | None | Replication APIs not present. |
| Get Estimates | `/rest/services/{serviceName}/FeatureServer/getEstimates` | GET | Not implemented | None | Not implemented. |
| Query | `/rest/services/{serviceName}/FeatureServer/query` | GET | Not implemented | None | Only layer-level query exists. |
| Query Domains | `/rest/services/{serviceName}/FeatureServer/queryDomains` | GET | Not implemented | None | Not implemented. |
| Relationships | `/rest/services/{serviceName}/FeatureServer/relationships` | GET | Not implemented | None | Not implemented. |
| Synchronize Replica | `/rest/services/{serviceName}/FeatureServer/synchronizeReplica` | POST | Not implemented | None | Not implemented. |
| Unregister Replica | `/rest/services/{serviceName}/FeatureServer/unRegisterReplica` | POST | Not implemented | None | Not implemented. |

## Feature Layer (resource + operations)

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Feature layer metadata (resource) | `/rest/services/{serviceId}/FeatureServer/{layerId}` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/{layerId}` | Layer metadata. |
| Add Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/addFeatures` | POST | Not implemented | None | Use `applyEdits` adds. |
| Append | `/rest/services/{serviceName}/FeatureServer/{layerId}/append` | POST | Not implemented | None | Not implemented. |
| Apply Edits | `/rest/services/{serviceName}/FeatureServer/{layerId}/applyEdits` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits` | Supports adds/updates/deletes, rollbackOnFailure, useGlobalIds. |
| Calculate | `/rest/services/{serviceName}/FeatureServer/{layerId}/calculate` | GET | Not implemented | None | Not implemented. |
| Cleanup Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/cleanupAssets` | GET | Not implemented | None | Not implemented. |
| Convert 3D | `/rest/services/{serviceName}/FeatureServer/{layerId}/convert3D` | GET | Not implemented | None | Not implemented. |
| Has Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/hasAssets` | GET | Not implemented | None | Not implemented. |
| Delete Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/deleteFeatures` | POST | Not implemented | None | Use `applyEdits` deletes. |
| Get Estimates | `/rest/services/{serviceName}/FeatureServer/{layerId}/getEstimates` | GET | Not implemented | None | Not implemented. |
| Generate Renderer | `/rest/services/{serviceName}/FeatureServer/{layerId}/generateRenderer` | GET | Implemented (simple) | `GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer` | Returns a simple renderer; `classificationDef` is validated but ignored. |
| Query | `/rest/services/{serviceName}/FeatureServer/{layerId}/query` | GET | Implemented | `GET/POST /rest/services/{serviceId}/FeatureServer/{layerId}/query` | GET + POST supported. |
| Query 3D | `/rest/services/{serviceName}/FeatureServer/{layerId}/query3D` | GET | Not implemented | None | Not implemented. |
| Query Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryAssets` | GET | Not implemented | None | Not implemented. |
| Query Attachments | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryAttachments` | GET | Partial | `GET/POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments` | Requires `objectId`; other Esri parameters not supported. |
| Query Bins | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryBins` | GET | Not implemented | None | Not implemented. |
| Query Date Bins | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryDateBins` | GET | Not implemented | None | Not implemented. |
| Query Top Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryTopFeatures` | GET | Not implemented | None | Not implemented. |
| Query Related Records | `/rest/services/{serviceName}/FeatureServer/{layerId}/queryRelatedRecords` | GET | Implemented | `GET/POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords` | Supports objectIds, relationshipId, where, outFields, returnGeometry, resultRecordCount. |
| Update Features | `/rest/services/{serviceName}/FeatureServer/{layerId}/updateFeatures` | POST | Not implemented | None | Use `applyEdits` updates. |
| Update Metadata | `/rest/services/{serviceName}/FeatureServer/{layerId}/metadata/update` | POST | Not implemented | None | Not implemented. |
| Upload Assets | `/rest/services/{serviceName}/FeatureServer/{layerId}/uploadAssets` | GET | Not implemented | None | Not implemented. |
| Validate SQL | `/rest/services/{serviceName}/FeatureServer/{layerId}/validateSQL` | GET | Not implemented | None | Not implemented. |

## Attachments (additional FeatureServer operations)

| Esri operation | Esri path | Methods | Honua status | Honua endpoint(s) | Notes |
| --- | --- | --- | --- | --- | --- |
| Add Attachment | `/rest/services/{serviceName}/FeatureServer/{layerId}/addAttachment` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment` | Form data: `objectId`, file, optional `keywords`. |
| Update Attachment | `/rest/services/{serviceName}/FeatureServer/{layerId}/updateAttachment` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateAttachment` | Form data: `objectId`, `attachmentId`, optional `keywords`. |
| Delete Attachments | `/rest/services/{serviceName}/FeatureServer/{layerId}/deleteAttachments` | POST | Implemented | `POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteAttachments` | Form data: `objectId`, comma-separated `attachmentIds`. |
| Download Attachment | `/rest/services/{serviceName}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}` | GET | Implemented | `GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}` | Binary download. |

## Query parameter coverage (layer `/query`)

| Area | Esri parameters | Honua status | Notes |
| --- | --- | --- | --- |
| Filtering | `where`, `objectIds` | Implemented | ArcGIS SQL parser; objectIds bypass where. |
| Spatial filters | `geometry`, `geometryType`, `spatialRel`, `distance`, `units` | Implemented | Distance + KNN supported; geometry supports GeoServices JSON or point/envelope CSV. |
| Spatial reference | `inSR`, `outSR` | Implemented | GeoJSON output requires EPSG:4326. |
| Pagination | `resultOffset`, `resultRecordCount` | Implemented | Validated against limits. |
| Fields/sort | `outFields`, `orderByFields` | Implemented | `*` returns all fields. |
| Output flags | `returnGeometry`, `returnIdsOnly`, `returnCountOnly`, `returnExtentOnly`, `returnZ`, `returnM` | Implemented | Standard query outputs supported. |
| Distinct | `returnDistinctValues` | Implemented (in-memory) | Distinct over returned features; best with explicit `outFields`. |
| KNN output | `nearestCount`, `returnDistance` | Partial | `returnDistance` only affects KNN queries. |
| Temporal | `time`, `timeRelation` | Implemented (limited) | Uses layer timeInfo or first temporal field. |
| Output format | `f=json`, `f=geojson` | Partial | `f=pbf` rejected. |
| Rejected | `returnCentroid` | Not implemented | Explicitly rejected. |
| Rejected | `returnTrueCurves`, `returnExceededLimitFeatures`, `resultType` (non-standard) | Not implemented | Explicitly rejected. |
| Rejected | `outStatistics`, `groupByFieldsForStatistics`, `having` | Not implemented | Explicitly rejected. |
| Rejected | `sqlFormat`, `gdbVersion`, `quantizationParameters`, `datumTransformation` | Not implemented | Explicitly rejected. |

## ApplyEdits parameter coverage (layer `/applyEdits`)

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `adds` | Implemented | Accepts GeoServices features; geometry required for layers. |
| `updates` | Implemented | Requires `objectId` in attributes. |
| `deletes` | Implemented | Expects object ID values; global/unique IDs not supported. |
| `rollbackOnFailure` | Implemented | Default is `true`. |
| `useGlobalIds` | Not implemented | Explicitly rejected; object IDs are required. |
| `gdbVersion` | Not implemented | Ignored. |
| `returnEditMoment` | Not implemented | Ignored. |
| `attachments` | Not implemented | Use dedicated attachment endpoints instead. |
| `assetMaps` | Not implemented | Ignored. |
| `trueCurveClient` | Not implemented | Ignored. |
| `sessionID` | Not implemented | Ignored. |
| `usePreviousEditMoment` | Not implemented | Ignored. |
| `datumTransformation` | Not implemented | Geometry must match layer SRID. |
| `timeReferenceUnknownClient` | Not implemented | Ignored. |
| `async` | Not implemented | Ignored. |
| `returnEditResults` | Not implemented | Results are always returned. |
| `editsUploadId` | Not implemented | Ignored. |
| `editsUploadFormat` | Not implemented | Ignored. |
| `useUniqueIds` | Not implemented | Ignored. |
| `f` | Not implemented | Response is always JSON. |

## QueryRelatedRecords parameter coverage (layer `/queryRelatedRecords`)

| Esri parameter | Honua status | Notes |
| --- | --- | --- |
| `objectIds` | Implemented | Required. |
| `relationshipId` | Implemented | Required. |
| `outFields` | Implemented | Supports `*` for all fields. |
| `definitionExpression` | Implemented | Aliased to `where` (combined with `where` if both are present). |
| `returnGeometry` | Implemented | Defaults to true. |
| `maxAllowableOffset` | Not implemented | Ignored. |
| `geometryPrecision` | Not implemented | Ignored. |
| `historicMoment` | Not implemented | Ignored. |
| `outSR` | Not implemented | Output SR always uses the related layer SR. |
| `returnZ` | Not implemented | Ignored. |
| `returnM` | Not implemented | Ignored. |
| `returnTrueCurves` | Not implemented | Ignored. |
| `gdbVersion` | Not implemented | Ignored. |
| `resultRecordCount` | Implemented | Applies limit. |
| `resultOffset` | Implemented | Applies offset. |
| `orderByFields` | Not implemented | Ignored. |
| `returnCountOnly` | Not implemented | Ignored. |
| `f` | Not implemented | Response is always JSON. |

## Notes and gaps worth highlighting

- Service-level FeatureServer operations (append, applyEdits, query, replicas, etc.) are not implemented; only layer-level operations are exposed.
- `generateRenderer` returns a simple renderer; `classificationDef` is accepted but not interpreted.
- `queryAttachments` supports only a required `objectId` and omits other Esri parameters such as `globalIds`, `attachmentTypes`, `definitionExpression`, and `size`.
- `returnCentroid` is explicitly rejected; `returnDistinctValues` is supported as an in-memory distinct of the returned features.
