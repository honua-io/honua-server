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

### Retry Policy

- `JobRetryPolicy` specifies `MaxAttempts`, `BackoffStrategy` (Fixed, Linear,
  Exponential), `BaseDelay`, and `MaxDelay`.
- Default: 3 attempts with exponential backoff starting at 30 seconds, capped
  at 10 minutes.
- When a heartbeat expires and retries remain, the reconciler requeues the job
  with a computed backoff delay. `AttemptCount` on the record tracks attempts.
- When retries are exhausted, the job transitions to Failed.

### Cancellation

- API-side cancellation uses the existing `IJobCancellationNotifier` to signal
  in-flight workers. If the worker owns the terminal state transition, the API
  defers; otherwise it marks the job as Cancelled directly.
- Worker-side cancellation flows through `CancellationToken` passed to
  `IJobExecutor.ExecuteAsync`.

### Progress Reporting

- Workers report progress through `IJobExecutionContext.ReportProgressAsync`,
  which updates `PercentComplete`, `CurrentPhase`, and refreshes the heartbeat.
- The existing `IUniversalProgressStore` remains the canonical progress surface
  for API consumers and admin dashboards.

### Structured Execution Logs

- `ExecutionLogEntry` captures timestamp, level, message, phase, and optional
  metadata as an opaque string dictionary (AOT-safe).
- `IExecutionLogStore` provides append-only writes during execution and read
  access for diagnostics. Redis list-backed implementation with configurable
  retention.

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
