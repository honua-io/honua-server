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

Output publication does not add values to this enum. Jobs with fenced output
intents retain `ExecutionJobStatus.Running` while a durable, orthogonal
`OutputPublicationPhase` is `Finalizing` or `Terminalizing`. During
either phase the job admits no new execution and is excluded from execution
heartbeat and timeout expiry as well as requeue. During terminalization the record also persists
the requested terminal status (`Failed` or `Cancelled`). The coordinator
changes the canonical status only after every sink intent is committed or
aborted. Pre-dispatch cancellation may conditionally change an unclaimed
`Queued` record to canonical `Running` while entering `Terminalizing`; in that
case `Running` is only the publication-phase carrier and does not append an
execution attempt or imply that execution began. Success additionally requires
the job-wide output-set manifest from ADR-0071 to be `Complete` for one winning
attempt; individual committed sink records are not projected into
`ArtifactReferences` before that point. Jobs without output intents keep the
existing direct transitions.

The `Running` to `Finalizing` handoff is a conditional job-store update. It
compares the expected record version, current execution claim, attempt
identifier, fencing token, and absence of `CancellationRequestedAt` before
changing the publication phase. A stale or already-cancelled attempt cannot
enter `Finalizing`, release the current execution claim, or publish; only the
winning update may stop its execution heartbeat and transfer recovery ownership
to the publication lease. If the same compare-and-set observes a pending
cancellation, the coordinator instead records requested status `Cancelled`,
enters `Terminalizing`, references the prepared output set, and transfers
recovery to a new publication-lease generation. It never starts sink commits or
passes through `Finalizing`; the output reconciler aborts the intents before the
canonical job becomes `Cancelled`.

Cancellation during `Finalizing` is also a conditional job-store transition.
It compares the expected record version, canonical `Running` status,
`Finalizing` phase, winning attempt/output-set manifest, and publication-lease
generation before recording requested status `Cancelled`, changing the phase
to `Terminalizing`, and advancing the publication-lease generation. It does
not depend on an execution claim, because that claim may already have been
released. A final-success update and this cancellation update contend on the
same job record: success that was already made durable remains terminal, while
a winning cancellation update prevents the manifest from becoming `Complete`
or the job from becoming `Succeeded`. An output reconciler must re-read the
phase and lease generation before starting each sink action and before its
final manifest/job update. A sink commit already in flight contends with abort
on the sink intent's attempt fence and record version; committed members remain
committed, but they are not exposed through the incomplete manifest, and every
remaining member is aborted before the job becomes `Cancelled`.

### Claim and Heartbeat

- `IJobQueue.TryClaimAsync` atomically removes a job from the pending set and
  associates it with a worker ID. The claim sets `ClaimedBy`, `ClaimedAt`, and
  `LastHeartbeatAt` on the `ExecutionJobRecord`.
- Workers pump heartbeats at the interval specified by `JobHeartbeatPolicy`
  (default: 30 seconds). Each heartbeat updates `LastHeartbeatAt`.
- `JobReconciliationService` sweeps active jobs every 30 seconds. If
  `LastHeartbeatAt` exceeds the heartbeat timeout (default: 90 seconds), the
  job is considered abandoned only while its output publication phase is not
  `Finalizing` or `Terminalizing`. Those phases prove that execution has ended
  and must never be requeued by the execution-heartbeat sweeper.
- Output reconciliation owns a separate durable lease and heartbeat/deadline
  while the publication phase is `Finalizing` or `Terminalizing`. An expired
  publication lease is recovered only through fenced, idempotent output
  reconciliation. That reconciler may complete publication or terminalize the
  job according to policy, but it never starts a new execution attempt.

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
- When retries are exhausted, a job without a current fenced sink intent
  transitions directly to Failed. A job with any such intent instead records
  requested status `Failed` and enters `OutputPublicationPhase.Terminalizing`.
  The output reconciler conditionally aborts every uncommitted intent under its
  attempt fence before changing the canonical status to Failed. Exhausting the
  retry budget therefore never bypasses sink terminalization.

### Timeout Policy

- `JobTimeoutPolicy` specifies `MaxDuration`.
- Default: 1 hour. A `LongRunning` preset provides a 24-hour ceiling.
- Enforcement is dual-layered: `JobExecutionService` sets a
  `CancellationTokenSource` timeout for in-process detection, while
  `JobReconciliationService` catches workers that crash without cancelling.
- `Finalizing` and `Terminalizing` are not executing phases and are excluded
  from `MaxDuration` expiry. Their separate publication deadline and fenced
  output reconciler exclusively govern stalled publication or terminalization.
- Jobs that exceed their timeout are **not** retried. A job without fenced
  output intents is marked Failed directly. A job with such intents first
  records requested status `Failed` plus output phase `Terminalizing`, revokes
  the intents as defined by ADR-0071, and then changes the canonical status to
  Failed. This is one terminal transition with no requeue or replacement
  attempt, not a retry state.

### Cancellation

- API-side cancellation first branches on the durable output-publication phase.
  A job in `Finalizing` follows the claim-independent fenced transition to
  `Terminalizing` defined above. A job already in `Terminalizing` is handled
  idempotently by the output reconciler; an existing requested terminal status
  is not overwritten. These branches run before execution-claim inspection so
  a released execution claim cannot cause finalization cancellation to be
  misclassified as unclaimed execution.
- API-side cancellation first attempts to signal in-flight workers via the
  process-local `IJobCancellationNotifier`. The worker registers its per-job
  cancellation token source before the `Provisioning → Running` transition so
  that operator cancellation arriving during that window is delivered through
  the token rather than as a direct store write.
- When the local notifier confirms the signal was delivered, the worker owns
  the terminal state transition and the API returns immediately.
- When no local notifier can reach the worker (the common case in split
  API/worker deployments), the API checks the job's claim state:
  - **Unclaimed** (`ClaimedBy` is null): the API removes the job from the queue
    and marks it Cancelled directly when it owns no fenced output intent.
    When prepared intents exist, the API instead uses a job-store
    compare-and-set on the expected record version, canonical `Queued` status,
    absent claim, and publication phase `None`. The winning update changes the
    canonical status to `Running`, records requested status `Cancelled`, enters
    `Terminalizing`, and references the already-durable intents for the fenced
    output reconciler. It then removes any stale pending-queue member
    idempotently. A concurrent worker claim and this cancellation update contend
    on the same job record: if the claim wins, the API follows the actively
    claimed branch; if cancellation wins, the queue entry is no longer eligible
    for execution. The output reconciler aborts the referenced intents under
    their fences before changing the canonical status to Cancelled.
  - **Actively claimed**: the API persists `CancellationRequestedAt` on the
    `ExecutionJobRecord` as a durable cancellation signal. The worker observes
    this signal during its next heartbeat read and cancels locally. If the
    worker's heartbeat expires before it processes the signal, the reconciler
    honours the request instead of retrying. It first aborts fenced output
    intents under `OutputPublicationPhase.Terminalizing`, when present, and
    only then writes the terminal Cancelled status.
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

- For jobs without fenced output intents, workers publish artifact references
  through `IJobExecutionContext.PublishArtifactAsync`. Those references
  accumulate in `ExecutionJobRecord.ArtifactReferences` and are available for
  result packaging after terminal state.
- A worker producing a fenced output set must not append its members to public
  `ArtifactReferences`. It writes attempt-scoped references only to the private
  sink-intent/staging records governed by ADR-0071. After every required member
  is committed and the job-wide manifest becomes `Complete`, the coordinator
  conditionally projects the complete winning set into `ArtifactReferences` in
  one job-record update. A stale, partial, failed, or cancelled attempt therefore
  exposes no public result references, including through intermediate job
  projections.

### API/Worker Boundary

- `AddJobOrchestration()` registers shared infrastructure (queue, log store)
  and is safe for the lean API-serving image.
- `AddJobWorker()` registers the execution host and reconciliation service.
  Only worker or combined-mode hosts call this method.
- `IJobExecutor` implementations are registered per `ExecutionJobKind` and
  only resolved in worker hosts.

### Graceful Shutdown

When a worker host shuts down (e.g. rolling deployment, scale-down), in-flight
jobs that are still executing (`OutputPublicationPhase.None`) are abandoned
rather than marked as terminal failures. The worker conditionally transitions
the job back to Queued, clears the claim fields (`ClaimedBy`, `ClaimedAt`,
`LastHeartbeatAt`), and re-enqueues it immediately. This applies during active
execution and during the pre-execution window between claim and Running.
Shutdown requeue ignores the retry budget because a host shutdown is an
infrastructure event, but its update is conditional on the publication phase
remaining `None`.

A job in `Finalizing` or `Terminalizing` is never returned to the execution
queue during shutdown. The stopping worker leaves its canonical state, attempt
fence, and sink intents unchanged and conditionally relinquishes only a
publication lease it owns (or lets that lease expire). The fenced output
reconciler then resumes publication or terminalization from the durable phase;
it does not start another execution attempt.

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
