# Query Optimization

Operations guide for monitoring and improving Honua query performance in production.

**Scope**: Practical tuning levers and database checks. This is not a Postgres tuning manual.

---

## Quick Checks

- Verify response times on a representative query.
- Check database load and query hotspots.
- Confirm spatial indexes exist and are used by the planner.

---

## Honua Query Limits

These limits apply across protocols and are the first line of protection for runaway queries:

- `Limits__Query__MaxRecordCount`
- `Limits__Query__DefaultRecordCount`
- `Limits__Query__MaxOffset`
- `Limits__Query__MaxBboxAreaSqKm`
- `Limits__Query__QueryTimeout`
- `Limits__Connections__RequestTimeout`

Use `/api/v1/admin/config` to confirm effective values.

---

## Query Design Best Practices

- Always use `bbox`, `limit`, and server-side filters.
- Avoid large offsets; prefer paging from the last seen ID where possible.
- Return only the fields you need.

---

## Database Checks

**Index usage**:
```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM honua.features
WHERE layer_id = 1
AND ST_Intersects(geometry, ST_MakeEnvelope(-122.5, 37.7, -122.3, 37.8, 4326));
```

**Statistics refresh after bulk loads**:
```sql
ANALYZE honua.features;
```

**Spatial index sanity check**:
```sql
SELECT indexname, idx_scan
FROM pg_stat_user_indexes
WHERE schemaname = 'honua'
AND indexname LIKE '%geom%';
```

---

## If Queries Are Slow

- Check for missing indexes or sequential scans.
- Confirm statistics are current after imports.
- Reduce geometry complexity or simplify where possible.
- Tighten limits if users are requesting large result sets.

---

## Related Docs

- [Performance Monitoring](performance-monitoring.md)
- [Connection Pool Sizing](connection-pool-sizing.md)
- [Spatial Query Troubleshooting](troubleshooting/spatial-query-problems.md)
