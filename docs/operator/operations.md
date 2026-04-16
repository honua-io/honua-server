# Operations

Database management, connection tuning, query optimization, caching, and memory for Honua Server.

---

## Backups and Restores

### Restore Checklist

1. Restore the database snapshot.
2. Verify PostGIS extensions: `SELECT PostGIS_Version();`
3. Validate a known feature query to confirm data integrity.

---

## Zero-Downtime Migrations

Honua uses DbUp for migrations (`src/Honua.Postgres/Migrations/`).

1. **Add, don't drop**: make backward-compatible schema changes first (add columns, create tables).
2. **Deploy the new application** after the schema is in place.
3. **Remove old columns** in a later release, once no running instance depends on them.

### Compatibility Review Marker

Potentially breaking migrations must declare an explicit compatibility review in the SQL file so the rollout risk is visible in code review and CI.

Add a comment near the top of the migration:

```sql
-- honua:compatibility-review reason=removes v1-only column after two release windows
```

Use the marker when a migration performs top-level changes such as:
- `ALTER TABLE ... DROP COLUMN`
- `ALTER TABLE ... RENAME COLUMN`
- `ALTER TABLE ... ALTER COLUMN ... TYPE`
- `ALTER TABLE ... ALTER COLUMN ... SET NOT NULL`
- `DROP TABLE`, `DROP SCHEMA`, or `DROP SEQUENCE`

The compatibility marker does not make a migration safe by itself. It signals that the change needs an explicit rollout plan, backward-compatibility review, and recovery path.

### Rollout Checklist

1. Apply migrations in a rolling fashion.
2. Verify `/healthz/ready` and a critical query after each step.
3. Monitor error rates and latency during rollout.

---

## Connection Pool Sizing

### Quick Configuration

```bash
Limits__Connections__MaxConnectionPoolSize=100
Limits__Connections__MaxConcurrentQueries=100
Limits__Connections__RequestTimeout=00:02:00

ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=yourpassword;Minimum Pool Size=10;Maximum Pool Size=100;Connection Idle Lifetime=300"
```

### Sizing Rule

Keep `MaxConnectionPoolSize x replica_count` below the database `max_connections` minus headroom:

```
max_connections >= (pool_size x app_replicas) + headroom
```

### Monitoring

- Prometheus scrape endpoint: `GET /metrics` with admin credentials
- Honua metrics: `GET /api/v1/metrics/database`
- Postgres activity:
  ```sql
  SELECT count(*) AS active_connections
  FROM pg_stat_activity
  WHERE datname = 'honua' AND state = 'active';
  ```

### Pool Exhaustion

**Symptoms**: timeouts, latency spikes, "connection pool exhausted" logs.

**Fixes**:
- Increase `MaxConnectionPoolSize` and `MaxConcurrentQueries` in small steps.
- Add application replicas and lower per-replica pool size.
- Reduce slow queries and oversized result sets.
- Ensure Postgres `max_connections` can handle your total pool size.

---

## Query Optimization

### Honua Query Limits

These limits apply across protocols:

- `Limits__Query__MaxRecordCount`
- `Limits__Query__DefaultRecordCount`
- `Limits__Query__MaxOffset`
- `Limits__Query__MaxBboxAreaSqKm`
- `Limits__Query__QueryTimeout`
- `Limits__Connections__RequestTimeout`

### Spatial Analytics Limits (Pro)

The Pro-tier spatial analytics endpoints (`queryClusters`, `spatialJoin`, `queryBufferAggregate`, `queryDensity` and their OGC mirrors) bound input/output size and parameter ranges via `Limits__Analytics__*`. All caps are enforced server-side. The `queryBufferAggregate` `distance` parameter accepts a `unit` (meters/kilometers/feet/miles); its cap is applied in meters **after** unit conversion so non-meter units cannot be used to bypass the limit. DBSCAN `eps`, `dwithin` `distance`, and density `cellSize` are always meters on the wire and are capped directly.

- `Limits__Analytics__MaxInputFeatures` — input row cap (SQL `LIMIT n+1` overflow probe)
- `Limits__Analytics__MaxClusters` — per-result cap for clustering when `returnHullPerCluster=true`
- `Limits__Analytics__MaxDensityCells` — per-result cap for density binning
- `Limits__Analytics__MaxBufferDistanceMeters` — buffer-aggregate distance cap (meters, post unit conversion)
- `Limits__Analytics__MaxDWithinDistanceMeters` — spatial-join `dwithin` distance cap (meters)
- `Limits__Analytics__MinDensityCellSizeMeters` / `Limits__Analytics__MaxDensityCellSizeMeters` — density `cellSize` clamp
- `Limits__Analytics__MaxKMeansK` — K-Means partition cap
- `Limits__Analytics__MaxDbscanEpsMeters` — DBSCAN `eps` cap (meters)

Use `/api/v1/admin/config` to confirm effective values.

### Database Checks

**Index usage**:
```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM honua.features
WHERE layer_id = 1
AND ST_Intersects(geometry, ST_MakeEnvelope(-122.5, 37.7, -122.3, 37.8, 4326));
```

**Statistics refresh after bulk loads**:
```sql
ANALYZE honua.features;
```

**Spatial index sanity check**:
```sql
SELECT indexname, idx_scan
FROM pg_stat_user_indexes
WHERE schemaname = 'honua'
AND indexname LIKE '%geom%';
```

### If Queries Are Slow

- Check for missing indexes or sequential scans.
- Confirm statistics are current after imports.
- Reduce geometry complexity or simplify where possible.
- Tighten limits if users are requesting large result sets.

---

## Caching

Honua uses a layered caching approach:

| Layer | Surface | Notes |
|-------|---------|-------|
| **Edge / CDN** | Tile traffic, public read endpoints | Recommended for production |
| **Server output cache** | OGC / FeatureServer metadata | Short TTLs; invalidated on writes |
| **Response cache** | Query responses | Only safe for anonymous GETs |
| **Database query cache** | Prepared statements | Npgsql internal cache |

### Cache Metrics

| Endpoint | What it shows |
|----------|---------------|
| `GET /api/v1/metrics/cache` | Output cache hit/miss rates |
| `GET /api/v1/admin/performance/database/query-cache/statistics` | Prepared statement cache health |

**Investigate when**: miss rate spikes after deployments, high latency despite stable app metrics, or sudden drops in cache utilization.

---

## Job Orchestration

The durable job orchestration substrate provides queuing, claim/heartbeat
liveness, retry, cancellation, progress reporting, and structured execution
logs for geoprocessing, ETL, and tile-cache workloads. The substrate
contract and infrastructure are implemented; end-to-end wiring from
submission endpoints through the queue to worker execution lands with the
per-kind executor tickets (#721, #724, #727).

> **Deployment note:** The substrate is split into API-side and worker-side
> registrations. The API image registers shared infrastructure (queue, log
> store) via `AddJobOrchestration()`. A future worker or combined-mode image
> will additionally register the execution host and reconciliation sweep via
> `AddJobWorker()`. `AddJobWorker()` is not yet invoked from a host
> entrypoint; it will be wired when the first concrete executor is
> integrated in follow-on tickets (#721, #724, #727). Lean API-only
> deployments will not run execution or reconciliation overhead. See
> [ADR-0031](../contributor/adr/0031-durable-job-orchestration-substrate.md)
> and [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md#apiworker-host-separation).

### Supported Job Kinds

| Kind | Description |
|------|-------------|
| Geoprocessing | Analytical compute workloads (buffer, spatial join, etc.) |
| ExtractTransformLoad | Data movement and transformation workloads |
| TileCache | Tile cache generation or refresh workloads |

Workers can filter which job kinds they claim. In the default configuration
a single worker host processes all kinds.

### Job Lifecycle

| State | Meaning |
|-------|---------|
| Queued | In the queue, not yet claimed by a worker |
| Provisioning | Worker has claimed the job, preparing to execute |
| Running | Execution is in progress |
| Succeeded | Terminal: completed successfully |
| Failed | Terminal: failed (may be retried if policy allows) |
| Cancelled | Terminal: cancelled by user or system |

### Heartbeat and Liveness

Workers poll for claimable jobs every 5 seconds. Once a job is claimed,
the worker registers its cancellation token and re-reads the job record
before promoting to Running. If the job was cancelled or reached another
terminal state during the claim-to-Running window, the worker skips
execution and removes the queue entry. If the job was requeued or
reclaimed by another worker, the worker skips execution but preserves
the queue entry since it belongs to the new attempt. This prevents
operator cancellations arriving between claim and Running from being
silently overwritten, while also protecting pending retries from
accidental deletion.

Once the Running transition succeeds, the worker pumps heartbeats at the
configured interval (default: 30 seconds). The heartbeat pump, progress
reports, and artifact publications all verify ownership before writing —
if the reconciler requeues the job or another worker reclaims it, the
stale worker's writes are silently dropped. When a new worker registers
its cancellation token for a reclaimed job, the previous worker's token
source is cancelled automatically so the stale executor observes
cancellation promptly even if the reconciler's Revoke has not yet run.
Transient store failures during heartbeat persistence are caught and
logged; the pump continues on the next interval rather than faulting.
If the pump task does fault for any reason, finalization still proceeds
from the executor outcome. The reconciliation service
sweeps active jobs every 30 seconds. If a worker's last heartbeat exceeds
the heartbeat timeout (default: 90 seconds), the job is considered
abandoned. The claim itself sets `LastHeartbeatAt` to the claim time, so
the reconciler normally uses this value; as a defensive fallback it uses
`ClaimedAt` if `LastHeartbeatAt` is ever null. If retries remain, the
reconciler requeues the job with a computed backoff delay; otherwise the
job transitions to Failed.

### Stale Claim Recovery

The queue uses a two-phase claim: an atomic Redis move from the pending
set to the claimed set, followed by a job store update. If the store
update fails (e.g. transient Redis error), the claim is rolled back
immediately. If the rollback also fails, the reconciliation service
detects the orphaned claim after 60 seconds and requeues the job
automatically. No operator intervention is required; monitor for
`RedisJobQueue` Warning entries (`OrphanedClaimRequeued`,
`ClaimRolledBack`) or Error entries (`ClaimRollbackFailed`) if you
want visibility into recovery events.

### Priority Queue

Jobs are dequeued in priority order. Within a priority band, FIFO ordering
applies. Delayed entries (jobs requeued with a visibility delay for retry
backoff) and kind-mismatched entries do not consume the scan budget.
Each poll scans up to 100 claimable candidates within a traverse window
of 5,000 total entries. A rotating cursor advances the window across
successive polls so that ready jobs beyond the first traverse window are
discovered on subsequent claim attempts. When the queue is drained from
the cursor position, the cursor resets to the beginning. Exceeding the
traverse threshold is logged at Warning level to signal queue pathology.

| Priority | Use case |
|----------|----------|
| Critical | Operator-initiated urgent work |
| High | Time-sensitive processing |
| Normal | Default for most workloads |
| Low | Background or deferrable work |

### Retry Policy

Default: 3 attempts with exponential backoff starting at 30 seconds, capped
at 10 minutes. Per-job override is supported via `JobRetryPolicy` on the
`ExecutionJobRecord`.

Supported backoff strategies: `Fixed` (constant delay), `Linear` (delay
grows linearly per attempt), `Exponential` (delay doubles per attempt).
Use `JobRetryPolicy.None` to disable retries for a specific job.

### Timeout Policy

Default maximum execution duration is 1 hour. Long-running ETL or
geoprocessing workloads can use the 24-hour policy. Jobs exceeding their
timeout are marked Failed directly by the worker (without retry). The
reconciler serves as a fallback for jobs whose workers stop reporting
heartbeats.

### Structured Execution Logs

Workers append `ExecutionLogEntry` records (timestamp, level, message,
phase, optional metadata) through `IExecutionLogStore`. Logs are
append-only during execution and read-only after terminal state. Redis-backed
with 7-day retention applied when the owning worker finalizes the job.

When a job is abandoned and requeued for retry, any warnings reported by the
executor are persisted to the structured log before clearing them from the
requeued record. This persistence is best-effort: a transient log-store
failure is logged at Warning level but does not block the durable
requeue or terminal transition. On terminal failure (retries exhausted),
warnings are retained on the job record for post-mortem inspection.

> **Note:** Execution logs are stored internally but are not yet exposed
> through a public REST endpoint. Retrieval is available only through
> direct Redis access or internal diagnostics.

### Monitoring

Active jobs surface through the existing operations endpoints:

- `GET /api/v1/admin/operations/active` — lists all active operations
- `GET /api/v1/admin/operations/{operationId}` — job progress and status
- `GET /api/v1/admin/operations/type/Geoprocessing` — geoprocessing jobs

> **Note:** The operations endpoints read from `IUniversalProgressStore`.
> Execution job progress written through `IJobExecutionContext` is stored in
> `IExecutionJobStore` and is not yet projected to the operations surface.
> A substrate-level projection is a follow-on integration point.

The reconciliation service logs sweep results at Debug level only when at
least one job was reconciled; clean sweeps are silent. Heartbeat and
timeout expiry are logged at Warning/Error level. See
[Monitoring — Job Orchestration Observability](monitoring.md#job-orchestration-observability)
for the full log level table across `JobExecutionService`,
`JobReconciliationService`, and `RedisJobQueue`.

### Policy Defaults and Tuning

The substrate's timing and retry behavior is governed by built-in defaults on
the policy models. Per-job overrides are set on the `ExecutionJobRecord` at
submission time.

| Parameter | Default | Notes |
|-----------|---------|-------|
| Worker claim poll interval | 5 s | How often workers check for claimable jobs |
| Heartbeat interval | 30 s | `JobHeartbeatPolicy.Interval` — how often the worker refreshes liveness |
| Heartbeat timeout | 90 s | `JobHeartbeatPolicy.Timeout` — stale threshold before reconciler acts |
| Reconciliation sweep interval | 30 s | How often the reconciler checks active jobs for expiry |
| Stale claim threshold | 60 s | Orphaned claims in the claimed set are recovered after this window |
| Retry max attempts | 3 | `JobRetryPolicy.MaxAttempts` — includes initial attempt (1 = no retries) |
| Retry backoff strategy | Exponential | `JobRetryPolicy.Strategy` — Fixed, Linear, or Exponential |
| Retry base delay | 30 s | `JobRetryPolicy.BaseDelay` — starting delay for first retry |
| Retry max delay | 10 min | `JobRetryPolicy.MaxDelay` — upper bound on computed backoff |
| Execution timeout | 1 h | `JobTimeoutPolicy.MaxDuration` — default ceiling |
| Long-running timeout | 24 h | `JobTimeoutPolicy.LongRunning` preset for ETL/large workloads |
| Execution log retention | 7 d | TTL applied to Redis log lists at job finalization |

These values are compile-time defaults on the policy record types. To change
defaults for all jobs, modify the submission path that creates
`ExecutionJobRecord` instances. To change a single job, set the corresponding
policy field on the record before enqueuing.

### Graceful Shutdown

When a worker host shuts down, in-flight jobs are abandoned rather than
marked as terminal failures. The worker itself transitions the job back to
Queued, clears the claim fields (`ClaimedBy`, `ClaimedAt`,
`LastHeartbeatAt`), and re-enqueues it immediately. This applies both
during active execution and during the pre-execution window between
claim and Running — if shutdown arrives before the executor is entered,
the claim loop performs the same force-requeue. Shutdown requeue always
succeeds regardless of the job's retry budget because a host shutdown is
an infrastructure event, not an execution failure.

Both the worker and the reconciler re-read the current job record
before writing any state transition. If the record is already terminal or
the claim owner has changed, the writer skips its update. Additionally,
the reconciler re-validates the expiry predicate (heartbeat or timeout)
against the fresh record — a heartbeat that landed between the sweep
snapshot and the handler invocation means the worker is still alive, and a
job reclaimed with a fresh claim time has not actually timed out. In both
cases the transition is skipped. This bidirectional guard prevents four
specific race windows:

- **Worker completes after reconciler snapshot**: the reconciler snapshots
  active jobs, then a worker finalizes the job before the reconciler
  handler runs. Without the guard the reconciler would overwrite the
  terminal state.
- **Reconciler transitions before worker finalizes**: the reconciler
  requeues or fails the job, then the worker's post-execution handler
  runs. Without the guard the worker would overwrite the reconciler's
  update.
- **Heartbeat arrives between sweep and handler**: a heartbeat lands
  after the reconciler's sweep snapshot but before the handler runs.
  Without the guard the reconciler would requeue or fail a healthy
  running job.
- **Timeout snapshot outdated by reclaim**: the reconciler snapshots a
  timed-out job, but the job is requeued and reclaimed (possibly by the
  same worker) with a fresh claim time before the handler runs. Without
  the guard the reconciler would fail a new attempt that has not yet
  timed out.

When the reconciler requeues or terminally fails a job, it also revokes the
stale worker's cancellation token source. This ensures that a subsequent
API-side `Cancel()` call does not return a false positive for a token that
no longer corresponds to an active execution.

In split API/worker deployments, the API host has no local cancellation
notifier for jobs running on remote workers. When a cancel request arrives
for a claimed job and no local notifier can signal the worker, the API
persists a `CancellationRequestedAt` timestamp on the job record as a
durable cancellation signal. The worker observes this signal during its
next heartbeat read and cancels locally. If the worker's heartbeat expires
before it processes the signal, the reconciler honours the request with a
terminal Cancelled state instead of retrying.

If a worker crashes without clean abandonment, the reconciliation service
detects the stale heartbeat and performs the same recovery. This ensures
rolling deployments and scale-down events do not permanently fail jobs
that still have retry budget.

---

## Workflow Orchestration

The workflow orchestration layer composes canonical `AnalysisPlan` jobs into
declarative multi-step DAG runs. A `WorkflowDefinition` specifies steps,
dependencies, artifact-to-input bindings, per-step retry policy, failure
policy, and an optional cron trigger. Runs are reconciled by a lease-based
engine on top of the durable job orchestration substrate; every step is
submitted through `IWorkflowJobExecutor` (backed by the geoprocessing job
service) so workflow steps reuse the same claim, heartbeat, retry, and
cancellation semantics described in [Job Orchestration](#job-orchestration).

> **Deployment note:** Orchestration stores (`IWorkflowDefinitionStore`,
> `IWorkflowRunStore`) only register when an `IConnectionMultiplexer` is
> available. Redis-less deployments do not host the orchestration engine or
> its background services; the admin cancel endpoint returns `503` for
> `Orchestration` operations in that configuration.

### Run Lifecycle

| State | Meaning |
|-------|---------|
| Pending | Run created; reconciliation has not yet started |
| Running | Engine is reconciling steps |
| Succeeded | All required steps reached a successful terminal state |
| Failed | A required step exhausted retries under `Fail` policy or a workflow invariant broke |
| Cancelled | Cancellation was requested; cascade cancellation finishes on the next tick |

Per-step states are `Pending`, `Queued`, `Running`, `Succeeded`, `Failed`,
`Cancelled`, and `Skipped`. The engine maps the underlying execution-job
status onto the step status, and cascades failure/skip decisions across
dependents according to each step's `FailurePolicy`.

### Step Wiring

- `DependsOn` — structural order; a step enters `Pending` eligibility only when
  every listed dependency is terminal.
- `InputBindings` — artifact-to-input wiring. `SourceArtifactSelector` accepts
  `artifact:{index}` or `artifact:{label}` and resolves against the upstream
  step's `AnalysisResultPackage.Artifacts` (label match is case-insensitive).
  The resolved reference is string-substituted into the downstream
  `AnalysisPlanStep.Inputs` dictionary without extending the canonical model.
- `RetryPolicy` — optional. Computes exponential backoff as
  `min(InitialDelaySeconds * BackoffMultiplier^(attempt-1), MaxDelaySeconds)`.
  A null policy means a single attempt with no retry.
- `FailurePolicy` — `Fail` propagates run failure and cancels non-terminal
  dependents; `Skip` marks the step `Skipped` and cascades `Skipped` only to
  dependents that bind artifacts from this step (structural-only dependents
  still proceed).
- `TimeoutSeconds` — optional per-step wall-clock bound. When set to a
  positive value it is surfaced to the underlying job substrate via the
  `orchestration.timeoutSeconds` protocol metadata key; null or
  non-positive values are omitted. Substrate enforcement is executor-specific.

### Idempotency and Crash Safety

- Each submission uses the idempotency key `{runId}:{stepId}:{attemptCount}`,
  so retries and crash recovery never enqueue duplicate jobs.
- Protocol metadata `orchestration.runId`, `orchestration.workflowId`,
  `orchestration.stepId`, and `orchestration.attempt` is stamped on each
  submitted job so downstream rate/cost controls and audit surfaces can
  correlate step executions to their parent run. When the step definition
  sets a positive `TimeoutSeconds`, the value is also stamped as
  `orchestration.timeoutSeconds`.
- When a completed step's artifact retrieval fails and any downstream step
  binds artifacts from it, the step is marked `Failed` with a descriptive
  error rather than succeeding with null artifacts. This prevents the
  workflow from silently producing a null-artifact success and ensures
  operators see the real cause (event `8120`).
- All state is persisted through `IWorkflowRunStore` before side effects.
  After a crash the reconciler rehydrates state from Redis and resumes from
  the same DAG position; no in-memory workflow state is required.
- `IWorkflowRunStore` extends `IOperationStore`, so reconciliation uses the
  canonical lease pattern with a 30-second lease renewed every 10 seconds.

### Scheduler

A separate background service evaluates cron-triggered definitions once per
tick (30-second interval). For each definition, the scheduler:

1. Compiles the trigger's 5-field cron expression with the declared IANA
   time zone (default UTC) and caches the compiled expression.
2. Seeds its in-memory cursor from the durable per-workflow cursor so
   restarts never rewind into previously-fired occurrences.
3. Claims the fire-time occurrence via `TryClaimScheduleFireAsync` so only
   one replica creates a run per (workflow, fire-time) pair.
4. Advances the durable cursor only after the winning replica successfully
   creates the run, or after the definition is deleted/permanently invalid.
   Transient `CreateRunAsync` failures release the claim so the same
   occurrence can be retried on a later tick without losing the fire.

Invalid cron expressions or unknown time zones are skipped and logged at
`Warning` with event `8116`; the workflow stops firing until the definition
is corrected.

Deleting a scheduled workflow clears its durable cursor so a later recreate
of the same workflow id starts fresh; the scheduler also evicts its
in-memory compiled-cron cache on the next tick for any workflow id that is
no longer present, so the replacement definition never inherits the
predecessor's fire history.

### Cancellation

Cancellation on an `Orchestration` operation flows through
`/api/v1/admin/operations/{runId}/cancel` to the
`WorkflowOrchestrationEngine`, which marks the run `Cancelled` on the durable
record. The cancel path acquires the same per-run reconcile lease used by the
reconciler so a concurrent tick cannot overwrite the `Cancelled` status with
a stale `SetAsync`. Cancelled runs stay in the active reconcile set until
every step reaches a terminal state, so cascade cancellation keeps making
progress across ticks. On each reconcile pass over a cancelled run the engine
inspects every step:

- `Pending` steps transition to `Cancelled` immediately.
- `Queued` or `Running` steps are cancelled through `CancelJobAsync` on the
  job executor before the step moves to `Cancelled`. Best-effort cancel
  failures are recorded as run warnings but do not block terminal transition.
- Already-terminal steps are left as-is.

The engine swallows `GeoprocessingNotFoundException` and
`GeoprocessingPreconditionFailedException` from the cascade so partially
pruned or already-terminal child jobs stay idempotent.

If the cancel path cannot acquire the reconcile lease within its bounded
retry window (a concurrent reconcile tick is holding it longer than expected),
the endpoint returns a shaped `409 Conflict` with a retriable message rather
than a `500`. The run state has not been mutated, so callers can safely retry
the same cancel request.

If the workflow definition is deleted while a run is still active, the next
reconcile tick fails the run with a descriptive error and finalises every
non-terminal step state to `Cancelled`. This guarantees the run leaves the
active reconcile set exactly once and emits terminal telemetry.

### Policy Defaults and Tuning

| Parameter | Default | Notes |
|-----------|---------|-------|
| Reconcile poll interval | 5 s | `WorkflowOrchestrationBackgroundService` poll cadence |
| Reconcile lease duration | 30 s | Matches the deploy reconciler lease pattern |
| Reconcile lease renewal | 10 s | Background renewal while a run is being reconciled |
| Scheduler tick interval | 30 s | `WorkflowSchedulerBackgroundService` cadence |
| Scheduler claim retention | 24 h | Window during which a `(workflow, fire-time)` claim is honoured |
| Run progress retention | 7 d | TTL applied to the `WorkflowProgress` projection in `IUniversalProgressStore` |

### Persistence

| Store | Key pattern | TTL | Index sets |
|-------|-------------|-----|------------|
| `RedisWorkflowDefinitionStore` | `orchestration:def:{workflowId}` | none | `orchestration:def:all` |
| `RedisWorkflowRunStore` | `orchestration:run:{runId}` | 7 d | `orchestration:run:active`, `orchestration:run:wf:{workflowId}` |
| Run leases | `orchestration:run:lease:{runId}` | 30 s | — |
| Schedule claims/cursors | `orchestration:schedule:*` | claim retention / unbounded cursor | — |

All serialization uses the source-generated `OrchestrationJsonContext`;
orchestration does not introduce reflection or runtime JSON discovery.

### Observability

`WorkflowOrchestrationEngine`, the reconciler loop, and the scheduler emit
structured logs in the `8100-8199` event-id band (`OrchestrationLog`). Notable
events include `8100 WorkflowRunCreated`, `8101 WorkflowRunCompleted`,
`8102 WorkflowStepSubmitted`, `8104 WorkflowStepRetrying`,
`8105 WorkflowStepSkipped`, `8107 InputBindingFailed`,
`8108 SchedulerTriggered`, `8110 ReconciliationFailed`,
`8116 SchedulerDefinitionInvalid`, `8117 WorkflowStepCancelJobFailed`,
`8119 WorkflowCancelLeaseContention`, and
`8120 WorkflowStepArtifactsUnavailableForBoundDependents`.

The engine contributes activities
(`honua.orchestration.reconcile_run`, `honua.orchestration.execute_step`,
`honua.orchestration.resolve_bindings`, `honua.orchestration.scheduler_tick`)
and metrics (`honua.orchestration.runs_created_total`,
`honua.orchestration.runs_completed_total`,
`honua.orchestration.steps_completed_total`,
`honua.orchestration.steps_retried_total`,
`honua.orchestration.run_duration_ms`,
`honua.orchestration.step_duration_ms`).

---

## OGC API Processes

The OGC API Processes adapter is always registered and serves routes under
`/ogc/processes`. Job lifecycle operations (execute, list, status, results,
dismiss) require Redis-backed durable storage; they return `503` when the
store is not configured.

### Configuration

All settings live under `OgcProcesses` (env prefix `OgcProcesses__`):

| Setting | Default | Description |
|---------|---------|-------------|
| `DefaultJobLimit` | 100 | Maximum jobs returned per `GET /ogc/processes/jobs` request |

---

## Workspace Lifecycle

Geoprocessing workspaces manage temporary and durable artifacts produced by
analysis workflows. The workspace lifecycle service handles creation, retention,
promotion, and background cleanup.

> **Note:** Workspace lifecycle requires concrete `IWorkspaceStore` and
> `IArtifactStore` implementations to be registered. The lifecycle service and
> background cleanup are skipped when no store provider is available.

### Configuration

All settings live under `Geoprocessing:Workspace` (env prefix
`Geoprocessing__Workspace__`):

| Setting | Default | Description |
|---------|---------|-------------|
| `CleanupInterval` | 15 minutes | How frequently the cleanup service runs |
| `CleanupGracePeriod` | 1 hour | Grace period after expiration before deletion |
| `EnableAutomaticCleanup` | `true` | Whether background cleanup is active |
| `MaxCleanupBatchSize` | 100 | Workspaces processed per sweep |
| `ScratchDefaultTtl` | (built-in: 1 hour) | TTL override for scratch workspaces |
| `TempLayerDefaultTtl` | (built-in: 24 hours) | TTL override for temp layer workspaces |
| `ResultCollectionDefaultTtl` | (built-in: 7 days) | TTL override for result collections |
| `MaxWorkspaceCount` | (built-in: 100) | Per-owner workspace limit |
| `MaxArtifactCount` | (built-in: 1,000) | Per-owner artifact limit |
| `MaxStorageBytes` | (built-in: 10 GB) | Per-owner storage limit |

Use `/api/v1/admin/config` to confirm effective values at runtime.

Nullable TTL and quota settings fall back to built-in defaults when unset.
`MaxTimeToLive` and `AllowPromotionBeforeCleanup` per workspace kind are not
config-overridable. TTL overrides are validated at startup; values exceeding
the `MaxTimeToLive` ceiling for the kind are rejected. Per-request TTL values
supplied during workspace creation are silently clamped to the ceiling rather
than rejected.

Quota enforcement is caller-initiated: the lifecycle service does not
automatically check quotas during workspace creation. gRPC endpoints and
workflow orchestrators should call `EvaluateQuota` before creating workspaces.

### Retention Defaults

| Workspace Kind | Default TTL | Max TTL | Promotion allowed |
|----------------|-------------|---------|-------------------|
| Scratch | 1 hour | 24 hours | Yes |
| TempLayer | 24 hours | 7 days | Yes |
| Persistent | none | none | No |
| SavedLayer | none | none | No |
| ResultCollection | 7 days | 30 days | Yes |

### Cleanup Behavior

Cleanup runs in two phases:

1. **Expire** — active workspaces at or past their expiration (`>=`) transition
   to `Expired`.
2. **Delete** — expired workspaces at or past the grace period boundary have
   their artifacts deleted, then the workspace is removed.

Artifacts in expired workspaces can still be promoted to durable workspaces
during the grace period when the retention policy allows it. Individual
failures during a sweep are recorded without halting cleanup, so one failing
workspace does not block subsequent workspaces. If any artifact in a workspace
fails to delete, the workspace itself is skipped for that sweep to prevent
orphaned artifact records.

The lifecycle rules described in this section are the current workspace contract for expiration, grace-period promotion, and cleanup behavior.

### GPServer Job Lifecycle

GPServer REST endpoints expose the canonical geoprocessing job lifecycle to
Esri clients. Currently, job status polling and cancellation are functional;
`submitJob` and `execute` return 501 pending GPServer per-task projection of the
built-in `IProcessCatalog` (14 seeded processes) and canonical `ExecutePlan`
support. The mapping between admin operation tracking and GPServer responses
(once submission is available):

| Admin operation state | GPServer `jobStatus` | Endpoint |
|-----------------------|----------------------|----------|
| Queued | `esriJobSubmitted` | `submitJob` returns 202 |
| Provisioning | `esriJobWaiting` | `jobs/{jobId}` status poll |
| Running | `esriJobExecuting` | `jobs/{jobId}` status poll |
| Succeeded | `esriJobSucceeded` | `jobs/{jobId}` status (result URLs pending result-storage support) |
| Failed | `esriJobFailed` | `jobs/{jobId}` with error messages |
| Cancelled | `esriJobCancelled` | After `jobs/{jobId}/cancel` |

GPServer jobs appear in `GET /api/v1/admin/operations/type/Geoprocessing`
alongside jobs submitted through the gRPC `ProcessService`. Both share the
same underlying `IExecutionJobStore` and `IUniversalProgressStore`, so admin
observability covers all protocol surfaces.

---

## Memory Optimizations

Key memory-management patterns in Honua:

- **Array pooling** for large buffers in geometry and streaming paths (`MemoryPool.cs`)
- **Streaming APIs** for large result sets (`IStreamingFeatureStore`)
- **Geometry processing optimizations** for coordinate handling
- **Response and metadata caching** to reduce repeated allocations (`MemoryResponseCache.cs`)
