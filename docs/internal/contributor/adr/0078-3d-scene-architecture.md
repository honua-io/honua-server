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

**Decision:** The catalog models its canonical `servingFormat` and semantic
`contentKind` as independent axes. Runtime resolution separately identifies the
format of each endpoint projection. Protocol adapters and clients must not infer
any one of these values from another.

The initial vocabulary is:

| Axis | Values | Meaning |
| --- | --- | --- |
| `servingFormat` | `3d-tiles`, `quantized-mesh`, `copc` | The canonical persisted representation registered by the catalog. |
| `contentKind` | `3d-object`, `building`, `point-cloud`, `terrain`, `unclassified` | The semantic kind used for presentation and compatibility projection; `unclassified` is the explicit migration-safe wire value when legacy provenance cannot prove a semantic kind. |

Examples make the distinction load-bearing: CityGML registers
`3d-tiles/building`; LAS registers `3d-tiles/point-cloud`; an ordinary mesh is
`3d-tiles/3d-object`; a future Cesium terrain endpoint is
`quantized-mesh/terrain`. I3S `layerType` and store profile derive from
`contentKind`, never from `servingFormat`.

Resolution uses a distinct closed `endpointFormat` vocabulary: `3d-tiles`,
`i3s`, `quantized-mesh`, and `copc`. It describes the protocol/representation at
the returned endpoint, not catalog storage. For example, a scene whose canonical
`servingFormat` is `3d-tiles` may also resolve to an `i3s` compatibility
projection; that projection does not register I3S as another canonical store.

The existing enum/database/JSON values require a compatibility migration without
record loss, but their semantic axis cannot always be backfilled losslessly.
`HostedTiles` identifies the serving representation for existing mesh, CityGML,
and point-cloud publications, and configuration-defined scenes may carry no
semantic provenance at all. [#3409](https://github.com/honua-io/honua-server/issues/3409)
therefore maps `servingFormat` independently and assigns a classified
`contentKind` only when durable publish provenance or validated asset metadata
proves it. Otherwise list and metadata contracts return the exact
`contentKind: "unclassified"` wire value until an operator reclassifies the
record. That record stays discoverable through its canonical 3D-Tiles
representation, but `unclassified` never maps to an I3S layer/store profile or
another semantic projection. Migration must never guess `HostedTiles` to mean
`3d-object`. Unknown future values likewise fail or remain opaque rather than
silently falling back to `3DObject` in a public contract.

### 4. Canonical discovery and runtime resolution contracts

**Decision:** Scene list, scene metadata, and scene resolution are three distinct
canonical REST contracts. `GET /api/scenes/{sceneId}/resolve` is the sole
authority for runtime endpoint selection.

- `GET /api/scenes` returns compact summaries for discovery and filtering.
- `GET /api/scenes/{sceneId}` returns descriptive metadata and stable links.
- `GET /api/scenes/{sceneId}/resolve` returns the endpoints usable for the
  current scene. Each endpoint includes `endpointFormat`, media type, and
  authentication requirement. A protected endpoint also includes the
  credential-free `access-session` HTTP affordance (`href` plus
  `method: POST`) that a host invokes to establish access. The method is part
  of the versioned contract and golden fixture, not an implicit link default;
  the resolution result never embeds the resulting credential.

URLs retained in list or metadata shapes for compatibility are hints, not an
invitation for a client to choose a renderer or synthesize nested-asset URLs.
They must agree with resolution, but new endpoint selection behavior is added to
`/resolve`. A client selects from the resolution result according to its actual
provider support; it does not derive an endpoint from `contentKind`, a capability
label, or a URL convention.

The closed `endpointFormat` vocabulary defines schema values, not a claim that
every projection is currently available. Resolution returns an endpoint only
when its production provider and complete asset route are active, the current
request passes the capability entitlement and resource-authorization gates, and
the evidence posture permits advertising it as usable. In 2026.1 it must omit
I3S because descriptor/node metadata without production geometry is not
renderable, and it must omit COPC because no direct range-serving route exists.
I3S becomes selectable only after its geometry provider, Enterprise entitlement
gate, and real-client evidence land; COPC becomes selectable only after its
serving route, policy, and evidence land. Section 6 applies the same suppression
rule to quantized mesh.

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

The POST response has its own versioned runtime access-session contract. Along
with the opaque token, expiry/refresh timestamps, and allowed methods, it returns
structured request-credential transports. Each transport states its kind
(`header` or `query`), parameter name, and token value template. The canonical
header option is `X-Honua-Token`; the `token` query option remains available for
browser/WebView renderers that cannot attach headers to nested 3D-Tiles fetches.
The response also binds each transport to server-authoritative asset scopes,
expressed as an exact scheme, host, effective port, and canonical path-prefix
boundary. Hosts prefer the header when supported and attach either credential
only after canonicalizing the target URL and matching one of those scopes. They
strip the credential before following a redirect and re-evaluate the redirect
target; user-info URLs, foreign origins, sibling path prefixes, and other
out-of-scope nested resources receive no Honua credential. Template substitution
happens only inside the non-serializable host session: a query-token URL is never
placed in a plan, fixture, receipt, log, or collaboration record, and retains the
protected asset route's private/no-store cache treatment.

[#3433](https://github.com/honua-io/honua-server/issues/3433) owns the versioned
response fixture and transport drift tests.

For a protected scene, the host:

1. calls the canonical resolve contract and observes the auth requirement;
2. invokes the returned credential-free `access-session` POST affordance, using
   its configured identity to acquire a short-lived asset session or access
   envelope rather than synthesizing a server route or assuming GET semantics;
3. selects a returned credential transport that its renderer supports and
   applies its request template only to root and nested asset requests inside the
   returned origin/path scopes, without mutating the serializable plan or leaking
   the credential to an absolute/cross-origin URI or redirect; and
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

For multipart CityGML/LAS/LAZ/COPC input, the endpoint first creates or claims a
durable staging reservation scoped to the tenant, submitter, and submission
idempotency key. That reservation has an independently discoverable identifier,
lease generation, and expiry in a staging index or provider lifecycle policy;
its cleanup does not depend on a job record existing. Request-level validation,
streaming into immutable staging, and digest verification complete before the
endpoint returns `202` or exposes a dispatchable job. The job persists only an
opaque input reference plus integrity metadata, never request streams, upload
bytes, credentials, or a machine-local temporary path. A live submitter/worker
renews or claims the lease. If the API dies after upload but before job creation,
an orphan sweeper/provider expiry reclaims the unclaimed stage; terminal jobs
schedule idempotent cleanup after the configured diagnostic/retry retention.

Async multipart submission requires the shared idempotency key. Its canonical
request fingerprint covers normalized form fields, target scene identity, and
the staged content digest/size/media type, and is scoped to the tenant and
submitter. Job creation conditionally binds that key and fingerprint to exactly
one staged digest and status URL. A retry with the same key and fingerprint
returns the existing job/reference, even after a lost `202`; a different
fingerprint returns a conflict, and neither path dispatches a duplicate job.
Any losing duplicate stage is aborted or left to its bounded orphan lease.

Feature-layer generation applies the same immutability rule without multipart:
submission pins a provider snapshot/version and includes it in the request
fingerprint. If the provider cannot retain a stable readable snapshot for the
job lifetime, submission materializes the bounded source into job-owned durable
staging before `202`. A retry never re-queries an unversioned live layer whose
features may have changed since admission.

Admission also captures the submitting principal's
`OperationAuditInfo.SubmitterSecurityContext` beside that source snapshot. The
background executor enters the shared job-security scope and reads/materializes
through the canonical query pipeline with the same tenant, row predicate, and
field mask. Managed membership is revalidated before deferred execution; an
inactive identity, removed role, absent/stale context, or failed revalidation
fails closed rather than falling back to a service-wide catalog read. A later
grant may never widen the already-pinned view for the existing job.

Admission also derives and pins an explicit output audience/access policy and
includes it in the request fingerprint. That policy must be provably no broader
than the principals whose canonical row predicate and field mask are equivalent
to the materialized view. It must never mechanically copy a source layer's
coarse `AccessPolicy`: an administrator's unmasked snapshot of an otherwise
public layer cannot become a public scene. If the scene policy model cannot
represent a safe audience, submission fails closed (or uses a representable
submitter-only private audience); it never widens the output. The catalog
candidate and protected asset session bind the pinned policy, normal scene reads
revalidate it, and later grants do not expand the audience of an existing static
scene.

The current synchronous `201` generation endpoint is transitional. An optional
bounded wait mode may preserve compatibility for small inputs, but it is not the
canonical contract and must converge on the same job/result record. Scene jobs
reuse the shared `ExecutionJobStatus` lifecycle from ADR-0031 unchanged:
`Queued`, `Provisioning`, `Running`, `Succeeded`, `Failed`, and `Cancelled`.
They do not introduce a scene-specific status enum or mapping. Jobs also expose
stable error codes, progress, and the final scene identity/resolve link.

[#3284](https://github.com/honua-io/honua-server/issues/3284) adds a dedicated
`ExecutionJobKind.SceneGeneration` and one unambiguous executor for that kind;
scene generation and staged scene ingest must not masquerade as `TileCache` or
reuse its executor. The scene executor may orchestrate the separately profiled
`pcloud.translate` native step from Decision 2, but the durable parent job keeps
its scene-generation identity and lifecycle.

Submission also pins the managed scene-generation execution contract in the
durable `ExecutionJobSpec`: `ContractVersion` names the serving/worker contract,
and `Artifact` is an immutable build or image digest rather than a moving tag.
Every automatic retry reuses both pins. A worker that cannot run them fails
closed, and changing either pin requires an explicitly submitted new job. When a
native translation is required, its child specification independently pins the
`pcloud.translate` contract version and immutable worker artifact digest.

The native child is idempotent with its parent. Before submitting translation,
the scene job derives a deterministic child operation/idempotency key and request
fingerprint from the parent operation id, step identity, staged input digest,
normalized translation arguments, and the pinned native contract/artifact. The
parent durably records that child reference and submits with the deterministic
key, never a random plan id plus a null idempotency key. After a crash, a retry
verifies the fingerprint and reattaches to the existing child/result instead of
dispatching another translation; a mismatch fails and requires a new parent job.

Native translation crosses the job boundary only by durable typed artifact
reference. The child reads the parent's immutable staged LAZ/COPC reference
directly and never receives base64 input in its plan. It publishes an immutable,
integrity-bound LAS reference with locator/version, size, media type, and digest,
never a `data:` URI or whole-file `byte[]` result. The parent durably binds that
reference before consumption and holds/renews its retention through every parent
retry; it cannot expire before parent terminalization plus the configured retry
or diagnostic window. Parent terminal cleanup releases both input and translated
artifact leases idempotently.

Parent cancellation/abandonment is a durable child-operation signal, not only a
one-time parent cleanup pass. It propagates cooperative cancellation to the
recorded child and identifies the expected parent operation generation/fencing
token. Child result publication conditionally verifies that the parent is still
active at that generation before binding or retaining an LAS artifact. A late or
racing result after parent terminalization is rejected/quarantined and reclaimed
by an independently retrying child-output reconciler or bounded provider expiry;
it cannot create or renew an unbound retained object. Parent cleanup remains
idempotent until the child acknowledges cancellation or any fenced late output
has been reclaimed.

The promoted asset tree and active catalog registration are required members of
one job-wide, attempt-fenced output-set manifest governed by ADR-0031 and
ADR-0071. Before dispatch, the coordinator prepares idempotent sink intents for
both members and captures their expected destination versions. An attempt writes
only to its immutable staged asset tree and a non-public catalog candidate. The
conditional `Running` to `Finalizing` handoff freezes the complete manifest to
the winning attempt/fencing token; a publication reconciler then advances both
sink intents under the same completion token. A changed or concurrently reserved
normalized scene destination fails its expected-version check; publication never
refreshes the expectation or falls back to last-writer-wins. Public asset/catalog
readers and job result projections expose neither member until the durable
manifest is `Complete`, and the job cannot become `Succeeded` before that point.
A crash or partial sink commit is reconciled idempotently for the same winning
attempt, never re-executed as a competing publication; cancellation/failure
aborts or quarantines the incomplete set without changing the previously
visible scene.

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

The currently published geospatial-grpc `SceneService` exposes only
`ListScenes` and `GetScene`, so it is not yet a resolve projection. Protocol
parity is blocked on
[geospatial-grpc#90](https://github.com/honua-io/geospatial-grpc/issues/90),
which owns an additive `ResolveScene` contract carrying endpoint format, media
type, auth requirement, and the credential-free `access-session` POST
affordance. After a new protocol package is published, Honua.Server must update
its `Geospatial.Grpc` dependency/generated surface before implementing and
claiming gRPC resolution. Neither gRPC scene resolution nor cross-SDK parity may
be claimed before both changes land.

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
- Four separate public contract schemas and downstream fixture consumers must
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
- [#3433](https://github.com/honua-io/honua-server/issues/3433): versioned
  access-session response and credential-transport request templates.
- [geospatial-grpc#90](https://github.com/honua-io/geospatial-grpc/issues/90):
  additive `ResolveScene` protocol contract and published package; the server
  dependency update and projection follow that release.
- [#3280](https://github.com/honua-io/honua-server/issues/3280): production I3S
  geometry, if/when the deferred lane resumes.
- [#3284](https://github.com/honua-io/honua-server/issues/3284): durable jobs and
  validated LOD fixture.
- [#3285](https://github.com/honua-io/honua-server/issues/3285): quantized-mesh
  terrain provider.
- [#3286](https://github.com/honua-io/honua-server/issues/3286): MCP projections
  after the canonical contract pack.
