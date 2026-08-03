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

Every durable raster job owns an append-only sequence of attempt-scoped routing
records. Before any executor attempt starts—including the initial selection, a
same-engine retry, a pre-execution fallback, or a post-failure replan—the
durable coordinator appends a record with an immutable attempt identifier and
current-attempt fencing token, selected engine, placement, runtime/worker-image
contract version, input
residency, cost estimate, output sink, decision reason, and applicable operator
override. A new selection or retry never updates or replaces a prior record.
The attempt identifier also scopes the executor outcome and staged artifacts so
operators can reconstruct which decision produced each side effect. Static
process catalog runtime profiles may declare capabilities or defaults, but they
are not a sufficient placement decision.

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
completion promotes the artifact and may register it in the catalog or
PostGIS. Promotion and registration are atomic only when they share a
transactional sink, as with PostGIS materialization and its catalog row. For an
object artifact plus database catalog entry, the object commit marker is the
authoritative winner and registration is an explicit, durable, idempotently
reconciled cross-store step. The executor may stage bytes, but it must not
promote the artifact, commit the sink intent, report terminal execution, or
stop its execution heartbeat before the coordinator transitions the durable
job to canonical status `Running` with
`OutputPublicationPhase.Finalizing`. The sink-local attempt-fenced intent is
created before dispatch as described below; the job-store transition persists
an immutable reference to that already-durable intent. It is a compare-and-set
on the expected job record version, current execution claim, attempt
identifier, fencing token, and absence of a pending cancellation. If
`CancellationRequestedAt` is already present, the same conditional handoff
enters `Terminalizing` with requested status `Cancelled`, binds the prepared
output set to the current attempt for abort reconciliation, advances the
publication-lease generation, and performs no sink commit. The ordinary
handoff does not claim an atomic transaction across the job store and sink.

The same job-store compare-and-set freezes a job-wide output-set manifest to
that winning attempt. The manifest enumerates every required logical output
and its sink intent/version. Individual sinks may then commit sequentially,
but a partial commit leaves the job in `Finalizing`; the output reconciler must
resume the same winning attempt and no replacement execution is eligible. The
manifest becomes `Complete` only after every required output and registration
is durably committed under that attempt's fence. Result packages and artifact
readers expose none of the set until that completion transition succeeds, so a
successful result can never combine outputs from different attempts. If the
set cannot be completed, the job terminalizes without publishing a mixed or
partial result.

A stale attempt is rejected without changing the phase or releasing the
current claim and may not publish its staged output. Only the winning
job-store compare-and-set permits the coordinator to stop that attempt's
execution heartbeat. A crash before that job-store update leaves
attempt-scoped staging and an uncommitted sink intent that execution recovery
may discard or advance; a crash after the update is recovered exclusively by
the output reconciler, which uses the referenced sink intent's own
compare-and-set to commit. There is
consequently no state in which a committed object is still eligible for
execution requeue. A job requesting registration remains in `Finalizing` until
the matching catalog entry is durable. `Finalizing` admits no new execution
and excludes the job from both execution-heartbeat and execution-timeout
expiry. A separate durable publication lease,
heartbeat/deadline, and fenced output reconciler recover stalled registration;
that recovery never requeues raster execution. The protocol does not claim
cross-store atomicity. Creating an object does not implicitly create or replace
a layer.

### Failure, fallback, retry, and idempotency

- A job does not silently switch engines or placement after a mutating attempt
  starts. This includes PostGIS materialization, object publication, catalog
  registration, and overwrite of an existing output.
- A pre-execution fallback is allowed only when no externally visible mutation
  occurred, the alternate engine is declared semantically compatible, policy
  permits it, and a new attempt-scoped routing record is appended before
  execution.
- A post-failure change of engine is a newly planned attempt, not an internal
  retry. Its routing record is appended rather than replacing the failed
  attempt's record. The prior attempt remains auditable and its staged outputs
  are either cleaned up or retained according to policy.
- Automatic retries stay on the selected engine and placement and occur only
  for classified retryable failures. They append a new attempt record and reuse
  a stable idempotency key.
- Every job with declared outputs owns a durable output-set manifest keyed by
  job. Before dispatch it records the complete required logical-output set and
  references each prepared sink intent. The attempt-fenced transition to
  `Finalizing` conditionally freezes that manifest to one winning attempt. A
  later attempt may replace a prepared manifest only before `Finalizing` and
  only after every required uncommitted sink intent has been advanced to its
  fence; failure to advance any member prevents dispatch. Once frozen, partial
  sink success is publication-recovery work for the same attempt, never a
  reason to execute another attempt.
- Every output sink owns a durable commit record keyed by job and logical
  output. The record contains the current attempt identifier, fencing token,
  idempotency key, immutable artifact locator, and publication state. Before
  dispatch, the coordinator conditionally creates or advances an uncommitted
  intent in that sink and gives the executor the resulting record version. A
  new attempt may advance an uncommitted intent but may not replace a committed
  one. Finalization is a sink-local compare-and-set against both the token and
  record version; checking the coordinator's Redis job state before writing is
  only an optimization and is not the fence.
- PostGIS materialization is first written to an attempt-scoped staging or
  versioned relation. Its catalog row carries the output-set manifest identity
  and an explicit publication-candidate state. Committing a member's sink-local
  record, staged relation, and candidate registration occurs in one database
  transaction. A unique job/output key plus a conditional update on the
  expected token and record version makes a competing or stale attempt fail,
  but that member commit alone never replaces the currently published raster.
  `QueryCatalogAsync`, tile/coverage reads, and every other public catalog path
  require both the candidate registration and the authoritative job-wide
  manifest to be `Complete`; they must filter an incomplete manifest rather
  than relying only on `ArtifactReferences` or the catalog row's local state.
- After every required sink member and registration is durably committed in
  its private state, one PostGIS transaction makes every PostGIS member in the
  winning set promotion-ready while it remains hidden by the incomplete
  manifest. The coordinator also persists a `PromotionReady` copy of the full
  manifest in a publication store whose retention matches the outputs (the
  catalog database for registered outputs, or a stable object manifest for an
  object-only set). Only then may the coordinator use the final job-store CAS
  to change the live manifest to `Complete`, create an immutable completion
  token, project the full reference set, and mark the job `Succeeded`. This CAS
  is the orchestration visibility decision and contends with cancellation on
  the same job record. During durable-manifest reconciliation, object/result
  readers and PostGIS catalog readers require that completed live decision plus
  the matching promotion-ready durable manifest, so its success exposes the
  already-ready set together without claiming a cross-store transaction.
- A completed visibility decision is never retained solely in the expiring job
  record. The success CAS places the job under a retention hold instead of the
  normal Redis TTL. Using its completion token, the output reconciler
  conditionally changes the durable `PromotionReady` manifest to `Complete`;
  only that exact winning set can be acknowledged. After the durable state is
  readable, the reconciler records the acknowledgement and releases the job to
  its ordinary retention policy. Long-lived catalog, tile, coverage, object,
  and result readers then use the durable `Complete` manifest and do not depend
  on the execution job still existing. A crash leaves the held job and ready
  manifest recoverable. If cancellation wins the job CAS, no completion token
  exists, the durable manifest cannot become `Complete`, and terminalization
  aborts or quarantines the staged versions without altering the previously
  published raster.
- Object outputs remain at immutable attempt-scoped keys. The stable object is
  a small intent/commit marker: the coordinator creates or advances the intent
  before dispatch using create-if-absent or `If-Match` on its prior ETag, and
  finalizes it with `If-Match` on the ETag issued to that attempt. Advancing an
  intent therefore changes its ETag before the next attempt runs, so a stale
  attempt cannot publish. Only the coordinator may write the stable marker;
  worker credentials permit writes only under the attempt-scoped staging key.
  The reconciler may resolve the immutable artifact named by a committed
  marker, but public/result readers expose it only through a `Complete`
  job-wide output-set manifest. Cross-store catalog registration follows that
  authoritative marker and is idempotently reconciled to the same winner; it
  must not pretend that an object-store and database transaction is atomic. A
  cross-service read of Redis followed by an unconditional copy, marker
  update, or catalog write is not an acceptable fencing protocol.
- Repeating commit with the same current token and idempotency key returns the
  existing result. A different or stale token cannot replace it. Losing
  attempt-scoped artifacts remain uncommitted for policy-driven cleanup.
- Cancellation and terminal failure first abort every uncommitted sink intent
  with a conditional update on its current token and record version. If
  cancellation arrives during `Finalizing`, a claim-independent job-store CAS
  on the expected record version, winning output-set manifest, and
  publication-lease generation records requested status `Cancelled`, changes
  the phase to `Terminalizing`, and advances that lease generation. This CAS
  contends with the final manifest/job success update. A reconciler must re-read
  the phase and lease generation before each sink action and before completing
  the manifest; a sink commit already in flight instead contends with abort on
  the same sink record. Either that commit wins and remains durable but hidden
  by the incomplete manifest, or abort wins and the later commit fails. For
  object output this is an `If-Match` transition of the stable intent marker to
  an aborted state; for PostGIS it is a conditional update in the sink
  database. The durable job does not become `Cancelled` or `Failed` until all
  intents are committed or aborted. While reconciling an unreachable sink, its
  canonical status remains `Running`, its durable `OutputPublicationPhase` is
  `Terminalizing`, and a separate requested terminal status records
  `Cancelled` or `Failed`; no new execution is admitted. Like `Finalizing`,
  `Terminalizing` is excluded from execution heartbeat and timeout expiry as
  well as requeue. Its separate durable publication lease and
  heartbeat/deadline are recovered only by the fenced output reconciler, which
  may finish the terminal transition but never re-executes the job. This is the
  orthogonal phase mapping defined by ADR-0031, not a new
  `ExecutionJobStatus`. Cancellation also stops new work, propagates to the
  selected executor, and cleans uncommitted staging artifacts without deleting
  a previously committed result.

These rules prevent a timeout in one engine from producing a second,
numerically different result or duplicating a partially committed output in
another engine.

### Operator policy and semantic parity

Operators may cap, prefer, deny, or force an engine or placement by workload,
tenant, deployment profile, and resource budget. A force rule constrains
preference and fallback; it never bypasses capability compatibility,
authorization, input/output locality and access, semantic equivalence, or hard
database/local/remote admission and resource budgets. If the forced choice is
unavailable or ineligible under any hard gate, admission fails closed with an
actionable error; Honua does not override the choice or weaken the gate
silently. Database-health admission may promote eligible work from PostGIS to
native execution before the attempt starts or defer it in the queue.

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
