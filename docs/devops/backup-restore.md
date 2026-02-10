# Backup and Restore

Guidance for protecting Honua data and recovering quickly.

---

## Best Practices

- Use managed Postgres backups if available.
- Enable point-in-time recovery (PITR).
- Test restores regularly.

---

## Restore Checklist

1. Restore the database snapshot.
2. Verify PostGIS extensions.
3. Validate a known feature query.

---

## Related Docs

- [Zero Downtime Migrations](zero-downtime-migrations.md)
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
