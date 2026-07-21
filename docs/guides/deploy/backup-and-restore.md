# Back up and restore

You'll know exactly what state lives where, run a restorable PostGIS backup, and execute a clean restore sequence.

**Prerequisites:** `pg_dump`/`pg_restore` matching your server's PostgreSQL major version, and credentials for the Honua database.

## Where state lives

| Store | Contents | Backup need |
|---|---|---|
| PostGIS database | Everything durable: features, layers, service metadata, connections, roles, audit data, migration journal | Primary backup target |
| File storage (`FileStorage__Provider`) | Uploaded files and attachments — a local volume by default, or S3-compatible object storage | Back up the volume, or rely on bucket versioning/replication |
| Redis | Cache entries plus durable-job queue, execution logs, and workflow run state | Transient; enable AOF so queued/in-flight jobs survive a Redis restart, but exclude it from restore planning |

Honua replicas themselves are stateless — nothing on an application container needs backup.

## Steps

1. Dump the database. A compressed custom-format dump restores selectively and in parallel.

```bash
PGPASSWORD=replace-with-db-password pg_dump \
  -h db.example.com -U honua -d honua \
  --format=custom --file=honua-$(date +%Y%m%d).dump
```

2. For point-in-time recovery, enable WAL archiving on the database side (or use your managed provider's PITR — RDS and Azure Flexible Server enable it by default). Application-level dumps then become your portable/offsite layer.

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

1. Stop traffic: scale Honua replicas to zero (or stop the container) so nothing writes during restore.
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

4. Start Honua. Migrations run automatically at startup and roll the restored schema forward to match the running version; set `HONUA_SKIP_MIGRATIONS=true` only if you intend to run them out-of-band, and check state via `GET /api/v1/admin/observability/migrations`.

## Verify

```bash
psql -h db.example.com -U honua -d honua -c "SELECT PostGIS_Version();" && \
curl -s http://localhost:8080/healthz/ready
```

Expected: a PostGIS version row, then `Ready`. Finish with a known feature query against a restored layer to confirm data integrity.

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

Honua Server is a **stateless** application tier: every replica can be destroyed and
recreated without data loss, because durable state lives in PostgreSQL (and, for
in-flight jobs, Redis — see [Redis durable-job loss contract](#redis-durable-job-loss-contract)
below), not in the application process. Following from that, **disaster recovery
(backup automation, failover, and RTO/RPO reporting) is owned by the deployment's
infrastructure/managed-database layer, not implemented inside this server** (#2946
re-grade of #356/ADR-0024):

| Capability key | What actually gates it today |
|---|---|
| `dr.backup-automation` | No server-side background job or route. Managed-database backups (for example RDS automated backups/snapshots and multi-AZ) are parameterized by `honua-terraform`. |
| `dr.failover` | No server-side evaluator or orchestrator. Failover (promoting a standby, cutover) is owned by the managed-database layer and documented as a drill procedure in `honua-terraform`. |
| `dr.cache-backup` | No cache-state backup/restore feature exists in this server. Redis persistence (RDB/AOF) is an infrastructure-layer setting the deployment owns. |
| `dr.rto-rpo-reporting` | No server-computed RTO/RPO posture. Recovery-point/recovery-time evidence comes from the infrastructure layer's backup/restore and failover drills, captured against `honua-terraform`'s `dr-evidence-template.json` schema. |

A **bring-your-own-database** deployment delegates all four of the above entirely to
the customer's managed database — there is nothing for Honua Server to do regardless
of edition.

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
drills, not a Honua Server endpoint.

## Redis durable-job loss contract

Redis is required for durable jobs/workflows (queue, execution logs, run state) — it is not a
pure cache. The contract for what happens to an in-flight job when Redis becomes unavailable
mid-execution:

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
