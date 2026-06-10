# GeoServices REST (ArcGIS-compatible)

Honua serves an ArcGIS-compatible GeoServices REST surface under `/rest/services`, plus a Portal Sharing slice under `/sharing/rest` for token issuance and item discovery. Existing Esri clients (ArcGIS JS API, ArcGIS Pro, Field Maps, Koop) connect without modification.

## Catalog and portal routes

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/rest/info` | Server info (version, token endpoint). |
| GET | `/rest/services` | Service catalog listing. |
| GET, POST | `/sharing/rest/generateToken` | ArcGIS token issuance. |
| GET | `/sharing/rest/info` | Portal sharing info. |
| GET | `/sharing/rest/portals/self`, `/sharing/rest/community/self` | Portal/community self documents. |
| GET | `/sharing/rest/search` | Portal item search. |
| GET | `/sharing/rest/content/items/{id}`, `.../{id}/data` | Portal item metadata and data. |
| GET, POST | `/sharing/rest/oauth2/authorize`, `/callback`, `/token` | Portal OAuth2 named-user bridge. |

```bash
curl "https://server.example.com/rest/services?f=json"
```

## FeatureServer

Base: `/rest/services/{serviceId}/FeatureServer` (service and `/{layerId}` metadata via GET or POST).

| Operation group | Routes | Notes |
| --- | --- | --- |
| Query | `/{layerId}/query` (GET, POST), `/query` (service level) | `where`, `objectIds`, `geometry`, `geometryType`, `inSR`, `spatialRel`, `outFields`, `outSR`, `returnGeometry`, `orderByFields`, `resultOffset`, `resultRecordCount`, `outStatistics`, `groupByFieldsForStatistics`, `returnCountOnly`, `returnIdsOnly`, `time`, `f`. See [GeoServices parity — FeatureServer](../compatibility/geoservices-parity.md#featureserver) for full parameter semantics. |
| Edits | `applyEdits` (service and layer), `/{layerId}/addFeatures`, `updateFeatures`, `deleteFeatures` | POST only. |
| Attachments | `/{layerId}/queryAttachments`, `/{layerId}/{featureId}/attachments`, `addAttachment`, `updateAttachment`, `deleteAttachments`, `attachments/{attachmentId}` | |
| Related records | `/{layerId}/queryRelatedRecords` (GET, POST), `/relationships` | |
| Offline sync | `createReplica`, `extractChanges`, `synchronizeReplica`, `unRegisterReplica`, `replicas`, `replicas/{replicaId}` | |
| Branch versioning | `/rest/services/{serviceId}/VersionManagementServer` — `versions`, `create`, per-version `alter`, `delete`, `startReading`/`stopReading`, `startEditing`/`stopEditing`, `reconcile`, `inspectConflicts`, `resolveConflicts`, `post`, `jobs/{jobId}` | |
| Bulk and SQL | `append` (service and layer), `/{layerId}/calculate`, `validateSQL`, `queryDomains`, `getEstimates` | |
| Temporal and binning | `/{layerId}/queryTopFeatures`, `queryDateBins`, `temporalExtent`, `queryBins` | |
| Spatial analytics (Pro tier) | `/{layerId}/queryH3` (GET, POST), `queryClusters`, `spatialJoin`, `queryBufferAggregate`, `queryDensity` (POST) | Return 402 when the entitlement is inactive. |
| Renderer | `/{layerId}/generateRenderer` (GET, POST) | |

Registered but **not implemented** (return a spec-shaped not-implemented error): `queryContingentValues`, `sharedTemplates` (and its `query`/`add`/`update`/`delete`), `htmlPopup`, `image`, `/{layerId}/hasAssets`, `queryAssets`, `cleanupAssets`, `uploadAssets`, `convert3D`, `query3D`, `/{layerId}/metadata/update`.

```bash
curl "https://server.example.com/rest/services/roads/FeatureServer/0/query?where=1%3D1&outFields=*&resultRecordCount=10&f=json"
```

```bash
curl -X POST "https://server.example.com/rest/services/roads/FeatureServer/0/applyEdits" \
  -d 'adds=[{"geometry":{"x":-122.4,"y":37.8},"attributes":{"name":"New point"}}]&f=json'
```

## MapServer

Base: `/rest/services/{serviceId}/MapServer` (service and `/{layerId}` metadata via GET or POST).

| Operation | Routes | Notes |
| --- | --- | --- |
| Export map | `/export` (GET, POST) | `bbox`, `bboxSR`, `imageSR`, `size`, `format`, `transparent`, `layers`, `layerDefs`, `time`, `f`. |
| Identify | `/identify` (GET, POST) | `geometry`, `geometryType`, `sr`, `layers`, `tolerance`, `mapExtent`, `imageDisplay`. |
| Legend | `/legend` (GET, POST) | |
| Find | `/find` (GET, POST) | `searchText`, `searchFields`, `layers`, `contains`. |
| Generate KML | `/generateKml` (GET, POST) | |
| Query | `/{layerId}/query`, `/query` (GET, POST) | Same query parameters as FeatureServer. |
| Layer metadata | `/layers`, `/allLayersAndTables`, `/{layerId}/{featureId}`, `/queryDomains` | |
| Related/attachments | `/{layerId}/queryRelatedRecords`, `/{layerId}/queryAttachments`, `/{layerId}/generateRenderer` | |
| Cached tiles | `/tile/{z}/{y}/{x}` | |
| OGC pass-through | `/WMS`, `/WMTS`, `/WMTS/{**restPath}` | See [WMS, WFS, WCS, WMTS](wms-wfs-wcs-wmts.md). |

```bash
curl -o map.png "https://server.example.com/rest/services/roads/MapServer/export?bbox=-122.5,37.7,-122.3,37.9&size=800,600&format=png&f=image"
```

See [GeoServices parity — MapServer](../compatibility/geoservices-parity.md#mapserver--wms--wmts) for parameter-level coverage.

## ImageServer

Base: `/rest/services/{serviceId}/ImageServer` (raster-backed services).

| Operation | Routes | Notes |
| --- | --- | --- |
| Export image | `/exportImage` (GET, POST) | `bbox`, `bboxSR`, `imageSR`, `size`, `format`, `mosaicRule`, `renderingRule`, `f`. |
| Identify | `/identify` (GET, POST) | Pixel value lookup. |
| Cached tiles | `/tile/{level}/{row}/{col}` | |
| Catalog query | `/query` (GET, POST) | |
| Statistics | `/computeStatisticsHistograms`, `/computeHistograms`, `/computeClassStatistics`, `/statistics`, `/histograms`, `/getSamples` (GET, POST) | |
| Metadata | `/keyProperties`, `/rasterAttributeTable`, `/rasterFunctionInfos`, `/multidimensionalInfo`, `/slices`, `/legend`, `/conf.json` | |
| OGC pass-through | `/WCS` | WCS 2.0.1 KVP; see [WMS, WFS, WCS, WMTS](wms-wfs-wcs-wmts.md). |

```bash
curl -o dem.tif "https://server.example.com/rest/services/dem/ImageServer/exportImage?bbox=-122.5,37.7,-122.3,37.9&size=512,512&format=tiff&f=image"
```

See [GeoServices parity — ImageServer](../compatibility/geoservices-parity.md#imageserver).

## Geometry service

Base: `/rest/services/Utilities/Geometry/GeometryServer` (all operations accept GET and POST).

Implemented operations: `buffer`, `simplify`, `project`, `intersect`, `union`, `clip`, `difference`, `areasAndLengths`, `lengths`, `distance`, `relation`, `densify`, `convexHull`, `generalize`, `labelPoints`, `cut`, `trimExtend`, `offset`, `autoComplete`, `reshape`, `findTransformations`, `toGeoCoordinateString`, `fromGeoCoordinateString`.

```bash
curl "https://server.example.com/rest/services/Utilities/Geometry/GeometryServer/buffer?geometries=-122.4,37.8&inSR=4326&distances=100&unit=9001&f=json"
```

See [GeoServices parity — Geometry Service](../compatibility/geoservices-parity.md#geometry-service).

## GPServer (geoprocessing)

| Method | Path | Purpose |
| --- | --- | --- |
| GET, POST | `/rest/services/{serviceId}/GPServer`, `.../GPServer/{taskName}` | Service and task metadata. |
| GET, POST | `.../GPServer/{taskName}/execute` | Synchronous execution. |
| GET, POST | `.../GPServer/{taskName}/submitJob` | Asynchronous job submission. |
| GET | `.../GPServer/{taskName}/jobs/{jobId}`, `.../jobs/{jobId}/results/{paramName}` | Job status and results. |
| GET, POST | `.../GPServer/{taskName}/jobs/{jobId}/cancel` | Job cancellation. |
| GET, POST | `/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute`, `/submitJob`, `/jobs/{jobId}`, `/jobs/{jobId}/results/Output_File` | Print service (Export Web Map Task). |
| GET | `/rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task/execute` | Layout templates. |

## GeocodeServer

Base: `/rest/services/{locatorName}/GeocodeServer` and the default `/rest/services/GeocodeServer` (GET and POST).

Operations: `findAddressCandidates`, `reverseGeocode`, `suggest`, `geocodeAddresses`.

## NAServer (network analysis)

Minimal mobile routing compatibility (POST only): `/rest/services/{serviceId}/NAServer/Route/solve`, `.../ServiceArea/solveServiceArea`, `.../ClosestFacility/solveClosestFacility`.

## SceneServer (I3S)

`GET /scenes/{sceneId}/SceneServer` and `.../SceneServer/layers/{layerId}` serve Esri I3S scene layers (Enterprise-gated) — see [3D Tiles and scenes](3d-tiles-and-scenes.md).

## Conformance

GeoServices REST parity is tracked per surface in [GeoServices parity](../compatibility/geoservices-parity.md); OGC conformance status lives in the [API standards summary](../compatibility/ogc-conformance.md) backed by [cite-status.md](../../cite-status.md).

## Guides that use this

- [Migrate from ArcGIS Server](../../guides/migrate/from-arcgis-server.md)
- [Connect from ArcGIS Pro](../../guides/connect/arcgis-pro.md)
- [Query features](../../guides/query-analyze/query-features.md)
- [Edit features](../../guides/edit/edit-features.md)
- [Run geoprocessing](../../guides/query-analyze/run-geoprocessing.md)
- [Work with time](../../guides/query-analyze/work-with-time.md)
- [Client compatibility](../compatibility/clients.md)
