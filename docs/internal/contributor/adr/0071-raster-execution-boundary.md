# ADR-0071: PostGIS-first, database-SLO-aware raster execution boundary

## Status

Accepted (2026-08). This is the controlling raster engine and placement
decision for honua-server#3085 and honua-server#3086.

## Context

Honua has several legitimate raster execution locations:

- the native-AOT web process can perform bounded, pure-managed COG and Zarr
  reads;
- PostGIS Raster provides data-local serving and analysis, including operations
  whose database implementation uses GDAL internally;
- the managed job substrate can orchestrate asynchronous PostGIS work;
- the native GP worker provides GDAL utilities without adding native
  dependencies to the serving image; and
- remote batch backends such as AWS Batch can isolate bursty, high-memory, and
  high-scratch jobs from the database and serving fleet.

Earlier decisions describe parts of this boundary. ADR-0029 introduced
PostGIS-backed raster and surface primitives behind the canonical process
model. ADR-0038 keeps native dependencies out of the serving image and says
heavy spatial transforms should delegate to PostGIS. ADR-0057 says heavy raster
GP uses the GDAL worker. Proposed ADR-0060 describes local and remote execution
backends. Read separately, they do not decide which engine and placement the
same canonical raster operation should use.

The unresolved boundary creates four risks:

1. Treating "delegate to PostGIS" as permission to run unbounded analysis on
   the primary database can damage request-serving SLOs.
2. Treating a raster process ID as permanently native duplicates PostGIS
   capabilities and moves database-resident data unnecessarily.
3. Moving large raster payloads inline through the web heap and durable job
   store defeats worker and Batch isolation.
4. Switching engines implicitly on failure can change numerical semantics or
   duplicate partially materialized results.

## Decision

Adopt **PostGIS-first for capability and data locality, database-SLO-aware for
placement**.

PostGIS-first is a preference, not an unconditional execution rule. Honua uses
PostGIS when the operation is supported, the inputs are data-resident or cheap
to stage, and predicted work fits the configured database resource budget.
Honua uses isolated native GDAL when format support, external data locality,
scratch or memory demand, burst isolation, or protection of the database SLO
makes it the safer engine.

The canonical process definition remains independent of the physical engine.
Protocol adapters and SDKs submit the same process contract regardless of
whether a particular job runs in PostGIS, a local native worker, or a remote
batch backend.

### Plane responsibilities and dependency boundary

| Plane | Responsibility | Raster dependency policy |
| --- | --- | --- |
| Native-AOT web | Protocol adaptation, authorization, validation, metadata-only planning, bounded request execution, job submission, and bounded pure-managed COG/Zarr reads | No GDAL libraries, CLI tools, bindings, or transitive native packages |
| PostGIS | Default engine for bounded and data-resident raster serving and analysis | PostGIS use of database-side GDAL is allowed; it does not make an otherwise unbounded job safe |
| Managed PostGIS raster worker | Durable orchestration, cancellation, retries, and result registration for asynchronous database raster work | No local GDAL; uses a dedicated runtime profile and governed database connections |
| Local native GP worker | Modest native-format or compute-heavy raster jobs, including on-premises and air-gapped deployments | GDAL is allowed and isolated from public ingress |
| Remote native backend | Bursty, database-disruptive, object-store-local, high-memory, or high-scratch raster jobs | A versioned GDAL worker image runs through `IBatchComputeBackend`, including AWS Batch or another configured backend |
| Object storage | Exchange of large immutable inputs, intermediate products, and outputs | Typed references cross process boundaries; payload bytes do not travel in durable job specifications |

The serving image may issue bounded PostGIS raster queries as part of a request.
It must not claim the dedicated asynchronous PostGIS raster profile or host a
local GDAL execution path. Installing GDAL in the PostGIS service or native
worker does not weaken the no-GDAL web-image invariant.

### Execution envelopes

Honua selects one of four execution envelopes:

1. **Bounded request execution.** Use for metadata, identify/sample, bounded
   tiles and exports, request-sized clip/resample/reprojection, cached
   statistics, and similarly predictable work. Database-resident operations
   prefer PostGIS. Pure-managed COG/Zarr reads remain limited to their declared
   bounded-serving envelopes.
2. **Durable PostGIS execution.** Use when a supported, data-resident operation
   should remain in PostGIS but exceeds a request budget. A dedicated managed
   raster worker owns the job lifecycle and uses separate database admission,
   concurrency, timeout, cancellation, and tenant controls.
3. **Durable local native execution.** Use a GDAL worker for moderate native
   jobs where local capacity, latency, deployment topology, or air-gapped
   operation favors local placement.
4. **Durable remote native execution.** Use AWS Batch or another
   `IBatchComputeBackend` for bursty jobs, object-store-local bulk work, large
   decoded surfaces, high or unpredictable scratch/memory, or jobs whose
   predicted database impact would threaten the database SLO.

Typical PostGIS-preferred operations include bounded clip/window, mosaic,
reproject/resample, rendering functions, map algebra, reclassification,
spectral indices, statistics, zonal statistics, and terrain derivatives when
their inputs and predicted cost fit the database budget. This list expresses a
capability preference, not a promise that every instance runs synchronously or
in PostGIS.

Typical native-preferred operations include arbitrary format decode/encode,
COG construction, NetCDF/HDF/GRIB conversion, bulk external-object transforms,
large mosaics or warps, and algorithms whose supported PostGIS implementation
or semantics have not been proven. Capability and benchmark evidence, rather
than this ADR, determines the permanent per-operation support matrix.

### Selection and durable record

Engine selection happens before execution and before a worker claims work that
can mutate state. The raster execution planner uses metadata rather than raster
payload bytes and evaluates, in order:

1. authorization and source/output accessibility;
2. engine capability and required algorithm semantics;
3. input residency and staging cost;
4. predicted decoded pixels, bands, source count, output size, memory, scratch,
   database work, and duration;
5. request, database, local-worker, and remote-backend budgets and health; and
6. operator policy and backend availability.

Every durable raster job records at least the selected engine, placement,
runtime/worker-image contract version, input residency, cost estimate, output
sink, decision reason, and applicable operator override. Static process catalog
runtime profiles may declare capabilities or defaults, but they are not a
sufficient placement decision.

Numeric routing thresholds remain configuration informed by benchmark and
production evidence. This ADR deliberately does not freeze universal values.

### Artifact transport

Large raster inputs and outputs cross plane boundaries by typed artifact
reference, never by whole-file download into the web process or base64 encoding
into Redis, environment variables, or the durable job specification.

A source or output reference identifies its kind and residency (for example a
PostGIS raster, object-store COG, or object-store Zarr), immutable locator or
version, media type, declared size, checksum/integrity metadata, and a scoped
authorization or credential reference. Durable specifications store no raw
credentials. Workers resolve references directly under the authorization
snapshot carried by the job.

Small inline values remain allowed under an explicit, deliberately small
ceiling for protocol compatibility. The inline path is never an automatic
fallback for a reference the selected worker cannot resolve.

Outputs are first published to an attempt-scoped staging location. Successful
completion promotes the artifact and optionally registers it in the catalog or
PostGIS atomically. Registration is a separate explicit output-sink decision;
creating an object does not implicitly create or replace a layer.

### Failure, fallback, retry, and idempotency

- A job does not silently switch engines or placement after a mutating attempt
  starts. This includes PostGIS materialization, object publication, catalog
  registration, and overwrite of an existing output.
- A pre-execution fallback is allowed only when no externally visible mutation
  occurred, the alternate engine is declared semantically compatible, policy
  permits it, and the new decision reason is persisted before execution.
- A post-failure change of engine is a newly planned attempt, not an internal
  retry. The prior attempt remains auditable and its staged outputs are either
  cleaned up or retained according to policy.
- Automatic retries stay on the selected engine and placement and occur only
  for classified retryable failures. They reuse a stable idempotency key.
- PostGIS outputs use transaction/staging semantics where possible. Object
  outputs use attempt-scoped keys followed by atomic promotion or an equivalent
  compare-and-set registration. Catalog registration is idempotent by job,
  logical output, and attempt.
- Cancellation stops new work, propagates to the selected executor, and cleans
  uncommitted staging artifacts without deleting a previously committed result.

These rules prevent a timeout in one engine from producing a second,
numerically different result or duplicating a partially committed output in
another engine.

### Operator policy and semantic parity

Operators may cap, prefer, deny, or force an engine or placement by workload,
tenant, deployment profile, and resource budget. A forced unavailable engine
fails closed with an actionable admission error; Honua does not override that
choice silently. Database-health admission may promote eligible work from
PostGIS to native execution before the attempt starts or defer it in the queue.

Operations implemented by both PostGIS and GDAL share a canonical contract for
NoData, grid origin and alignment, extent, CRS, pixel type and rounding,
resampling, edge behavior, and output registration. Where algorithms are not
semantically equivalent, they are distinct capabilities or require explicit
selection; the planner must not present them as interchangeable fallback
engines.

### Raster and 3D boundary

- DEM-derived elevation, terrain tiles, hillshade and other raster surface
  derivatives remain raster capabilities and follow this execution decision.
- Orthomosaic production is a durable native/photogrammetric workflow whose
  registered output is a canonical raster artifact. Subsequent serving and
  raster analysis follow this decision.
- Point clouds (LAS/LAZ/COPC), meshes, scene-layer generation, stereo feature
  matching, and general 3D reconstruction are not raster engines. They use
  separate native capability families and worker dependencies. If they produce
  a DEM or orthomosaic, the handoff occurs through the typed raster artifact
  and registration boundary.

This keeps the raster roadmap focused while allowing issue #2442 and
ADR-0065's advanced imagery boundary to evolve independently.

## Consequences

### Positive

- The public web image remains small, native-AOT, and free of GDAL while Honua
  retains the breadth of PostGIS Raster and isolated GDAL.
- Database-resident work avoids unnecessary export and transfer when it fits
  the database budget.
- AWS Batch and other remote backends become genuine database-protection and
  burst-capacity mechanisms instead of universal GP destinations.
- One canonical process can be placed differently without protocol or SDK
  drift, and every decision remains explainable from the job record.
- Typed references remove large raster copies from the web heap and durable job
  store.

### Negative

- Honua must maintain capability, cost, health, and semantic evidence for more
  than one raster engine.
- Operators gain additional worker profiles, database governance settings, and
  routing policy to understand.
- Some jobs require staging between PostGIS and object storage, trading transfer
  and storage cost for isolation.
- Cross-engine tests and versioned worker contracts become release gates before
  dynamic placement can be trusted broadly.

## Alternatives considered

- **PostGIS for every supported operation.** Rejected because supported does
  not mean safe for the primary database under every input size or concurrency
  level.
- **GDAL for every GP raster operation.** Rejected because it duplicates mature
  PostGIS capability and needlessly exports data-resident rasters.
- **GDAL in the web image.** Rejected because it violates the native-AOT,
  cold-start, memory, and dependency boundary.
- **Always use remote Batch when configured.** Rejected because small local or
  data-resident work pays avoidable queue, staging, and cloud costs.
- **Automatic cross-engine fallback after failure.** Rejected because it hides
  semantic changes and cannot safely reason about partial mutation.

## Implementation and verification

The implementation plan and exit criteria live in parent epic #3085. This ADR
owns policy; the following tickets own contracts and implementation:

- serving-image and security invariants: #3087 and #3068;
- source/output reference contracts and removal of inline COG transport: #3088,
  #3089, and #3090;
- capability, cost, placement, and worker-profile contracts: #3091, #3092,
  #3093, and #3094;
- PostGIS execution and database governance: #3095, #3096, and #3097;
- durable ingest and storage policy: #3098 and #3099;
- cross-engine semantics and synchronous budgets: #3100 and #3101;
- bounded COG/Zarr paths and raster function chains: #3102, #3103, and #2438;
- execution telemetry and orthomosaic handoff: #3104 and #3105.

Verification must prove that a large registered COG and a PostGIS-backed raster
can execute through an eligible AWS Batch path without raster bytes entering the
web process or Redis; bounded data-resident operations prefer PostGIS; web and
database admission are independent; cross-engine fixtures prevent silent
semantic drift; and the canonical serving image contains no GDAL bytes.

## References

- [ADR-0029: Geoprocess Canonical Model Mappings](0029-geoprocess-canonical-model-mappings.md)
- [ADR-0038: GeoETL Pipeline Architecture and Runtime Boundary](0038-geoetl-pipeline-architecture-and-runtime-boundary.md)
- [ADR-0039: Cloud-Optimized HDF5 / NetCDF4 Reader Strategy](0039-cloud-optimized-hdf-netcdf-reader-strategy.md)
- [ADR-0057: Geoprocessing Capability Boundaries](0057-geoprocessing-capability-boundaries.md)
- [ADR-0060: Two-Plane Operability Architecture](0060-two-plane-operability-architecture.md)
- [ADR-0065: ImageServer Photogrammetric Analytics](0065-imageserver-photogrammetric-analytics.md)
- honua-io/honua-server#3085 - raster strategy epic
- honua-io/honua-server#3086 - this decision
