# ADR-0038: GeoETL pipeline architecture and runtime boundary

## Status

Accepted

## Context

`honua-io/honua-server#361` is the GeoETL epic — repeatable, scheduled,
multi-source spatial extract-transform-load pipelines for Honua. The
strategy spike `#682` (`docs/contributor/geoetl-spike.md`) settled the
positioning question: pipeline-as-code for the API-first audience, not a
desktop workbench, and not a Spark-scale lakehouse engine. The roadmap
(`docs/contributor/geoetl-roadmap.md`) decomposes the epic into
reviewable child tickets.

This ADR captures the architectural decisions that bind every GeoETL
child ticket. They cannot be re-litigated per child ticket without
breaking the decomposition.

Three constraints dominate:

1. The default `honua-server` ECS / serverless image must stay lean.
   Cold-start time and memory ceiling are budgeted around Minimal APIs,
   PostgreSQL, and managed format readers. Heavy native binaries
   (GDAL/OGR, PROJ, GEOS) cannot enter that image without breaking the
   serving SLO.
2. The job orchestration substrate (`#681`, ADR-0031) already provides
   claim, lease, heartbeat, retry, cancellation, and progress
   semantics. GeoETL must consume those semantics, not invent
   parallel ones.
3. Baseline delivery cannot require AWS Batch, Azure Batch, Kubernetes
   Jobs, or any other cloud batch backend. Operators must be able to
   run GeoETL with only Honua + PostgreSQL.

A handful of secondary forces shape the design:

- The existing format readers in `Honua.Core/Features/Import/Services/`
  (GeoJSON, Shapefile, GeoPackage, KML, GPX, CSV, FlatGeobuf,
  GeoParquet) cover most Phase 1 sources with no new native deps.
  Reimplementing them inside GeoETL would violate DRY and double the
  surface to test.
- The geoprocessing admission evaluator (`ExecutionAdmissionEvaluator`)
  is the canonical edition gate. Inventing a parallel gate for
  pipelines would fragment the contract.
- Heavy spatial transforms (large-scale reprojection, `ST_Buffer`,
  spatial joins) already have a mature engine in PostGIS. GeoETL should
  delegate to PostGIS for those, not reproduce them in process.

## Decision

GeoETL ships as a vertical slice on top of the `#681` substrate, with a
hard runtime boundary between the lean API server and a dedicated
worker profile. The pipeline contract, definition format, edition gate,
and rollback semantics are decided as below and inherited by every
child ticket.

### Pipeline contract

- **Shape**: `PipelineDefinition → []PipelineStage`, where stages are
  Source → []Transform → Sink.
- **Definition format**: JSON, source-generated. YAML is accepted only
  through the SDK / CLI, which normalize to JSON before persistence.
  Server-side YAML parsing is rejected to keep the AOT/trim surface
  lean.
- **Versioning**: every successful `PUT` increments the definition's
  `Version`. Older versions remain readable for audit and rollback.
- **Schema version**: `schema_version` lives on the definition root from
  Child Ticket A onward. Migration helpers run at deserialize time so
  stored definitions remain readable across format evolutions.
- **Stage typing**: `ConnectorConfig` and `TransformConfig` are
  discriminated unions backed by source-generated JSON polymorphism.
  No reflection-based serialization.
- **Stage-chain validation**: a stage's output schema must satisfy the
  next stage's input schema. The validator runs at CRUD time and at
  pre-execution. Generic REST/JSON sources with dynamic schemas use a
  documented permissive passthrough mode.

### Substrate consumption

- GeoETL is a job kind on top of `IJobQueue`. It uses the existing
  `ExecutionJobKind.ExtractTransformLoad` value already reserved by
  ADR-0031.
- GeoETL **does not** introduce a queue, a heartbeat protocol, a
  cancellation channel, a retry policy, or a progress store. All of
  those flow through `IJobQueue`, `IExecutionJobStore`,
  `IExecutionLogStore`, and `IDistributedProgressStore`.
- Cancellation propagation follows ADR-0031 exactly: API-side
  cancellation goes through the local notifier, the durable
  `CancellationRequestedAt` signal, and the reconciler's terminal state
  guard. GeoETL passes the worker-side `CancellationToken` through every
  connector, transform, and sink; it never owns the terminal state
  transition.
- Retry uses the substrate's `JobRetryPolicy.ShouldRetry(attemptCount)`
  (job-level, attempt-count driven). The current substrate
  `JobExecutionResult` exposes only `Status`, `ErrorMessage`, and
  `Warnings` — there is no executor-result flag to mark a failure as
  fatal vs. retryable, and `ShouldRetry` does not inspect failure
  context. Phase 1 GeoETL therefore relies on the job-level retry
  policy uniformly: every `Failed` result is requeued until the
  attempt budget is exhausted, regardless of whether the underlying
  cause was auth, transient network, or unrecoverable. Row-level data
  errors never throw — they route to the quarantine sink and do not
  consume retry attempts. Per-stage fatal vs. retryable classification
  (auth failure → fatal, transient 5xx → retryable) is documented as a
  Phase 2 enhancement requiring a `JobExecutionResult` extension; it
  is **not** a launch requirement and is listed under
  [Re-evaluation triggers](#re-evaluation-triggers).

### Runtime boundary — two images, one substrate

```
honua-server (default)             honua-worker-etl (heavyweight, post-F)
  ┌─────────────────────────┐        ┌──────────────────────────┐
  │ Minimal APIs            │        │ honua-server base        │
  │ Pipeline CRUD           │        │   + GDAL                 │
  │ Submission endpoints    │        │   + PROJ                 │
  │ Phase 1 connectors      │        │   + GEOS                 │
  │ Dry-run preview         │        │   + Phase 2 connectors   │
  │ PipelineExecutionBg     │        │ PipelineExecutionBg      │
  │   (managed profile)     │        │   (native profile)       │
  │   IJobQueue ────────────┼────────┼── claims                 │
  │   AcceptedKinds = {ETL} │        │   AcceptedKinds = {ETL}  │
  │   ClaimFilter:          │        │   ClaimFilter:           │
  │     RuntimeProfile=     │        │     RuntimeProfile=      │
  │       Managed           │        │       Native             │
  └─────────────────────────┘        └──────────────────────────┘
              │                                    │
              └──────── PostgreSQL (shared) ───────┘
```

The substrate today filters claims only by `ExecutionJobKind`
(`IJobQueue.TryClaimAsync(workerId, IReadOnlySet<ExecutionJobKind>)`,
implemented by `RedisJobQueue` against `job.Spec.Kind`). Two
`IJobExecutor` instances registered for the same kind would race for
every claim; a managed-profile executor that returned `Failed` on a
Phase 2 job would simply re-enter the queue at the same kind and the
managed executor could re-claim it, exhausting the retry budget
instead of routing to the native worker. Profile-aware routing
therefore requires a small substrate extension and is bundled into
Child Ticket F (see roadmap § Child Ticket F).

The two-stage rollout below makes the contract honest against the
substrate at every step:

**Phase 1 (pre-Child-Ticket-F, single-image baseline).** Only
`honua-server` registers an `IJobExecutor` for
`ExecutionJobKind.ExtractTransformLoad`. Its
`AcceptedKinds = { ExtractTransformLoad }` is therefore the sole
claimer. The managed-profile executor runs only Phase 1 connectors —
no GDAL/PROJ/GEOS. CRUD remains edition-agnostic and stores any
pipeline definition, including those that reference a
`RequiresNativeGeo` connector, with a CRUD-time advisory warning so
operators may stage Phase 2 pipelines ahead of a worker rollout.
Every path that creates an `ExtractTransformLoad` job (manual
trigger, scheduler enqueue, dry-run submission) refuses Phase 2
pipelines with a clear "native-profile worker not registered" error,
since no worker profile that can satisfy them exists. Operators
running `honua-server` + PostgreSQL can author and execute every
Phase 1 pipeline; Phase 2 connectors are simply unenqueueable until
F lands and the operator opts into the worker image. The
"Honua + PostgreSQL only" baseline is preserved.

**Phase 2 (post-Child-Ticket-F, two images).** F introduces a
`RuntimeProfile`-aware claim filter on `IJobQueue.TryClaimAsync`. The
`Spec.RuntimeProfile` field already exists on `ExecutionJobSpec`
(reserved by ADR-0031 for "specialized worker or runtime profile" but
not previously used as a claim filter); F extends the substrate's
claim filter to honor it. After F:

- `honua-server`'s managed-profile executor claims only jobs with
  `Spec.RuntimeProfile = "managed"`.
- `honua-worker-etl`'s native-profile executor claims only jobs with
  `Spec.RuntimeProfile = "native"`.
- Pipeline submission stamps `Spec.RuntimeProfile` based on the
  connector set: `"managed"` when every connector is Phase 1,
  `"native"` when any connector is `RequiresNativeGeo`.
- The substrate's atomic claim still guarantees exactly one worker
  executes each job; the new filter ensures the *right* worker is the
  only candidate, so the managed executor never claims a Phase 2 job.

This means there is no "defense-in-depth retry-reroute" path — there
is no need for one once the claim filter exists, and there is no
mechanism for one before it does. The earlier framing was wrong
against the substrate; this section corrects it.

Implementation notes for the substrate extension (sized to fit inside
Child Ticket F):

- Extend `IJobQueue.TryClaimAsync` with an optional
  `IReadOnlySet<string>? acceptedRuntimeProfiles` parameter (or an
  equivalent capability filter object). Null preserves today's
  behavior (claim any profile) so non-ETL kinds remain unaffected.
- Update `RedisJobQueue` to honor the filter when set
  (`job.Spec.RuntimeProfile is null || acceptedRuntimeProfiles
  contains it`).
- Have `JobExecutionService` derive its accepted-profile set from a
  new optional `IJobExecutor.AcceptedRuntimeProfiles` (default null
  for non-ETL executors so they continue to claim regardless of
  profile).
- The ETL executors implement the new property; non-ETL executors do
  not, and their behavior is unchanged.

This is a strictly additive substrate change with a null-default
backward compatibility path. It does not mutate any non-ETL job
kind's claim semantics.

- **Default image (`honua-server`)** stays lean. No GDAL, no PROJ, no
  GEOS native libraries. It registers a **managed-profile** ETL
  executor that runs Phase 1 connectors — pure-managed wrappers over
  the existing `Honua.Core/Features/Import/Services/` readers.
- **GeoETL worker image (`honua-worker-etl`)** is built from a
  separate `Dockerfile` in the `honua-devops` repository. It layers
  GDAL, PROJ, and GEOS on top of the lean base image and runs the
  `PipelineExecutionBackgroundService` with the native-profile
  executor (`AcceptedKinds = { ExtractTransformLoad }`,
  `AcceptedRuntimeProfiles = { "native" }` once F lands). It is not
  reachable from the public ingress.
- **Capability detection** runs at three layers, mirroring the
  edition-gate pattern (CRUD stores; submission gates):
  - *CRUD validation (advisory only)* surfaces a
    `connector_availability` warning when a pipeline references a
    `RequiresNativeGeo` connector and no native-profile worker is
    registered as available. **CRUD never refuses to store the
    definition** — operators may stage Phase 2 pipelines ahead of
    deploying `honua-worker-etl`, exactly as edition-gated
    definitions may be authored ahead of an edition upgrade. The
    native-worker registration signal comes from F.
  - *Execution-submission gate* re-runs capability detection on
    every path that creates an `ExtractTransformLoad` job (manual
    trigger, scheduler enqueue, dry-run submission). If no
    registered profile can satisfy the connector set, submission is
    refused with a descriptive error and no job is enqueued.
  - *Claim filter* (post-F) is the substrate-level guard that
    prevents the managed-profile executor from ever claiming a
    `RuntimeProfile = "native"` job, regardless of upstream gating.
  Connector factories declare which profile they need (`Managed` vs
  `RequiresNativeGeo`). The pipeline CRUD surface stores the
  definition; execution submission stamps `Spec.RuntimeProfile` and
  the substrate routes by claim filter.
- **Dry runs** follow the same routing — a dry run that references a
  `RequiresNativeGeo` connector requires the native-profile worker
  for the same reason a real run does (the source connector still
  executes; only the sink is replaced with the null preview).
  Dry-run submission is itself an `ExtractTransformLoad` job, so it
  honors the same execution-submission gate and claim-filter guards.
- **CI image scan** asserts that GDAL bytes do not appear in the
  default `honua-server` image artifact. The scan is wired in the
  child ticket that introduces `honua-worker-etl` (Child Ticket F) and
  is merge-blocking from that point on.

### Baseline runtime requirements

The baseline GeoETL deployment requires:

- Honua server image
- PostgreSQL with PostGIS

Specifically **not required** for baseline:

- AWS Batch
- Azure Batch
- Kubernetes Jobs
- Apache Sedona / Spark
- Any cloud queue beyond what `IJobQueue` already supports
- The `honua-worker-etl` image — only needed when an operator opts into
  Phase 2 connectors

External executors (Kubernetes Jobs, AWS Batch, Sedona) are pluggable
add-ons under Child Ticket J, never delivery prerequisites.

### Edition enforcement

Pipeline CRUD endpoints are edition-agnostic. Operators may author and
store pipeline definitions on any edition tier so that an upgrade does
not require re-authoring. Edition enforcement happens at job
submission via `ExecutionAdmissionEvaluator`, the same gate that
governs geoprocessing job submission. The gate decides:

| Tier | What is allowed |
|---|---|
| Community | One-shot file import remains intact. No scheduled pipelines. |
| Pro | Scheduled pipelines, full transform library, Phase 1 + 2 connectors, versioning, rollback, dry run. |
| Enterprise | Streaming sources, sandboxed custom transform plugins, cross-tenant pipelines, pluggable distributed executors. |

### Rollback strategy

- **Phase 1**: soft-delete batch ID. Honua-layer sink writes tag every
  row with `pipeline_batch_id`. A failed run issues a targeted
  `DELETE WHERE pipeline_batch_id = ...`. Consistent with the existing
  import path.
- **Phase 2 (optional, deferred)**: staging-table swap. The sink writes
  to a staging table, and on success swaps the staging table into the
  live name in a single transaction. This buys atomicity for very
  large loads at the cost of double storage during the run.

The Pro tier guarantee is the soft-delete batch ID. The staging-table
escalation lands when an operator workload requires it.

### Row-level errors

Row-level errors do not throw. The connector or transform writes the
rejected row + reason to a quarantine sink (companion GeoJSON or CSV
artifact) and increments an error counter. The execution summary
includes the error count, a sample of rejected rows, and a
quarantine-artifact reference. Run-aborting errors (auth failure,
unreachable host, schema-incompatible source) return `Failed` from the
executor and mark the job `Failed` via the substrate. The substrate
then applies the job-level retry policy uniformly — see
[Substrate consumption](#substrate-consumption) for why auth and
transient failures both route through the same retry path until the
`JobExecutionResult.FailureKind` extension lands.

This is the same shape as the existing import path's row-level error
capture, so operators see a consistent reporting surface across import
and pipelines.

### Telemetry

GeoETL emits structured logs through the existing logging conventions
— no new telemetry libraries. Canonical event names:

- `pipeline.started`
- `pipeline.stage.completed`
- `pipeline.stage.failed`
- `pipeline.completed`
- `pipeline.failed`

Each event carries pipeline ID, definition version, stage index,
feature counts (in / out / quarantined), and the substrate's
`ExecutionJobRecord.Id`. The `pipeline.*` family complements the
substrate-level telemetry rather than replacing it.

### Observability of dry runs

Dry runs execute every stage but route the sink to a null preview that
returns row counts, schema diff, and a quarantine sample. Dry run is a
Pro capability and runs through the same execution path so that what
the dry run validates is what production would execute.

## Consequences

### Easier

- **Reviewable child tickets.** Each child ticket binds against a
  decided contract. Child Ticket A's CRUD shape is fixed; B/C/D each
  ship one factory worth of code; E ships the executor wired into the
  substrate; F ships the worker image. None has to re-decide pipeline
  semantics.
- **Lean serving image.** The default `honua-server` image stays the
  same shape it has today. Operators who never need Phase 2 connectors
  never deploy a second image.
- **Substrate reuse.** Cancel, retry, heartbeat, progress, and history
  all flow through the existing surfaces. GeoETL is one new vertical
  slice and one new job kind, not a new orchestration system.
- **DRY format readers.** Phase 1 connectors are thin wrappers — no
  duplicated parsing code. The fixtures the import path already uses
  exercise the connectors for free.
- **Portable pipelines.** Pipeline definitions are identical regardless
  of the executor backend. A future Sedona executor under Child Ticket
  J does not require authored pipelines to change.

### More difficult

- **Two images to operate.** Phase 2 connectors require operators to
  deploy `honua-worker-etl` alongside `honua-server`. The roadmap
  documents this as opt-in: Phase 1 alone runs on a single image.
- **Three-layer capability gating.** CRUD validation surfaces a
  `connector_availability` advisory but never refuses to store, so
  authors and GitOps operators may stage Phase 2 pipelines ahead of
  deploying `honua-worker-etl`. The execution-submission gate refuses
  to enqueue any `ExtractTransformLoad` job (manual trigger,
  scheduler, or dry run) when no registered worker profile can
  satisfy the connector set. The substrate-level claim filter (post-F)
  is the load-bearing guarantee that prevents the managed executor
  from ever claiming a Phase 2 job once the worker image registers a
  competing executor. The CRUD-time advisory is an authoring
  ergonomics convenience; the execution-submission gate keeps
  scheduled jobs from queueing against an unavailable profile; the
  claim filter is the correctness invariant. Operator runbooks
  (Child Ticket F) must explain all three layers so an operator who
  removes the worker image understands why scheduled Phase 2 jobs
  fail to enqueue the next time submission runs.
- **Substrate extension required for two-image rollout.** Child
  Ticket F bundles the `RuntimeProfile` claim filter on `IJobQueue`.
  Until F lands, only the managed-profile executor is registered and
  Phase 2 connectors are unavailable. There is no path that runs the
  worker image alongside `honua-server` without the claim filter — the
  ADR's correctness guarantee is conditional on F shipping the filter
  before the worker image registers a competing executor.
- **YAML lives in the SDK.** Authors who want YAML must use the SDK or
  `honua-cli`. Raw `curl` users send JSON. SDK teams own a
  YAML-to-JSON normalizer.
- **Discriminated-union evolution.** Adding a new connector or
  transform requires a `schema_version` bump and a migration helper.
  This is the cost of source-generated, AOT-safe deserialization, and
  it is the right tradeoff for the trim/cold-start budget.

### Re-evaluation triggers

This ADR should be revisited if any of the following occur:

- The serving image cold-start budget changes such that a single-image
  deployment with GDAL becomes acceptable. The two-image story exists
  because GDAL is heavy, not because two images are inherently better.
- A pluggable executor backend (Child Ticket J) demonstrates that the
  pipeline contract is not portable across executors. The portability
  guarantee is a load-bearing assumption of this ADR.
- The substrate changes shape (heartbeat semantics, claim semantics,
  progress surface). GeoETL should follow ADR-0031 changes; if it
  cannot, this ADR has to recapture the gap.
- Per-stage failure classification (auth → fatal, transient → retryable)
  becomes a launch requirement before a substrate-level
  `JobExecutionResult` extension lands. Today the substrate retries by
  attempt count only; if customer feedback or the execution engine
  child ticket needs to short-circuit auth failures or surface
  retryability hints to the operator, this ADR's
  [Substrate consumption](#substrate-consumption) section has to be
  paired with a `JobExecutionResult.FailureKind` (or equivalent)
  contract change before the per-stage classification is documented as
  supported behavior.
- Operator feedback indicates that the soft-delete-batch-ID rollback
  is insufficient before the staging-table escalation lands. At that
  point the rollback strategy section needs an update; the rest of the
  ADR is unaffected.

A re-evaluation publishes a follow-on ADR rather than editing this one
in place, so the decision history stays auditable.

## References

- `honua-io/honua-server#361` — GeoETL epic.
- `honua-io/honua-server#681` — durable worker / job orchestration
  substrate (merged).
- `honua-io/honua-server#682` — GeoETL competitor evaluation and
  product strategy spike.
- `honua-io/honua-server#316` — CDC event bus (event-trigger
  coordination).
- `honua-io/honua-server#351` — GitOps change management (pipeline-as-code
  coordination).
- `honua-io/honua-server#374` — enrichment library coordination.
- ADR-0024 — Open-core edition model.
- ADR-0025 — Multi-provider operation architecture.
- ADR-0029 — Geoprocess canonical model mappings.
- ADR-0031 — Durable job orchestration substrate.
- ADR-0034 — GDAL/OGR honua driver delivery strategy.
- `docs/contributor/geoetl-roadmap.md` — companion roadmap and child
  ticket decomposition.
- `docs/contributor/geoetl-spike.md` — strategy spike from `#682`.
