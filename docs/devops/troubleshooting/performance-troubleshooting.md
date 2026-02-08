# Performance Troubleshooting

Use this guide when requests are slow, time out, or spike in latency.

**Scope**: Quick diagnosis, common causes, and links to tuning guides.

---

## Quick Checks

- Check latency and error rate via metrics:
  - `GET /api/v1/metrics/performance`
  - `GET /api/v1/metrics/database`
- Review recent logs for slow queries or timeouts.

---

## Common Causes

- Missing spatial or attribute indexes
- Excessive result sizes (large bbox, no limit, high offset)
- Connection pool saturation
- Stale database statistics after bulk import

---

## Fixes to Try

- Tighten `Limits__Query__MaxRecordCount` and `Limits__Query__MaxBboxAreaSqKm`.
- Reduce large offsets and add server-side filters.
- Refresh database stats: `ANALYZE honua.features;`
- Add or rebuild spatial indexes.

---

## Related Docs

- [Query Optimization](../query-optimization.md)
- [Connection Pool Sizing](../connection-pool-sizing.md)
- [Performance Monitoring](../performance-monitoring.md)
