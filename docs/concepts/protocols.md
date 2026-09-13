---
type: concept
title: "Protocols"
description: "Honua serves every published layer through multiple protocols at once — the same PostGIS table can be queried from ArcGIS Pro, QGIS, Excel, a MapLibre web map, and an AI agent without ETL or duplication."
---
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
| GeoServices GeocodeServer | `/rest/services/{locatorName}/GeocodeServer` | Esri geocoding clients | Esri clients call `findAddressCandidates`-style geocoding against registered locators |
| GeoServices VectorTileServer | `/rest/services/{serviceId}/VectorTileServer` | Esri vector-tile clients, ArcGIS SDKs | Esri clients render vector tiles (`/tile/{z}/{y}/{x}.pbf`) |
| GeoServices NAServer **(Pro)** | `/rest/services/{serviceId}/NAServer` | Esri routing clients | Esri clients solve routes, service areas, closest facility, OD cost matrices |
| GeoServices VersionManagementServer **(Pro, Preview; experimental opt-in)** | `/rest/services/{serviceId}/VersionManagementServer` | Esri branch-versioning workflows | Named gdb versions with isolated edits and reconcile/post |
| Portal token issuance | `/sharing/rest/generateToken` | ArcGIS Pro, Esri SDKs | Esri clients authenticate with username/password tokens |
| OGC API Features | `/ogc/features` | QGIS, GDAL, any OGC client | Standards-based feature access and CRUD with CQL2 filtering |
| OGC API Maps | `/ogc/maps` | OGC map clients | Standards-based server-rendered map images |
| OGC API Tiles | `/ogc/tiles` | QGIS, MapLibre | Standards-based vector/raster tile access with tile matrix sets |
| OGC API Coverages | `/ogc/coverages` | Science/raster tooling | REST/JSON raster coverage discovery and GeoTIFF export |
| OGC API Processes | `/ogc/processes` | OGC processing clients | Standards-based async geoprocessing |
| OGC API Records | `/ogc/records` | Catalog/metadata search clients | Standards-based catalog discovery and record search |
| OGC API Environmental Data Retrieval (EDR) **(Preview)** | `/edr` | Environmental/scientific tooling | Query environmental data resources by position or cube. Preview: CRS, temporal-selection, output-format and coordinate-query corrections target 2026.2 |
| OGC API Styles | `/ogc/styles` | Style-aware map clients | Discover and fetch published layer styles and metadata |
| OGC SensorThings v1.1 (Preview) | `/sta/v1.1` | IoT/observation clients | Service discovery and linked REST access to Things, Datastreams, Sensors, ObservedProperties, and Observations; FeaturesOfInterest and Locations are not exposed; enable `Capabilities:Experimental:serve.sensorthings:Enabled=true` |
| WMS 1.3 / 1.1.1 | `/rest/services/{serviceId}/MapServer/WMS` or `/ogc/services/{serviceId}/wms` | QGIS, legacy OGC clients | Clients expect classic GetMap/GetFeatureInfo |
| WFS 2.0 / 1.1.0 / 1.0.0 | `/wfs` | QGIS, GDAL/OGR, legacy stacks | Clients expect classic GetFeature with GML output |
| WCS 2.0.1 | `/rest/services/{serviceId}/ImageServer/WCS` or `/ogc/services/{serviceId}/wcs` | Science/elevation tooling | Clients need raw subsetted coverage values |
| WMTS 1.0 | `/rest/services/{serviceId}/MapServer/WMTS` or `/ogc/services/{serviceId}/wmts` | QGIS, legacy tile clients | Clients expect classic GetTile |
| WPS 2.0 | `/wps` | Classic OGC processing clients | Clients expect classic WPS Execute/GetStatus/GetResult over the same job runtime |
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

Surfaces marked **(Pro)** / **(Enterprise)** require a signed license; everything unmarked is Community. The machine-readable capability vocabulary behind this matrix is [`docs/gis/data/capability-keys.v1.json`](../gis/data/capability-keys.v1.json) (generated from `CapabilityKeyCatalog.cs`) — keep the matrix in sync with it, and query `/api/v1/capabilities/manifest` for what a running deployment supports.

## Cloud-native formats

Beyond the protocol endpoints, Honua works directly with the cloud-native geospatial format family — see the [cloud-native formats reference](../reference/protocols/cloud-native-formats.md) for endpoints and current status per format:

- **COG (Cloud-Optimized GeoTIFF)** — file import, plus in-place registration of rasters living in S3/Azure; served through ImageServer, WCS 2.0.1, and OGC API Coverages.
- **PMTiles** — tile archives produced by tile-operations jobs and served with HTTP range requests for serverless/CDN hosting.
- **GeoParquet / GeoArrow / FlatGeobuf** — import formats and FeatureServer query output formats (`f=parquet|arrow|fgb`) for analytics and notebook workflows.
- **Zarr** — store registration and catalog metadata (`/api/v1/admin/zarr-stores`); protocol serving is not yet exposed.
- **Cloud-optimized HDF5 / NetCDF4** — multidimensional coverage registration and catalog metadata (`/api/v1/admin/multidim-coverages`); the reader is build-optional and protocol serving is not yet exposed.
- **STAC** — the catalog surface (`/stac`) for discovering these assets.

## Where each protocol is documented

Each protocol has at most one reference page, and where it exists that page is its canonical
home. This page stays the matrix: what exists, where it lives, and which one to
reach for. Per-operation detail, request and response shapes, version
differences and limits live in the reference.

| Protocol family | Canonical reference |
|---|---|
| GeoServices REST — FeatureServer, MapServer, ImageServer, Geometry Service, GPServer, GeocodeServer, VectorTileServer, NAServer, VersionManagementServer, portal tokens | [GeoServices REST](../reference/protocols/geoservices-rest.md) |
| OGC API — Features, Maps, Tiles, Coverages, Processes, Records, Styles, EDR | [OGC APIs](../reference/protocols/ogc-apis.md) |
| Classic OGC — WMS, WFS, WCS, WMTS | [WMS, WFS, WCS & WMTS](../reference/protocols/wms-wfs-wcs-wmts.md) |
| Classic OGC — WPS 2.0 | No reference page yet — see [honua-server#4685](https://github.com/honua-io/honua-server/issues/4685) |
| OData v4 | [OData](../reference/protocols/odata.md) |
| STAC | [STAC](../reference/protocols/stac.md) |
| Vector tiles (MVT) + TileJSON, PMTiles | [Vector tiles](../reference/protocols/vector-tiles.md) |
| Terrain-RGB + elevation | [Terrain & elevation](../reference/protocols/terrain-and-elevation.md) |
| 3D Tiles scenes | [3D Tiles & scenes](../reference/protocols/3d-tiles-and-scenes.md) |
| gRPC (`geospatial.v1`) | [gRPC](../reference/protocols/grpc.md) |
| MCP | [AI agents and MCP](../guides/connect/ai-agents-mcp.md) |

Three surfaces are worth calling out because they are one runtime behind several
front doors, which is not obvious from the matrix:

- **GPServer, OGC API Processes, WPS and MCP are not four geoprocessing
  implementations.** They are four adapters over the same canonical process
  runtime — the same job lifecycle, the same cancellation, the same results. A
  geoprocessing bug is almost never in the protocol.
- **Authentication is shared.** Portal token issuance at
  `/sharing/rest/generateToken` mints tokens the other GeoServices surfaces
  accept; see [Authentication](../guides/secure/authentication.md).
- **Conformance evidence is separate from the contract.** For what a client can
  actually do today, use the [client compatibility
  contract](../reference/compatibility/clients.md), [GeoServices
  parity](../reference/compatibility/geoservices-parity.md) and [OGC conformance
  evidence](../reference/compatibility/ogc-conformance.md) rather than this
  matrix.

## Choosing a protocol

- **Existing Esri clients or workflows** — use the GeoServices REST surfaces; nothing changes on the client side.
- **Open-source GIS (QGIS, GDAL) and new integrations** — prefer OGC API Features/Tiles; fall back to WMS/WFS/WMTS only for clients that require them.
- **Web maps** — MVT + TileJSON with the auto-generated MapLibre style is the fastest path; add Terrain-RGB for 3D terrain.
- **BI and spreadsheets** — OData v4.
- **Programmatic/high-volume access** — gRPC via an SDK; REST otherwise.
- **AI agents** — MCP.

Standards-defined paths are stable and not version-prefixed by Honua; backward compatibility is defined by the upstream specification. Honua-specific extensions are additive only. Deprecations follow the [versioning and support policy](../reference/versioning-and-support.md).
