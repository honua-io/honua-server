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
STORAGE_VOLUME=honua_storage
docker run --rm -v "$STORAGE_VOLUME":/data -v "$PWD":/backup alpine \
  tar czf /backup/honua-files-$(date +%Y%m%d).tar.gz -C /data .
```

## Restore sequence

1. Stop traffic: scale Honua replicas to zero (or stop the container) so nothing writes during restore.
2. Restore the dump into a clean database:

```bash
PGPASSWORD=replace-with-db-password pg_restore \
  -h db.example.com -U honua -d honua --clean --if-exists honua-20260609.dump
```

3. Restore the file-storage volume or bucket from the matching snapshot.
4. Start Honua. Migrations run automatically at startup and roll the restored schema forward to match the running version; set `HONUA_SKIP_MIGRATIONS=true` only if you intend to run them out-of-band, and check state via `GET /api/v1/admin/observability/migrations`.

## Verify

```bash
psql -h db.example.com -U honua -d honua -c "SELECT PostGIS_Version();" && \
curl -s http://localhost:8080/healthz/ready
```

Expected: a PostGIS version row, then `Ready`. Finish with a known feature query against a restored layer to confirm data integrity.

## Troubleshoot

- **`pg_restore` errors on missing extension** — create `postgis` in the target database first: `CREATE EXTENSION IF NOT EXISTS postgis;`.
- **Server starts but layers are empty** — the dump predates the data, or you restored into the wrong database; check `ConnectionStrings__DefaultConnection` matches the restored host.
- **Startup migration failure after restore** — the restored schema is newer than the deployed image (you restored a dump taken under a later release); deploy the matching or newer application version.
- **Attachments 404 after restore** — file-storage snapshot not restored or `FileStorage__*` settings point at a different bucket/volume.

## Next steps

- [Upgrade and roll back](upgrade-and-rollback.md)
- [Monitor Honua Server](monitoring.md)
- [Configure Honua Server](configuration.md)
