# Backup and Restore Procedures

This document defines baseline backup/restore procedures and target recovery objectives.

## Recovery Targets

- **RPO (Recovery Point Objective)**: 15 minutes (adjust per environment)
- **RTO (Recovery Time Objective)**: 60 minutes (adjust per environment)

## Backup Scope

- **PostgreSQL database** (schemas, data, `schema_versions`)
- **Secure connection registry** (stored in PostgreSQL)
- **Uploaded/imported files** (local volume or object storage)
- **Configuration and secrets** (managed by your secret store)

## Backup Strategy

### PostgreSQL Logical Backups (Portable)
```bash
# Daily full backup
pg_dump -h postgres.honua.internal -U honua_prod -Fc -f honua_$(date +%F).dump honua
```

### PostgreSQL Physical Backups (Faster Restore)
```bash
# Base backup with WAL
pg_basebackup -h postgres.honua.internal -U honua_prod -D /backups/honua_base -Fp -Xs -P
```

### WAL Archiving (Point-in-Time Recovery)
- Enable WAL archiving in PostgreSQL
- Ship WAL segments to durable storage
- Retain WALs for at least the RPO window

### File Storage Backups
- **Local volumes**: snapshot the volume or rsync to backup storage
- **Object storage**: enable versioning and lifecycle policies

## Restore Procedure

### Logical Restore
```bash
# Restore to a new database
createdb -h postgres.honua.internal -U honua_prod honua_restore
pg_restore -h postgres.honua.internal -U honua_prod -d honua_restore honua_2025-01-01.dump
```

### Physical Restore
1. Stop PostgreSQL
2. Restore base backup to data directory
3. Replay WAL up to target time
4. Start PostgreSQL and verify

### Validation
- Verify schema version:
  ```sql
  SELECT * FROM schema_versions ORDER BY applied DESC LIMIT 5;
  ```
- Run sanity checks on critical tables
- Validate application readiness: `/healthz/ready`

## Backup Verification

- Test restores monthly
- Record restore time vs. RTO target
- Validate data integrity checks

## Operational Notes

- Store backups encrypted at rest
- Restrict access to backup storage
- Document backup retention policy and ownership
