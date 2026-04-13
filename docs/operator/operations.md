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

## Job Orchestration

The durable job orchestration substrate provides queuing, claim/heartbeat
liveness, retry, cancellation, progress reporting, and structured execution
logs for geoprocessing, ETL, and tile-cache workloads. The substrate
contract and infrastructure are implemented; end-to-end wiring from
submission endpoints through the queue to worker execution lands with the
per-kind executor tickets (#721, #724, #727).

> **Deployment note:** The substrate is split into API-side and worker-side
> registrations. The API image registers shared infrastructure (queue, log
> store) via `AddJobOrchestration()`. The worker or combined-mode image
> additionally registers the execution host and reconciliation sweep via
> `AddJobWorker()`. Lean API-only deployments do not run execution or
> reconciliation overhead. See
> [ADR-0031](../contributor/adr/0031-durable-job-orchestration-substrate.md).

### Supported Job Kinds

| Kind | Description |
|------|-------------|
| Geoprocessing | Analytical compute workloads (buffer, spatial join, etc.) |
| ExtractTransformLoad | Data movement and transformation workloads |
| TileCache | Tile cache generation or refresh workloads |

Workers can filter which job kinds they claim. In the default configuration,
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
the worker pumps heartbeats at the configured interval (default: 30 seconds).
The reconciliation service sweeps active jobs every 30 seconds. If a
worker's last heartbeat exceeds the heartbeat timeout (default: 90 seconds),
the job is considered abandoned. When `LastHeartbeatAt` has not yet been set
(the window between claim and first heartbeat pump), the reconciler uses
`ClaimedAt` as the reference timestamp. If retries remain, the reconciler
requeues the job with a computed backoff delay; otherwise the job transitions
to Failed.

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
applies.

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
| Retry backoff strategy | Exponential | `JobRetryPolicy.BackoffStrategy` — Fixed, Linear, or Exponential |
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
`LastHeartbeatAt`), and re-enqueues it with the applicable retry backoff
delay. Both the worker and the reconciler re-read the current job record
before writing any state transition. If the record is already terminal or
the claim owner has changed, the writer skips its update. This
bidirectional guard prevents two specific race windows:

- **Worker completes after reconciler snapshot**: the reconciler snapshots
  active jobs, then a worker finalizes the job before the reconciler
  handler runs. Without the guard the reconciler would overwrite the
  terminal state.
- **Reconciler transitions before worker finalizes**: the reconciler
  requeues or fails the job, then the worker's post-execution handler
  runs. Without the guard the worker would overwrite the reconciler's
  update.

If a worker crashes without clean abandonment, the reconciliation service
detects the stale heartbeat and performs the same recovery. This ensures
rolling deployments and scale-down events do not permanently fail jobs
that still have retry budget.

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

For full lifecycle semantics, see the
[AI Operator Contract](../developer/AI_OPERATOR_CONTRACT.md#workspace-lifecycle).

### GPServer Job Lifecycle

GPServer REST endpoints expose the canonical geoprocessing job lifecycle to
Esri clients. Currently, job status polling and cancellation are functional;
`submitJob` and `execute` return 501 pending process catalog and ExecutePlan
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
