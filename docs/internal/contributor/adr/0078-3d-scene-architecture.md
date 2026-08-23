# ADR-0078: 3D Scene Architecture — Canonical 3D Tiles, I3S Projection, and Runtime Resolution

## Status

Accepted (2026-08-22). Tracked by
[#3278](https://github.com/honua-io/honua-server/issues/3278) and the 3D epic
[#3249](https://github.com/honua-io/honua-server/issues/3249). This ADR is the
architecture input to the 2026.1 capability truth pass in
[#3279](https://github.com/honua-io/honua-server/issues/3279); it does not promote
any deferred capability to implemented.

## Context

Honua already has several substantial but uneven 3D surfaces:

- hosted 3D Tiles routes, protected-asset access envelopes, and a scene registry;
- deterministic generation from PostGIS features, CityGML/BIM ingest, and
  LAS/LAZ/COPC point-cloud ingest;
- public scene list, metadata, and resolve routes plus gRPC projections;
- I3S service, layer, node-page, statistics, geometry, and attribute routes;
- Terrain-RGB tiles and numeric elevation/terrain-analysis endpoints.

The presence of a route is not proof that its capability is renderable or
release-ready. In particular, I3S has no production
`ISceneNodeGeometryProvider`, so its geometry and geometry-backed attribute
routes return an honest `404`. Its descriptor and node metadata are a preview,
not a renderable I3S claim. The public scene contracts are not yet published as
versioned cross-SDK fixtures. Hosted 3D Tiles also lacks a governed client
evidence lane. Those facts require 2026.1 maturity demotions even though much of
the server implementation exists.

The domain model also conflates two independent questions. `SceneDatasetType`
currently mixes how bytes are served (`HostedTiles`, `Terrain`) with what those
bytes mean (`Building`, `PointCloud`). That ambiguity leaks into discovery and
I3S layer-type selection. At the same time, summary and metadata DTOs expose a
tileset URL even though endpoint choice must account for representation,
authentication, and client support. Without an authority for that choice, SDKs
can invent incompatible resolution behavior.

Finally, protected 3D assets are a cascade: a renderer loads the root document
and then many nested resources. A serializable plan containing an API key,
Bearer token, or signed access-envelope token would leak credentials through
saved maps, collaboration logs, receipts, telemetry, and retries. Authentication
therefore has to be a runtime host resource rather than scene-plan data.

## Decision

### 1. Canonical hosted representation and I3S projection

**Decision:** OGC 3D Tiles 1.1 is the canonical representation for a hosted
Honua scene. I3S is an Enterprise compatibility projection of the same catalog
record and semantic content, never a second source of truth.

`/scenes/{sceneId}/tileset.json` and its asset tree are the canonical hosted
scene. CityGML/BIM and point-cloud ingest produce 3D Tiles. Generation from a
feature layer also produces and registers 3D Tiles. A scene's identity,
authorization policy, extent, content kind, and lifecycle remain catalog facts;
an I3S adapter projects those facts into SceneServer documents.

For 2026.1, the honest I3S posture is **descriptor/node-metadata preview**:
service and layer documents, node pages, statistics, and the available attribute
metadata may be projected, but renderable geometry is deferred until a real
provider and real-client evidence exist. A mapped geometry route that returns
`404` without a production provider is not an implemented rendering capability.

I3S geometry work may transcode from canonical scene content, but must not create
an independently managed I3S scene registry or let I3S metadata diverge from the
catalog. Per-node geometry, textures, and paging are later implementation work.

### 2. Ingest and native-process boundary

**Decision:** CityGML and LAS are accepted through admin ingest and materialized
as 3D Tiles. LAZ/COPC decompression and projected point-cloud reprojection run
through the out-of-process native `pcloud.translate` worker; PDAL and other
native point-cloud libraries do not enter the AOT web-server image.

The native-profile worker performs only the work that needs PDAL: decompressing
LAZ/COPC to uncompressed LAS and, when requested, reprojecting to EPSG:4979. The
managed server pipeline then parses LAS and builds the same PNTS/3D-Tiles output
as an uncompressed upload. This keeps tiling, catalog registration, policy, and
output determinism in the managed scene pipeline.

When no compatible worker is configured, compressed or projected input fails
explicitly; it must not be accepted as if conversion occurred. Documentation
must describe worker-configured dispatch and the honest failure path. Direct
COPC range serving is a separate future representation, not evidence that COPC
ingest or 3D-Tiles generation completed.

### 3. Separate serving representation from semantic content

**Decision:** The catalog models `servingFormat` and `contentKind` as independent
axes. Protocol adapters and clients must not infer one from the other.

The initial vocabulary is:

| Axis | Values | Meaning |
| --- | --- | --- |
| `servingFormat` | `3d-tiles`, `quantized-mesh`, `copc` | The wire/storage representation a selected endpoint serves. |
| `contentKind` | `3d-object`, `building`, `point-cloud`, `terrain` | The semantic kind used for presentation and compatibility projection. |

Examples make the distinction load-bearing: CityGML registers
`3d-tiles/building`; LAS registers `3d-tiles/point-cloud`; an ordinary mesh is
`3d-tiles/3d-object`; a future Cesium terrain endpoint is
`quantized-mesh/terrain`. I3S `layerType` and store profile derive from
`contentKind`, never from `servingFormat`.

The existing enum/database/JSON values require a lossless compatibility mapping
and migration. [#3409](https://github.com/honua-io/honua-server/issues/3409)
owns that implementation. Unknown future values must fail or remain opaque; they
must not silently fall back to `3DObject` in a public contract.

### 4. Canonical discovery and runtime resolution contracts

**Decision:** Scene list, scene metadata, and scene resolution are three distinct
canonical REST contracts. `GET /api/scenes/{sceneId}/resolve` is the sole
authority for runtime endpoint selection.

- `GET /api/scenes` returns compact summaries for discovery and filtering.
- `GET /api/scenes/{sceneId}` returns descriptive metadata and stable links.
- `GET /api/scenes/{sceneId}/resolve` returns the endpoints usable for the
  current scene, including format, media type, and authentication requirement.

URLs retained in list or metadata shapes for compatibility are hints, not an
invitation for a client to choose a renderer or synthesize nested-asset URLs.
They must agree with resolution, but new endpoint selection behavior is added to
`/resolve`. A client selects from the resolution result according to its actual
provider support; it does not derive an endpoint from `contentKind`, a capability
label, or a URL convention.

Each contract gets its own versioned schema and golden JSON. Additive and
breaking-change rules apply per contract, rather than treating their different
shapes as accidental drift. [#3410](https://github.com/honua-io/honua-server/issues/3410)
owns the contract pack and drift gates.

### 5. Protected assets use a non-serializable host session

**Decision:** Serializable scene plans contain identity, presentation intent,
and authentication requirements, but never credentials. A renderer host owns an
opaque, non-serializable resource/session that resolves, obtains, refreshes, and
attaches credentials at request time.

The prohibition covers API keys, Bearer tokens, signed access-envelope tokens,
cookie material, prebuilt `Authorization` headers, and credential-bearing URLs.
None may appear in a saved map/app specification, authoring operation, SDK plan,
MCP result intended for persistence, collaboration record, receipt, or log.

For a protected scene, the host:

1. calls the canonical resolve contract and observes the auth requirement;
2. uses its configured identity to acquire a short-lived asset session or access
   envelope;
3. attaches that material to the root and nested asset requests without mutating
   the serializable plan; and
4. refreshes or disposes the session independently of plan lifetime.

Language SDKs may expose this as an opaque handle, callback, interceptor, or
renderer resource. Whatever the spelling, serialization must omit it and a
deserialized plan must require a host to rebind authentication.

### 6. Terrain and elevation client paths

**Decision:** Terrain-RGB and raster DEM remain MapLibre/raster paths. Cesium
terrain consumes quantized mesh; Honua must not advertise a Cesium terrain
provider until a real quantized-mesh producer/provider and client evidence
exist.

Terrain-RGB is a raster encoding for MapLibre/Mapbox `raster-dem`. Numeric
elevation value/profile endpoints and their higher-order analyses operate on the
raster catalog independently of a 3D renderer. A `Terrain` enum value or a
TileJSON endpoint does not constitute quantized-mesh support, and wrapping a
Terrain-RGB URL in a Cesium provider is not an acceptable compatibility shim.

Quantized-mesh generation/serving from DEM rasters is deferred to
[#3285](https://github.com/honua-io/honua-server/issues/3285). Until that lands,
`/resolve` must not offer a `quantized-mesh` endpoint. Elevation value/profile
remain Community; Pro terrain analyses retain their separate analytics keys and
license gates.

### 7. Edition and unlicensed-response contract

**Decision:** The seven existing capability keys use the editions and canonical
unlicensed responses below. Enterprise I3S and ingest denial is `402` through
`LicenseGate.RequireEntitlement`, not an ad hoc `403`.

| Capability key | Edition | Canonical unlicensed response | Scope note |
| --- | --- | --- | --- |
| `serve.3d-tiles-scene` | Community | N/A — Community has no lower unlicensed edition | Hosted 3D Tiles routes; maturity remains a separate evidence decision. |
| `serve.i3s-scene` | Enterprise | `402 Payment Required` | I3S compatibility projection, currently metadata preview. |
| `scene.catalog` | Community | N/A — Community has no lower unlicensed edition | List, metadata, and resolve; cross-SDK maturity is separately deferred. |
| `scene.bim-ingest` | Enterprise | `402 Payment Required` | CityGML/BIM ingest to canonical 3D Tiles. |
| `scene.pointcloud-ingest` | Enterprise | `402 Payment Required` | LAS and worker-backed LAZ/COPC ingest to canonical 3D Tiles. |
| `serve.elevation` | Community | N/A — Community has no lower unlicensed edition | Numeric value/profile serving; Pro analytics use their own keys. |
| `raster.terrain-rgb` | Community | N/A — Community has no lower unlicensed edition | MapLibre/Mapbox raster-dem path, not Cesium terrain. |

`403 Forbidden` remains appropriate for an authenticated identity that lacks
resource authorization after it has the required product entitlement. It is not
the response for a missing edition entitlement. Capability maturity and edition
are independent: Community does not mean GA, and a deferred capability must not
be advertised as implemented merely because its edition is decided.

No generic `scene.generate` capability key is introduced by this ADR. Feature
layer generation is an admin operation producing Community 3D Tiles; typed BIM
and point-cloud ingest retain their existing Enterprise keys. A future product
boundary may add a key through the capability-registry process rather than an
endpoint-local check.

### 8. SLPK reader disposition

**Decision:** Delete the call-site-free `ReadFromSlpk` and `ConvertFromSlpk`
entry points and their dedicated tests in the truth pass. Do not publish an SLPK
import claim or capability key.

Those methods have no production route, no capability key, and do not provide a
complete geometry import pipeline. Keeping them creates a false impression that
an SLPK can be imported into a renderable scene. A future SLPK proposal must
start with a user-facing route, explicit edition decision, bounded archive
handling, full geometry/texture output, and real-client evidence; it can reuse
general I3S parsing primitives that remain useful.

### 9. Durable generation with deterministic LOD

**Decision:** 3D Tiles generation and CityGML/point-cloud ingest are durable,
job-based operations. Canonical submission returns `202 Accepted` plus a job
status URL; generated output is promoted and registered only after the complete
tileset succeeds validation.

The current synchronous `201` generation endpoint is transitional. An optional
bounded wait mode may preserve compatibility for small inputs, but it is not the
canonical contract and must converge on the same job/result record. Jobs expose
queued, running, succeeded, failed, and cancelled states plus stable error codes,
progress, and the final scene identity/resolve link.

Large inputs retain deterministic quadtree LOD: stable partitioning, bounded
features per tile and depth, decreasing geometric error, and byte-stable asset
names for identical inputs. Validation covers the whole LOD tree and referenced
glTF assets, not only a single-tile fixture. Cancellation or failure cannot
leave a discoverable partial scene. [#3284](https://github.com/honua-io/honua-server/issues/3284)
owns this transition and its durable multi-level fixture.

### 10. MCP, gRPC, and SDKs are projections

**Decision:** MCP scene tools, gRPC scene services, and language SDKs project the
canonical REST list/metadata/resolve semantics. They do not define competing
scene contracts or independently infer runtime URLs.

Server adapters may call the shared catalog/domain service directly rather than
looping through HTTP, but their observable fields and endpoint-selection
behavior are governed by the versioned REST contract pack. MCP uses distinct
list/get/resolve operations. SDKs consume the same fixtures and treat resolution
as a runtime action. A projection may be idiomatic for its transport while
preserving scene identity, the two catalog axes, endpoint format/media type,
authentication requirement, and stable links.

Credential handling follows Decision 5 on every transport. A gRPC message or
MCP result may advertise that authentication is required; it must not turn an
ephemeral asset credential into serializable scene data.

## 2026.1 truth posture

This ADR fixes architecture and licensing; it does not waive evidence. The
2026.1 truth pass records:

| Capability | 2026.1 maturity truth |
| --- | --- |
| `serve.i3s-scene` | Deferred — descriptor/node metadata preview; no production geometry provider. |
| `scene.catalog` | Deferred — canonical cross-SDK wire fixtures/evidence are absent. |
| `serve.3d-tiles-scene` | Deferred — server routes exist, but the governed client lane is absent. |
| `scene.bim-ingest` | Deferred. |
| `scene.pointcloud-ingest` | Deferred. |
| `serve.elevation` | Implemented as an independent server analysis capability. |
| `raster.terrain-rgb` | Implemented for MapLibre/raster use, not Cesium terrain. |

[#3279](https://github.com/honua-io/honua-server/issues/3279) applies these
truth labels, aligns the capability catalog and I3S license gate, corrects
LAZ/COPC documentation, and removes the dead SLPK entry points.

## Consequences

### Improved

- One catalog identity and one canonical hosted representation govern all scene
  projections.
- Renderer endpoint selection has a single versioned authority.
- Saved scene state is safe to persist because credentials are host resources,
  not plan properties.
- BIM and point-cloud semantics survive conversion to the same wire format.
- Terrain claims distinguish the MapLibre path that exists from the Cesium path
  that does not.
- Edition failures become consistent with the product-wide licensing contract.
- Capability truth can remain conservative without undoing architectural work.

### Costs

- The catalog needs a compatibility migration from the mixed dataset enum to two
  axes.
- Three separate public contract schemas and downstream fixture consumers must
  be maintained.
- SDK/rendering hosts need an explicit ephemeral resource/session abstraction.
- I3S remains a preview until production geometry and real-client evidence land.
- Generation and ingest need durable job state and atomic output promotion.

### Rejected alternatives

- **Make I3S an independent canonical store.** This would duplicate identity,
  policy, and lifecycle state and permit protocol drift.
- **Infer content from serving format.** Both buildings and point clouds are
  served as 3D Tiles, so the inference loses required semantics.
- **Treat Terrain-RGB as Cesium terrain.** Cesium's terrain provider contract is
  quantized mesh; relabeling raster tiles would be a false compatibility claim.
- **Put access tokens in resolved plans.** That makes short-lived secrets durable
  and observable in every consumer of the plan.
- **Keep the SLPK helpers as latent support.** Call-site-free metadata conversion
  is not a product capability and obscures the missing geometry pipeline.
- **Keep synchronous generation canonical.** Large LOD builds and native-worker
  conversion do not fit an HTTP request lifetime and cannot expose durable
  progress, cancellation, or retry semantics.

## Follow-up ownership

- [#3279](https://github.com/honua-io/honua-server/issues/3279): 2026.1 truth,
  licensing alignment, documentation correction, and SLPK removal.
- [#3409](https://github.com/honua-io/honua-server/issues/3409): catalog axes and
  compatibility migration.
- [#3410](https://github.com/honua-io/honua-server/issues/3410): versioned
  list/metadata/resolve contract pack.
- [#3280](https://github.com/honua-io/honua-server/issues/3280): production I3S
  geometry, if/when the deferred lane resumes.
- [#3284](https://github.com/honua-io/honua-server/issues/3284): durable jobs and
  validated LOD fixture.
- [#3285](https://github.com/honua-io/honua-server/issues/3285): quantized-mesh
  terrain provider.
- [#3286](https://github.com/honua-io/honua-server/issues/3286): MCP projections
  after the canonical contract pack.
