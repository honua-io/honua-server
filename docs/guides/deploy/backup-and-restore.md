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
docker run --rm -v "$STORAGE_VOLUME":/data -v "$PWD":/backup alpine \
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
  sh -c 'find /data -mindepth 1 -delete && tar xzf "/backup/'"$ARCHIVE"'" -C /data && chown -R 1001:1001 /data'
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
container, stop writes and copy any surviving files out of it:

```bash
docker compose stop honua
OLD_HONUA_CONTAINER=$(docker compose ps -a -q honua)
mkdir -p honua-storage-migration
docker cp "$OLD_HONUA_CONTAINER:/tmp/honua-storage/." honua-storage-migration/
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

## Automated backups and RTO/RPO (Enterprise)

The manual sequence above is the portable baseline every deployment can run. Enterprise
deployments add automated, objective-driven disaster recovery (ADR-0024, #356):

| Capability | Entitlement key | What it does |
|---|---|---|
| Backup Automation | `dr.backup-automation` | Scheduled `pg_basebackup` base backups plus continuous WAL archiving for point-in-time recovery. |
| Failover Playbooks | `dr.failover` | Active-passive failover driven by automated health checks against the primary serving surface. |
| Cache State Backup | `dr.cache-backup` | Backup/restore Redis cache state so warm cache contents survive a regional failover. |
| RTO/RPO Reporting | `dr.rto-rpo-reporting` | Tracks recovery objectives and reports recovery readiness, last successful backup, and the restorable point. |

### Recovery objectives (RTO/RPO)

- **Recovery Time Objective (RTO)** — the maximum tolerable time to restore service after a
  disaster. Default enterprise target: **1 hour**.
- **Recovery Point Objective (RPO)** — the maximum tolerable data loss, measured as the age
  of the most recent restorable point. Default enterprise target: **5 minutes**, sustained by
  a daily base backup plus a 5-minute WAL `archive_timeout`.

For the restorable point to stay inside the RPO, the WAL archive interval must be at or below
the RPO; a base backup is always required to anchor recovery (archived WAL alone cannot
rebuild a cluster).

### Recovery readiness

Readiness reporting projects recorded backups onto the objectives and reports one of:

| Readiness | Meaning | Action |
|---|---|---|
| `not_ready` | No successful base backup exists. | Cannot recover — take a base backup immediately. |
| `at_risk` | A base backup exists, but the restorable point is older than the RPO. | Investigate WAL archiving / backup cadence. |
| `ready` | A base backup exists and the restorable point is within the RPO. | Recoverable inside objectives. |

The shared posture rules live in `Honua.Core` (`Features/DisasterRecovery`), so the admin
reporting surface and provider implementations agree on a single definition of recoverable.

### Failover decision (active-passive)

The failover playbook reacts to automated health checks against the primary serving surface.
The shared decision rules also live in `Honua.Core` (`Features/DisasterRecovery`) so the
failover automation, the admin reporting surface, and tests agree on when to fail over:

| Decision | When | Action |
|---|---|---|
| `hold` | The primary is healthy, or it has fewer consecutive failed health checks than the configured threshold. | Stay on the primary. |
| `promote` | The primary has reached the consecutive-failure threshold and the standby is recoverable inside its objectives (`ready`). | Promote the standby. |
| `block` | The primary has reached the threshold but the standby is not recoverable inside its objectives. | Block automated failover and page an operator — promoting now would breach the RPO. |

The default enterprise policy triggers after **three consecutive failed health checks** and
requires the standby to be recoverable before an automated promotion proceeds; a policy may
opt out of the standby-readiness gate for environments that accept best-effort failover.

> Scope note (#356): the landed slices ship the licensing catalog entries, the
> recovery-objective / backup-record / readiness domain, the active-passive failover decision
> domain, and this runbook. The PostgreSQL backup service that executes the schedule, the
> Redis backup path, the failover orchestration runtime, the admin endpoint, and the
> multi-region Terraform modules are tracked as follow-up work on the same issue.

## Next steps

- [Upgrade and roll back](upgrade-and-rollback.md)
- [Monitor Honua Server](monitoring.md)
- [Configure Honua Server](configuration.md)
