# Back up and restore

Inventory the deployment's durable state, take a PostGIS backup, and plan a
restore that accounts for Redis and referenced file/object bytes as well.

> **2026.1 qualification boundary:** Full-platform recovery for a topology with
> durable Redis jobs/workflows is **not qualified in 2026.1** until a matching
> candidate-bound destructive-restore receipt is linked below. The PostgreSQL
> and file-storage commands on this page are component procedures, not proof
> of complete platform recovery. AOF/reconnection and same-container restart
> tests do not establish recovery after primary Redis storage is destroyed.

**Prerequisites:** `pg_dump`/`pg_restore` matching your server's PostgreSQL major version, and credentials for the Honua database.

**Edition boundary:** Essential backup, restore and recoverability are baseline product obligations in every edition. Enterprise adds advanced scheduling, failover automation, governance and reporting; it does not make basic recovery paid-only. See [commercial boundaries](../../concepts/editions-and-licensing.md#commercial-boundaries-for-20261).

## Where state lives

| Store | Contents | Backup need |
|---|---|---|
| PostGIS database | Features, layers, service metadata, connections, roles, database-backed audit data, migration journal and enabled transactional outbox rows | Dump/PITR plus the matching restore point for every other enabled store; PostgreSQL is not the only durable substrate |
| File storage (`FileStorage__Provider`) and other configured output stores | Uploaded files, attachments and job/workflow output bytes referenced by database or Redis records; local volumes or object storage | Preserve the actual referenced bytes and object versions, including outputs outside the default file-storage volume; replication alone is not a historical recovery point |
| Redis durable state | Enabled execution-job records and queue membership, execution logs, workflow definitions/runs/watermarks, proposals, operation state and GP result packages/references | Include every configured Redis database/namespace in the recovery inventory. Persist and back up durable state; do not omit it or flush a mixed cache/job instance |
| Redis disposable cache | Reconstructible cache entries only, where the deployment inventory proves they share no required durable state | May be rebuilt after restore; exclude only the explicitly identified disposable state, never Redis as a whole |
| Deployment configuration and recovery keys | Store locations, namespaces, image/config versions and access to encryption/signing keys and secrets needed to read restored state | Retain versioned configuration and a tested secret-manager recovery/access procedure; do not put plaintext credentials in the backup manifest |

Application replicas are replaceable only when their durable bytes live on the
declared persistent stores. Inventory container-local uploads/outputs before
replacement; the older Compose migration below covers a known exception.

## Consistent recovery set

For Local Docker, inventory named volumes and any external buckets/Redis
instances. For AWS ECS-small or another managed placement, inventory the
database, Redis service, object stores and infrastructure/secret configuration
actually used. A managed database backup does not cover its Redis or bucket
neighbors. Enabled Preview features still count when they store durable state.

1. Record the release lock and server/worker image digests, PostgreSQL/Redis
   versions, database/schema and Redis database/namespace identities, output
   locations, retention/TTL policies, and all enabled durable features. Identify
   the recovery owner and backup mechanism for each store.
2. Fence new submissions and writes, pause scheduled producers, and drain
   accepted jobs and outbox deliveries where possible. Record unresolved queued,
   running and pending work by stable ID. Stop every server replica, GP worker,
   workflow scheduler, outbox dispatcher and external writer before snapshots;
   blocking HTTP traffic alone does not stop background mutations.
3. While writers remain stopped, capture PostgreSQL and file/object recovery
   points as described below. Capture Redis with the deployment's version-specific
   persistence/backup procedure: for self-managed Redis, preserve a completed
   restorable RDB snapshot or the complete AOF set and manifest required by that
   Redis version on storage independent of the primary volume; for managed Redis,
   retain its supported snapshot/export and restore instructions. Do not copy a
   live, changing AOF file and call it a backup. AOF on the lost primary disk is
   not an independent recovery copy; persistence and no-eviction settings alone
   are not DR qualification.
4. Bind the recovery set to one backup ID with per-store capture times, artifact
   checksums or provider snapshot/object-version IDs, completion status, and the
   queued/running/outbox/output inventory. Redis expiration continues to matter:
   record TTLs and restore delay, and treat expired records as unavailable rather
   than extending retention by editing keys. If writers could not be quiesced or
   store points cannot be reconciled, record that inconsistency; do not label the
   set a complete platform backup.
5. Verify independent backup access, then rehearse restoration into isolated
   replacement stores with production writers fenced out. Keep the original
   recovery set immutable. A backup listing or successful snapshot command is
   insufficient; retain the destructive-restore and application-value assertions
   in the [qualification receipt](#recovery-qualification-and-receipts).

This is the required consistency plan, not a certified Redis restore recipe for
every provider. If the selected provider has no supported recoverable backup,
or any enabled durable substrate is omitted, that topology's full-platform
recovery remains **not qualified in 2026.1**. Do not replace it with an empty
Redis instance and describe a PostgreSQL-only restore as complete.

## Steps

These component commands assume the writers above are stopped and the backup
ID already identifies the matching Redis and output recovery points.

1. Dump the database. A compressed custom-format dump restores selectively and in parallel.

```bash
PGPASSWORD=replace-with-db-password pg_dump \
  -h db.example.com -U honua -d honua \
  --format=custom --file=honua-$(date +%Y%m%d).dump
```

2. For point-in-time recovery, configure WAL archiving or the managed provider's PITR and verify its retention and restorable window. Application-level dumps then become your portable/offsite layer. Select a database recovery point consistent with the other stores; independent latest snapshots need not agree.

```bash
psql -h db.example.com -U honua -d honua -c "SHOW archive_mode;"
```

3. If you use local file storage, snapshot the volume alongside the database dump so attachments and uploads stay consistent with metadata.

```bash
STORAGE_VOLUME="${HONUA_STORAGE_VOLUME_NAME:-honua_storage}"
ARCHIVE="honua-files-$(date +%Y%m%d).tar.gz"

# Fail rather than letting `docker run -v` silently create a new empty volume.
docker volume inspect "$STORAGE_VOLUME" >/dev/null
SOURCE_FILE_COUNT=$(docker run --rm -v "$STORAGE_VOLUME":/data:ro alpine \
  sh -c 'find /data -type f | wc -l')
docker run --rm -v "$STORAGE_VOLUME":/data:ro -v "$PWD":/backup alpine \
  tar czf "/backup/$ARCHIVE" -C /data .

# Prove the archive is readable and contains the same number of files.
tar tzf "$ARCHIVE"
ARCHIVE_FILE_COUNT=$(tar tzf "$ARCHIVE" | grep -vc '/$' || true)
test "$SOURCE_FILE_COUNT" -eq "$ARCHIVE_FILE_COUNT"
```

The repo Compose file mounts this named volume at `/var/lib/honua/storage` and
sets its engine-level name from `HONUA_STORAGE_VOLUME_NAME`. A zero file count is
valid only when the deployment intentionally has no uploads or attachments; record
that fact with the backup evidence rather than assuming an empty archive is correct.

## Restore sequence

1. Fence traffic and stop all writers listed in the consistent recovery set, including GP workers, schedulers and outbox dispatchers. Select a complete recovery set and preserve evidence of the failed state. Use isolated replacement stores; the following commands overwrite target data and must never point at a still-serving store.
2. Restore the dump into a clean database:

```bash
PGPASSWORD=replace-with-db-password pg_restore \
  -h db.example.com -U honua -d honua --clean --if-exists honua-20260609.dump
```

3. Restore the file-storage volume or bucket from the matching snapshot. For the
   repo Compose path, keep Honua stopped and restore into the existing declared volume:

```bash
STORAGE_VOLUME="${HONUA_STORAGE_VOLUME_NAME:-honua_storage}"
ARCHIVE=honua-files-20260609.tar.gz
docker volume inspect "$STORAGE_VOLUME" >/dev/null
docker run --rm -v "$STORAGE_VOLUME":/data -v "$PWD":/backup:ro alpine \
  sh -c 'find /data -mindepth 1 -delete && tar xzf "/backup/$1" -C /data && chown -R 1001:1001 /data' \
  restore "$ARCHIVE"
```

4. Restore every enabled durable Redis database/namespace from the matching recovery set using the recorded Redis-version/provider procedure. Start Redis without application clients and verify the persistence load completed, the expected job/queue/workflow/proposal/result records are present, and expiration has not removed required records. Restore output bytes held outside the file-storage snapshot too. Keep workers fenced if records, queue membership or output references disagree; use the loss expectations below to reconcile instead of guessing key values or blindly resubmitting work.
5. Point the recorded application/worker configuration and recovery keys at the replacement stores. Start the matching Honua version only in the isolated recovery environment with external side effects fenced: startup can resume background work. Migrations run automatically at startup and roll the restored schema forward to match the running version; set `HONUA_SKIP_MIGRATIONS=true` only if you intend to run them out-of-band, and check state via `GET /api/v1/admin/observability/migrations`. Startup also compares the journaled core-schema floor with its required physical tables and storage policy. For a non-default `Database:Schema`, the numbered adoption migration moves complete legacy guarded families forward and rejects partial or duplicate source/target state. This move is contract-gated on an existing database: keep restored traffic fenced, drain older nodes, review `GET /api/v1/admin/deploy/preflight`, and supply the reported one-shot `HONUA_APPROVE_CONTRACT_MIGRATIONS` nonce before apply. A partial restore or hand-created core object fails readiness/startup; Honua never repairs that mismatch from an ordinary store read or write. Reconcile the restore through the numbered migrations before readmitting traffic.
6. Complete the value and work-state verification below, record all losses and replay decisions, then separately authorize production cutover and re-enable submissions, workers and external delivery. Readiness alone does not authorize replay of uncertain work.

## Verify

```bash
psql -h db.example.com -U honua -d honua -c "SELECT PostGIS_Version();"
```

> Open `http://localhost:8080/healthz/ready` in a browser.

Expected: a PostGIS version row, then `Ready`. These establish component
availability only. Query a known restored layer and assert independently
recorded feature IDs, counts, attribute values, geometry/ordinates and SRID;
compare attachment/output bytes with the saved checksums. For enabled raster
outputs, verify pixel values, nodata and metadata as well. Compare queued,
running and terminal job IDs, logs, workflow state, result references and
outbox status against the recovery-set inventory, including expired/missing
records. Verify restored output references resolve to the intended bytes.

### Expectations after destructive state loss

| Work at the recovery boundary | Operator expectation and required disposition |
|---|---|
| Queued jobs | Only records and queue entries retained at the recovery point and still within retention can be recovered. Submissions after that point, or lost/expired Redis records, may be absent. PostgreSQL cannot reconstruct them; reconcile against the submission inventory before deliberate resubmission. |
| In-flight jobs/workflows | A recovered running record may have an obsolete worker lease/heartbeat and may be retried or failed by reconciliation. Effects completed after the snapshot can already exist elsewhere. Do not assume exactly-once replay or automatic recovery of missing records; inspect committed effects and use the operation's supported idempotency/retry contract, or record manual intervention. |
| Terminal jobs and output references | Restored metadata does not restore output bytes, and restored bytes do not recreate lost result packages. Missing/expired references, partial outputs and unreferenced output objects require reconciliation against the manifest; do not report such jobs as verified successful or publish partial output as complete. |
| Transactional outbox work | PostgreSQL restores rows/statuses at its selected point. A delivery completed after that point can be pending again; work committed after the point may be lost. External consumers are not rolled back with the database. Reconcile destination acknowledgements and stable event IDs before enabling dispatch; expect possible duplicate delivery and apply consumer deduplication. Do not mark rows delivered merely to clear a backlog. |
| Proposals and operation evidence | Restore their enabled Redis state with its policy/approval/audit context. A missing or expired proposal/operation is not approval to execute; obtain a newly reviewed action where needed after reconciling prior effects. |

The transient-restart contract below applies only while the original durable
records survive. It does not promise no lost jobs, exactly-once side effects,
or reconstruction after destruction of the primary state.

### Recovery qualification and receipts

**Current full-platform receipt: none linked; not qualified in 2026.1 for
durable Redis topologies.** [Release #257](https://github.com/honua-io/honua-release/issues/257)
tracks restore-evidence substrate coverage; a tracker or a green validator is
not itself an executed restore receipt.

Before publishing a recovery guarantee, link an immutable receipt bound to
the exact candidate lock/image/worker digests, placement and enabled-store
inventory. It must identify independent backups, destructive loss of primary
state, per-store restore points, the executed restore procedure, expected and
actual application values, job/output/outbox reconciliation, and measured
loss/recovery times. Every enabled durable store must be accounted for; skipped,
missing or PostgreSQL-only cells cannot certify full-platform recovery. Keep
PostgreSQL-only evidence explicitly scoped to PostgreSQL. No RTO/RPO guarantee
is established by this guide or by a same-container restart test.

### Upgrading an older Compose quickstart

Compose versions before #2624 stored local files under the container's
`/tmp/honua-storage` and did not mount that directory. Before replacing the old
container, quiesce writes at the edge or through a maintenance window, then copy
any surviving files from the still-running container before stopping it:

```bash
OLD_HONUA_CONTAINER=$(docker compose ps -q honua)
test -n "$OLD_HONUA_CONTAINER"
mkdir -p honua-storage-migration
docker cp "$OLD_HONUA_CONTAINER:/tmp/honua-storage/." honua-storage-migration/
docker compose stop honua
```

After updating the Compose file, create the new named volume and copy the files
into it before starting Honua:

```bash
STORAGE_VOLUME="${HONUA_STORAGE_VOLUME_NAME:-honua_storage}"
docker volume create "$STORAGE_VOLUME" >/dev/null
docker run --rm -v "$STORAGE_VOLUME":/data -v "$PWD/honua-storage-migration":/source:ro alpine \
  sh -c 'cp -a /source/. /data/ && chown -R 1001:1001 /data'
docker compose up -d
```

If the old container has already been deleted, its tmpfs contents cannot be
recovered; restore the matching file-storage backup instead.

## Troubleshoot

- **`pg_restore` errors on missing extension** — create `postgis` in the target database first: `CREATE EXTENSION IF NOT EXISTS postgis;`.
- **Server starts but layers are empty** — the dump predates the data, or you restored into the wrong database; check `ConnectionStrings__DefaultConnection` matches the restored host.
- **Startup migration failure after restore** — the restored schema is newer than the deployed image (you restored a dump taken under a later release); deploy the matching or newer application version.
- **Attachments 404 after restore** — file-storage snapshot not restored or `FileStorage__*` settings point at a different bucket/volume.

## Disaster recovery ownership (IaC / managed database, by design)

Honua Server is a replaceable application tier when all durable state is on
the declared PostgreSQL, Redis and file/object stores. Recreating replicas does
not reconstruct lost backing-store data. **Disaster recovery
(backup automation, failover, and RTO/RPO reporting) is owned by the deployment's
infrastructure/managed-database layer, not implemented inside this server** (#2946
re-grade of #356/ADR-0024):

| Capability key | What actually gates it today |
|---|---|
| `dr.backup-automation` | No server-side background job or route. Managed-database backups (for example RDS automated backups/snapshots and multi-AZ) are parameterized by `honua-iac`. |
| `dr.failover` | No server-side evaluator or orchestrator. Failover (promoting a standby, cutover) is owned by the managed-database layer and documented as a drill procedure in `honua-iac`. |
| `dr.cache-backup` | No cache-state backup/restore feature exists in this server. Redis persistence (RDB/AOF) is an infrastructure-layer setting the deployment owns. |
| `dr.rto-rpo-reporting` | No server-computed RTO/RPO posture. Recovery-point/recovery-time evidence comes from the infrastructure layer's backup/restore and failover drills, captured against `honua-iac`'s `dr-evidence-template.json` schema. |

A **bring-your-own-database** deployment retains responsibility for recovery of
every enabled store, including Redis and file/object storage. Its managed
database covers only the database portion; infrastructure ownership does not
waive the full-platform consistency and qualification requirements in any edition.

These four keys are catalogued in `docs/gis/data/capability-keys.v1.json` (Enterprise
edition) for sales/roadmap visibility, but carry no HTTP route and are recorded with an
`infra-owned` reason in
[`capability-no-surface-allowlist.v1.json`](../../gis/data/capability-no-surface-allowlist.v1.json)
rather than silently omitted. `Honua.Core`'s `Features/DisasterRecovery` domain still
holds reusable, pure vocabulary types (`RecoveryObjectives`, `BackupRecord`,
`BackupSchedule`, `RecoveryReadiness`, `IBackupStatusProvider`) that a future concrete
backup-status provider could implement; a prior pure `FailoverDecisionEvaluator` /
`RecoveryReadinessEvaluator` pair and their supporting types had zero callers anywhere
in the codebase and were removed as dead code in #2946 (recoverable from git history if
that work resumes).

### Recovery objectives (RTO/RPO) as vocabulary

- **Recovery Time Objective (RTO)** — the maximum tolerable time to restore service after a
  disaster.
- **Recovery Point Objective (RPO)** — the maximum tolerable data loss, measured as the age
  of the most recent restorable point.

`RecoveryObjectives.Default` (1 hour RTO / 5 minute RPO) exists in `Honua.Core` as a shared
default for whoever computes these numbers — today that is the infrastructure layer's backup
drills, not a Honua Server endpoint. Those vocabulary defaults are not published
recovery guarantees for any deployment.

## Redis durable-job loss contract

Redis is required for durable jobs/workflows (queue, execution logs, run state) — it is not a
pure cache. The contract for what happens to an in-flight job when Redis becomes unavailable
mid-execution **and its durable state survives**:

- `JobExecutionService`'s heartbeat pump writes are wrapped so a failed write is logged and
  retried on the next heartbeat interval — it never crashes the worker.
- The worker's outer claim loop (and the separate `JobReconciliationService` sweep) both catch
  broadly around every Redis call for the same reason: one failed Redis operation must not take
  down the worker process.
- The server's Redis connection is configured with `AbortOnConnectFail = false` and an
  exponential reconnect policy (`src/Honua.Server/Program.cs`), so a transient Redis restart is
  followed by automatic reconnection rather than a permanent failure.
- If a job's heartbeat goes stale (the owning worker never recovers in time), the reconciliation
  sweep detects the expired heartbeat and — depending on the job's retry policy — either
  requeues it to `Queued` (`ClaimedBy`/`LastHeartbeatAt` cleared, immediately re-claimable by any
  worker) or fails it terminally. Either outcome is loud (a status transition + log line) and
  re-submittable; there is no silent loss and no permanent wedge in `Running` with nobody able
  to observe or recover it.
- This is proven end-to-end against a real Redis container stop/restart (not just a hand-set
  stale timestamp) by
  `RedisJobExecutionResilienceTests.JobExecutionService_WhenRedisRestartsMidJob_JobSurvivesWithNoSilentLossOrPermanentWedge`
  (`tests/dotnet/Honua.Server.Tests/Features/Infrastructure/ControlPlane/`), which starts a job,
  stops the Redis container while it is `Running`, restarts it, and asserts the job still
  completes; the complementary "owning worker never comes back" half of the contract (stale
  heartbeat → reconciler requeues for retry) was already covered by
  `RedisExecutionSubstrateIntegrationTests.JobReconciliationService_WithRedis_RequeuesHeartbeatExpiredJobForRetry`
  in the same directory.

## Next steps

- [Upgrade and roll back](upgrade-and-rollback.md)
- [Monitor Honua Server](monitoring.md)
- [Configure Honua Server](configuration.md)
