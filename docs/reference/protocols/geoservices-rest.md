# GeoServices REST (ArcGIS-compatible)

Honua serves a GeoServices REST surface under `/rest/services`, plus a Portal Sharing slice under `/sharing/rest` for token issuance and item discovery. Compatibility is limited to the operations documented in the [GeoServices parity matrix](../compatibility/geoservices-parity.md) and the client workflows covered by the [cross-client certification matrix](../../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md); it does not imply blanket support for ArcGIS Pro, Field Maps, Koop, or the ArcGIS Maps SDK for JavaScript.

## Catalog and portal routes

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/rest/info` | Compatibility info (`currentVersion: 10.8`, SOAP URLs, and `authInfo.isTokenBasedSecurity: false`; no token endpoint is emitted). |
| GET | `/rest/services` | Service catalog listing. |
| GET, POST | `/sharing/rest/generateToken` | ArcGIS token issuance. |
| GET | `/sharing/rest/info` | Portal sharing info. |
| GET | `/sharing/rest/portals/self`, `/sharing/rest/community/self` | Portal/community self documents. |
| GET | `/sharing/rest/search` | Portal item search. |
| GET | `/sharing/rest/content/items/{id}`, `.../{id}/data` | Portal item metadata and data. |
| GET, POST | `/sharing/rest/oauth2/authorize`, `/callback`, `/token` | Portal OAuth2 named-user bridge. |

> Open `https://server.example.com/rest/services?f=json` in a browser.

## FeatureServer

Base: `/rest/services/{serviceId}/FeatureServer` (service and `/{layerId}` metadata via GET or POST).

| Operation group | Routes | Notes |
| --- | --- | --- |
| Query | `/{layerId}/query` (GET, POST), `/query` (service level) | `where`, `objectIds`, `geometry`, `geometryType`, `inSR`, `spatialRel`, `outFields`, `outSR`, `returnGeometry`, `orderByFields`, `resultOffset`, `resultRecordCount`, `outStatistics`, `groupByFieldsForStatistics`, `returnCountOnly`, `returnIdsOnly`, `time`, `f`. See [GeoServices parity — FeatureServer](../compatibility/geoservices-parity.md#featureserver) for full parameter semantics. |
| Edits | `applyEdits` (service and layer), `/{layerId}/addFeatures`, `updateFeatures`, `deleteFeatures` | POST only. |
| Attachments | `/{layerId}/queryAttachments`, `/{layerId}/{featureId}/attachments`, `addAttachment`, `updateAttachment`, `deleteAttachments`, `attachments/{attachmentId}` | |
| Related records | `/{layerId}/queryRelatedRecords` (GET, POST), `/relationships` | |
| Offline sync | `createReplica`, `extractChanges`, `synchronizeReplica`, `unRegisterReplica`, `replicas`, `replicas/{replicaId}` | Preview/opt-in surface. `synchronizeReplica` accepts `conflictHandling`: `lastWriteWins` or `manualReview`; other values return 400. |
| Branch versioning | `/rest/services/{serviceId}/VersionManagementServer` — `versions`, `create`, per-version operations and jobs | Experimental and off by default; routes return 404 until `versioning.branch` is enabled. |
| Bulk and SQL | `append` (service and layer), `/{layerId}/calculate`, `validateSQL`, `queryDomains`, `getEstimates` | |
| Temporal and binning | `/{layerId}/queryTopFeatures`, `queryDateBins`, `temporalExtent`, `queryBins` | |
| Spatial analytics (Pro tier) | `/{layerId}/queryH3` (GET, POST), `queryClusters`, `spatialJoin`, `queryBufferAggregate`, `queryDensity` (POST) | Return 402 when the entitlement is inactive. |
| Renderer | `/{layerId}/generateRenderer` (GET, POST) | |

Registered but **not implemented** (return a spec-shaped not-implemented error): `sharedTemplates` (and its `query`/`add`/`update`/`delete`), `htmlPopup`, `image`, `/{layerId}/hasAssets`, `queryAssets`, `cleanupAssets`, `uploadAssets`, `convert3D`, `query3D`, `/{layerId}/metadata/update`. GET-only `queryContingentValues` is implemented and returns graph-backed definitions (or an empty collection).

> Open `https://server.example.com/rest/services/roads/FeatureServer/0/query?where=1%3D1&outFields=*&resultRecordCount=10&f=json` in a browser.

Use the `@honua/sdk-js` FeatureLayer client and call `applyEdits({ adds: [{ geometry: { x: -122.4, y: 37.8 }, attributes: { name: "New point" } }] })`.

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

> Open `https://server.example.com/rest/services/roads/MapServer/export?bbox=-122.5,37.7,-122.3,37.9&size=800,600&format=png&f=image` in a browser.

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

> Open `https://server.example.com/rest/services/dem/ImageServer/exportImage?bbox=-122.5,37.7,-122.3,37.9&size=512,512&format=tiff&f=image` in a browser.

### Tiled-consumption metadata (`tileInfo`)

By default an ImageServer advertises a **dynamic** service (`singleFusedMapCache: false`, no `tileInfo`). This keeps the descriptor — which the ArcGIS Maps SDK for .NET native runtime reads verbatim from `/conf.json` — compatible with the dynamic `exportImage` load path; advertising a cache makes that native runtime treat the service as cached and reject the configuration.

The `/tile/{level}/{row}/{col}` route is always served. To let tiled Esri clients (for example `L.esri.tiledMapLayer`, which keys off `metadata.tileInfo.lods`) consume it directly, opt in to the static WebMercatorQuad cache descriptor:

| Setting | Default | Effect |
| --- | --- | --- |
| `GeoServices:ImageServer:TileMetadata:Enabled` | `false` | When `true`, ImageServer metadata reports `singleFusedMapCache: true` plus a WebMercatorQuad `tileInfo` block (256×256 tiles, origin `(-20037508.34, 20037508.34)`, spatial reference 102100/3857, and the standard level-of-detail array). |
| `GeoServices:ImageServer:TileMetadata:MaxLevel` | `23` | Highest zoom level emitted in `tileInfo.lods` (the tile route accepts up to 28). |

Enable it only for deployments that primarily serve tiled Esri clients; the native ArcGIS Maps SDK for .NET dynamic `exportImage` path expects the default (dynamic) contract.

See [GeoServices parity — ImageServer](../compatibility/geoservices-parity.md#imageserver).

## Geometry service

Base: `/rest/services/Utilities/Geometry/GeometryServer` (all operations accept GET and POST).

Implemented operations: `buffer`, `simplify`, `project`, `intersect`, `union`, `clip`, `difference`, `areasAndLengths`, `lengths`, `distance`, `relation`, `densify`, `convexHull`, `generalize`, `labelPoints`, `cut`, `trimExtend`, `offset`, `autoComplete`, `reshape`, `findTransformations`, `toGeoCoordinateString`, `fromGeoCoordinateString`.

> Open `https://server.example.com/rest/services/Utilities/Geometry/GeometryServer/buffer?geometries=-122.4,37.8&inSR=4326&distances=100&unit=9001&f=json` in a browser.

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

GET and POST solves are available for Route, ServiceArea, ClosestFacility, ODCostMatrix, and LocationAllocation under `/rest/services/{serviceId}/NAServer`; see the parity matrix for per-solver limitations.

## SceneServer (I3S)

`GET /scenes/{sceneId}/SceneServer` and `.../SceneServer/layers/{layerId}` serve Esri I3S scene layers only when experimental capability `serve.i3s-scene` is enabled. The default is 404; an enabled route without the Enterprise entitlement returns 402. See [3D Tiles and scenes](3d-tiles-and-scenes.md).

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
