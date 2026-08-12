# ADR-0071: Raster execution boundary — single GDAL-worker engine, PostGIS as serving/storage plane

## Status

Accepted (2026-08); amended 2026-08-11 (see Amendments). This is the
controlling raster engine and placement decision for honua-server#3085 and
honua-server#3086. The dual-engine, database-SLO-aware placement design this
ADR originally adopted is superseded by the single-engine decision recorded on
honua-server#3085 and tracked to this document by honua-server#3167; read the
Amendments section before relying on this document's engine-selection
language.

## Context

Honua has a small number of raster execution locations, and only one of them
runs GP:

- the native-AOT web process can perform bounded, pure-managed COG and Zarr
  reads;
- PostGIS Raster provides data-local serving: bounded reads, persisted
  overviews, materialized tiles, and a registration target for GP outputs —
  including serving-side operations whose database implementation uses GDAL
  internally, which does not make PostGIS a GP execution engine;
- the native GP worker is the raster analysis execution engine: it runs every
  ordinary raster GP job governed by this ADR, either as a local worker pool
  or on a remote batch backend such as AWS Batch, selected by static operator
  configuration (with an optional bounded per-job size-threshold comparison);
- orthomosaic production is the explicit exception governed by ADR-0073: it
  runs only in the dedicated `photogrammetry-worker`, then hands its validated
  raster output to this ADR's registration, serving, and later-analysis path;
- imagery classification, segmentation, and object detection are the explicit
  ADR-0057 managed-inference exception: `imagery.classify` delegates to a
  configured cloud inference provider, then hands any raster result to this
  ADR's typed-artifact and registration path;
  and
- object storage exchanges large inputs, intermediate products, and outputs
  by typed reference.

Earlier decisions describe parts of this boundary. ADR-0029 introduced
PostGIS-backed raster and surface primitives behind the canonical process
model — capability, not an execution mandate. ADR-0038 keeps native
dependencies out of the serving image. ADR-0057 says raster GP uses the GDAL
worker. ADR-0060 describes local and remote execution backends. Read together
they now agree on a single answer for ordinary raster analysis: it executes on
the GDAL worker; PostGIS participates only as a data source and
registration/serving target. ADR-0073 separately controls the qualified
multi-stage photogrammetry engine used to produce an orthomosaic.

An earlier version of this ADR instead proposed a dual-engine design — per-job
selection between PostGIS and GDAL, and between local and remote placement,
driven by a capability/cost registry and live database health. That design is
rejected (2026-08-11; see Amendments) because it created four costs without a
corresponding benefit:

1. It required duplicate executors for every operation implemented in both
   PostGIS and GDAL.
2. It required a cross-engine semantic oracle to prove the two
   implementations agreed, and a planner to keep them from silently
   diverging.
3. It required database raster-load governance (a dedicated managed PostGIS
   worker profile, admission, and health signals) purely to make PostGIS a
   safe execution target — governance a database serving/storage plane does
   not otherwise need.
4. The simplest protection for the primary database's serving SLO is not
   running raster analysis in the database at all, which a capability/cost
   planner could only approximate.

The remaining risk this ADR still resolves is moving large raster payloads
inline through the web heap and durable job store, which defeats worker and
Batch isolation; the reference-based artifact transport below addresses it.

## Decision

Adopt a **single ordinary raster-analysis execution engine**. Except for the
dedicated orthomosaic-production capability controlled by ADR-0073 and the
managed imagery/ML inference lane controlled by ADR-0057, all ordinary raster
GP jobs execute on the isolated native GDAL worker. There is no per-job engine
selection and PostGIS never executes GP analysis.

The exception is narrow: orthomosaic production is a multi-stage
photogrammetry workflow, not a GDAL-worker algorithm, and executes only in the
qualified `photogrammetry-worker`. Its validated output crosses back as a
canonical typed raster artifact. Registration, serving, and any subsequent
raster analysis of that artifact follow this decision. This exception does not
create a second engine for the ordinary raster operations listed below.

The managed-inference exception is equally narrow: classification,
segmentation, and object detection execute through `imagery.classify` on the
configured cloud provider. Any raster output returns as a canonical typed
artifact; its registration, serving, and subsequent ordinary raster analysis
follow this decision. Managed inference is not an alternate engine for the
numerical raster/terrain operations listed below.

The GDAL worker runs in one of two placements — a local worker pool, or a
remote batch backend such as AWS Batch — chosen by **static operator
configuration**, optionally a simple size threshold (for example, "local
under N decoded-work bytes, AWS Batch above it"). Operators configure the
rule ahead of time. When the rule includes a threshold, a bounded admission
router evaluates each job's decoded size against that configured threshold;
it does not evaluate capabilities, live health, or an open-ended cost model.
The resulting placement is recorded on the job for observability.

For this threshold, **decoded size** has one normative meaning. All arithmetic
is checked and occurs before durable submission:

1. A raster surface contributes
   `width × height × band count × ceil(bits per sample / 8)` bytes.
2. The input contribution is the sum of every raster input, not the largest
   input, even when an implementation could read the inputs sequentially.
3. The process definition enumerates every full-grid output or intermediate
   admission surface it can materialize. The router derives each surface's
   post-transform grid, band count, and sample width from the validated process
   request and adds all of them. Scalar or metadata-only results contribute
   zero output bytes.
4. The job's decoded size is the checked sum of the input and admission-surface
   contributions. It is a deterministic conservative routing proxy, not a
   prediction of exact resident memory.

If any required dimension, band count, sample width, or output grid cannot be
bounded, or if the calculation overflows, the job cannot qualify for local
placement: the router selects the configured remote placement or fails closed
when none exists. Worker memory, dimension, scratch, and output limits remain
independent safety gates after placement.

PostGIS Raster is a **serving/storage plane and registration target only**:
bounded, data-resident reads; persisted overviews; materialized tiles; and the
target GP outputs register into through the canonical reference/ingest
contracts. PostGIS participates as a raster data source under the same
reference contracts. It never claims or executes a GP job.

Optimizations within this decision may improve PostGIS data access, serving,
or output registration, but may not execute a raster GP operation in PostGIS.
Any proposal to add PostGIS analysis as a second physical engine requires a
new amendment that explicitly supersedes this single-engine decision.

The canonical process definition remains independent of physical placement.
Protocol adapters and SDKs submit the same process contract regardless of
whether a particular job runs on the local worker pool or a remote batch
backend.

### Plane responsibilities and dependency boundary

| Plane | Responsibility | Raster dependency policy |
| --- | --- | --- |
| Native-AOT web | Protocol adaptation, authorization, validation, metadata-only planning, bounded request execution, job submission, and bounded pure-managed COG/Zarr reads | No GDAL libraries, CLI tools, bindings, or transitive native packages |
| PostGIS | Serving/storage plane: bounded, data-resident raster serving and a registration target for GP outputs | Database-side GDAL is allowed for storage/serving functions only; PostGIS never executes a GP job |
| Local native GP worker | **The** ordinary raster-analysis execution engine for local placement: format conversion and native raster algorithms other than ADR-0073 photogrammetry | GDAL is allowed and isolated from public ingress |
| Remote native backend | The same GDAL worker image, for placements selected by static operator configuration (bursty, high-memory, or high-scratch profiles) | A versioned GDAL worker image runs through `IBatchComputeBackend`, including AWS Batch or another configured backend |
| Dedicated photogrammetry worker | ADR-0073 orthomosaic production only; publishes a validated canonical raster artifact back through the shared handoff | Separately qualified optional image and dependency surface; never hosted by the general GP/GDAL worker |
| Managed imagery inference | ADR-0057 classification, segmentation, and object detection only; publishes any raster result through the shared typed-artifact handoff | Provider-hosted model runtime; never becomes an alternate numerical raster/terrain engine |
| Object storage | Exchange of large immutable inputs, intermediate products, and outputs | Typed references cross process boundaries; payload bytes do not travel in durable job specifications |

The serving image may issue bounded PostGIS raster serving queries as part of
a request. It must not host a local GDAL execution path. Installing GDAL in
the PostGIS service or native worker does not weaken the no-GDAL web-image
invariant.

### Execution envelopes

Honua selects one of three execution envelopes:

1. **Bounded request execution.** Use for metadata, identify/sample, bounded
   tiles and exports, request-sized clip/resample/reprojection served from
   PostGIS, cached statistics, and similarly predictable serving work. Pure
   managed COG/Zarr reads remain limited to their declared bounded-serving
   envelopes. This envelope never runs raster GP algorithms.
2. **Durable local native execution.** The GDAL worker runs on the local
   worker pool, per static operator configuration or a size threshold.
3. **Durable remote native execution.** The GDAL worker runs on AWS Batch or
   another `IBatchComputeBackend`, per the same static configuration, for
   bursty jobs, object-store-local bulk work, large decoded surfaces, or
   high/unpredictable scratch and memory demand.

Every ordinary raster GP operation governed by this ADR — clip/window,
mosaic, reproject/resample, map
algebra, reclassification, spectral indices, statistics, zonal statistics,
terrain derivatives, arbitrary format decode/encode, COG construction,
NetCDF/HDF/GRIB conversion, and large mosaics or warps — executes on the GDAL
worker under envelope 2 or 3. There is no per-operation or per-job choice
between PostGIS and GDAL. When operators configure a decoded-size threshold,
that bounded measurement selects local or remote placement; it never selects
the execution engine.

### Placement and durable record

Placement (local vs. remote backend) is decided once before a job is
submitted. A deployment-wide or workload-specific fixed placement needs no
per-job routing. If operators configure a decoded-size threshold, a bounded
admission router makes the necessary per-job comparison against that static
rule. It never chooses an engine or evaluates capabilities, live database
health, mutable operator policy, or an open-ended cost model at dispatch
time. Authorization and source/output accessibility are still checked before
dispatch, and admission still fails closed if the selected backend or
worker-image contract is unavailable.

Every durable raster job owns an append-only sequence of attempt-scoped
records, kept for auditability and incident reconstruction rather than to
justify a choice between engines: an immutable attempt identifier and
current-attempt fencing token, the placement (local or the configured remote
backend), runtime/worker-image contract version, input residency, cost
estimate, output sink, and any applicable operator override. A same-placement
retry appends a new attempt record; it never updates or replaces a prior one.
The attempt identifier also scopes the executor outcome and staged artifacts
so operators can reconstruct
which attempt produced each side effect. Static process catalog runtime
profiles may declare a default placement, but there is no capability/cost
registry or placement-planner service. A configured threshold router performs
only the bounded comparison described above.

Numeric placement thresholds remain configuration informed by benchmark and
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

- A job does not silently move to a different placement after a mutating
  attempt starts. This includes PostGIS output materialization, object
  publication, catalog registration, and overwrite of an existing output.
- There is no cross-engine fallback: the GDAL worker is the only raster
  execution engine, so a failed pre-execution admission or worker-image gate
  fails the job rather than retrying on an alternate engine.
- Automatic retries stay on the same worker placement and occur only for
  classified retryable failures. The first admitted attempt pins the worker
  image by immutable digest and pins the raster execution-contract version;
  every automatic retry must reuse both pins even during a worker rollout. A
  version change requires an explicitly approved replan/new job and is never
  an automatic retry. A retry is a newly planned attempt, not an implicit
  continuation: its attempt record is appended rather than replacing the
  failed attempt's record, the prior attempt remains auditable, its staged
  outputs are cleaned up or retained by policy, and the retry reuses a stable
  idempotency key.
- Every job with declared outputs owns a durable output-set manifest keyed by
  job. Before dispatch it records the complete required logical-output set and
  references each prepared sink intent. The attempt-fenced transition to
  `Finalizing` conditionally freezes that manifest to one winning attempt. A
  later attempt may replace a prepared manifest only before `Finalizing` and
  only after every required uncommitted sink intent has been advanced to its
  fence; failure to advance any member prevents dispatch. Once frozen, partial
  sink success is publication-recovery work for the same attempt, never a
  reason to execute another attempt.
- Every output sink owns both a durable attempt commit record, keyed by job and
  logical output, and a durable destination publication record, keyed by the
  normalized stable raster, catalog, or object destination. The attempt record
  contains the current attempt identifier, fencing token, idempotency key,
  immutable artifact locator, and private commit state. The destination record
  contains the currently published completion token and monotonically changing
  target version, plus at most one pending reservation. Before dispatch, the
  coordinator reads the target version (or records that the destination is
  absent), persists that expected version and destination identity in both the
  output-set manifest and attempt intent, and gives the executor the resulting
  attempt-record version. An overwrite without an explicit expected target
  version is invalid. A new attempt for the same job may advance an uncommitted
  attempt intent but may not replace a committed one. Attempt finalization is a
  sink-local compare-and-set against both its token and record version;
  checking the coordinator's Redis job state before writing is only an
  optimization and is neither the attempt fence nor the destination fence.
- PostGIS materialization is first written to an attempt-scoped staging or
  versioned relation. Its catalog row carries the output-set manifest identity
  and an explicit publication-candidate state. Committing a member's sink-local
  record, staged relation, and candidate registration occurs in one database
  transaction. A unique job/output key plus a conditional update on the
  expected token and record version makes a competing or stale attempt fail,
  but that member commit alone never replaces the currently published raster.
  The separate normalized-destination row is unique across jobs; reserving it
  conditionally updates its expected target version and, when the winning set
  contains only PostGIS destinations, all such reservations occur in the same
  database transaction.
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
  object-only set). Before the orchestration visibility decision, the
  coordinator must reserve every normalized destination for the same immutable
  completion token using a sink-local compare-and-set against the expected
  target version captured before dispatch. Create-only destinations use an
  equivalent create-if-absent condition. A version conflict fails publication;
  it never degrades to last-writer-wins or silently refreshes the expectation.
  Reservations acquired before a later member conflicts remain private and are
  conditionally released or recovered by the fenced reconciler. Only after all
  destination reservations name the same completion token may the coordinator
  use the final job-store CAS to change the live manifest to `Complete`, project
  the full reference set, and mark the job `Succeeded`. This CAS is the
  orchestration visibility decision and contends with cancellation on the same
  job record. During durable-manifest reconciliation, object/result readers and
  PostGIS catalog readers require that completed live decision, the matching
  promotion-ready durable manifest, and the destination publication record
  naming that completion token. Consequently two jobs targeting the same
  raster or object cannot both become the visible winner even though their
  attempt records are independently valid. The protocol exposes the
  already-ready set together without claiming a cross-store transaction.
- A completed visibility decision is never retained solely in the expiring job
  record. The success CAS places the job under a retention hold instead of the
  normal Redis TTL. Using its completion token, the output reconciler
  first finalizes every destination reservation for that exact winning set.
  For PostGIS destinations, one catalog-database transaction conditionally
  changes each normalized destination row from a pending reservation naming
  the completion token and expected previous target version to a current
  published pointer naming that token, clears the reservation, and changes the
  durable `PromotionReady` manifest to `Complete`. All predicates must match or
  the transaction changes nothing. Repeating the transaction is idempotent
  when every current pointer already names the token and no competing
  reservation exists. For a mixed-sink set, each object marker first advances
  to the completion token but remains in a token-owned committed hold that
  rejects another reservation while the durable manifest still hides the set;
  the PostGIS transaction then finalizes its destinations and the manifest
  together, after which the reconciler conditionally releases those object
  holds. An object-only set similarly changes its durable object manifest to
  `Complete` only after every stable marker names the token under a committed
  hold, then releases the holds. Thus successful acknowledgement never leaves
  a PostGIS destination reserved indefinitely or keeps its stable
  pointer on the previous output. Only that exact winning set can be
  acknowledged. After the durable state is readable, the reconciler records
  the acknowledgement and releases the job to its ordinary retention policy.
  Long-lived catalog, tile, coverage, object, and result readers then use the
  durable `Complete` manifest and do not depend on the execution job still
  existing. A crash leaves the held job, ready manifest, and token-owned
  reservations recoverable. If cancellation wins the job CAS, no completion
  token exists, the durable manifest cannot become `Complete`, and
  terminalization conditionally releases its reservations and aborts or
  quarantines the staged versions without altering the previously published
  raster.
- Object outputs remain at immutable attempt-scoped keys. The stable object is
  the destination publication record described above, represented by a small
  intent/commit marker at the stable key. The coordinator snapshots its current
  ETag as the expected target version before dispatch, reserves it for the
  winning completion token with create-if-absent or `If-Match`, and advances
  that reservation to a token-owned committed hold with `If-Match` on the
  reservation ETag. The hold is cleared conditionally only after the durable
  output-set manifest is `Complete`; until then another reservation fails.
  Advancing or releasing a reservation changes its ETag before another job or
  attempt can publish, so both a stale attempt and a competing job fail the
  destination CAS. Only the coordinator may write the stable marker;
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

These rules prevent a timeout or retry from producing a second, numerically
different result or duplicating a partially committed output.

### Operator configuration and semantic pinning

Operators configure placement statically, by workload, tenant, deployment
profile, and resource budget — for example, "local for jobs under N decoded-
work bytes, AWS Batch above it." The placement rule is set ahead of time and
applies uniformly. A fixed rule applies directly; a threshold rule is
evaluated for each job by the bounded admission router and the result is
recorded for observability. This router is not the rejected dynamic planner:
it has no capability, cost, or health policy to optimize. If the selected
backend is unavailable, admission fails closed with an actionable error
rather than silently retrying on a different engine or weakening the gate.

Raster semantics — NoData, grid origin and alignment, extent, CRS, pixel type
and rounding, resampling, edge behavior, and output registration — are defined
once, by the GDAL worker's canonical behavior, and pinned by golden-output
fixtures. There is no second engine's semantics to reconcile against.
PostGIS-side optimizations under this decision are limited to data access,
serving, and registration; raster GP analysis remains on the GDAL worker.

### Raster and 3D boundary

- DEM-derived elevation, terrain tiles, hillshade and other raster surface
  derivatives remain raster capabilities and follow this execution decision.
- Orthomosaic production is the explicit ADR-0073 exception: its durable job
  executes only in the dedicated `photogrammetry-worker`, never the general
  GP/GDAL worker. Its registered output is a canonical raster artifact;
  subsequent serving and raster analysis follow this decision.
- Imagery classification, segmentation, and object detection are the explicit
  ADR-0057 exception: `imagery.classify` delegates to managed cloud inference.
  Any raster result crosses back through the typed-artifact handoff; subsequent
  serving and ordinary raster analysis follow this decision.
- Point clouds (LAS/LAZ/COPC), meshes, scene-layer generation, stereo feature
  matching, and general 3D reconstruction are not raster engines. They use
  separate native capability families and worker dependencies. If they produce
  a DEM or orthomosaic, the handoff occurs through the typed raster artifact
  and registration boundary.

This keeps the raster roadmap focused while allowing issue #2442 and
ADR-0065's advanced imagery boundary to evolve independently.

## Consequences

### Positive

- The public web image remains small, native-AOT, and free of GDAL.
- One raster execution engine means one semantic surface: NoData, grid, CRS,
  pixel type, resampling, and output registration are defined once, by the
  GDAL worker, and pinned by golden-output fixtures instead of reconciled
  across two implementations.
- Static placement configuration is simple to reason about, audit, and change
  without a planner subsystem; local vs. Batch stays explainable straight
  from operator configuration and the per-job placement record.
- Typed references remove large raster copies from the web heap and durable
  job store.
- PostGIS keeps its role as a fast, data-resident serving and storage plane
  without carrying raster GP execution risk into the primary database's SLO.

### Negative

- Every raster GP job pays worker dispatch cost, and remote placement adds
  queue/staging/network cost; there is no PostGIS in-database shortcut for
  data-resident GP work under this decision.
- Operators still need to choose and validate a placement threshold per
  deployment profile, even though it is static configuration rather than a
  runtime decision.
- Local and remote worker images must stay in version lockstep with the
  golden-output fixtures that pin raster semantics; drift there is now the
  primary raster-correctness risk instead of cross-engine parity.

## Alternatives considered

- **PostGIS as a raster GP execution engine, selected per job alongside
  GDAL.** Rejected 2026-08-11. The original design chose PostGIS vs. GDAL per
  job from a capability/cost registry informed by input residency, predicted
  work, and live database health. That required duplicate PostGIS executors,
  a cross-engine semantic oracle, database raster-load governance, and a
  placement-planner subsystem whose only job was managing a choice Honua does
  not need to offer. PostGIS remains the serving/storage plane; it never
  executes GP analysis. Data-access, serving, and registration optimizations
  do not weaken that boundary.
- **GDAL in the web image.** Rejected because it violates the native-AOT,
  cold-start, memory, and dependency boundary.
- **Per-job dynamic local-vs-Batch placement from a cost/health model.**
  Rejected 2026-08-11 in favor of static operator configuration (optionally a
  size threshold). Trunk reality was already effectively single-placement per
  deployment profile; a dynamic model added a planner for a decision
  operators can make once, ahead of time.
- **Always dispatch to remote Batch regardless of job size.** Rejected
  because small local or data-resident work would pay avoidable queue,
  staging, and cloud costs; static configuration may include a simple size
  threshold instead.
- **Automatic engine or placement fallback after failure.** Rejected because
  it hides semantic changes and cannot safely reason about partial mutation;
  retries stay on the same worker and placement (see Failure, fallback,
  retry, and idempotency above).

## Implementation and verification

The implementation plan and exit criteria live in parent epic #3085. This ADR
owns policy; the following tickets own contracts and implementation:

- serving-image and security invariants: #3087 and #3068;
- source/output reference contracts and removal of inline COG transport: #3088,
  #3089, and #3090;
- durable ingest and storage policy: #3098 and #3099;
- synchronous budgets and durable promotion: #3101;
- bounded COG/Zarr paths and raster function chains: #3102, #3103, and #2438;
- execution telemetry (placement, artifact, and Batch-cost observability) and
  orthomosaic handoff: #3104 and #3105.

The capability/cost-registry, placement-planner, PostGIS-executor, and
cross-engine-oracle tickets this ADR originally cited (#3091, #3092, #3093,
#3094, #3095, #3096, #3097, and #3100) are closed as not planned under the
2026-08-11 single-engine decision; see Amendments.

Verification must prove that a large registered COG can execute through the
local worker pool or an eligible AWS Batch path without raster bytes entering
the web process or Redis; web and worker admission are independent;
golden-output fixtures pin the GDAL worker's raster semantics and catch silent
drift; static placement is recorded and observable per job; and the canonical
serving image contains no GDAL bytes.

## Amendments

### 2026-08-11 — Single-engine decision (honua-io/honua-server#3085, #3167)

The dual-engine design this ADR originally adopted — "PostGIS-first for
capability and data locality, database-SLO-aware for placement," with per-job
engine selection (PostGIS vs. GDAL) and placement (local vs. AWS Batch) chosen
by a capability/cost-registry-driven planner — is rejected. Decision recorded
on the parent epic (honua-server#3085):

> The original epic proposed a dual-engine design — per-job engine selection
> (PostGIS vs GDAL) and placement (local vs Batch) from a capability/cost
> registry, input residency, predicted work, live database health, and
> operator policy. That conditional-execution complexity is explicitly
> rejected: it required duplicate PostGIS executors, a cross-engine semantic
> oracle, database raster-load governance, and a planner subsystem whose only
> job was managing a choice we do not need to offer. Trunk reality was already
> effectively single-engine (static per-process GDAL profiles; PostGIS GP
> primitives unconsumed), and the simplest database-SLO protection is never
> running analysis in the database.

Effective decision, replacing everything this document previously said about
dynamic engine selection, database-SLO-aware placement, a capability/cost
registry, or cross-engine semantic equivalence:

- **All ordinary non-ML raster analysis GP executes on the isolated GDAL
  worker.** ADR-0073's dedicated photogrammetry runtime and ADR-0057's managed
  imagery/ML inference lane are the explicit specialized-runtime exceptions;
  there is no per-job engine selection, and PostGIS never executes GP analysis.
- **Local vs. AWS Batch placement is static operator configuration**
  (optionally a simple size threshold). A bounded admission router evaluates
  a configured threshold per job and records the result; it is not the
  rejected capability/cost/health planner.
- **PostGIS Raster is a serving/storage plane and registration target only**:
  bounded, data-resident reads; persisted overviews; materialized tiles; and
  an ingest/registration target for GP outputs.
- **Raster semantics are defined once**, by the GDAL worker's canonical
  behavior, and pinned by golden-output fixtures — there is no cross-engine
  oracle to reconcile against.
- **PostGIS optimizations remain serving/storage-only.** Executing GP analysis
  in PostGIS would introduce a second physical engine and requires a future
  amendment that supersedes this decision.

The following planner-family tickets, originally cited by this ADR's
"Implementation and verification" section, are closed as not planned under
this decision: #3091 (capability/raster-cost registry), #3092 (dynamic
engine/placement planner), #3093 (per-job local vs. Batch selection), #3094
(dedicated managed PostGIS raster worker profile), #3095 (surface/zonal
PostGIS primitives wired into GP), #3096 (PostGIS executors for overlapping
operations), #3097 (isolate/govern asynchronous database raster work), and
#3100 (cross-engine semantic oracle).

This amendment also corrects per-job engine-selection language that this
ADR's original landing (honua-io/honua-server#3106) introduced into
ADR-0029, ADR-0038, ADR-0057, and ADR-0060; those ADRs' own status/context
notes have been updated to match. ADR-0031's amendments from the same PR
describe engine-agnostic output-publication fencing (attempt records,
compare-and-set commits, output-set manifests) and did not require
correction — that model is unaffected by how many engines exist to produce
an attempt's output.

The durable output-publication fencing model defined above (attempt records,
compare-and-set commits, output-set manifests, cancellation/terminalization)
remains accurate: it governs how a single execution attempt publishes its
result safely, independent of engine count.

## References

- [ADR-0029: Geoprocess Canonical Model Mappings](0029-geoprocess-canonical-model-mappings.md)
- [ADR-0038: GeoETL Pipeline Architecture and Runtime Boundary](0038-geoetl-pipeline-architecture-and-runtime-boundary.md)
- [ADR-0039: Cloud-Optimized HDF5 / NetCDF4 Reader Strategy](0039-cloud-optimized-hdf-netcdf-reader-strategy.md)
- [ADR-0057: Geoprocessing Capability Boundaries](0057-geoprocessing-capability-boundaries.md)
- [ADR-0060: Two-Plane Operability Architecture](0060-two-plane-operability-architecture.md)
- [ADR-0065: ImageServer Photogrammetric Analytics](0065-imageserver-photogrammetric-analytics.md)
- honua-io/honua-server#3085 - raster strategy epic; single-engine decision of record
- honua-io/honua-server#3086 - original execution-boundary decision (amended above)
- honua-io/honua-server#3167 - this amendment
