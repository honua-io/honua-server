# ADR-0031: Durable Job Orchestration Substrate

## Status
Accepted

## Context

The canonical geoprocess framework (ADR-0026, ADR-0029, ticket #360) defines an
analysis plan lifecycle: intent capture, plan validation, execution, and result
packaging. The multi-provider operation architecture (ADR-0025) established the
`ExecutionJobRecord` model and `IExecutionJobStore` for durable job state. What
was missing is the execution substrate that connects these pieces: how jobs move
from submission to execution, how workers claim and report liveness, how failures
trigger retries, and how the API serving path stays lean.

Key constraints:

1. The API server must remain lightweight. Heavyweight execution dependencies
   (executor implementations, reconciliation loops) must not be loaded in the
   default request-serving image.
2. Workers must be horizontally scalable. Any worker instance must be able to
   claim any eligible job without coordination beyond the shared store.
3. Job execution must be durable across worker restarts. A worker crash must not
   silently lose a job; heartbeat expiry and retry policies must recover it.
4. The substrate must be provider-agnostic. Downstream tickets will implement
   executors for Kubernetes Jobs, AWS Batch, and in-process workloads. The
   substrate cannot bind to any one backend.
5. Structured execution logs and artifact references must be first-class outputs,
   not afterthoughts bolted onto progress reporting.

## Decision

Honua defines a durable job orchestration substrate with these components:

### Job Lifecycle States

The existing `ExecutionJobStatus` enum is retained unchanged:

| State | Meaning |
|-------|---------|
| Queued | Job is in the queue, not yet claimed |
| Provisioning | Worker has claimed the job, preparing to execute |
| Running | Execution is in progress |
| Succeeded | Terminal: execution completed successfully |
| Failed | Terminal: execution failed (may be retried) |
| Cancelled | Terminal: cancelled by user or system |

### Claim and Heartbeat

- `IJobQueue.TryClaimAsync` atomically removes a job from the pending set and
  associates it with a worker ID. The claim sets `ClaimedBy`, `ClaimedAt`, and
  `LastHeartbeatAt` on the `ExecutionJobRecord`.
- Workers pump heartbeats at the interval specified by `JobHeartbeatPolicy`
  (default: 30 seconds). Each heartbeat updates `LastHeartbeatAt`.
- `JobReconciliationService` sweeps active jobs every 30 seconds. If
  `LastHeartbeatAt` exceeds the heartbeat timeout (default: 90 seconds), the
  job is considered abandoned.

### Job Kinds

`ExecutionJobKind` categorises workloads so workers can filter what they claim:

| Kind | Description |
|------|-------------|
| Geoprocessing | Analytical compute (buffer, spatial join, etc.) |
| ExtractTransformLoad | Data movement and transformation |
| TileCache | Tile cache generation or refresh |

`IJobQueue.TryClaimAsync` accepts an optional `acceptedKinds` filter; when
omitted the worker accepts all kinds.

### Priority Queue

Jobs are dequeued in priority order. Within a priority band, FIFO ordering
applies.

| Priority | Score Band | Use case |
|----------|-----------|----------|
| Critical | 0 | Operator-initiated urgent work |
| High | 1 × 10¹² | Time-sensitive processing |
| Normal | 2 × 10¹² | Default for most workloads |
| Low | 3 × 10¹² | Background or deferrable work |

Millisecond timestamps break ties within a band.

### Retry Policy

- `JobRetryPolicy` specifies `MaxAttempts`, `BackoffStrategy` (Fixed, Linear,
  Exponential), `BaseDelay`, and `MaxDelay`.
- Default: 3 attempts with exponential backoff starting at 30 seconds, capped
  at 10 minutes.
- When a heartbeat expires and retries remain, the reconciler requeues the job
  with a computed backoff delay. `AttemptCount` on the record tracks attempts.
- When retries are exhausted, the job transitions to Failed.

### Timeout Policy

- `JobTimeoutPolicy` specifies `MaxDuration`.
- Default: 1 hour. A `LongRunning` preset provides a 24-hour ceiling.
- Enforcement is dual-layered: `JobExecutionService` sets a
  `CancellationTokenSource` timeout for in-process detection, while
  `JobReconciliationService` catches workers that crash without cancelling.
- Jobs that exceed their timeout are marked Failed and are **not** retried.

### Cancellation

- API-side cancellation uses the existing `IJobCancellationNotifier` to signal
  in-flight workers. The worker registers its per-job cancellation token source
  before the `Provisioning → Running` transition so that operator cancellation
  arriving during that window is delivered through the token rather than as a
  direct store write. If no worker token is registered, the API marks the job
  as Cancelled directly and removes it from the queue.
- Worker-side cancellation flows through `CancellationToken` passed to
  `IJobExecutor.ExecuteAsync`.
- When the reconciler requeues or terminally fails a job (heartbeat or timeout
  expiry), it revokes the stale worker's cancellation token source. This
  prevents a subsequent `Cancel()` call from returning a false positive for a
  token that no longer corresponds to an active execution.

### Progress Reporting

- Workers report progress through `IJobExecutionContext.ReportProgressAsync`,
  which updates `PercentComplete`, `CurrentPhase`, and refreshes the heartbeat
  on the `ExecutionJobRecord` in `IExecutionJobStore`.
- The existing `IUniversalProgressStore` remains the canonical progress surface
  for API consumers and admin dashboards. A substrate-level projection from
  `IExecutionJobStore` to `IUniversalProgressStore` is a follow-on integration
  point; until that projection exists, execution job progress is available
  through the execution job store directly.

### Structured Execution Logs

- `ExecutionLogEntry` captures timestamp, level, message, phase, and optional
  metadata as an opaque string dictionary (AOT-safe).
- `IExecutionLogStore` provides append-only writes during execution and read
  access for diagnostics. Redis list-backed implementation with configurable
  retention.
- When a job is abandoned and requeued for retry, the substrate persists any
  executor-reported warnings to the structured log before clearing them from
  the requeued record. On terminal failure (retries exhausted), warnings are
  retained on the `ExecutionJobRecord` for post-mortem inspection.

### Artifact References

- Workers publish artifact references through
  `IJobExecutionContext.PublishArtifactAsync`.
- References accumulate in `ExecutionJobRecord.ArtifactReferences` and are
  available for result packaging after terminal state.

### API/Worker Boundary

- `AddJobOrchestration()` registers shared infrastructure (queue, log store)
  and is safe for the lean API-serving image.
- `AddJobWorker()` registers the execution host and reconciliation service.
  Only worker or combined-mode hosts call this method.
- `IJobExecutor` implementations are registered per `ExecutionJobKind` and
  only resolved in worker hosts.

### Graceful Shutdown

When a worker host shuts down (e.g. rolling deployment, scale-down), in-flight
jobs are abandoned rather than marked as terminal failures. The worker itself
transitions the job back to Queued, clears the claim fields (`ClaimedBy`,
`ClaimedAt`, `LastHeartbeatAt`), and re-enqueues it immediately. This applies
both during active execution and during the pre-execution window between claim
and Running. Shutdown requeue always succeeds regardless of the job's retry
budget because a host shutdown is an infrastructure event, not an execution
failure.

Both the worker and the reconciler re-read the current job record before writing
any state transition. If the record is already terminal or the claim owner has
changed, the writer skips its update. Additionally, the reconciler re-validates
the expiry predicate (heartbeat or timeout) against the fresh record — a
heartbeat that landed between the sweep snapshot and the handler invocation
means the worker is still alive, and a job reclaimed with a fresh claim time has
not actually timed out. In both cases the transition is skipped. This
bidirectional guard prevents four race windows:

- The reconciler snapshots active jobs, then a worker finalizes before the
  reconciler handler runs — the reconciler would otherwise overwrite the
  terminal state.
- The reconciler requeues or fails a job, then the worker's post-execution
  handler runs — the worker would otherwise overwrite the reconciler's update.
- A heartbeat arrives between the sweep snapshot and the reconciler handler —
  the reconciler would otherwise requeue or fail a healthy running job.
- The reconciler snapshots a timed-out job, but the job is requeued and
  reclaimed (possibly by the same worker) with a fresh claim time before the
  handler runs — the reconciler would otherwise fail a new attempt that has
  not yet timed out.

If a worker crashes without clean abandonment, `JobReconciliationService`
detects the stale heartbeat and performs the same recovery.

### Integration with Canonical Process Model

The substrate supports the ADR-0029 canonical model as follows:

| Canonical Noun | Substrate Mapping |
|----------------|-------------------|
| AnalysisPlan | Serialized into `ExecutionJobSpec.Parameters` or a referenced artifact |
| ExecutionJob | `ExecutionJobRecord` with policies, claim, and heartbeat fields |
| Result Package | Assembled from `ArtifactReferences` after terminal state |
| Workspace/Artifact | Managed by `IWorkspaceLifecycleService`; references tracked via context |
| Provenance | Captured through `OperationAuditInfo` and structured execution logs |

## Consequences

- Follow-on tickets (#721, #724, #727) can implement `IJobExecutor` for their
  respective backends without modifying the substrate contracts.
- The API server image size and startup time are unaffected by executor
  dependencies.
- Redis remains the coordination layer for job state, consistent with ADR-0021
  and ADR-0025.
- The heartbeat/retry mechanism adds a background sweep but runs only in worker
  hosts, not in lean API deployments.
- The `ExecutionJobRecord` grows by ~7 fields; the existing Redis serialization
  via source-generated JSON handles this without runtime reflection.

## References

- ADR-0025: Multi-Provider Operation Architecture
- ADR-0026: AI-First Operator Contract
- ADR-0029: Geoprocess Canonical Model Mappings
- Ticket #360: Geoprocess framework comparative research and target model
- Ticket #681: Durable worker and job orchestration substrate
