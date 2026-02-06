# Zero-Downtime Migration Strategy

Honua Server uses DbUp to apply embedded SQL migrations at startup. To avoid downtime, use an expand/contract strategy and deploy migrations carefully.

## Principles

1. **Backward compatible**: Add new columns/tables without breaking existing reads.
2. **Expand/contract**: Write new data first, then remove old fields in a later release.
3. **Avoid locks**: Use `CONCURRENTLY` for indexes and `NOT VALID` constraints where possible.

## Recommended Process

### 1) Expand (Additive Changes)
- Add nullable columns or new tables
- Create indexes concurrently
- Keep existing columns intact

### 2) Deploy Migration Safely
- Run migrations once before rollout
- Use `HONUA_SKIP_MIGRATIONS=true` on remaining instances to avoid concurrent migrations

### 3) Deploy Application Code
- Deploy code that reads/writes both old and new fields
- Use feature flags to control behavior

### 4) Backfill
- Run a background job to populate new columns
- Monitor for completion and correctness

### 5) Contract (Cleanup)
- Remove old columns and constraints in a later release
- Ensure no live code depends on old schema

## Long-Running Migrations

- Split large migrations into smaller steps
- Schedule heavy backfills during low-traffic windows
- Validate migration time in staging

## Rollback Guidance

- Maintain `*_rollback.sql` scripts for critical migrations
- For breaking changes, use blue/green deployments or dual-write strategies

## Validation

- Verify `schema_versions` table is updated
- Validate application readiness: `/healthz/ready`
- Run smoke tests against critical endpoints
