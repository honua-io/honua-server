# ADR-0081: PostgreSQL is the durable job substrate

## Status
Proposed (2026-11-09). Supersedes the Redis-as-system-of-record statements in
[ADR-0025](0025-multi-provider-operation-architecture.md) ("Redis and Durability") and
[ADR-0031](0031-durable-job-orchestration-substrate.md) ("Redis remains the coordination layer for
job state") for execution jobs, the job queue, job logs and geoprocessing result packages.
Workflow operations, proposals, operation instances and operation secrets stay on Redis until their
own change. Decision record: honua-server#6809 (founder decision and amendment, 2026-11-09).

## Context
Execution jobs, their queue, their logs and geoprocessing result packages were Redis strings, sorted
sets, lists and Lua scripts. A deployment without Redis could not run geoprocessing, OGC API
Processes or background imports, and losing Redis lost every job. PostgreSQL is already required,
already holds the catalog, and already runs a `FOR UPDATE SKIP LOCKED` outbox (migration 024).

The four interfaces do not change shape: `IExecutionJobStore`, `IJobQueue` (with
`IQueueClaimReconciler`), `IExecutionLogStore`, `IGeoprocessingResultPackageStore`. The guarantees do
not change: at-least-once execution with fencing by version compare-and-set, lease-owned updates, the
per-attempt job token and `CommittedEffectReferences`.

## Decision
One table holds a job and its queue position. The queue is a query over that table. Each PostgreSQL
store replaces its Redis store outright; there is no selection setting and no dual-store period.

The stores are `PostgresExecutionJobStore`, `PostgresJobQueue`, `PostgresExecutionLogStore`
(`src/Honua.Jobs`, raw Npgsql on an `NpgsqlDataSource`) and `PostgresGeoprocessingResultPackageStore`
(`src/Honua.Geoprocessing`). They live beside the consumers because `Honua.Worker.Gdal` references
`Honua.Jobs` and must not reference `Honua.Server` (ADR-0038), and the module policy forbids a
`Honua.Postgres` to `Honua.Jobs` reference in either direction.

SQL below is written with the default system schema `honua`; the migration uses `$HonuaSchema$` and
the stores quote the configured `Database:Schema`. The migration only creates tables and indexes.

### 1. Tables

```sql
CREATE TABLE honua.execution_jobs (
    operation_id      TEXT COLLATE "C" PRIMARY KEY CHECK (length(operation_id) BETWEEN 1 AND 512),
    -- attempt state: authoritative, stamped by the database, overlaid on record when read
    version           BIGINT NOT NULL CHECK (version > 0),
    status            TEXT NOT NULL CHECK (status IN
                          ('Queued', 'Provisioning', 'Running', 'Succeeded', 'Failed', 'Cancelled')),
    attempt_count     INTEGER NOT NULL CHECK (attempt_count >= 0),
    claimed_by        TEXT,
    claimed_at        TIMESTAMPTZ,
    last_heartbeat_at TIMESTAMPTZ,
    next_retry_at     TIMESTAMPTZ,
    completed_at      TIMESTAMPTZ,
    percent_complete  DOUBLE PRECISION,
    current_phase     TEXT,
    updated_at        TIMESTAMPTZ NOT NULL,
    -- local queue position: authoritative; NULL means "not in the local queue"
    queue_priority    SMALLINT,
    queue_visible_at  TIMESTAMPTZ,
    -- retention
    expires_at        TIMESTAMPTZ NOT NULL,
    -- the whole ExecutionJobRecord as last written
    record            JSON NOT NULL,
    -- projections of record, written in the same statement as record
    kind                   TEXT NOT NULL,
    runtime_profile        TEXT NOT NULL,
    backend                TEXT NOT NULL,
    priority               SMALLINT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL,
    tenant_id              TEXT,
    cancellation_requested BOOLEAN NOT NULL,
    heartbeat_timeout      INTERVAL NOT NULL,
    max_duration           INTERVAL NOT NULL,
    lookup_keys            TEXT[] NOT NULL,
    CHECK ((queue_visible_at IS NULL) = (queue_priority IS NULL)),
    CHECK (queue_visible_at IS NULL OR backend = 'local')
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_threshold = 200,
        autovacuum_analyze_scale_factor = 0.05, autovacuum_analyze_threshold = 200);

CREATE INDEX idx_execution_jobs_created ON honua.execution_jobs (created_at DESC, operation_id DESC);
CREATE INDEX idx_execution_jobs_status_created
    ON honua.execution_jobs (status, created_at DESC, operation_id DESC);
CREATE INDEX idx_execution_jobs_kind_created
    ON honua.execution_jobs (kind, created_at DESC, operation_id DESC);
CREATE INDEX idx_execution_jobs_lookup_keys ON honua.execution_jobs USING GIN (lookup_keys);
CREATE INDEX idx_execution_jobs_claimable
    ON honua.execution_jobs (runtime_profile, queue_priority, queue_visible_at, operation_id)
    WHERE status = 'Queued' AND queue_visible_at IS NOT NULL;
CREATE INDEX idx_execution_jobs_undispatched ON honua.execution_jobs (backend)
    WHERE status = 'Queued' AND queue_visible_at IS NULL;
CREATE INDEX idx_execution_jobs_reconcilable ON honua.execution_jobs (status)
    WHERE status IN ('Queued', 'Provisioning', 'Running')
      AND NOT (status = 'Queued' AND backend = 'local' AND attempt_count = 0
               AND NOT cancellation_requested);
CREATE INDEX idx_execution_jobs_expired ON honua.execution_jobs (expires_at)
    WHERE status IN ('Succeeded', 'Failed', 'Cancelled');

CREATE TABLE honua.leases (
    lease_key  TEXT COLLATE "C" PRIMARY KEY,
    owner_id   TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX idx_leases_expires ON honua.leases (expires_at);

CREATE TABLE honua.execution_job_log_streams (
    operation_id         TEXT COLLATE "C" PRIMARY KEY,
    appended_count       BIGINT NOT NULL DEFAULT 0,
    dropped_count        BIGINT NOT NULL DEFAULT 0,
    event_identity_count INTEGER NOT NULL DEFAULT 0,
    expires_at           TIMESTAMPTZ NOT NULL,
    CONSTRAINT execution_job_log_streams_event_identity_limit CHECK (event_identity_count <= 8192)
);
CREATE INDEX idx_execution_job_log_streams_expires ON honua.execution_job_log_streams (expires_at);

CREATE TABLE honua.execution_job_log_entries (
    operation_id TEXT COLLATE "C" NOT NULL
        REFERENCES honua.execution_job_log_streams (operation_id) ON DELETE CASCADE,
    sequence     BIGINT NOT NULL,
    entry        JSON NOT NULL CHECK (octet_length(entry::text) <= 4096),
    PRIMARY KEY (operation_id, sequence)
);

CREATE TABLE honua.execution_job_log_event_identities (
    operation_id TEXT COLLATE "C" NOT NULL
        REFERENCES honua.execution_job_log_streams (operation_id) ON DELETE CASCADE,
    event_id     TEXT COLLATE "C" NOT NULL CHECK (length(event_id) BETWEEN 1 AND 160),
    PRIMARY KEY (operation_id, event_id)
);

CREATE TABLE honua.geoprocessing_result_packages (
    job_id     TEXT COLLATE "C" PRIMARY KEY,
    package    JSON NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX idx_geoprocessing_result_packages_expires
    ON honua.geoprocessing_result_packages (expires_at);
```

`record` is `JSON`, not `JSONB`: the stores never query inside it, and `JSONB` rejects the `\u0000`
escape that a caller-supplied job parameter can carry.

`lookup_keys` holds one `dimension:VALUE` entry per filterable value of the record: `backend`,
`queue`, `requested-by`, `correlation`, `trace`, `definition` (definition id and geoprocessing plan
id), `resource` (one per resource reference), `environment`, `server`, `release`, `change-set`,
`alert`. The value is trimmed and upper-cased, which reproduces the Redis store's trimmed,
case-insensitive match. These are the dimensions the Redis store indexed.

Autovacuum starts at 2 % dead rows (plus 200) instead of the default 20 %: the claim index is a small
partial index over a table that churns, and dead entries in it lengthen every claim.

### 2. Record of truth and projections

| Columns | Role | Kept consistent by |
|---|---|---|
| `version`, `status`, `attempt_count`, `claimed_by`, `claimed_at`, `last_heartbeat_at`, `next_retry_at`, `completed_at`, `percent_complete`, `current_phase`, `updated_at` | Record of truth for those eleven `ExecutionJobRecord` fields. | A read deserializes `record` and overlays these columns. `record` holds the writer's copy of them, which a reader never sees. |
| `queue_priority`, `queue_visible_at` | Record of truth for the local queue. Not part of `ExecutionJobRecord`. | Written only by the queue statements and by the compare-and-set statement (3.3). |
| `expires_at` | Record of truth for retention. | Set by every write of the job. |
| `record` | Record of truth for every other field. | Rewritten by every write of the job, in the one statement that writes the columns. |
| `kind`, `runtime_profile`, `backend`, `priority`, `created_at`, `tenant_id`, `cancellation_requested`, `heartbeat_timeout`, `max_duration`, `lookup_keys` | Projections of `record`, used only in `WHERE` and `ORDER BY`. | Computed from the record in the store and written in the same statement as `record`. |

There is one write path: every create and every compare-and-set is a single statement that writes
`record`, the attempt state and the projections together. The claim (3.2) and the queue statements
(3.6) are the only writes that do not carry the record; they change only columns whose record of
truth is the column.

### 3. Statements

Every statement runs as its own transaction on a pooled connection. `now()` is therefore the
statement start time on the database server.

#### 3.1 Create if absent (`TryCreateAsync`)
```sql
INSERT INTO honua.execution_jobs (<all columns except the queue columns>)
VALUES (@operation_id, 1, @status, ..., now() + @retention, @record, <projections>)
ON CONFLICT (operation_id) DO NOTHING;
```
One affected row means created. `SetAsync` (used only by the operate fixture seeder) is the same
`INSERT` with `ON CONFLICT (operation_id) DO UPDATE` over every column except the queue columns.
These two are the only statements that store the caller's timestamps as given.

#### 3.2 Claim (`IJobQueue.TryClaimAsync`)
```sql
WITH next_job AS (
    SELECT operation_id FROM honua.execution_jobs
    WHERE status = 'Queued' AND queue_visible_at <= now()
      AND runtime_profile = @runtime_profile
      AND (@kinds::text[] IS NULL OR kind = ANY (@kinds))
    ORDER BY queue_priority, queue_visible_at, operation_id
    LIMIT 1
    FOR UPDATE SKIP LOCKED)
UPDATE honua.execution_jobs AS job
SET status = 'Provisioning', version = job.version + 1, attempt_count = job.attempt_count + 1,
    claimed_by = @worker_id, claimed_at = now(), last_heartbeat_at = now(), next_retry_at = NULL,
    current_phase = 'Claimed', updated_at = now()
FROM next_job WHERE job.operation_id = next_job.operation_id
RETURNING <attempt-state columns>, record;
```
A worker's accepted runtime profiles are `{managed}` when it declares none, as
`RuntimeProfiles.CanClaim` defines. A worker with one profile, which is every worker today, runs the
statement with `runtime_profile = @runtime_profile`, so the scan is the ordered range of
`idx_execution_jobs_claimable` for that profile and a native worker never reads managed rows. A
worker with several profiles runs it with `runtime_profile = ANY (@runtime_profiles)`.

`queue_priority` is 0 Critical, 1 High, 2 Normal, 3 Low. Within a band the order is the time the job
became visible, which is the Redis score: a requeued or retrying job goes to the back of its band, so
a job that keeps being handed back (partition-lease contention requeues with a 5 s delay and does
not consume an attempt) cannot hold the head of the band.

**Lost reply.** If the claim fails after it was sent, the queue remembers that for its worker id
(unique per process). The next `TryClaimAsync` first runs
```sql
SELECT <attempt-state columns>, record FROM honua.execution_jobs
WHERE claimed_by = @worker_id AND status = 'Provisioning' AND current_phase = 'Claimed'
ORDER BY claimed_at LIMIT 1;
```
and returns that job if there is one. A worker runs one job at a time and moves a job out of the
`Claimed` phase before it claims again, so such a row can only be the claim whose reply was lost.

#### 3.3 Version compare-and-set (`TrySetAsync`), heartbeat, progress, cancel request
```sql
UPDATE honua.execution_jobs SET
    version = version + 1,
    status = @status, attempt_count = @attempt_count, claimed_by = @claimed_by,
    percent_complete = @percent_complete, current_phase = @current_phase, updated_at = now(),
    claimed_at        = <stamp>(claimed_at, @claimed_at),
    last_heartbeat_at = <stamp>(last_heartbeat_at, @last_heartbeat_at),
    completed_at      = <stamp>(completed_at, @completed_at),
    next_retry_at = CASE WHEN @next_retry_at IS NULL THEN NULL
                         WHEN next_retry_at IS NOT DISTINCT FROM @next_retry_at THEN next_retry_at
                         ELSE now() + @retry_delay END,
    queue_visible_at = CASE
        WHEN @status IN ('Succeeded', 'Failed', 'Cancelled') THEN NULL
        WHEN @status = 'Queued' AND status <> 'Queued' AND queue_visible_at IS NOT NULL
            THEN now() + @retry_delay
        ELSE queue_visible_at END,
    queue_priority = CASE WHEN @status IN ('Succeeded', 'Failed', 'Cancelled') THEN NULL
                          ELSE queue_priority END,
    expires_at = now() + @retention,
    record = @record, <projections>
WHERE operation_id = @operation_id AND version = @expected_version;
```
where `<stamp>(column, @value)` is
`CASE WHEN @value IS NULL THEN NULL WHEN column IS NOT DISTINCT FROM @value THEN column ELSE now() END`:
a timestamp the writer left as it read it is kept, one the writer changed becomes the database time,
and null clears it. No row affected is a version conflict and returns `false`.

`@retry_delay` is the writer's `NextRetryAt - UpdatedAt` (zero when `NextRetryAt` is null): the
writer states a delay on its own clock and the database turns it into a time on the database clock.
`next_retry_at` and `queue_visible_at` come from that one expression. `next_retry_at` is what the
record reports and what the provider reconciler waits on for a job no local worker runs;
`queue_visible_at` is the only time a claim compares.

The heartbeat, progress, cancel-request and terminal writes are all this statement. A job that was
claimed from the local queue and is written back to `Queued` becomes claimable again in this
statement; there is no window in which it is `Queued` and absent from the queue.

#### 3.4 Lease-owned update (`TrySetIfLeaseOwnedAsync`)
Statement 3.3 with the lease row locked for the statement:
```sql
WITH owned_lease AS MATERIALIZED (
    SELECT 1 FROM honua.leases
    WHERE lease_key = @lease_key AND owner_id = @owner_id AND expires_at > now()
    FOR UPDATE)
UPDATE honua.execution_jobs SET ...
WHERE operation_id = @operation_id AND version = @expected_version
  AND EXISTS (SELECT 1 FROM owned_lease);
```
A concurrent takeover of the lease waits for this statement, and this statement re-checks owner and
expiry if the takeover committed first.

#### 3.5 Leases (`TryAcquireLeaseAsync`, `RenewLeaseAsync`, `ReleaseLeaseAsync`)
```sql
INSERT INTO honua.leases AS lease (lease_key, owner_id, expires_at)
VALUES (@lease_key, @owner_id, now() + @duration)
ON CONFLICT (lease_key) DO UPDATE SET owner_id = EXCLUDED.owner_id, expires_at = EXCLUDED.expires_at
WHERE lease.expires_at <= now();                                              -- acquire

UPDATE honua.leases SET expires_at = now() + @duration
WHERE lease_key = @lease_key AND owner_id = @owner_id AND expires_at > now(); -- renew

DELETE FROM honua.leases WHERE lease_key = @lease_key AND owner_id = @owner_id; -- release
```
As with the Redis lock, the holder cannot re-acquire a lease it still holds.

#### 3.6 Enqueue, requeue with delay, remove, depth
```sql
UPDATE honua.execution_jobs SET queue_visible_at = now(), queue_priority = @priority
WHERE operation_id = @operation_id AND status = 'Queued' AND backend = 'local'
  AND queue_visible_at IS NULL;                                                          -- enqueue

UPDATE honua.execution_jobs SET queue_visible_at = now() + @visible_after, queue_priority = @priority
WHERE operation_id = @operation_id AND backend = 'local'
  AND status NOT IN ('Succeeded', 'Failed', 'Cancelled');                                -- requeue

UPDATE honua.execution_jobs SET queue_visible_at = NULL, queue_priority = NULL
WHERE operation_id = @operation_id AND status = 'Queued' AND queue_visible_at IS NOT NULL; -- remove

SELECT count(*) FROM honua.execution_jobs
WHERE status = 'Queued' AND queue_visible_at IS NOT NULL;                                -- depth
```
A repeated enqueue keeps the original position, as the Redis `ZADD NX` did. Only a job whose backend
is `local` can be in the queue; the table constraint enforces it. A job that a remote backend runs
(AWS Batch, Azure Batch, Kubernetes Jobs, the local process pool) sits `Queued` with
`queue_visible_at` `NULL` until that backend submits it.

#### 3.7 Dispatch repair (`IQueueClaimReconciler.ReconcileStaleClaimsAsync`)
Every producer creates the job and then enqueues it in a second call: geoprocessing, imports, tile
cache, layer publish, share export, provisioner build, topology rebuild, multidimensional scan, and
the console's manual retry. A process that dies between the two leaves a local job `Queued` and not
in the queue. `JobReconciliationService` repairs first-attempt geoprocessing jobs immediately
(`NeedsLocalDispatchRepair`); every other case is repaired by this statement, which the service
already calls each sweep with a 60 s threshold:
```sql
UPDATE honua.execution_jobs SET queue_visible_at = now(), queue_priority = priority
WHERE status = 'Queued' AND queue_visible_at IS NULL AND backend = 'local'
  AND NOT cancellation_requested AND updated_at <= now() - @stale_threshold;
```
Enqueueing is the step the producer was about to take and the job has not started, so the repair
enqueues for every local kind instead of failing the job. A cancellation-requested job is left for
the cancellation path.

#### 3.8 Reconciler candidates
`ListActiveAsync` returned every non-terminal job to two loops. With a deep queue that is the whole
backlog every 5 s on every serving node. The two loops now read candidates instead:
```sql
-- JobReconciliationService, every 30 s: claims that may have expired, and local dispatch repair
SELECT <attempt-state columns>, record FROM honua.execution_jobs
WHERE (status IN ('Provisioning', 'Running')
       AND (COALESCE(last_heartbeat_at, claimed_at) <= now() - heartbeat_timeout
            OR claimed_at <= now() - max_duration))
   OR (status = 'Queued' AND queue_visible_at IS NULL AND backend = 'local');

-- ExecutionJobReconcilerBackgroundService, every 5 s: jobs the reconciler acts on
SELECT operation_id FROM honua.execution_jobs
WHERE status IN ('Queued', 'Provisioning', 'Running')
  AND NOT (status = 'Queued' AND backend = 'local' AND attempt_count = 0
           AND NOT cancellation_requested);
```
The second excludes exactly the first-attempt local jobs waiting in the queue, for which the
reconciler only reads progress. It keeps local `Queued` jobs with a cancellation request or an
earlier attempt, because the reconciler cancels the former and resets the progress of the latter.
The reconciler's per-job lease is unchanged; the rows that made it expensive no longer reach it.
Both services keep their own checks on each record; the queries only narrow what they look at. They
are `IReconcilableJobSource`, implemented by the job store; a store without it (the in-memory
doubles) is read through `ListActiveAsync` as before.

#### 3.9 List and query with cursor
```sql
SELECT <attempt-state columns>, record FROM honua.execution_jobs
WHERE (@statuses::text[] IS NULL OR status = ANY (@statuses))
  AND (@kind::text IS NULL OR kind = @kind)
  AND (NOT @apply_tenant_scope OR tenant_id IS NOT DISTINCT FROM @tenant_id)
  AND (@created_from::timestamptz IS NULL OR created_at >= @created_from)
  AND (@created_to::timestamptz IS NULL OR created_at < @created_to)
  AND lookup_keys @> @required_lookup_keys
  AND (@cursor_created_at::timestamptz IS NULL
       OR (created_at, operation_id) < (@cursor_created_at, @cursor_operation_id))
ORDER BY created_at DESC, operation_id DESC
LIMIT @limit + 1;                                             -- QueryAsync, limit clamped to 1..200

SELECT <attempt-state columns>, record FROM honua.execution_jobs
WHERE status IN ('Queued', 'Provisioning', 'Running') AND (@kind::text IS NULL OR kind = @kind)
ORDER BY updated_at DESC LIMIT 2 * @limit;                    -- ListActiveAsync
```
The store builds the text with only the predicates a query uses. The cursor is the opaque base64 of
`<created_at in Unix microseconds>|<operation_id>` of the last row returned.

#### 3.10 Log append with dedupe and trim, tail, page, retention
Append is one batch (one round trip, one implicit transaction):
```sql
INSERT INTO honua.execution_job_log_streams (operation_id, expires_at)
VALUES (@operation_id, now() + @retention)
ON CONFLICT (operation_id) DO UPDATE SET expires_at = EXCLUDED.expires_at;      -- locks the stream

WITH accepted AS (
    INSERT INTO honua.execution_job_log_event_identities (operation_id, event_id)
    SELECT @operation_id, @event_id WHERE @event_id IS NOT NULL
    ON CONFLICT DO NOTHING RETURNING 1),
advanced AS (
    UPDATE honua.execution_job_log_streams
    SET appended_count = appended_count + 1,
        dropped_count = GREATEST(dropped_count, appended_count + 1 - @max_entries),
        event_identity_count = event_identity_count + (SELECT count(*) FROM accepted)::int
    WHERE operation_id = @operation_id AND (@event_id IS NULL OR EXISTS (SELECT 1 FROM accepted))
    RETURNING appended_count - 1 AS sequence, dropped_count),
inserted AS (
    INSERT INTO honua.execution_job_log_entries (operation_id, sequence, entry)
    SELECT @operation_id, sequence, @entry FROM advanced RETURNING 1)
DELETE FROM honua.execution_job_log_entries AS entry USING advanced
WHERE entry.operation_id = @operation_id AND entry.sequence < advanced.dropped_count;
```
`sequence` is the absolute zero-based position, which is what the existing offset cursor encodes.
`@max_entries` is 2,000 and an entry is bounded to 4 KiB before it is sent, as today. An event id
already recorded for the job appends nothing, including after its entry was trimmed. The limit of
8,192 event identities per job is the named `CHECK` constraint.

Tail (`@start = -1`) and page (`@start` from the cursor) are one statement, so counts and entries
come from one snapshot:
```sql
WITH stream AS (
    SELECT appended_count, dropped_count,
           CASE WHEN @start < 0 THEN GREATEST(dropped_count, appended_count - @limit)
                ELSE GREATEST(dropped_count, @start) END AS window_start
    FROM honua.execution_job_log_streams WHERE operation_id = @operation_id)
SELECT stream.appended_count, stream.dropped_count, stream.window_start, entry.entry
FROM stream LEFT JOIN LATERAL (
    SELECT sequence, entry FROM honua.execution_job_log_entries
    WHERE operation_id = @operation_id AND sequence >= stream.window_start
    ORDER BY sequence LIMIT @limit + 1) AS entry ON TRUE
ORDER BY entry.sequence;
```
Every append moves the stream's expiry to `Jobs:Store:LogRetention` from now, and
`SetRetentionAsync`, which the execution and reconciliation services call when a job reaches a
terminal state, sets it from that moment. The sweep (3.12) does not delete the log of a job that is
not terminal. A log therefore never expires while its job runs.

#### 3.11 Result packages
```sql
INSERT INTO honua.geoprocessing_result_packages (job_id, package, expires_at)
VALUES (@job_id, @package, now() + @retention)
ON CONFLICT (job_id) DO UPDATE SET package = EXCLUDED.package, expires_at = EXCLUDED.expires_at;

SELECT package FROM honua.geoprocessing_result_packages WHERE job_id = @job_id AND expires_at > now();
```

#### 3.12 Retention sweep
`JobStoreRetentionSweepService` (a hosted service on the serving host, modelled on
`AuditRetentionPruneService`) runs every `Jobs:Store:RetentionSweepInterval` and repeats each
statement while it deletes a full batch:
```sql
WITH expired AS (
    SELECT operation_id FROM honua.execution_jobs
    WHERE status IN ('Succeeded', 'Failed', 'Cancelled') AND expires_at <= now()
    ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
DELETE FROM honua.execution_jobs AS job USING expired WHERE job.operation_id = expired.operation_id;

WITH expired AS (
    SELECT stream.operation_id FROM honua.execution_job_log_streams AS stream
    WHERE stream.expires_at <= now()
      AND NOT EXISTS (SELECT 1 FROM honua.execution_jobs AS job
                      WHERE job.operation_id = stream.operation_id
                        AND job.status IN ('Queued', 'Provisioning', 'Running'))
    ORDER BY stream.expires_at LIMIT @log_stream_batch_size FOR UPDATE SKIP LOCKED)
DELETE FROM honua.execution_job_log_streams AS stream USING expired
WHERE stream.operation_id = expired.operation_id;
```
and the first shape for `geoprocessing_result_packages` and `leases`. A log stream cascades to up to
2,000 entries and 8,192 event identities, so `@log_stream_batch_size` is 10: one statement deletes at
most about 100,000 small rows. The sweep runs on every serving node without leader election: the
deletes are bounded, idempotent and skip rows another node has locked.

| Setting | Default | Was |
|---|---|---|
| `Jobs:Store:JobRetention` | 7 days | Redis key TTL of 7 days, hard-coded |
| `Jobs:Store:LogRetention` | 7 days | Redis key TTL of 7 days, hard-coded |
| `Geoprocessing:Executors:ResultRetention` | 7 days, unchanged | unchanged |
| `Jobs:Store:RetentionSweepInterval` | 10 minutes | Redis expiry |
| `Jobs:Store:RetentionSweepBatchSize` | 1,000 | n/a |

A job that is not in a terminal state is never swept. Redis silently dropped a job left `Queued` for
seven days; here it stays visible and, if local, is repaired by 3.7.

#### 3.13 Database time
`IJobStoreClock.GetUtcNowAsync`, implemented by the job store, returns `SELECT now()`. A consumer
given no clock uses the node clock, which is right for a store that stamps with the node clock (the
in-memory doubles).

### 4. Invariants and the tests that prove them
Tests are in `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/ControlPlane/` and run against
PostgreSQL through Testcontainers.

| # | Invariant | Mechanism | Test |
|---|---|---|---|
| 1 | A job is claimed by at most one worker per attempt. | Claim is one statement that row-locks the candidate with `SKIP LOCKED` and re-checks `status = 'Queued'` on the locked row. | `PostgresJobClaimContentionTests.TryClaimAsync_EightWorkersRaceOneJob_ExactlyOneWinsWithOneAttempt`, `...TryClaimAsync_WorkersDrainContestedQueue_EveryJobIsClaimedExactlyOnce` |
| 2 | A stale attempt cannot write after it is superseded. | Every write carries the version it read; a requeue or a new claim increments it. Finalization under a partition lease also requires the lease row. | `PostgresStaleAttemptFencingIntegrationTests.StaleAttempt_IsFencedAcrossReclaimDuplicateDeliveryAndTerminalization`, `PostgresJobClaimContentionTests.WorkerLoss_NaturalHeartbeatLapse_ReExecutesOnceAndSuppressesTheStaleWorkersTerminal`, `PostgresExecutionSubstrateIntegrationTests.ExecutionJobStore_FinalizesOnlyWhileLeaseIsOwned` |
| 3 | A local `Queued` job is always eventually claimable. | A return to `Queued` restores the queue position in the same statement; a local `Queued` job that is not in the queue is enqueued by the next sweep after 60 s (3.7); a job is never swept before it is terminal. | `PostgresJobQueueTests.TrySetAsync_ClaimedJobReturnedToQueued_IsClaimableWithoutRequeue`, `PostgresJobQueueTests.ReconcileStaleClaimsAsync_LocalJobCreatedButNeverEnqueued_IsEnqueued` (one case per job kind), `...ReconcileStaleClaimsAsync_ManualRetryThatNeverRequeued_IsEnqueued`, `...ReconcileStaleClaimsAsync_RemoteBackendJob_IsLeftForItsBackend`, `PostgresExecutionSubstrateIntegrationTests.JobReconciliation_PersistedLocalJobWithoutDelivery_ReplacementWorkerExecutesOnce` |
| 4 | Cancellation is observed. | Durable `CancellationRequestedAt`; read on each heartbeat and by the reconciler. | `PostgresExecutionSubstrateIntegrationTests.JobReconciliationService_CancelsDurablyRequestedStaleJob`, `PostgresStaleAttemptFencingIntegrationTests.CancellationDuringBackoff_PreventsTheNextAutomaticAttempt` |
| 5 | Retries respect the delay and go to the back of their band. | `queue_visible_at = now() + delay`; claim filters and orders on it. | `PostgresJobQueueTests.RequeueAsync_WithDelay_HidesTheJobUntilTheDelayElapses`, `...TryClaimAsync_RequeuedJob_GoesBehindJobsAlreadyWaitingInItsBand`, `PostgresStaleAttemptFencingIntegrationTests.AutomaticTransientFailure_UsesConfiguredBackoffAndStopsAtExactMaxAttempts` |
| 6 | A worker killed mid-job is recovered within the heartbeat timeout plus one sweep. | `last_heartbeat_at` stops advancing; the 30 s sweep requeues or fails the job once it is 90 s old. | `PostgresJobClaimContentionTests.WorkerLoss_NaturalHeartbeatLapse_ReExecutesOnceAndSuppressesTheStaleWorkersTerminal`, `PostgresStaleAttemptFencingIntegrationTests.StaleAttempt_IsFencedAcrossReclaimDuplicateDeliveryAndTerminalization` (kills the worker process) |
| 7 | Log events are not duplicated. | Primary key on `(operation_id, event_id)`, checked in the append transaction. | `PostgresExecutionLogStoreTests.BoundedLog_TrimsWithAbsoluteCursorsAndDeduplicatesExternalEvents`, `...AppendOnceAsync_ConcurrentDeliveriesOfOneEvent_AppendExactlyOneEntry` |
| 8 | Correctness depends only on database time. | Section 5. | `PostgresJobStoreClockTests.ClaimLeaseAndHeartbeatExpiry_UnderSkewedNodeClocks_FollowDatabaseTime` |
| 9 | A database restart during a running job loses nothing and does not run the job twice. | Section 8. | `PostgresJobExecutionResilienceTests.JobExecutionService_WhenPostgresRestartsMidJob_JobCompletesExactlyOnce` |
| 10 | A database outage longer than the heartbeat timeout does not expire healthy workers' jobs. | Section 8. | `PostgresJobExecutionResilienceTests.JobReconciliation_AfterAnOutageLongerThanTheHeartbeatTimeout_DoesNotExpireALiveWorkersJob` |
| 11 | A claim whose reply was lost is taken by the worker that made it and costs no attempt. | 3.2. | `PostgresJobQueueTests.TryClaimAsync_AfterAClaimWhoseReplyWasLost_ReturnsThatJobWithOneAttempt` |

### 5. Time
Every lease expiry, visibility delay, heartbeat timeout, execution timeout and retention comparison
uses the database clock.

- Stamped in SQL with `now()`: `claimed_at`, `last_heartbeat_at`, `next_retry_at`, `completed_at`,
  `updated_at`, `queue_visible_at`, `expires_at` and every lease `expires_at`. A compare-and-set
  write replaces a timestamp the writer changed with the database time (3.3); it never stores the
  writer's value. A duration between two of them cannot be negative.
- Compared in SQL with `now()`: claim visibility, lease acquire and renew, the lease-owned update,
  the reconciler candidate queries, result-package expiry on read, the retention sweep.
- Compared in C# against `IJobStoreClock`: the places that today compare a stored timestamp against
  the node clock.

| Today (node clock) | Change |
|---|---|
| `JobReconciliationService`: heartbeat and timeout expiry against `DateTimeOffset.UtcNow` | Reads the store clock once per sweep, and once more for each job it is about to expire. |
| `ExecutionJobReconciler`: `NextRetryAt > DateTimeOffset.UtcNow` for provider-run retries | Reads the store clock. `NextRetryAt` is database-stamped. |
| `AssignedExecutionJobQueue`: heartbeat expiry of another worker's claim against its `TimeProvider` | Reads the store clock. |
| `RedisJobQueue`: visibility and stale-claim age against `DateTimeOffset.UtcNow` | Deleted; SQL `now()`. |
| Redis key TTLs (server clock already) | `expires_at` and SQL `now()`. |

`CreatedAt` and `CancellationRequestedAt` remain the writer's values: they are shown and sorted,
never compared with a clock to make a decision. A worker's own execution timeout and drain deadline
are durations on its monotonic clock. `TryCreateAsync` and `SetAsync` store timestamps as given,
which is how a job is first written and how tests age a record.

### 6. Write volume and reconciler load
Every write of a job rewrites its row including `record`. A heartbeat is one read and one such
write: 200 running jobs at the 30 s interval is about 14 statements and 7 row versions per second.
A record over about 2 kB is stored out of line, so each of those writes also replaces its TOAST
chunks; autovacuum reclaims them. The measured row versions and heap, TOAST and index growth for that
load are in the pull request. If they show a problem, the fix is a heartbeat statement that does not
carry the record, which the column layout already allows.

Reconciler load per process, with R running or provisioning jobs and any number queued:

| Loop | Where | Statements |
|---|---|---|
| Claim poll, every 5 s when idle | each worker | 1 index-ordered claim |
| Heartbeat, every 30 s per running job | the worker running it | 2 |
| `JobReconciliationService`, every 30 s | each serving node, each GDAL worker | 1 clock read, 1 candidate query (3.8, returns only expired claims and undispatched local jobs), 1 dispatch repair (3.7); then work only for jobs it recovers |
| `ExecutionJobReconcilerBackgroundService`, every 5 s | each serving node in poll mode | 1 candidate query; per candidate a lease insert, a read, a write only if the observation changed something, a lease delete. Candidates are R plus remote-backend and retrying jobs, not the local backlog |

With 200 running jobs that last loop is about 120 to 160 statements per second per serving node,
which is what it costs on Redis today. A backlog of 10,000 queued local jobs adds nothing to any
loop.

### 7. Connections
The stores hold no connection between statements and use no session state of their own. They are
free of connection pinning behind RDS Proxy only when `Database:SessionSettings:Mode` is
`role-defaults`: in the default mode the shared data source runs `SET` in its connection
initializer, for every user of the pool.

A single-job execution worker (the server image started with `HONUA_OPERATION_ID`) defaults to a
pool of at most 8 and at least 0 connections when `Limits:Connections` sets neither; the GDAL worker
opens its own data source for the job store with at most 4 and at least 0
(`Jobs:Store:WorkerMaxPoolSize`), from `ConnectionStrings:DefaultConnection`, which it now requires
in place of `ConnectionStrings:redis`. The serving host is unchanged (200 and 20).

### 8. Failure behaviour
A statement that fails with a transient error (connection refused or reset, `57P01`, `57P03`, class
`08`, `53300`) is retried with backoff 0.2 s, 0.5 s, 1 s, 2 s when repeating it is safe:

- Always safe: failures while opening the connection; reads; enqueue, remove, lease renew and
  release, `AppendOnceAsync`, result-package writes.
- Safe with confirmation: `TryCreateAsync` and the compare-and-set writes. If an attempt failed after
  the statement was sent and the retry affects no row, the store reads the row; version
  `expected + 1` holding exactly the record that was sent means the first attempt committed, and the
  call returns `true`.
- Claim: not repeated blindly; recovered as in 3.2.
- Not repeated after send: lease acquire, `AppendAsync` without an event id. The error reaches the
  caller.

Beyond that budget the existing loops carry on: the claim loop retries at its 5 s poll and the
reconciler at its next sweep. A heartbeat pump whose write failed retries every 2 s instead of
waiting for its next interval, so heartbeats resume as soon as the database is back. Nothing is held
in memory that the database does not have, so an outage loses no job.

After an outage every heartbeat in the table is older than the outage, and a sweep that compared
them with the current database time would expire healthy workers' jobs, failing those with
`MaxAttempts = 1`. `JobReconciliationService` therefore does not expire a heartbeat until it has
completed sweeps without a store error for that job's heartbeat timeout: a failed sweep starts the
period again. Execution-timeout expiry is not delayed.

- An outage shorter than the retry budget is invisible to jobs.
- A longer outage, of any length, does not by itself expire a job whose worker is alive.
- A worker that died during the outage is recovered one heartbeat timeout after the reconciler next
  sees the database.
- The API returns its existing store-unavailable error for a job request it cannot persist.

Capability manifest: `jobs.runner` is available when the job store and queue are registered, which
is whenever the system database is configured. It no longer depends on Redis or on the
`caching.redis` entitlement. `DurableJobSubstrateCause` becomes `Available`, `JobStoreNotConfigured`,
`JobQueueNotRegistered`. The manifest does not flap with reachability. Readiness: the job store has
no dependency of its own; the existing database readiness check reports `DatabaseUnavailable` and
takes the node out of rotation. The Redis durability attestation leaves the job path;
`Jobs:RequireDurableStore` is accepted and always satisfied. `operations.proposals` keeps its Redis
requirement and is gated on its own store instead of on the job-store classification.

### 9. Upgrade
Jobs in progress are not carried over from earlier pre-release builds; start from a clean
deployment.

### 10. Limits
Expected envelope: hundreds of concurrently running jobs, a backlog of the order of 100,000 queued or
retained rows, tens of submissions per second, one database.

What would be added if that is exceeded, each behind the same interfaces: a heartbeat statement that
does not carry the record; claiming several jobs per statement (`LIMIT n`); an optional wake-up
signal so an idle worker does not wait for its 5 s poll.

Not reachable without a redesign: time partitioning of `execution_jobs` (a partitioned table cannot
keep `operation_id` unique across partitions, which create-if-absent relies on), and active-active
operation across regions (every guarantee here rests on one writable database and its clock).

Known follow-ups: a point-in-time restore of the database restores job state with it, so a job that
had finished can come back as `Queued` or `Running` and run again; there is no switch yet to hold
claiming while an operator reviews them. Job hosts (GDAL and single-job workers) use the system
database role; a role limited to the job tables is not implemented.

Deliberately not done: `LISTEN/NOTIFY`, session advisory locks, `SET`, any second queue structure,
any cross-statement transaction.

### 11. Fit for pipelines
The 2026.2 pipelines work (#7151) moves the orchestration stores to PostgreSQL on this substrate.
How a run uses jobs today: each step, and each fan-out item, becomes one geoprocessing job through
the normal submit path (`WorkflowOrchestrationEngine.SubmitStepAsync`,
`GeoprocessingJobService.SubmitJobCoreAsync`); the job id is derived from
`{runId}:{stepId}:{attempt}` (`JobOwnershipSecurity`); the run learns a step finished by reading that
job every 5 s (`WorkflowOrchestrationEngine.ObserveStepAsync`); there is no terminal callback and no
parent job.

- **Correlation.** A job carries its run in `Spec.Parameters["orchestration.runId"]` and its step in
  `orchestration.stepId`. The run id is projected now as a `run:<id>` entry in `lookup_keys`, so all
  jobs of a run are one GIN lookup. No query field uses it yet; adding `RunId` to
  `ExecutionJobQuery` is one more required lookup key. A step is a filter within a run's jobs
  (at most 1,000 today, `WorkflowForEachSpec.HardIterationLimit`).
- **Fan-out.** 5,000 child jobs of one run cost the reconcilers nothing while they wait: they are
  first-attempt local jobs in the queue, which neither candidate query (3.8) returns. They cost what
  any job costs once claimed. The run's own polling is the load to change: one read per unfinished
  step every 5 s is 1,000 statements per second for 5,000 steps, where one `run:<id>` listing would
  do.
- **Scheduling.** A firing that creates a job with an id derived from the schedule and fire time is
  exactly-once across nodes: `TryCreateAsync` is `ON CONFLICT DO NOTHING` on the primary key and
  returns `true` to one caller. A job can be created already delayed through the existing interface:
  `TryCreateAsync`, then `RequeueAsync(id, priority, visibleAfter)`, which puts a local job into the
  queue with a future visibility whether or not it was in the queue. If the second call is lost, the
  dispatch repair (3.7) enqueues the job without the delay after 60 s; a caller that cannot accept
  that would need the delay on the record at creation, which is one optional field.
- **Leases.** `leases` is `(lease_key, owner_id, expires_at)` with acquire, renew and release
  compared against the database clock. Run leases (`IOperationStore` on the run store) and schedule
  and trigger claims (set-if-absent with an expiry) fit it unchanged, under their own key prefixes.
- **Retention.** `expires_at` is per row and every write takes a retention (`ttl`), so retention can
  differ by kind or follow the owning run without a schema change. Run history itself belongs to the
  run store, not to job rows.
- **Live status.** A job transition and an outbox row are both rows in this database, but the stores
  run each statement on its own pooled connection, so today they are two transactions. Writing them
  together later is additive: the compare-and-set becomes the first branch of a `WITH ... INSERT
  INTO <outbox>` statement, or the store gains an overload that takes the caller's transaction.
- **To undo.** Nothing in the schema. One assumption would have to change if a worker ever claims
  several jobs at once under one worker id: recovery of a claim whose reply was lost (3.2) relies on
  a worker holding at most one job in the `Claimed` phase.

## Alternatives considered
- **A separate queue table.** Two rows per job have to change together in every transition, and the
  reconciler has to repair the cases where they did not; that is the Redis design's weakness
  re-created. One row cannot disagree with itself.
- **Advisory locks for claims or leases.** Session-level locks pin RDS Proxy connections and do not
  survive a frozen Lambda (#6760, #6765); transaction-level locks would hold a transaction open for
  the length of a job.
- **Keeping Redis for the queue only.** Keeps the two-store gap between queue and record, and keeps
  Redis as a requirement for running a job at all, which is what the decision removes.
- **An external broker (SQS, RabbitMQ).** A third required service, per-cloud variants, and still no
  atomic move between the broker and the job record.
- **A narrow heartbeat write that leaves `record` untouched** (a second statement, a record hash and
  heap-only-update tuning). Rejected in design review: two write paths can drift, and the volume one
  path writes is small. It stays available as the first step in section 10.

## Consequences
- A deployment with PostgreSQL and no Redis runs durable jobs. Community edition gets the durable job
  runtime.
- The GDAL worker needs database credentials and network access to PostgreSQL; it no longer needs
  Redis.
- Job state is queryable with SQL and is covered by database backup and restore.
- Job writes and reconciliation add load to the system database (section 6).
