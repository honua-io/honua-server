---
type: guide
title: "How published data becomes findable"
description: "How a published layer, scene, or process shows up on each protocol Honua serves, and what keeps it out of those catalogs."
---

# How published data becomes findable

Publishing a layer does not create a separate catalog for each protocol. It writes one Metadata v2 graph — a service, a resource, a storage binding, and one or more publications — and activates that graph as the snapshot for the running environment. Every data catalog below is a projection of the routable part of that snapshot, filtered by the caller's tenant and access. Scenes are the exception: they are registered separately and are not metadata-graph publications. The capability manifest is a third surface: it tells an SDK or agent what this deployment can do, not which layers exist.

[Publication](../../concepts/domain/publication.md) is the act of making a resource available. [Catalog and discovery](../../concepts/domain/catalog.md) is how a caller finds it. [Metadata](../../concepts/domain/metadata.md) is the description the catalogs project (name, title, description, extent, geometry type, fields). A title is not an authorization decision.

Publish with the admin SDK as in [Publish layers](../publish/publish-layers.md). Confirm the result with the CLI:

```bash
npx --yes -p @honua/sdk-js@0.2.6 honua services
npx --yes -p @honua/sdk-js@0.2.6 honua layers "$HONUA_SERVICE"
```

`honua services` and `honua layers` are the same commands as [your first dataset](../../get-started/first-dataset.md). An empty list there means this caller cannot see a routable publication, not that the protocol routes are down.

## What a layer publish writes

A new service is created with the shared protocol list enabled and an active lifecycle. That list is FeatureServer, MapServer, ImageServer, GPServer, VectorTileServer, the OGC API and classic OGC protocols, OData, gRPC, STAC, SensorThings, terrain, and elevation. SceneServer is not on it; scenes are registered separately. The primary publication is an Esri feature layer. When the storage has a geometry column, a STAC collection publication is written on the same service as well. Both use the numeric layer id as their publication identifier, which is the layer index catalogs match.

Publishing another layer into a service that already exists only adds publications. It does not turn protocols back on, and it does not clear a lifecycle the service already has.

`enabled=false` on the publish request stores the layer as retired. The publish call can succeed and the layer still be absent from every catalog below. Confirm the result says the layer is enabled before searching for it.

The snapshot is stored for `Metadata:Environment` (environment variable `Metadata__Environment`, otherwise the process `Environment`, otherwise `default`). A later publish in a different environment does not fill the catalogs of the running one.

## The shared visibility gate

A publication is listed only when all of the following hold:

- The service, the publication, the resource, and the storage binding are routable. Omitted `status` or omitted `status.lifecycle` is active and stays listed. Explicit `draft`, `retired`, and `archived` stay in the graph for administration and are omitted from every serving catalog. `deprecated` stays listed.
- The publication is visible to the caller's tenant.
- The caller may read that resource. A caller without a matching read grant sees an empty catalog. That is not permission to try another tenant.

An empty catalog for one protocol while another protocol lists the layer usually means that protocol is not enabled on the service, or that protocol requires a publication type the publish did not write (STAC without a geometry column is the common case).

## Per protocol

| Protocol | Capability | What becomes findable |
| --- | --- | --- |
| OGC API Records | `serve.ogc-api-records` | One read-only collection, `honua-catalog`, of service and dataset records. |
| STAC API | `serve.stac` | One collection per routable STAC-collection publication. |
| GeoServices directory | `serve.geoservices-root` | One entry per enabled Esri service type, plus a few built-ins. |
| OGC API Features, Maps, Tiles | the matching `serve.ogc-api-*` key | Collections for routable publications on services that enable that protocol. |
| OGC API Coverages | `serve.ogc-api-coverages` | Raster-backed collections. Preview in 2026.1. |
| WMS, WFS 2.0, WMTS, WCS | the matching classic serve keys | Capabilities documents built from the same routable publications. |
| OData | `serve.odata` | One entity set per primary OData publication that has a layer id and a storage layer. |
| MCP | `ai.mcp-discovery` | `honua_list_layers` over the same routable publications. |
| Scene discovery | `scene.catalog` | Active registered scenes, not metadata-graph layers. |
| Capability manifest | `discovery.capability-manifest` | Deployment capabilities for this caller. Not a layer list. |

Wire shapes, query parameters, and conformance classes stay on the protocol pages linked below. This page is only the path from a publish to a hit.

### OGC API Records

Records reads the current snapshot and emits two kinds of record, both into `honua-catalog`:

- One service record, id `service:{name}`, per public service name. Several protocol-specific service rows that share a name are merged. The record is omitted when that name has no routable publication the caller may read. Alternate links include FeatureServer and MapServer when those protocols are enabled.
- One dataset record, id `layer:{storageLayerId}` (or the resource id when there is no storage layer), per resource that has a routable publication. When several resources share one storage layer id, the feature dataset is the record that is kept. Alternate links include OGC API Features and STAC when the primary service enables those protocols. The STAC link is only a collection that exists when publish wrote a STAC collection (a geometry column).

Search accepts `q` (comma-separated terms, matched as OR against id, title, and description), `bbox`, `datetime`, `type` (`service` or `dataset`), `ids`, and `externalIds`. A record with no timestamp is not dropped by `datetime`. Create, update, delete, harvesting, and CQL2 on Records are not implemented. Details: [OGC APIs](../../reference/protocols/ogc-apis.md#ogc-api-records).

### STAC

`/stac` lists collections for publications whose type is a STAC collection, whose service enables STAC, which have a layer index, and which pass the shared visibility gate. A vector publish writes that publication only when the storage has a geometry column. Collection ids match the publication's service-local id, path, name, graph id, or numeric layer index.

Item search follows [STAC](../../reference/protocols/stac.md). A `datetime` search returns items only from collections with a resolvable temporal field; a collection without one contributes nothing, including when named explicitly. Undated items stay readable when the request has no temporal filter.

The open-data STAC projection under the open-data API is not this catalog. A published layer is not listed there until that item is opted into open-data publication. See the note on [STAC](../../reference/protocols/stac.md).

### GeoServices directory

The services directory (`serve.geoservices-root`) lists each Esri protocol the service enables: FeatureServer, MapServer, ImageServer, GPServer, and VectorTileServer. A service enabled for more than one of those is listed once per type. OGC, STAC, and OData protocols are not directory entries; they have their own catalogs. There is no ArcGIS folder tree. The only folder-shaped node is the built-in Utilities/PrintingTools GPServer.

A service with no visible resource is omitted, except a layerless GPServer the caller is allowed to query. ImageServer is listed only when a raster probe resolves a layer. SceneServer entries are appended only for active registered scenes, and only on an Enterprise license; other editions omit them with no error. An enabled geocoder is listed as GeocodeServer even when the metadata graph is empty. PrintingTools is added to a directory the caller is allowed to see.

A fresh server therefore can show PrintingTools, a seeded geoprocessing service, or a locator before any layer is published. That is not evidence the layer publish landed. `honua services` and `honua layers` are. Set `Geoprocessing:SeedDefaultService=false` (or `HONUA_GEOPROCESSING_SEED_DEFAULT_SERVICE=false`) to skip the seeded GPServer. Directory behavior: [GeoServices REST](../../reference/protocols/geoservices-rest.md).

### OGC API Features, Maps, Tiles, and Coverages

OGC API Features lists a resource when a publication on an OgcFeatures-enabled service is routable and the caller may read it. If the resource has a primary publication, the collection is that primary one; access to only a secondary publication does not leak the resource into the collection list. A new layer publish enables OgcFeatures, so the primary feature publication is an OGC Features collection.

OGC API Maps and OGC API Tiles use the same rule for the protocol each one checks: the service must enable that protocol, and the publication must be routable for the caller. OGC API Coverages lists raster-backed collections and is Preview in 2026.1, with no GA support commitment. Collection paths and parameters: [OGC APIs](../../reference/protocols/ogc-apis.md).

### WMS, WFS 2.0, WMTS, and WCS

GetCapabilities for these services is another projection of the same snapshot, not a second catalog you register. WFS 2.0 advertises one feature type per storage layer that has a routable publication on a WFS-enabled service the caller may query. WMS advertises geometry resources on WMS-enabled services. WCS advertises routable publications on services that enable WCS or ImageServer. A layer hidden by lifecycle or access is absent from the capabilities document, not present and denied. Parameter detail: [WMS, WFS, WCS, and WMTS](../../reference/protocols/wms-wfs-wcs-wmts.md).

### OData

The OData service document lists one entity set per primary publication on an OData-enabled service that has a layer index and a storage layer id, and that the caller may read. A new layer publish enables OData, so the primary feature publication is an entity set. Secondary publications for the same layer are not extra entity sets. Detail: [OData](../../reference/protocols/odata.md).

### MCP and agents

`ai.mcp-discovery` is the agent read surface. `honua_list_layers` walks the same snapshot: routable publications with a layer index, visible to the tenant, that this caller may read as metadata. Several protocol publications of one service and layer come back as one layer. `honua_describe_layer` and `honua_ground_candidates` use that list; grounding does not rank a publication the caller cannot read.

`tools/list` advertises tools. It is not the data catalog. `honua_list_layers` requires an authenticated call and a read grant (`read:*`, `read:{service}`, or a tighter layer grant). `admin:read` alone returns an empty list. Handshake and grants: [Connect AI agents](../connect/ai-agents-mcp.md).

gRPC feature calls do not have their own catalog. They resolve the same published layers when the service enables gRPC. Scene listing is below. Transport: [gRPC](../../reference/protocols/grpc.md).

### Scenes

3D scenes are not publications in the metadata graph. HTTP scene discovery and gRPC `ListScenes` share one catalog: active scene-registry records, plus configuration datasets whose id the registry does not already own, ordered by id. Inactive registry records are omitted from that list. The HTTP list can include inactive records when the caller asks to include disabled scenes; gRPC `ListScenes` stays active-only. Tile and scene reads still follow the scene access policy.

SceneServer rows in the GeoServices directory are a narrower, Enterprise-only projection of active registry records. They are not a substitute for the scene catalog on other editions.

`scene.catalog` is deferred in the 2026.1 capability record even though these discovery routes stay registered. Treat scene discovery as evaluation, the same boundary as [Publish 3D scenes](../publish/publish-3d-scenes.md).

### Capability manifest

SDKs, Console, QGIS, native hosts, and agents read `discovery.capability-manifest` before they assume a protocol or operation exists on this deployment. The manifest is `GET /api/v1/capabilities/manifest`. Authentication is optional; an anonymous caller receives the public view. Optional `environment` and `workspaceId` parameters scope the result. The response is generated per request, marked uncacheable, and is informational: an operation endpoint still decides authorization.

Each capability reports whether the build supports it and whether it is available for this request after deployment profile, configuration, license, and policy. `reasonCode` is set only when it is unavailable. The document also carries transports, limits, and policies. It does not list layers, services, or scenes. Field meanings: [Capability manifest](../../reference/admin-api/overview.md#capability-manifest).

Two nearby documents are not this manifest and not the data catalog:

- MCP `honua_list_capabilities` reports which tools this running composition actually advertises.
- `GET /api/v1/admin/capabilities` is the control-plane compatibility handshake (server version and contract versions). Control-plane SDKs call it once per session.

Asking the manifest for an environment that has no activated snapshot does not fail the document. That environment is reported unavailable with `environment-unavailable`.

### Console workspace discovery

Console can project FeatureServer and MapServer directory entries into an explicitly mapped workspace. The mapping does not publish anything, does not cover other protocols, and is not a default. An unmapped workspace is not found. Detail: [Console catalog discovery](../../user/console-catalog-discovery.md).

## Value domains

Two different "domains" show up in catalogs. They are not interchangeable.

**Process parameters.** A geoprocessing parameter with a finite set of values the server already validates publishes that set as `AllowedValues`. GPServer exposes it as `choiceList`. OGC API Processes exposes it as a JSON Schema `enum`. Values outside the set are rejected, compared case-insensitively. A parameter that does not have a finite enforced set does not get an invented list; prose in a description is not a domain. Process list and execution: [Geoprocessing operations](../../reference/geoprocessing-operations.md) and [OGC API Processes](../../reference/protocols/ogc-apis.md#ogc-api-processes).

**Feature fields.** A coded-value domain or a numeric range domain captured from a GeoServices import is stored on the published field and returned on the FeatureServer layer description. Edits outside a stored domain are rejected. A coded-value domain with more than 100 entries is not stored and is not enforced; it is dropped, not truncated. A range domain is stored only when it has a minimum and a maximum. Domains are field metadata, not extra catalog records.

## When a catalog is empty or a route fails

These are the checks to make at the catalog the caller is using. They replace two older failure modes that the server no longer has: a missing snapshot does not by itself turn catalog reads into HTTP 500, and a service written without `status` is not silently drafted and hidden.

**No activated snapshot.** Catalog reads for the running environment (Records, STAC, the GeoServices directory, OGC collections, OData, WFS capabilities, `honua_list_layers`) load `Metadata:Environment`. If that environment has no activated snapshot, the server synthesizes one from a legacy catalog when that catalog still has published layers. If neither exists, those reads return an empty success, not an error. A snapshot that is present but cannot be loaded still fails the read.

The admin metadata inventory for an environment name with no active revision is not found, with `Metadata v2 environment '<env>' does not have an active revision.` A cross-environment binding query reports `environment-unavailable` for that name. If serving catalogs are empty while inventory for a different name is populated, `Metadata__Environment` does not match the snapshot you activated. Publishing a layer, or another metadata write, in the running environment activates a snapshot there. The first publish starts from an empty graph; it does not adopt the synthesized legacy view as its write base, and it does not fail merely because no snapshot existed yet.

**Lifecycle.** Omitting `status` or `status.lifecycle` leaves the entity active on every protocol in the table above. Set `draft`, `retired`, or `archived` only to keep it in the graph and hide it everywhere it would have been listed. `deprecated` remains listed. `enabled=false` at publish is the control-plane way to retire a layer; the publish result is the place to see that, because the catalogs will not contain the layer afterward.

**This caller.** An empty `honua layers` or `honua_list_layers` result with a successful publish usually means the key has no read grant, the tenant does not match, or the service lifecycle was already non-serving before this publish. Fix the grant or the lifecycle; do not republish into a different environment to compensate.
