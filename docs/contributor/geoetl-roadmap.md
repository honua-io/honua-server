# GeoETL — architecture and decomposition roadmap

**Ticket**: `honua-io/honua-server#361` (Epic — design and decomposition)
**Strategy spike**: [GeoETL competitor evaluation and product strategy](geoetl-spike.md) (`#682`)
**Substrate dependency**: [ADR-0031 Durable Job Orchestration Substrate](adr/0031-durable-job-orchestration-substrate.md) (`#681`, merged)
**Companion ADR**: [ADR-0038 GeoETL pipeline architecture and runtime boundary](adr/0038-geoetl-pipeline-architecture-and-runtime-boundary.md)

This roadmap is the canonical design and decomposition deliverable for the
GeoETL epic. It replaces ad-hoc planning notes with bounded child tickets that
can each ship in a reviewable PR. The roadmap is **not** the implementation —
it is the contract that constrains what each child ticket may and may not do.

The strategy spike answered "what should we build and against whom"; the
roadmap answers "what are the smallest reviewable pieces that get us there."

## Why this is an epic, not a single PR

The contract for `#361` explicitly forbids landing the full GeoETL system
under one PR. Three reasons drive that constraint:

1. **Heavy native dependencies.** Some Phase 2 connectors require GDAL/OGR,
   PROJ, and GEOS. Those binaries cannot enter the default
   `honua-server` image without breaking the cold-start and memory profile
   the API serving path commits to. They live in a dedicated worker
   profile (`honua-worker-etl`); the API server stays lean.
2. **Substrate boundary.** Job orchestration semantics (claim, lease,
   heartbeat, retry, cancellation, progress) belong to `IJobQueue` and
   `IExecutionJobStore` from `#681`, not to GeoETL. GeoETL is a *job kind*
   (`ExecutionJobKind.ExtractTransformLoad`) on top of that substrate.
3. **Reviewability.** The full surface — pipeline schema, six file
   connectors, a transform library, four sinks, the execution engine, the
   worker image, the admin UI — is too large for a single PR to be
   reviewed cleanly.

## Acceptance criteria coverage

The epic acceptance criteria from `#361` map to sections of this roadmap:

| Criterion | Where covered |
|---|---|
| Decomposed into bounded child tickets | [Child ticket decomposition](#child-ticket-decomposition) |
| First implementation child ticket selected and explicitly scoped | [Child Ticket A](#child-ticket-a--pipeline-domain-models--crud-api-first-implementation-ticket) |
| Baseline runtime requires only Honua + PostgreSQL, not cloud batch | [Runtime and worker boundary](#runtime-and-worker-boundary) |
| Heavy native deps isolated to dedicated worker profile | [Runtime and worker boundary](#runtime-and-worker-boundary) and ADR-0038 |
| Curated first connector/driver target set as 80% coverage, not blanket GDAL | [Connector phasing](#connector-phasing-extract) |
| Pipeline semantics — declarative defs, schedule/event triggers, history/logs/errors, dry run, cancel/retry/progress, rollback | [Pipeline execution model](#pipeline-execution-model) |
| Edition boundaries — Community keeps one-shot import, Pro owns scheduled pipelines + transforms, Enterprise owns streaming + custom plugins | [Edition gating](#edition-gating) |
| Follow-up tickets linked for admin UI, optional cloud/distributed executors, advanced connector/plugin work | [Child ticket decomposition](#child-ticket-decomposition) and [Linked follow-ons](#linked-follow-ons) |

## Architecture fit

The existing codebase provides ~90% of the required infrastructure. GeoETL
**wraps** rather than replaces.

| Existing asset | GeoETL role |
|---|---|
| `Honua.Core/Features/Import/` format readers (GeoJSON, Shapefile, GeoPackage, GPX, KML, CSV, FlatGeobuf, FileGdb, GeoParquet, WKT) | Phase 1 source connector implementations — wrapped, never duplicated |
| `IJobQueue` + `ExecutionJobKind.ExtractTransformLoad` (from `#681`) | Job submission, claim, lease, heartbeat, cancellation |
| `IDistributedProgressStore` and `ImportBackgroundServiceCoordinator` | Progress tracking and leader election — reused as-is |
| `IExecutionJobStore` and `IExecutionLogStore` | Execution history and structured log sink |
| `GeoservicesImportBackgroundService` pattern | Reference shape for `PipelineExecutionBackgroundService` |
| `Honua.Server/Features/Geoprocessing/` (`BuiltInProcessCatalog`, `ProcessPlanValidator`, `ExecutionAdmissionEvaluator`) | Reference for connector catalog, stage-chain validation, edition admission |
| `StreamingFileImportService` insert path | Reused by Honua-layer sink connector |
| `ArcGisRestClient`, `GeoServerRestClient` | Phase 1 remote source connectors (Esri REST, OGC WFS / OGC API Features — managed, no native deps) |

GeoETL code lives in new vertical slices that follow the same project
conventions as `Import/` and `Geoprocessing/`:

```
src/Honua.Core/Features/GeoETL/
  Abstractions/   IPipelineSourceConnector, IPipelineTransform, IPipelineSinkConnector,
                  IPipelineDefinitionStore, IPipelineExecutionStore
  Domain/         PipelineDefinition, PipelineStage, PipelineStageKind, ConnectorConfig,
                  TransformConfig, PipelineExecution, PipelineExecutionStatus
  Services/
    Connectors/   one file per source connector
    Transforms/   one file per transform
    PipelineValidator.cs

src/Honua.Postgres/Features/GeoETL/
  Services/       PostgreSQL implementations of store interfaces; spatial transforms
                  delegating to PostGIS

src/Honua.Server/Features/GeoETL/
  PipelineEndpoints.cs
  PipelineExecutionBackgroundService.cs
  PipelineJobExecutor.cs
  Models/         API DTOs — source-generated JSON context
```

## Connector phasing (Extract)

The first wave covers the **common 80%** of production sources, not blanket
GDAL coverage. Each phase corresponds to a worker-profile boundary, not a
release calendar.

### Phase 1 — managed sources, no native deps required

Run inside `honua-server` directly. Wrapper around an existing reader.

| Source | Wraps |
|---|---|
| GeoJSON | `StreamingGeoJsonReader` |
| Shapefile (.zip) | `NetTopologySuite.IO.Esri` path |
| GeoPackage (.gpkg) | `Microsoft.Data.Sqlite` path used by the existing reader |
| CSV with coordinates | `CsvFormatReader` |
| KML / KMZ | `KmlFormatReader` |
| GPX | `GpxFormatReader` |
| FlatGeobuf | `FlatGeobufFormatReader` |
| GeoParquet | `GeoParquetReader` |
| PostGIS (remote) | Existing `Npgsql` + spatial-provider client pattern |
| Esri REST feature services | `ArcGisRestClient` |
| OGC WFS / OGC API Features | `GeoServerRestClient` |

### Phase 2 — heavyweight worker profile, requires GDAL/OGR

Run only inside `honua-worker-etl`. CRUD stores pipelines that reference
these connectors regardless of worker availability; execution submission
refuses to enqueue when no worker profile can satisfy them (see
[Capability detection](#capability-detection)).

- GML
- SQL Server spatial (depending on driver footprint)
- MySQL spatial (depending on driver footprint)
- Generic REST/JSON with mapping DSL for arbitrary spatial fields

### Phase 3 — Pro/Enterprise cloud and streaming sources

- S3 / Azure Blob / GCS file watchers (event-triggered, see `#316`)
- Webhook receivers
- MQTT topics

## Transform library

The transform set covers the analyst's daily 80%. Heavy spatial
transforms continue to delegate to PostGIS, where the engine is already
mature.

| Family | First wave |
|---|---|
| Geometry | reproject, validate / repair, simplify, clip-to-AOI, coordinate precision clamp |
| Attribute | rename, type cast, concat, split, regex extract, lookup / join |
| Filtering | spatial filter, attribute filter, spatial dedup, attribute dedup |
| Enrichment | spatial-join enrichment, reverse geocode (Phase 2, coordinated with `#374`) |
| Quality | null-fill, outlier detection, geometry validation batch report (Phase 2) |

Transforms operate on `IAsyncEnumerable<Feature>` and never materialize the
full set in memory. Stage-chain validation rejects pipelines whose output
schema is incompatible with the next input schema before execution starts.

## Sink phasing (Load)

| Phase | Sink |
|---|---|
| 1 | Honua layer (create / append / upsert; reuses `StreamingFileImportService` insert path) |
| 1 | GeoJSON export (file or presigned URL) |
| 1 | GeoPackage export |
| 1 | External PostGIS database |
| 1 | Dry-run null sink (preview row counts and schema diff) |
| 2 | Shapefile export |
| 2 | Honua attachments (per-feature artifact upload) |

## Pipeline execution model

The pipeline contract is what each child ticket binds against. Changing it
late is expensive, so it is fully scoped in Child Ticket A.

- **Definition format**: JSON only at the HTTP layer, source-generated.
  SDK and `honua-cli` accept YAML and normalize to JSON before sending
  (server-side YAML parsing avoided to keep the AOT/trim surface lean).
  A `schema_version` field lives in the definition root from day one.
- **Schema shape**: `PipelineDefinition → []PipelineStage`
  (Source → []Transform → Sink), typed via `ConnectorConfig` /
  `TransformConfig` discriminated unions.
- **Storage**: `honua.pipeline_definitions` and `honua.pipeline_executions`
  in PostgreSQL.
- **Versioning**: every successful `PUT` increments `Version`. Older versions
  remain readable for audit and rollback.
- **Scheduling**: cron scheduler background service submits
  `ExtractTransformLoad` jobs to `IJobQueue` (same shape as
  `WorkflowSchedulerBackgroundService`).
- **Event triggers**: file-upload events and CDC events from `#316` enqueue
  the same job kind through the same queue.
- **Execution worker**: `PipelineExecutionBackgroundService` extends
  `BackgroundService`; uses `ImportBackgroundServiceCoordinator` for leader
  election and heartbeat — reused unchanged.
- **Dry run**: every stage executes, but the sink is the null preview sink.
  Row counts, schema diff, and rejected-row samples return to the caller
  without writing to durable targets.
- **Cancellation**: `CancellationToken` propagated through every connector,
  transform, and sink. Substrate semantics from ADR-0031 govern terminal
  state writes; GeoETL never owns its own cancellation channel.
- **Retry**: `JobRetryPolicy.ShouldRetry(attemptCount)` from the
  substrate (job-level, attempt-count driven). The current
  `JobExecutionResult` exposes `Status`/`ErrorMessage`/`Warnings` only —
  no fatal-vs-retryable flag — so every `Failed` result routes through
  the same retry path. Auth failures and transient network blips both
  surface via `ErrorMessage`; the substrate retries them uniformly
  until the attempt budget is exhausted. Per-stage failure
  classification (auth → fatal, transient → retryable) is a Phase 2
  enhancement that requires a `JobExecutionResult.FailureKind` (or
  equivalent) substrate extension — not a launch requirement. See
  ADR-0038 § Substrate consumption and § Re-evaluation triggers.
- **Rollback**: soft-delete batch ID pattern. Every Honua-layer write tags
  rows with `pipeline_batch_id`; a failed run issues a targeted delete on
  that batch. The pattern matches the existing import path. Limits and a
  staging-table escalation are documented in the
  [risks](#risks-and-tradeoffs) section.
- **Row-level errors**: rejected rows + reason write to a quarantine sink
  (companion GeoJSON or CSV artifact); the execution summary includes the
  error count and a sample.
- **Progress**: structured progress events flow through
  `IDistributedProgressStore`, identical to the import path.
- **Telemetry**: `pipeline.started`, `pipeline.stage.completed`,
  `pipeline.stage.failed`, `pipeline.completed`, `pipeline.failed` log
  events with pipeline ID, stage index, and feature counts. Existing
  structured logging conventions; no new logger libraries. See
  ADR-0038 § Telemetry for the canonical event set.

## Runtime and worker boundary

The serving image must stay lean. The worker image carries the heavy
native dependencies. ADR-0038 captures this decision with full
consequences and the substrate-extension prerequisite for the
two-image rollout; the summary is below.

```
Phase 1 (pre-Child-Ticket-F, single-image baseline)
  Default image (honua-server)
    → Serves API: pipeline CRUD, execution triggers, history, dry-run
    → No GDAL, no PROJ, no GEOS native libs
    → Registers a managed-profile ETL executor;
      AcceptedKinds = { ExtractTransformLoad } — sole claimer
    → Phase 1 connectors (managed, no native) execute here
    → CRUD stores any pipeline definition (edition-agnostic). Pipelines
      that reference a RequiresNativeGeo connector are stored with a
      CRUD-time advisory warning ("no native worker registered") and
      refused at execution submission time (manual trigger, scheduled
      enqueue, or dry-run submission) until a native-profile worker
      registers

Phase 2 (post-Child-Ticket-F, two images + substrate claim filter)
  Default image (honua-server)
    → As above, but the executor registers
      AcceptedRuntimeProfiles = { "managed" } and only claims jobs
      whose Spec.RuntimeProfile = "managed"

  GeoETL worker image (honua-worker-etl) [Dockerfile in honua-devops]
    → Layers GDAL, PROJ, GEOS on the lean base
    → Runs PipelineExecutionBackgroundService with the
      native-profile executor;
      AcceptedKinds = { ExtractTransformLoad },
      AcceptedRuntimeProfiles = { "native" } — only claims
      Spec.RuntimeProfile = "native" jobs
    → Phase 2 connectors registered only here
    → Submission stamps Spec.RuntimeProfile based on connector set:
      "managed" if every connector is Phase 1, "native" if any is
      RequiresNativeGeo
```

### Capability detection

Capability detection runs at three layers, mirroring the
geoprocessing edition-gate pattern (CRUD stores, submission gates).
The execution-submission gate and the substrate-level claim filter
are the load-bearing correctness invariants; the CRUD-time advisory
exists for authoring ergonomics only.

1. **CRUD validation (advisory only)**: connector factories declare
   which profile they need (`Managed` vs `RequiresNativeGeo`). When
   the API receives a pipeline that references a `RequiresNativeGeo`
   connector and no native-profile worker is registered as available,
   the CRUD response includes a `connector_availability` warning so
   authors see the gap early. **CRUD never refuses to store the
   definition** — operators may stage Phase 2 pipelines ahead of
   deploying `honua-worker-etl`, exactly as edition-gated definitions
   may be authored ahead of an edition upgrade.
2. **Execution-submission gate**: every path that creates an
   `ExtractTransformLoad` job (manual trigger, scheduler enqueue,
   dry-run submission) re-runs capability detection against the
   currently-registered worker profiles. If no profile can satisfy
   the connector set, submission is refused with a descriptive error
   and no job is enqueued. This is the same shape as
   `ExecutionAdmissionEvaluator`'s edition gate.
3. **Substrate claim filter** (post-Child-Ticket-F): submission
   stamps `Spec.RuntimeProfile` based on the connector set. The
   substrate's `IJobQueue.TryClaimAsync` honors an
   `acceptedRuntimeProfiles` filter so the managed-profile executor
   never claims a `Spec.RuntimeProfile = "native"` job, even if one
   were enqueued during a worker outage that races the
   execution-submission check. Today the substrate filters claims
   only by `ExecutionJobKind`; F adds the profile-aware filter as a
   small, strictly additive substrate change with a null-default
   backward compatibility path so non-ETL kinds remain unaffected.

The default deployment requires **only Honua + PostgreSQL**. The
`honua-worker-etl` image is not necessary for Phase 1 and only deploys
when an operator opts into Phase 2 connectors. No cloud batch backend
(AWS Batch, Azure Batch, Kubernetes Jobs) is required for the baseline.

## Edition gating

Edition checks follow the existing
`ExecutionAdmissionEvaluator` pattern: enforcement happens at job
submission time, not at the endpoint level. The pipeline CRUD surface is
edition-agnostic so that operators can prepare definitions ahead of an
edition upgrade.

| Capability | Community | Pro | Enterprise |
|---|:---:|:---:|:---:|
| One-shot file import (existing) | Yes | Yes | Yes |
| Bundled open-data enrichment (Natural Earth, timezones) | Yes | Yes | Yes |
| Scheduled pipelines | — | Yes | Yes |
| Multi-source extract | — | Yes | Yes |
| Full transform library | — | Yes | Yes |
| Pipeline versioning + rollback | — | Yes | Yes |
| Execution history + logs | — | Yes | Yes |
| Dry run | — | Yes | Yes |
| Premium enrichment datasets | — | Yes | Yes |
| S3 / Azure / GCS file watchers | — | Yes | Yes |
| Streaming sources (webhook, MQTT) | — | — | Yes |
| Custom transform plugins (sandboxed) | — | — | Yes |
| Cross-tenant pipelines | — | — | Yes |
| Pluggable distributed executor backends | — | — | Yes |

Community continues to ship one-shot import unchanged. Pro is the
GeoETL home tier. Enterprise covers the integrations and runtime
extension points.

## Validation strategy

Validation runs at three levels.

1. **Pipeline definition validation** (CRUD time, hard fails plus
   advisories): JSON schema, source schema reachability for managed
   connectors, transform stage-chain compatibility, and sink schema
   compatibility are hard fails. Edition gate and connector / worker
   availability are **advisory warnings only** — the definition stores
   regardless so operators may stage Phase 2 pipelines or higher-tier
   capabilities ahead of an upgrade.
2. **Pre-execution admission** (every path that creates an
   `ExtractTransformLoad` job — manual trigger, scheduler enqueue,
   dry-run submission): edition gate via
   `ExecutionAdmissionEvaluator`, capability detection against the
   currently-registered worker profiles (refuses to enqueue if no
   profile can satisfy the connector set), secret resolution. See
   [Capability detection](#capability-detection).
3. **Execution-time validation** (per-stage): row-level error capture for
   geometry validation, attribute type cast, regex parse, etc. Errors
   route to the quarantine sink rather than aborting unless the connector
   itself fails (auth, unreachable host).

Test layers:

- **Unit**: pipeline validator, connector factory, transform factory,
  stage-chain compatibility.
- **Integration (Testcontainers)**: CRUD, scheduling, execution against a
  real PostGIS, Honua-layer sink round-trip.
- **End-to-end**: the four proof workloads from
  [the strategy spike § 8](geoetl-spike.md) — Esri REST nightly, S3 drop
  on-upload, PostGIS scheduled export, inline GeoJSON enrichment.

## Child ticket decomposition

Each row below is a self-contained, reviewable PR. Dependencies are
hard — a child ticket may not start until its dependency has merged. The
`#`-numbered tickets that have not yet been opened are placeholders;
issue creation tracks against this roadmap.

| ID | Title | Depends on | Repo | Edition |
|---|---|---|---|---|
| **A** | Pipeline domain models + CRUD API (first impl) | — | honua-server | Pro |
| B | Source connector abstraction + Phase 1 file connectors | A | honua-server | Pro |
| C | Core transform library + stage-chain validator | A | honua-server | Pro |
| D | Phase 1 sink connectors | A | honua-server | Pro |
| E | Pipeline execution engine + cron / event scheduler | B, C, D | honua-server | Pro |
| F | `honua-worker-etl` image + GML + substrate `RuntimeProfile` claim filter + capability detection | E | honua-devops + honua-server | Pro |
| G | Phase 1 remote API sources (Esri REST, OGC WFS / OGC API Features, remote PostGIS) | B | honua-server | Pro |
| H | Admin UI for pipeline authoring + execution monitor | E | honua-server-admin | Pro |
| I | Streaming sources + custom transform plugin sandbox | E | honua-server | Enterprise |
| J | Pluggable distributed executor backends | E | honua-server (+ honua-devops) | Enterprise |
| K | Phase 2 database connectors (SQL Server spatial, MySQL spatial) | F | honua-server | Pro |

The order is the merge order. B, C, and D can be implemented in parallel
after A; E unblocks F, H, I, and J; F unblocks K. G can land after B
without waiting on the worker image because its connectors are managed
Phase 1 (no GDAL).

### Child Ticket A — pipeline domain models + CRUD API (first implementation ticket)

**Repo**: honua-server. **Native deps**: none. **AOT**: safe. **Execution
logic**: none.

**In scope**

- `Honua.Core/Features/GeoETL/Domain/`
  - `PipelineDefinition`, `PipelineStage`, `PipelineStageKind`
  - `ConnectorConfig` (discriminated union, source-generated JSON)
  - `TransformConfig` (discriminated union, source-generated JSON)
  - `PipelineExecution`, `PipelineExecutionStatus`
  - `schema_version` field on the definition root from day one
- `Honua.Core/Features/GeoETL/Abstractions/`
  - `IPipelineDefinitionStore`, `IPipelineExecutionStore`
- `Honua.Postgres/Features/GeoETL/`
  - EF Core entity configurations
  - DbUp migration adding `honua.pipeline_definitions` and
    `honua.pipeline_executions`
- `Honua.Server/Features/GeoETL/PipelineEndpoints.cs` — Minimal API
  - `GET    /api/v1/admin/geoetl/pipelines`
  - `POST   /api/v1/admin/geoetl/pipelines`
  - `GET    /api/v1/admin/geoetl/pipelines/{id}`
  - `PUT    /api/v1/admin/geoetl/pipelines/{id}`
  - `DELETE /api/v1/admin/geoetl/pipelines/{id}`
  - `GET    /api/v1/admin/geoetl/pipelines/{id}/executions`
  - `GET    /api/v1/admin/geoetl/pipelines/{id}/executions/{executionId}`
- Source-generated JSON contexts for every DTO
- XML documentation on every public member
- Unit tests for domain validation
- Testcontainers integration tests for CRUD round-trip
- Execution endpoints (trigger, cancel) return `501 Not Implemented`
  until Child Ticket E lands

**Out of scope for A**

- Connector implementations
- Transform implementations
- Execution engine
- Scheduling
- GDAL / native deps
- Worker image
- Admin UI

**Acceptance**

- All seven CRUD routes respond per the OpenAPI contract.
- Pipeline definitions round-trip through PostgreSQL with schema_version
  preserved.
- A pipeline that references a Phase 2 connector validates and stores;
  trigger returns 501 with a "execution engine not yet implemented" body.
- Edition gate is wired through `ExecutionAdmissionEvaluator` ready to
  enforce in E.

### Child Tickets B–J (planning summaries)

**B — Source connector abstraction + Phase 1 file connectors.**
`IPipelineSourceConnector`, `SourceConnectorFactory`, wrappers for
GeoJSON, Shapefile, GeoPackage, CSV, KML, GPX, FlatGeobuf, and
GeoParquet — every Phase 1 file source listed in the
[Connector phasing](#phase-1--managed-sources-no-native-deps-required)
table that is *not* a remote API source (those are Child Ticket G).
Per-connector tests against the fixtures already used by the import
path.

**C — Core transform library.** `IPipelineTransform`, `TransformFactory`,
the geometry and attribute transforms in
[Transform library](#transform-library). Stage-chain compatibility
validator rejects pipelines whose output schema does not satisfy the
next input.

**D — Phase 1 sink connectors.** `IPipelineSinkConnector`,
`SinkConnectorFactory`, Honua-layer / GeoJSON / GeoPackage / external
PostGIS / dry-run null sinks. Honua-layer sink reuses
`StreamingFileImportService` insert path.

**E — Pipeline execution engine + scheduling.**
`PipelineExecutionBackgroundService`, `PipelineJobExecutor` (linear
Source → []Transform → Sink). Cron / event scheduler enqueues
`ExtractTransformLoad` jobs. Row-level quarantine, progress reporting,
cancellation, retry, soft-delete rollback. Execution endpoints from A
return real terminal states instead of 501.

**F — `honua-worker-etl` worker image + substrate claim filter.**
`Dockerfile.worker-etl` in honua-devops layers GDAL/PROJ/GEOS on the
base image. GML connector. Substrate extension: extends
`IJobQueue.TryClaimAsync` with an optional
`acceptedRuntimeProfiles` filter, has `RedisJobQueue` honor it
against `Spec.RuntimeProfile`, and adds an optional
`AcceptedRuntimeProfiles` property on `IJobExecutor` (null-default,
backward-compatible — non-ETL executors keep claiming regardless of
profile). Submission stamps `Spec.RuntimeProfile` based on the
connector set. The native-profile executor (in
`honua-worker-etl`) registers with
`AcceptedRuntimeProfiles = { "native" }`; the managed-profile
executor (in `honua-server`) registers with
`AcceptedRuntimeProfiles = { "managed" }` so it can no longer claim
a Phase 2 job. Execution-submission capability check (manual
trigger, scheduler enqueue, dry-run submission) refuses pipelines
whose connectors no registered profile can satisfy; CRUD continues
to store such definitions with the advisory warning. CI image scan
asserts no GDAL bytes leaked into the default `honua-server` image.

**G — Phase 1 remote API sources.** Remote PostGIS, Esri REST, OGC WFS /
OGC API Features. All managed — no GDAL required, run inside
`honua-server` via the managed-profile executor. Depends only on B
because the worker image is not on the critical path for these
connectors.

**H — Admin UI.** Pipeline list, execution monitor, error inspector,
dry-run trigger. Implementation lives in honua-server-admin; this row
exists in this roadmap so the cross-repo dependency is explicit.

**I — Streaming sources + custom plugin sandbox.** S3 / Azure Blob / GCS
file watchers, webhook receiver, MQTT, sandboxed custom transform
plugin. Enterprise-only.

**J — Pluggable distributed executor backends.** External executors
(Kubernetes Jobs, AWS Batch, Azure Container Apps Jobs, Apache Sedona)
that can subscribe to `ExtractTransformLoad` jobs. Pipeline definitions
remain identical across executors. Enterprise-only.

**K — Phase 2 database connectors.** SQL Server spatial, MySQL spatial.
Phase 2 — require `honua-worker-etl` for the underlying spatial
drivers. Depends on F so the worker image and capability-detection
contract exist before these connectors register.

## Linked follow-ons

- **Admin UI** — Child Ticket H, blocked on E. Owns the
  honua-server-admin pipeline builder and execution monitor.
- **Optional cloud / distributed executors** — Child Ticket J. Pluggable
  executor backends, deferred until a named-account ask justifies the
  investment.
- **Advanced connector and plugin work** — Child Ticket I (streaming +
  custom transform plugins). Phase 3 surface.
- **GitOps-applied pipelines** — coordinated with `#351`. Pipelines as
  code in Git, applied through the spec engine, exit through the same
  CRUD store.
- **CDC-triggered pipelines** — coordinated with `#316`. CDC events
  enqueue pipelines through the same `IJobQueue` path scheduling uses.
- **Enrichment library** — coordinated with `#374`. Spatial-join
  enrichment against bundled (Community) and premium (Pro) datasets.

## Risks and tradeoffs

- **Discriminated-union versioning.** If the `ConnectorConfig` /
  `TransformConfig` JSON shape changes, stored definitions can become
  unreadable. **Mitigation**: `schema_version` on the definition root
  from Child Ticket A, plus migration helpers that run at deserialize
  time. Older versions stay readable until a deliberate deprecation.
- **Stage-chain type safety for dynamic schemas.** Stage-chain validation
  works cleanly for typed connectors. Generic REST/JSON connectors with
  dynamic schemas need a permissive passthrough mode. **Risk**: invalid
  pipelines fail at runtime instead of at submission. **Mitigation**:
  document the caveat and surface it in the validator output.
- **Rollback scope at scale.** Soft-delete batch ID is correct for
  bounded loads. For very large loads that span multiple transactions,
  the inconsistency window between failed-write and rollback can be
  significant. **Mitigation**: document a max-safe batch size; offer a
  staging-table swap as a Phase 2 enhancement that swaps `LIVE` and
  `STAGING` table pointers atomically.
- **Worker image operational complexity.** Two images is more deployment
  surface. **Mitigation**: Phase 1 connectors all run inside
  `honua-server`, so `honua-worker-etl` is not required until Child
  Ticket F. Operators can adopt GeoETL on a single image and add the
  worker only when Phase 2 connectors are needed.
- **Two executors competing for the same `ExecutionJobKind`.** The
  substrate today filters claims only by `ExecutionJobKind`. If both
  images registered an `ExtractTransformLoad` executor without a
  profile-aware claim filter, the managed-profile executor could
  claim a Phase 2 job, fail it, and re-claim it on retry until the
  attempt budget was exhausted. **Mitigation**: Child Ticket F
  bundles a small, strictly additive substrate extension — an
  `acceptedRuntimeProfiles` filter on `IJobQueue.TryClaimAsync` and
  an optional `IJobExecutor.AcceptedRuntimeProfiles` property
  (null-default for non-ETL kinds). Until F lands, only the
  managed-profile executor is registered and Phase 2 connectors are
  refused at execution submission time (no job is ever enqueued),
  so the race cannot occur.
- **Per-stage fatal vs retryable classification.** ADR-0038 originally
  promised a per-stage distinction (auth → fatal, transient →
  retryable). The current substrate
  `JobExecutionResult` exposes `Status`/`ErrorMessage`/`Warnings`
  only and `JobRetryPolicy.ShouldRetry` is purely attempt-count
  driven. **Mitigation**: launch with job-level retry only.
  Per-stage failure classification is a documented Phase 2
  enhancement requiring a `JobExecutionResult.FailureKind` (or
  equivalent) substrate extension. Auth and transient failures both
  surface via `ErrorMessage` and route through the same retry path
  until then.
- **Admin UI cross-repo coupling.** Child Ticket H spans honua-server
  and honua-server-admin. **Mitigation**: H is blocked on E. The API
  contract from A + E must be stable before honua-server-admin work
  begins.
- **YAML at the wire.** Server-side YAML parsing was rejected to keep the
  AOT trim surface lean. **Tradeoff**: SDK and `honua-cli` own
  YAML-to-JSON normalization. Authors who want YAML must use a Honua
  client; raw `curl` users send JSON.

## Open questions resolved

The design-brief questions for `#361` are resolved as follows. These are
the constraints child-ticket implementers should treat as decided.

1. **First child ticket scope.** Child Ticket A is domain models + CRUD
   only, no execution. The execution surface returns 501 until E. This
   keeps A reviewable and lets B/C/D start in parallel without coupling
   to execution semantics.
2. **YAML vs JSON wire format.** JSON only on the server. YAML
   normalization happens in `Honua.Sdk.*` and `honua-cli`. AOT/trim
   surface stays lean.
3. **Rollback guarantee tier.** Soft-delete batch ID is the Pro-tier
   guarantee. Staging-table swap is documented as a Phase 2
   enhancement; it is not a launch requirement.
4. **Worker image deployment timeline and substrate extension.**
   Phase 1 ships connectors that run inside `honua-server`.
   `honua-worker-etl` is required only when an operator wants Phase 2
   connectors, and Child Ticket F is the only ticket that introduces
   the worker image. F also bundles the
   `RuntimeProfile`-aware claim filter on `IJobQueue` so the
   managed-profile executor cannot claim Phase 2 jobs once the worker
   image registers a competing executor — the substrate extension is
   in F's scope, not a separate ticket. The initial Pro release does
   not require operators to deploy a second image.
5. **GML scope.** GML is deferred to Child Ticket F. The minimal
   XmlReader path for simple GML 2/3 was rejected because it would
   commit honua-server to maintaining GML parsing forever.
6. **Edition enforcement.** Pipeline CRUD is edition-agnostic;
   enforcement happens at job submission via
   `ExecutionAdmissionEvaluator`. This matches the geoprocessing
   precedent and lets operators stage pipeline definitions ahead of an
   edition upgrade.
7. **Per-stage failure classification.** Launch uses job-level retry
   only via `JobRetryPolicy.ShouldRetry(attemptCount)`. Per-stage
   fatal vs retryable classification (auth → fatal, transient →
   retryable) is a documented Phase 2 enhancement requiring a
   `JobExecutionResult.FailureKind` substrate extension before child
   tickets can honor it. The decomposition does not block on this.

## References

- `honua-io/honua-server#361` — this epic.
- `honua-io/honua-server#681` — durable worker / job orchestration
  substrate (merged).
- `honua-io/honua-server#682` — GeoETL competitor evaluation (the
  strategy spike).
- `honua-io/honua-server#316` — CDC event bus (event-trigger
  coordination).
- `honua-io/honua-server#351` — GitOps change management (pipeline-as-code).
- `honua-io/honua-server#374` — enrichment library coordination.
- ADR-0024 (open-core edition model), ADR-0025 (multi-provider
  operation architecture), ADR-0029 (geoprocess canonical model
  mappings), ADR-0031 (durable job orchestration substrate),
  ADR-0034 (GDAL/OGR honua driver delivery strategy),
  ADR-0038 (GeoETL pipeline architecture and runtime boundary —
  companion to this roadmap).
