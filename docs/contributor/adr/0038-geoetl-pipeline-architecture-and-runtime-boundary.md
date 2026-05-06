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
- Retry classification is per-stage. Auth failure on a source is fatal;
  transient HTTP 5xx is retryable; row-level data errors do not throw at
  all — they route to the quarantine sink.

### Runtime boundary — two images, one substrate

```
honua-server (default)             honua-worker-etl (heavyweight)
  ┌─────────────────────────┐        ┌─────────────────────────┐
  │ Minimal APIs            │        │ honua-server base       │
  │ Pipeline CRUD           │        │   + GDAL                │
  │ Submission endpoints    │        │   + PROJ                │
  │ Phase 1 connectors      │        │   + GEOS                │
  │ Dry-run preview         │        │   + Phase 2 connectors  │
  │                         │        │ PipelineExecutionBg     │
  │   IJobQueue ────────────┼────────┼── claims                │
  │   AcceptedKinds =       │        │   AcceptedKinds = {ETL} │
  │     {registered, no ETL}│        │                         │
  └─────────────────────────┘        └─────────────────────────┘
              │                                    │
              └──────── PostgreSQL (shared) ───────┘
```

`AcceptedKinds` on each image is derived from the `IJobExecutor`
instances registered in that image. The default `honua-server` does not
register an ETL executor, so it does not claim ETL jobs even though the
shared `IJobQueue` carries them. `honua-worker-etl` registers the ETL
executor exclusively and claims only `ExtractTransformLoad` jobs.

- **Default image (`honua-server`)** stays lean. No GDAL, no PROJ, no
  GEOS native libraries. Phase 1 managed connectors run inside this
  image — they are pure-managed wrappers over the existing
  `Honua.Core/Features/Import/Services/` readers.
- **GeoETL worker image (`honua-worker-etl`)** is built from a
  separate `Dockerfile` in the `honua-devops` repository. It layers
  GDAL, PROJ, and GEOS on top of the lean base image and runs the
  `PipelineExecutionBackgroundService` with
  `AcceptedKinds = { ExtractTransformLoad }`. It is not reachable from
  the public ingress.
- **Capability detection**: connector factories declare which profile
  they need (`Managed` vs `RequiresNativeGeo`). The pipeline CRUD
  surface always accepts a definition. Job submission refuses to
  enqueue when the deployed worker profile cannot satisfy the
  pipeline's connector requirements, returning a clear error.
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
quarantine-artifact reference. Fatal errors (auth failure, unreachable
host) abort the run and mark the job Failed via the substrate.

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
- **Capability detection lag.** Job submission refusal (rather than
  CRUD-time refusal) is intentional — definitions outlive deployments
  — but it means an operator who removes the worker image will see
  scheduled jobs fail to enqueue rather than fail to validate. The
  error message must be clear; the runbook for the operator must be
  written when Child Ticket F lands.
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
