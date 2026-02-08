# Zero Downtime Migrations

Guidance for applying database migrations without service interruption.

---

## Recommended Approach

- Use backward-compatible schema changes first (add columns, not drop).
- Deploy application changes after schema is in place.
- Remove old columns in a later release.

---

## Checklist

1. Apply migrations in a rolling fashion.
2. Verify health endpoints and critical queries.
3. Monitor error rates and latency during rollout.

---

## Related Docs

- [Backup and Restore](backup-restore.md)
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
