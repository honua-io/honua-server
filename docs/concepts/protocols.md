# Protocols

Honua serves every published layer through multiple protocols at once — the same PostGIS table can be queried from ArcGIS Pro, QGIS, Excel, a MapLibre web map, and an AI agent without ETL or duplication. This page is the canonical protocol matrix. For per-operation support detail use the [client compatibility contract](../reference/compatibility/clients.md), [GeoServices parity](../reference/compatibility/geoservices-parity.md), and [OGC conformance evidence](../reference/compatibility/ogc-conformance.md).

## Protocol matrix

| Protocol | Base endpoint pattern | Typical clients | Use this when… |
|---|---|---|---|
| GeoServices FeatureServer | `/rest/services/{serviceId}/FeatureServer` | ArcGIS Pro, Esri SDKs | Esri clients need feature query, editing, attachments, replicas |
| GeoServices MapServer | `/rest/services/{serviceId}/MapServer` | ArcGIS Pro, Esri map clients | Esri clients need rendered map images, identify, legends |
| GeoServices ImageServer | `/rest/services/{serviceId}/ImageServer` | ArcGIS raster workflows | Esri clients need raster export, identify, tiles, statistics |
| GeoServices Geometry Service | `/rest/services/Utilities/Geometry/GeometryServer` | Esri SDKs | Clients need server-side buffer, project, intersect, etc. |
| GeoServices GPServer | `/rest/services/{serviceId}/GPServer` | ArcGIS Pro, Esri SDKs | Esri clients run async geoprocessing jobs |
| Portal token issuance | `/sharing/rest/generateToken` | ArcGIS Pro, Esri SDKs | Esri clients authenticate with username/password tokens |
| OGC API Features | `/ogc/features` | QGIS, GDAL, any OGC client | Standards-based feature access and CRUD with CQL2 filtering |
| OGC API Maps | `/ogc/maps` | OGC map clients | Standards-based server-rendered map images |
| OGC API Tiles | `/ogc/tiles` | QGIS, MapLibre | Standards-based vector/raster tile access with tile matrix sets |
| OGC API Coverages | `/ogc/coverages` | Science/raster tooling | REST/JSON raster coverage discovery and GeoTIFF export |
| OGC API Processes | `/ogc/processes` | OGC processing clients | Standards-based async geoprocessing |
| WMS 1.3 / 1.1.1 | `/rest/services/{serviceId}/MapServer/WMS` or `/ogc/services/{serviceId}/wms` | QGIS, legacy OGC clients | Clients expect classic GetMap/GetFeatureInfo |
| WFS 2.0 / 1.1.0 / 1.0.0 | `/wfs` | QGIS, GDAL/OGR, legacy stacks | Clients expect classic GetFeature with GML output |
| WCS 2.0.1 | `/rest/services/{serviceId}/ImageServer/WCS` or `/ogc/services/{serviceId}/wcs` | Science/elevation tooling | Clients need raw subsetted coverage values |
| WMTS 1.0 | `/rest/services/{serviceId}/MapServer/WMTS` or `/ogc/services/{serviceId}/wmts` | QGIS, legacy tile clients | Clients expect classic GetTile |
| OData v4 | `/odata` | Excel, Power BI, Tableau | BI tools consume spatial tables as entity sets |
| STAC | `/stac` | STAC browsers, catalog tooling | Catalog discovery and item search with extension metadata |
| Vector tiles (MVT) + TileJSON | `/tiles/{layerId}/{z}/{x}/{y}.mvt`, `/tiles/{layerId}/tile.json` | MapLibre, OpenLayers, Leaflet | Web maps render features client-side |
| Terrain-RGB + elevation | `/terrain/{datasetId}/…`, `/elevation/{datasetId}/…` | MapLibre `raster-dem`, field apps | Web terrain rendering or numeric elevation lookups |
| 3D Tiles scenes | `/scenes/{sceneId}/tileset.json` | CesiumJS, 3D Tiles clients | Serving hosted or generated OGC 3D Tiles tilesets |
| gRPC (`geospatial.v1`) | port `8081` (h2c), gRPC-Web on `8080` | Honua SDKs, mobile, services | High-throughput programmatic access from SDK clients |
| MCP | `/mcp` | AI agents | Agents validate, plan, and run geoprocessing via JSON-RPC |
| PMTiles | `/api/v1/tiles/pmtiles/{artifactId}` | MapLibre, serverless tile hosting | Single-file tile archives served with HTTP range requests |
| Cloud rasters (COG) | registered via admin API, served via ImageServer / WCS / OGC Coverages | Raster pipelines | Cloud-optimized GeoTIFFs in S3/Azure served without copying |

Protocols are enabled per service; a layer is reachable through every protocol its service lists. Read-only providers expose the same surfaces minus write operations. See [Data model](data-model.md).

## Cloud-native formats

Beyond the protocol endpoints, Honua works directly with the cloud-native geospatial format family — see the [cloud-native formats reference](../reference/protocols/cloud-native-formats.md) for endpoints and current status per format:

- **COG (Cloud-Optimized GeoTIFF)** — file import, plus in-place registration of rasters living in S3/Azure; served through ImageServer, WCS 2.0.1, and OGC API Coverages.
- **PMTiles** — tile archives produced by tile-operations jobs and served with HTTP range requests for serverless/CDN hosting.
- **GeoParquet / GeoArrow / FlatGeobuf** — import formats and FeatureServer query output formats (`f=parquet|arrow|fgb`) for analytics and notebook workflows.
- **Zarr** — store registration and catalog metadata (`/api/v1/admin/zarr-stores`); protocol serving is not yet exposed.
- **Cloud-optimized HDF5 / NetCDF4** — multidimensional coverage registration and catalog metadata (`/api/v1/admin/multidim-coverages`); the reader is build-optional and protocol serving is not yet exposed.
- **STAC** — the catalog surface (`/stac`) for discovering these assets.

## GeoServices REST (Esri-compatible)

Honua implements the ArcGIS GeoServices REST contract so Esri clients connect without plugins — see [GeoServices parity](../reference/compatibility/geoservices-parity.md) for the per-operation contract.

- **FeatureServer** — query, `addFeatures`/`updateFeatures`/`deleteFeatures`/`applyEdits`, attachments, related records, replicas. Output: Esri JSON, GeoJSON, PBF, FlatGeobuf, GeoParquet, GeoArrow.
  `GET /rest/services/parcels/FeatureServer/0/query?where=1=1&f=geojson`
- **MapServer** — `export`, `identify`, `legend`, `find`, per-layer query, tiles.
  `GET /rest/services/parcels/MapServer/export?bbox=-122.5,37.7,-122.4,37.8&f=image`
- **ImageServer** — `exportImage`, `identify`, tiles, raster catalog `query`, per-band statistics and histograms. See [GeoServices parity](../reference/compatibility/geoservices-parity.md) for limits.
  `GET /rest/services/dem/ImageServer/exportImage?bbox=…&f=image`
- **Geometry Service** — buffer, simplify, project, intersect, union, clip, difference, areas/lengths, and more.
  `POST /rest/services/Utilities/Geometry/GeometryServer/buffer`
- **GPServer** — catalog-backed task discovery, async `submitJob`, job polling, cancellation, per-parameter results over the canonical process runtime.
  `POST /rest/services/analysis/GPServer/geometry.buffer/submitJob`
- **Portal token issuance** — `POST /sharing/rest/generateToken` exchanges username/password for an opaque token presented via `?token=`, `Authorization: Bearer`, or `X-Esri-Authorization: Bearer`. See [Authentication](../guides/secure/authentication.md).

## OGC API

Modern resource-oriented OGC standards, each with its own landing page, `/conformance`, and OpenAPI document. CITE conformance evidence: [OGC conformance](../reference/compatibility/ogc-conformance.md).

- **Features** (`/ogc/features`) — collections, items, feature CRUD, CQL2 filtering, GML via content negotiation.
  `GET /ogc/features/collections/parcels/items?bbox=-122.5,37.7,-122.4,37.8`
- **Maps** (`/ogc/maps`) — rendered map images for datasets and collections (PNG, JPEG, TIFF).
  `GET /ogc/maps/collections/parcels/map?bbox=…`
- **Tiles** (`/ogc/tiles`) — tile access addressed by tile matrix set.
  `GET /ogc/tiles/collections/parcels/tiles/WebMercatorQuad/12/654/1583`
- **Coverages** (`/ogc/coverages`) — raster collection metadata and coverage export with bbox/CRS/band/scaling controls.
  `GET /ogc/coverages/collections/dem/coverage?bbox=…&f=png`
- **Processes** (`/ogc/processes`) — async process execution and job lifecycle over the same runtime as GPServer and MCP.
  `POST /ogc/processes/processes/honua-geoprocessing/execution`

## Classic OGC services

For clients pinned to the pre-REST OGC generation. All are KVP-style and read-only except WFS 2.0 transactions.

- **WMS 1.3 / 1.1.1** — GetCapabilities, GetMap, GetFeatureInfo, GetLegendGraphic. WMS 1.1.1 uses `SRS`, `X`/`Y`, and lon/lat `EPSG:4326` BBOX order.
  `GET /ogc/services/parcels/wms?service=WMS&version=1.3.0&request=GetMap&…`
- **WFS 2.0 / 1.1.0 / 1.0.0** — GetFeature with version-appropriate GML (3.2 / 3.1.1 / 2.1.2). Legacy versions are read-only.
  `GET /wfs?service=WFS&version=2.0.0&request=GetFeature&typeNames=parcels`
- **WCS 2.0.1** — GetCapabilities, DescribeCoverage, GetCoverage for raster layers (GeoTIFF default).
  `GET /ogc/services/dem/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId=0&format=image/tiff`
- **WMTS 1.0** — GetCapabilities, GetTile, GetFeatureInfo. WebMercatorQuad tile matrix set.
  `GET /rest/services/parcels/MapServer/WMTS?service=WMTS&request=GetTile&…`

## OData v4

`/odata` exposes published layers as entity sets with `$metadata`, `$filter` (including spatial functions), `$batch`, and CRUD — built for Excel, Power BI, Tableau, and other non-GIS consumers.

`GET /odata/Layers(0)/Features?$filter=assessed_value gt 100000&$top=50`

## STAC

`/stac` serves a STAC API catalog over published collections: collection metadata, items, and GET/POST `/stac/search` with CQL2 filtering, `fields`, and `sortby`. Collection details cross-link to the matching OGC API Features collection.

`GET /stac/search?bbox=-122.5,37.7,-122.44,37.75&limit=10`

## Vector tiles (MVT) + TileJSON

Mapbox Vector Tiles generated from PostGIS with TileJSON metadata and an auto-generated MapLibre style per layer (`/api/styles/{layerId}.json`, with deterministic `?theme=dark|colorblind-safe|print` variants).

`GET /tiles/0/12/654/1583.mvt`

## Terrain-RGB + elevation API

For registered DEM rasters:

- **Terrain-RGB tiles** — `/terrain/{datasetId}/tile.json` and `/terrain/{datasetId}/{z}/{x}/{y}.png` for MapLibre/Mapbox `raster-dem` sources.
- **Elevation API** — `/elevation/{datasetId}/value` (point lookup) and `/elevation/{datasetId}/profile` (distance/elevation samples along a WKT LineString).

`GET /elevation/dem/value?lon=-122.45&lat=37.75`

## 3D Tiles scenes

`/scenes/{sceneId}/tileset.json` serves OGC 3D Tiles tilesets — either already-built bundles registered through the admin scene registry, or tilesets generated from a PostGIS layer via `POST /api/v1/admin/scenes/generate`. Relative asset URIs resolve under the scene route, so CesiumJS loads them without URL rewriting. See [Hosted 3D scenes](../guides/publish/publish-3d-scenes.md) and [Scene generation](../guides/publish/publish-3d-scenes.md).

## gRPC (`geospatial.v1`)

Native gRPC on port `8081` (h2c) and gRPC-Web on `8080`, implementing the open [geospatial-grpc](https://github.com/honua-io/geospatial-grpc) protocol: `FeatureService`, `ProcessService`, `SpecService`, `SceneService`, `TileService`, and `ElevationService`. Used by the Honua SDKs and mobile clients. See the [gRPC reference](../reference/protocols/grpc.md) for versioning and stability guarantees.

## MCP

`/mcp` is a JSON-RPC Model Context Protocol surface for AI agents: geoprocessing plan validation, dry runs, execution submission, cancellation, job/result inspection, and natural-language grounding over the process and layer catalogs. It adapts the same canonical runtime as GPServer and OGC API Processes. See [AI agents and MCP](../guides/connect/ai-agents-mcp.md) and the open [geospatial-mcp](https://github.com/honua-io/geospatial-mcp) standard.

## Choosing a protocol

- **Existing Esri clients or workflows** — use the GeoServices REST surfaces; nothing changes on the client side.
- **Open-source GIS (QGIS, GDAL) and new integrations** — prefer OGC API Features/Tiles; fall back to WMS/WFS/WMTS only for clients that require them.
- **Web maps** — MVT + TileJSON with the auto-generated MapLibre style is the fastest path; add Terrain-RGB for 3D terrain.
- **BI and spreadsheets** — OData v4.
- **Programmatic/high-volume access** — gRPC via an SDK; REST otherwise.
- **AI agents** — MCP.

Standards-defined paths are stable and not version-prefixed by Honua; backward compatibility is defined by the upstream specification. Honua-specific extensions are additive only. Deprecations follow the [versioning and support policy](../reference/versioning-and-support.md).
