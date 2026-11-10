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
per-attempt job token and `CommittedEffectReferences`. `JobExecutionService` (5 s poll),
`JobReconciliationService` (30 s sweep) and durable cancellation keep their flows.

## Decision
One table holds a job and its queue position. The queue is a query over that table. Each PostgreSQL
store replaces its Redis store outright; there is no selection setting and no dual-store period.

The stores are `PostgresExecutionJobStore`, `PostgresJobQueue`, `PostgresExecutionLogStore`
(`src/Honua.Jobs`, raw Npgsql on an `NpgsqlDataSource`) and `PostgresGeoprocessingResultPackageStore`
(`src/Honua.Geoprocessing`). They live beside the consumers because `Honua.Worker.Gdal` references
`Honua.Jobs` and must not reference `Honua.Server` (ADR-0038), and the module policy forbids a
`Honua.Postgres` to `Honua.Jobs` reference in either direction.

SQL below is written with the default system schema `honua`; migrations use `$HonuaSchema$` and the
stores quote the configured `Database:Schema`.

### 1. Tables

```sql
CREATE TABLE honua.execution_jobs (
    operation_id      TEXT COLLATE "C" PRIMARY KEY CHECK (length(operation_id) BETWEEN 1 AND 512),
    -- attempt state: authoritative, changed by claim, heartbeat, progress and transitions
    version           BIGINT NOT NULL CHECK (version > 0),
    status            TEXT NOT NULL CHECK (status IN
                          ('Queued', 'Provisioning', 'Running', 'Succeeded', 'Failed', 'Cancelled')),
    attempt_count     INTEGER NOT NULL CHECK (attempt_count >= 0),
    claimed_by        TEXT,
    claimed_at        TIMESTAMPTZ,
    last_heartbeat_at TIMESTAMPTZ,
    next_retry_at     TIMESTAMPTZ,
    percent_complete  DOUBLE PRECISION,
    current_phase     TEXT,
    updated_at        TIMESTAMPTZ NOT NULL,
    -- local queue position: authoritative; NULL means "not in the local queue"
    queue_priority    SMALLINT,
    queue_visible_at  TIMESTAMPTZ,
    -- retention
    expires_at        TIMESTAMPTZ NOT NULL,
    -- the rest of ExecutionJobRecord: authoritative for every field not listed above
    record            JSON NOT NULL,
    record_hash       BYTEA NOT NULL CHECK (octet_length(record_hash) = 32),
    -- projections of record, written in the same statement as record
    kind              TEXT NOT NULL,
    runtime_profile   TEXT NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL,
    tenant_id         TEXT,
    lookup_keys       TEXT[] NOT NULL,
    CHECK ((queue_visible_at IS NULL) = (queue_priority IS NULL))
) WITH (fillfactor = 50, toast_tuple_target = 128,
        autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_threshold = 200,
        autovacuum_analyze_scale_factor = 0.05, autovacuum_analyze_threshold = 200);

-- Every variable-length column except record stays in the heap row, so only record moves to TOAST.
ALTER TABLE honua.execution_jobs
    ALTER COLUMN operation_id SET STORAGE MAIN,  ALTER COLUMN status SET STORAGE MAIN,
    ALTER COLUMN claimed_by SET STORAGE MAIN,    ALTER COLUMN current_phase SET STORAGE MAIN,
    ALTER COLUMN record_hash SET STORAGE MAIN,   ALTER COLUMN kind SET STORAGE MAIN,
    ALTER COLUMN runtime_profile SET STORAGE MAIN, ALTER COLUMN tenant_id SET STORAGE MAIN,
    ALTER COLUMN lookup_keys SET STORAGE MAIN;

CREATE INDEX idx_execution_jobs_created ON honua.execution_jobs (created_at DESC, operation_id DESC);
CREATE INDEX idx_execution_jobs_status_created
    ON honua.execution_jobs (status, created_at DESC, operation_id DESC);
CREATE INDEX idx_execution_jobs_kind_created
    ON honua.execution_jobs (kind, created_at DESC, operation_id DESC);
CREATE INDEX idx_execution_jobs_lookup_keys ON honua.execution_jobs USING GIN (lookup_keys);
CREATE INDEX idx_execution_jobs_claimable
    ON honua.execution_jobs (queue_priority, created_at, operation_id)
    WHERE status = 'Queued' AND queue_visible_at IS NOT NULL;
CREATE INDEX idx_execution_jobs_expired ON honua.execution_jobs (expires_at)
    WHERE status IN ('Succeeded', 'Failed', 'Cancelled');

CREATE TABLE honua.leases (
    lease_key  TEXT COLLATE "C" PRIMARY KEY,
    owner_id   TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
) WITH (fillfactor = 50);
CREATE INDEX idx_leases_expires ON honua.leases (expires_at);

CREATE TABLE honua.execution_job_log_streams (
    operation_id         TEXT COLLATE "C" PRIMARY KEY,
    appended_count       BIGINT NOT NULL DEFAULT 0,
    dropped_count        BIGINT NOT NULL DEFAULT 0,
    event_identity_count INTEGER NOT NULL DEFAULT 0,
    expires_at           TIMESTAMPTZ NOT NULL,
    CONSTRAINT execution_job_log_streams_event_identity_limit CHECK (event_identity_count <= 8192)
) WITH (fillfactor = 50);
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

`record` is `JSON`, not `JSONB`: the stores never query inside it, `JSON` keeps the serialized text
byte for byte, and `JSONB` rejects the `\u0000` escape that a caller-supplied job parameter can carry.

`lookup_keys` holds one `dimension:VALUE` entry per filterable value of the record: `backend`,
`queue`, `requested-by`, `correlation`, `trace`, `definition` (definition id and geoprocessing plan
id), `resource` (one per resource reference), `environment`, `server`, `release`, `change-set`,
`alert`. The value is trimmed and upper-cased, which reproduces the Redis store's trimmed,
case-insensitive match. These are the dimensions the Redis store indexed.

### 2. Record of truth and projections

| Columns | Role | Kept consistent by |
|---|---|---|
| `version`, `status`, `attempt_count`, `claimed_by`, `claimed_at`, `last_heartbeat_at`, `next_retry_at`, `percent_complete`, `current_phase`, `updated_at` | Record of truth for those ten `ExecutionJobRecord` fields. | `record` stores placeholders for them; a read overlays the columns on the deserialized record, so a reader never sees the placeholder. |
| `queue_priority`, `queue_visible_at` | Record of truth for the local queue. Not part of `ExecutionJobRecord`. | Written only by the queue statements and by the compare-and-set statement (section 3.3). |
| `expires_at` | Record of truth for retention. | Set in the statement that changes `status` or `record`. |
| `record` | Record of truth for every other field (spec, audit, policies, artifact and committed-effect references, error, provider id, completion and cancellation times, priority, creation time). | Rewritten only when one of those fields changes. |
| `record_hash` | SHA-256 of the stored `record` text. | Written with `record`. |
| `kind`, `runtime_profile`, `created_at`, `tenant_id`, `lookup_keys` | Projections of `record`, used only in `WHERE` and `ORDER BY`. | Computed from the record in the store and written in the same `INSERT` or `UPDATE` as `record`; no statement writes one without the other. |

A status change that touches none of the JSON-held fields (claim, `Provisioning` to `Running`, the
heartbeat, progress) does not rewrite `record`.

### 3. Statements

Every statement below runs as its own transaction on a pooled connection. `now()` is therefore the
statement start time on the database server.

#### 3.1 Create if absent (`TryCreateAsync`)
```sql
INSERT INTO honua.execution_jobs (operation_id, version, status, attempt_count, claimed_by,
    claimed_at, last_heartbeat_at, next_retry_at, percent_complete, current_phase, updated_at,
    expires_at, record, record_hash, kind, runtime_profile, created_at, tenant_id, lookup_keys)
VALUES (@operation_id, 1, @status, @attempt_count, @claimed_by, @claimed_at, @last_heartbeat_at,
    @next_retry_at, @percent_complete, @current_phase, @updated_at, now() + @retention, @record,
    @record_hash, @kind, @runtime_profile, @created_at, @tenant_id, @lookup_keys)
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
      AND (@kinds::text[] IS NULL OR kind = ANY (@kinds))
      AND runtime_profile = ANY (@runtime_profiles)
    ORDER BY queue_priority, created_at, operation_id
    LIMIT 1
    FOR UPDATE SKIP LOCKED)
UPDATE honua.execution_jobs AS job
SET status = 'Provisioning', version = job.version + 1, attempt_count = job.attempt_count + 1,
    claimed_by = @worker_id, claimed_at = now(), last_heartbeat_at = now(), next_retry_at = NULL,
    current_phase = 'Claimed', updated_at = now()
FROM next_job WHERE job.operation_id = next_job.operation_id
RETURNING job.operation_id;
```
`@runtime_profiles` is the worker's accepted set, or `{managed}` when the worker declares none, as
`RuntimeProfiles.CanClaim` defines. `queue_priority` is 0 Critical, 1 High, 2 Normal, 3 Low. The
Redis score ordered a band by enqueue time; this orders it by creation time, so a retried job keeps
its place instead of moving to the back of its band.

#### 3.3 Version compare-and-set (`TrySetAsync`), heartbeat and progress
The store serializes the record with the ten attempt-state fields replaced by placeholders and hashes
it. It first sends the narrow statement, which carries no JSON:
```sql
UPDATE honua.execution_jobs SET
    version = version + 1,
    status = @status, attempt_count = @attempt_count, claimed_by = @claimed_by,
    percent_complete = @percent_complete, current_phase = @current_phase, updated_at = now(),
    claimed_at = CASE WHEN @claimed_at IS NULL THEN NULL
                      WHEN claimed_at IS NOT DISTINCT FROM @claimed_at THEN claimed_at
                      ELSE now() END,
    last_heartbeat_at = CASE WHEN @last_heartbeat_at IS NULL THEN NULL
                             WHEN last_heartbeat_at IS NOT DISTINCT FROM @last_heartbeat_at
                                 THEN last_heartbeat_at
                             ELSE now() END,
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
    expires_at = CASE WHEN status IS DISTINCT FROM @status THEN now() + @retention
                      ELSE expires_at END
WHERE operation_id = @operation_id AND version = @expected_version AND record_hash = @record_hash;
```
If no row is affected it sends the full statement: the same `SET` list plus
`record = @record, record_hash = @record_hash, kind = @kind, runtime_profile = @runtime_profile,
created_at = @created_at, tenant_id = @tenant_id, lookup_keys = @lookup_keys,
expires_at = now() + @retention`, with `WHERE operation_id = @operation_id AND version =
@expected_version`. No row affected by the full statement is a version conflict and returns `false`.

The **heartbeat** and **progress** writes are `TrySetAsync` calls from `JobExecutionService` whose
record differs from the stored one only in `LastHeartbeatAt`, `UpdatedAt`, `PercentComplete` and
`CurrentPhase`, so they are always the narrow statement. `@retry_delay` is the writer's
`NextRetryAt - UpdatedAt` (zero when `NextRetryAt` is null): the writer states a delay on its own
clock and the database turns it into a time on the database clock.

A job that was claimed from the local queue and is written back to `Queued` becomes claimable again
in that same statement (`queue_visible_at = now() + @retry_delay`). There is no window in which such
a job is `Queued` and absent from the queue, which is the window `ReconcileStaleClaimsAsync` existed
to repair.

#### 3.4 Lease-owned update (`TrySetIfLeaseOwnedAsync`)
The narrow and full statements of 3.3 with the lease row locked for the statement:
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
WHERE operation_id = @operation_id AND status = 'Queued' AND queue_visible_at IS NULL;   -- enqueue

UPDATE honua.execution_jobs SET queue_visible_at = now() + @visible_after, queue_priority = @priority
WHERE operation_id = @operation_id AND status NOT IN ('Succeeded', 'Failed', 'Cancelled'); -- requeue

UPDATE honua.execution_jobs SET queue_visible_at = NULL, queue_priority = NULL
WHERE operation_id = @operation_id AND queue_visible_at IS NOT NULL;                     -- remove

SELECT count(*) FROM honua.execution_jobs
WHERE status = 'Queued' AND queue_visible_at IS NOT NULL;                                -- depth
```
A repeated enqueue keeps the original position, as the Redis `ZADD NX` did. A job that a remote
backend runs (AWS Batch, Azure Batch, Kubernetes Jobs, the local process pool) is never enqueued, so
its `queue_visible_at` stays `NULL` and no local worker can claim it while it is `Queued` awaiting
submission.

#### 3.7 Cancel request
Cancellation stays a durable `CancellationRequestedAt` on the record, written by the API host through
`TrySetAsync` (the full statement of 3.3, because the field is JSON-held). The worker reads it on its
next heartbeat; the reconciler honours it when a heartbeat has expired.

#### 3.8 Stale-claim reconciliation
`PostgresJobQueue.ReconcileStaleClaimsAsync` does nothing. The Redis queue moved a job between two
sorted sets and then updated the store, and repaired the gap between those steps. Claim (3.2) is one
statement, and a return to `Queued` re-queues in the same statement (3.3), so the state it repaired
cannot occur. `JobReconciliationService` still calls it every sweep.

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
ORDER BY updated_at DESC LIMIT @limit;                        -- ListActiveAsync
```
The store builds the text with only the predicates a query uses. The cursor is the opaque base64 of
`<created_at in Unix microseconds>|<operation_id>` of the last row returned. The Redis cursor carried
milliseconds; ordering within one millisecond is now by time first, then by id.

#### 3.10 Log append with dedupe and trim, tail, page, retention
Append is one batch (one round trip, one implicit transaction):
```sql
DELETE FROM honua.execution_job_log_streams WHERE operation_id = @operation_id AND expires_at <= now();

INSERT INTO honua.execution_job_log_streams (operation_id, expires_at)
VALUES (@operation_id, now() + @retention)
ON CONFLICT (operation_id) DO UPDATE SET operation_id = EXCLUDED.operation_id;  -- locks the stream

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
    FROM honua.execution_job_log_streams WHERE operation_id = @operation_id AND expires_at > now())
SELECT stream.appended_count, stream.dropped_count, stream.window_start, entry.entry
FROM stream LEFT JOIN LATERAL (
    SELECT sequence, entry FROM honua.execution_job_log_entries
    WHERE operation_id = @operation_id AND sequence >= stream.window_start
    ORDER BY sequence LIMIT @limit + 1) AS entry ON TRUE
ORDER BY entry.sequence;
```
`SetRetentionAsync` is `UPDATE honua.execution_job_log_streams SET expires_at = now() + @ttl WHERE
operation_id = @operation_id`.

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
```
and the same shape for `execution_job_log_streams` (entries and event identities cascade),
`geoprocessing_result_packages` and `leases`. It runs on every serving node without leader election:
the deletes are bounded, idempotent and skip rows another node has locked.

| Setting | Default | Was |
|---|---|---|
| `Jobs:Store:JobRetention` | 7 days | Redis key TTL of 7 days, hard-coded |
| `Jobs:Store:LogRetention` | 7 days | Redis key TTL of 7 days, hard-coded |
| `Geoprocessing:Executors:ResultRetention` | 7 days, unchanged | unchanged |
| `Jobs:Store:RetentionSweepInterval` | 10 minutes | Redis expiry |
| `Jobs:Store:RetentionSweepBatchSize` | 1,000 | n/a |

Two differences from Redis expiry, both deliberate. A job's retention runs from its last status or
record change, not from its last heartbeat. A job that is not in a terminal state is never swept:
Redis silently dropped a job left `Queued` for seven days, which contradicts invariant 3 below.

#### 3.13 Database time
`IExecutionJobStore.GetUtcNowAsync` returns `SELECT now()`. It is a default interface method that
returns the node clock, so the in-memory test doubles, which stamp with the node clock, stay
consistent.

### 4. Invariants and the tests that prove them
Tests are in `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/ControlPlane/` and run against
PostgreSQL through Testcontainers.

| # | Invariant | Mechanism | Test |
|---|---|---|---|
| 1 | A job is claimed by at most one worker per attempt. | Claim is one statement that row-locks the candidate with `SKIP LOCKED`, and re-checks `status = 'Queued'` on the locked row. | `PostgresJobClaimContentionTests.TryClaimAsync_EightWorkersRaceOneJob_ExactlyOneWinsWithOneAttempt`, `...TryClaimAsync_WorkersDrainContestedQueue_EveryJobIsClaimedExactlyOnce` |
| 2 | A stale attempt cannot write after it is superseded. | Every write carries the version it read; a requeue or a new claim increments it. Finalization under a partition lease also requires the lease row. | `PostgresStaleAttemptFencingIntegrationTests.StaleAttempt_IsFencedAcrossReclaimDuplicateDeliveryAndTerminalization`, `PostgresJobClaimContentionTests.WorkerLoss_NaturalHeartbeatLapse_ReExecutesOnceAndSuppressesTheStaleWorkersTerminal`, `PostgresExecutionJobStoreTests.TrySetIfLeaseOwnedAsync_FinalizesOnlyWhileLeaseIsOwned` |
| 3 | A `Queued` row in the local queue is always eventually claimable. | Queue position is a column on the row; a return to `Queued` restores it in the same statement; a job is never swept before it is terminal. A persisted initial local job that was never enqueued is still enqueued by the reconciliation sweep. | `PostgresJobQueueTests.TrySetAsync_ClaimedJobReturnedToQueued_IsClaimableWithoutRequeue`, `PostgresExecutionSubstrateIntegrationTests.JobReconciliation_PersistedLocalJobWithoutDelivery_ReplacementWorkerExecutesOnce` |
| 4 | Cancellation is observed. | Durable `CancellationRequestedAt`; read on each heartbeat and by the reconciler. | `PostgresExecutionSubstrateIntegrationTests.JobReconciliationService_CancelsDurablyRequestedStaleJob`, `PostgresStaleAttemptFencingIntegrationTests.CancellationDuringBackoff_PreventsTheNextAutomaticAttempt` |
| 5 | Retries respect the delay. | `queue_visible_at = now() + delay`; claim filters on it. | `PostgresJobQueueTests.RequeueAsync_WithDelay_HidesTheJobUntilTheDelayElapses`, `PostgresStaleAttemptFencingIntegrationTests.AutomaticTransientFailure_UsesConfiguredBackoffAndStopsAtExactMaxAttempts` |
| 6 | A worker killed mid-job is recovered within the heartbeat timeout plus one sweep. | `last_heartbeat_at` stops advancing; the 30 s sweep requeues or fails the job once it is 90 s old. | `PostgresJobClaimContentionTests.WorkerLoss_NaturalHeartbeatLapse_ReExecutesOnceAndSuppressesTheStaleWorkersTerminal`, `PostgresStaleAttemptFencingIntegrationTests.StaleAttempt_IsFencedAcrossReclaimDuplicateDeliveryAndTerminalization` (kills the worker process) |
| 7 | Log events are not duplicated. | Primary key on `(operation_id, event_id)`, checked in the append transaction. | `PostgresExecutionLogStoreTests.BoundedLog_TrimsWithAbsoluteCursorsAndDeduplicatesExternalEvents` |
| 8 | Correctness depends only on database time. | Section 5. | `PostgresJobStoreClockTests.ClaimLeaseAndHeartbeatExpiry_UnderSkewedNodeClocks_FollowDatabaseTime` |
| 9 | A database restart during a running job loses nothing and does not run the job twice. | Section 8. | `PostgresJobExecutionResilienceTests.JobExecutionService_WhenPostgresRestartsMidJob_JobCompletesExactlyOnce` |

### 5. Time
Every lease expiry, visibility delay, heartbeat timeout, execution timeout and retention comparison
uses the database clock.

- Stamped in SQL with `now()`: `claimed_at`, `last_heartbeat_at`, `next_retry_at`, `updated_at`,
  `queue_visible_at`, `expires_at` and every lease `expires_at`. A compare-and-set write replaces a
  timestamp the writer changed with the database time (3.3); it never stores the writer's value.
- Compared in SQL with `now()`: claim visibility, lease acquire and renew, the lease-owned update,
  log and result-package expiry on read, the retention sweep.
- Compared in C# against `IExecutionJobStore.GetUtcNowAsync`: the places that today compare a stored
  timestamp against the node clock.

| Today (node clock) | Change |
|---|---|
| `JobReconciliationService`: heartbeat and timeout expiry against `DateTimeOffset.UtcNow` (five reads) | Reads the store clock. `LastHeartbeatAt` and `ClaimedAt` it compares against are database-stamped. |
| `ExecutionJobReconciler`: `NextRetryAt > DateTimeOffset.UtcNow` for provider-run retries | Reads the store clock. `NextRetryAt` is database-stamped. |
| `AssignedExecutionJobQueue`: heartbeat expiry of another worker's claim against its `TimeProvider` | Reads the store clock. |
| `RedisJobQueue`: visibility and stale-claim age against `DateTimeOffset.UtcNow` | Deleted; SQL `now()`. |
| Redis key TTLs (server clock already) | `expires_at` and SQL `now()`. |

`CreatedAt`, `CompletedAt` and `CancellationRequestedAt` remain the writer's values: they are shown
and sorted, never compared with a clock to make a decision. A worker's own execution timeout and
drain deadline are durations on its monotonic clock.

`TryCreateAsync` and `SetAsync` store timestamps as given, which is how a job is first written and
how tests age a record.

### 6. Write volume
A heartbeat or progress write changes `version`, `updated_at`, `last_heartbeat_at`,
`percent_complete` and `current_phase`. None is in an index or an index predicate, so PostgreSQL can
make the update heap-only. Three settings make that the normal case:

- `toast_tuple_target = 128` with every other variable-length column `STORAGE MAIN` moves `record`
  out of the heap row, so a new row version is a few hundred bytes and does not copy the JSON.
- `fillfactor = 50` leaves half of each heap page free at insert for those row versions; a later
  access to the page prunes the dead ones without vacuum.
- Autovacuum at 2 % dead rows (plus 200) instead of the default 20 %, because the claim index is a
  small partial index over a table that churns, and dead entries in it lengthen every claim.

Each heartbeat is one read of the row and one narrow update: 200 running jobs at the 30 s interval is
about 14 statements per second. The measured row versions, heap-only ratio and size growth for that
load are in the pull request.

### 7. Connections
The stores hold no connection between statements and use no session state, so they are safe behind
RDS Proxy and on a frozen Lambda.

| Host | Pool | Configured by |
|---|---|---|
| Serving host | The system `NpgsqlDataSource` (default max 200, min 20). | `Limits:Connections:*` |
| Single-job execution worker (AWS Batch, Azure Batch, Kubernetes Jobs; the server image started with `HONUA_OPERATION_ID`) | The system data source, with max 8 and min 0 when the operator set neither. | `Limits:Connections:MaxConnectionPoolSize` / `MinConnectionPoolSize`; the default changes in `PostgresDataSourceFactory.ResolveConnectionLimits`, next to the existing Lambda default. |
| GDAL worker (`Honua.Worker.Gdal`) | Its own data source for the job store only: max 4, min 0. | `Jobs:Store:WorkerMaxPoolSize`; connection string `ConnectionStrings:DefaultConnection`, which the GDAL worker now requires in place of `ConnectionStrings:redis`. |

A single-job worker uses at most three job-store connections at once (claim or finalize, heartbeat,
log append).

### 8. Failure behaviour
A statement that fails with a transient error (connection refused or reset, `57P01`, `57P03`, class
`08`, `53300`) is retried with backoff 0.2 s, 0.5 s, 1 s, 2 s when repeating it is safe:

- Always safe: failures while opening the connection; reads; enqueue, remove, lease renew and
  release, `AppendOnceAsync`, result-package writes.
- Safe with confirmation: `TryCreateAsync` and the compare-and-set writes. If an attempt failed after
  the statement was sent and the retry affects no row, the store reads the row; version
  `expected + 1` with the same `record_hash` and attempt state means the first attempt committed, and
  the call returns `true`.
- Not repeated after send: claim, lease acquire, `AppendAsync` without an event id. The error reaches
  the caller.

Beyond that budget the existing loops carry on: the claim loop retries at its 5 s poll, the heartbeat
pump at its next interval, the reconciler at its next sweep. Nothing is held in memory that the
database does not have, so an outage loses no job.

- An outage shorter than the retry budget is invisible to jobs.
- An outage shorter than the heartbeat timeout minus the interval (60 s by default) causes no
  re-execution: heartbeats resume before any job looks dead.
- A longer outage can make a live job look dead; the reconciler requeues it and the original attempt
  is fenced by version on its next write. That is the existing at-least-once guarantee, unchanged.
- A claim whose reply is lost leaves a `Provisioning` row with no worker; it is requeued after the
  heartbeat timeout and has consumed one attempt.
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
There is no migration tool. Operators let running jobs finish before upgrading. A job still in Redis
after the upgrade is abandoned: nothing claims it, its record, logs and result are not visible
through any API, and its keys expire in Redis after seven days. Upgrade the serving host first (it
applies the migrations); a worker started against a database without the tables logs the store error
and retries until they exist.

### 10. Limits
Expected envelope: hundreds of concurrently running jobs, a backlog of the order of 100,000 queued or
retained rows, tens of submissions per second. The claim is an index scan of the first claimable row;
at 200,000 rows it reads about ten buffers.

What would be added if that is exceeded, each behind the same interfaces:

- a heartbeat statement that does not read the record first (a default interface method on
  `IExecutionJobStore`);
- claiming several jobs per statement (`LIMIT n`) for a worker that runs several at once;
- time partitioning of `execution_jobs` so retention becomes dropping a partition;
- an optional wake-up signal so an idle worker does not wait for its 5 s poll.

Deliberately not done: `LISTEN/NOTIFY`, session advisory locks, `SET`, any second queue structure,
any cross-statement transaction.

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

## Consequences
- A deployment with PostgreSQL and no Redis runs durable jobs. Community edition gets the durable job
  runtime.
- The GDAL worker needs database credentials and network access to PostgreSQL; it no longer needs
  Redis.
- Job state is queryable with SQL and is covered by database backup and restore.
- Job writes add load to the system database: about 14 statements per second per 200 running jobs,
  plus about ten statements per job over its life.
- Jobs in Redis at upgrade are abandoned (section 9).
