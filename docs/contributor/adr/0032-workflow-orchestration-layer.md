# ADR-0032: Workflow Orchestration Layer

## Status

Accepted

## Context

ADR-0029 established the canonical process model (`AnalysisPlan`,
`ExecutionJobRecord`, `AnalysisResultPackage`). ADR-0031 established the
durable job orchestration substrate (queue, claim/heartbeat, retry,
reconciliation) that drives individual plan executions. What remained was a
first-class representation of higher-order workflows: multi-step analysis
chains, scheduled workflows, and DAG-style runs whose state must be durable
and resumable across restarts.

Ticket #724 required a solution that:

1. Reuses canonical process, job, and result semantics rather than inventing a
   second execution language alongside ADR-0029.
2. Keeps orchestration state durable, resumable, and explicit about step
   dependencies, retries, and failure handling.
3. Exercises the same runtime substrate used for single-step process
   execution (ADR-0031) so testing does not require a parallel execution
   path.
4. Exposes structured metadata that downstream rate/cost controls (#732),
   deployment lifecycle (#739), and cloud executor adapters (#727) can consume
   without reopening the orchestration contract.

Encoding multi-step analysis as ad hoc job glue would have re-created
per-caller retry semantics, duplicated dependency tracking, and hidden the
cross-step artifact flow from observability.

## Decision

Honua defines a declarative workflow orchestration layer as a new vertical
slice under `Honua.Core/Features/Orchestration` (domain + abstractions) and
`Honua.Server/Features/Orchestration` (stores, engine, background services).

### Domain Types

Workflow state is fully described by transport-neutral records in
`Honua.Core.Features.Orchestration.Domain`:

| Type | Role |
|------|------|
| `WorkflowDefinition` | Declarative DAG specification (steps, trigger, metadata) |
| `WorkflowStepDefinition` | One step: plan, `DependsOn`, `InputBindings`, `RetryPolicy`, `FailurePolicy`, optional `TimeoutSeconds` |
| `StepInputBinding` | Artifact-to-input wiring (`SourceStepId`, `SourceArtifactSelector`, `TargetInputKey`) |
| `StepRetryPolicy` | Exponential backoff (`MaxAttempts`, `InitialDelaySeconds`, `BackoffMultiplier`, `MaxDelaySeconds`) |
| `WorkflowTrigger` | `Manual` or `Cron` (5-field expression + IANA time zone + `Enabled`) |
| `WorkflowRun` | Durable run instance (status, `StepStates`, `Audit`, `TriggerKind`, `Metadata`) |
| `WorkflowStepState` | Per-step runtime state (`JobId`, `AttemptCount`, `ResolvedInputs`, `OutputArtifacts`) |
| `WorkflowProgress` | `IOperationProgress` + `ICancellableOperationProgress` projection surfaced as `OperationType.Orchestration` |

State machines:

- **Run**: `Pending → Running → {Succeeded | Failed | Cancelled}` with a direct
  `Pending → Cancelled` short-circuit before any step starts.
- **Step**: `Pending → Queued → Running → Succeeded`, plus `Failed → Pending`
  for retry, `Failed/Skipped` cascades across dependents, and `Cancelled`
  from a run-level cancel.

### Reuse, Not Reinvention

The orchestration engine delegates every step submission to
`IWorkflowJobExecutor`. The only production implementation,
`GeoprocessingWorkflowJobExecutor`, adapts `IGeoprocessingJobService` so
orchestration never reaches directly into the geoprocessing slice. This
adapter reuses canonical `AnalysisPlan`/`ExecutionJobRecord`/
`AnalysisResultPackage` semantics — orchestration does not define a parallel
job model.

`IWorkflowRunStore` extends `IOperationStore` so reconciliation uses the
canonical lease pattern already established for deploy workflows. The engine
acquires a 30-second lease per run and renews every 10 seconds while
reconciling; the lease is released in a `finally` block even on failure.

### Submission Idempotency

Each job submission is keyed by `{runId}:{stepId}:{attemptCount}`. Combined
with the durable substrate's idempotency semantics (ADR-0031), this makes
retries and crash recovery free of duplicate submissions. Every submission
also stamps protocol metadata:

```
orchestration.runId     = {runId}
orchestration.workflowId = {workflowId}
orchestration.stepId    = {stepId}
orchestration.attempt   = {attemptCount}
```

Downstream consumers (rate/cost, audit, cloud executors) correlate step
executions to their parent run through this metadata without needing an
orchestration-specific transport.

### Artifact Binding

`StepInputBinding.SourceArtifactSelector` supports:

- `artifact:{index}` — positional reference into
  `AnalysisResultPackage.Artifacts`.
- `artifact:{label}` — case-insensitive match against `ArtifactRef.Label`.

Resolved references are string-substituted into a copy of the downstream
plan's `Inputs` dictionary before submission. The canonical plan step's
opaque-string-dict contract remains unchanged.

### Failure and Cancellation

- `WorkflowStepFailurePolicy.Fail` (default) — after retries are exhausted,
  the step is `Failed`, the run is `Failed`, and non-terminal dependents are
  `Cancelled`.
- `WorkflowStepFailurePolicy.Skip` — the step becomes `Skipped`; dependents
  that **bind** artifacts from this step cascade `Skipped`; dependents that
  only declare structural `DependsOn` proceed.
- Run cancellation: `Pending` steps become `Cancelled`; `Queued`/`Running`
  steps are cancelled through `IWorkflowJobExecutor.CancelJobAsync`, and the
  step then transitions to `Cancelled`. Cascade-cancel errors are swallowed
  for already-terminal child jobs and recorded as run warnings otherwise.

### Scheduler

`WorkflowSchedulerBackgroundService` evaluates `Cron`-triggered definitions
once per 30-second tick. Compiled expressions are cached per workflow and
recompiled when the cron expression or time zone changes. A durable per-
workflow cursor (`GetScheduleCursorAsync` / `AdvanceScheduleCursorAsync`)
protects against re-firing occurrences after restart. A per-fire-time claim
(`TryClaimScheduleFireAsync`) deduplicates fires across replicas.

### Persistence

Redis is the coordination layer, consistent with ADR-0021 and ADR-0025:

| Store | Key pattern | TTL |
|-------|-------------|-----|
| `RedisWorkflowDefinitionStore` | `orchestration:def:{workflowId}` | none |
| `RedisWorkflowRunStore` | `orchestration:run:{runId}` | 7 days |
| Run leases | `orchestration:run:lease:{runId}` | 30 s |
| Schedule claims/cursors | `orchestration:schedule:*` | claim retention / unbounded cursor |

Stores only register when `IConnectionMultiplexer` is present; otherwise
orchestration services are not registered and the admin cancel endpoint for
`Orchestration` operations returns `503`.

### AOT and Trimming

- All serialization flows through source-generated `OrchestrationJsonContext`.
- All logging flows through `[LoggerMessage]` source generation
  (`OrchestrationLog`, EventId band `8100-8199`).
- No `System.Reflection`, no `dynamic`, no `Activator.CreateInstance`.
- Domain types use `required`/`init` properties exclusively.
- Cron parsing is an in-repo allocation-light 5-field implementation
  (`CronExpression`) — no new NuGet dependency.

### Telemetry

Activities: `honua.orchestration.reconcile_run`,
`honua.orchestration.execute_step`, `honua.orchestration.resolve_bindings`,
`honua.orchestration.scheduler_tick`.

Metrics: `honua.orchestration.runs_created_total`,
`honua.orchestration.runs_completed_total`,
`honua.orchestration.steps_completed_total`,
`honua.orchestration.steps_retried_total`,
`honua.orchestration.run_duration_ms`,
`honua.orchestration.step_duration_ms`.

## Consequences

- Multi-step analysis, scheduled workflows, and DAG-style runs now have a
  single, durable representation that reuses every canonical substrate
  contract. Ad hoc job glue is no longer needed for chained execution.
- Downstream tickets (#732, #739, #727) consume orchestration metadata
  through stamped protocol metadata and the `Orchestration` operation type
  surface — no orchestration-internal types leak into their contracts.
- Workflow runs surface through the existing admin operations endpoints as
  `OperationType.Orchestration`. Cancellation on the admin endpoint routes
  to `WorkflowOrchestrationEngine.CancelRunAsync` rather than writing
  progress directly, which would be overwritten on the next reconcile tick.
- Redis becomes a hard dependency for workflow execution. Definitions are
  long-lived configuration held in Redis; mitigation is to re-submit
  definitions through the admin/API surface. A Postgres-backed store can
  slot in behind `IWorkflowDefinitionStore` later without changing the
  engine.
- Independent DAG branches are reconciled serially per tick. Parallel
  execution is possible but adds complexity to lease management and progress
  tracking; it is deferred to a follow-up.
- The `OperationType` enum grows by one value (`Orchestration`), and
  `OperationsProgressJsonContext` grows a `WorkflowProgress` case. No other
  canonical types are modified.

## References

- ADR-0021: Redis Usage and HybridCache Deferral
- ADR-0025: Multi-Provider Operation Architecture
- ADR-0026: AI-First Operator Contract
- ADR-0029: Geoprocess Canonical Model Mappings
- ADR-0031: Durable Job Orchestration Substrate
- Ticket #360: Geoprocess framework comparative research and target model
- Ticket #681: Durable worker and job orchestration substrate
- Ticket #721: Canonical geoprocessing domain and job service
- Ticket #724: Geoprocessing orchestration layer (this ADR)
