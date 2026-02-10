# Database Query Caching

Honua exposes metrics for prepared statement and query cache performance.

**Scope**: Where to view query cache health and how to interpret it.

---

## Metrics Endpoint

```text
GET /api/v1/admin/performance/database/query-cache/statistics
```

**What to look for**:
- Hit ratio trending upward
- Low miss rate during steady traffic
- Stable utilization under normal load

---

## When to Investigate

- Miss rate spikes after deployments or schema changes
- High latency despite stable application metrics
- Sudden drops in cache utilization

---

## Related Docs

- [Caching Strategy](CACHING_STRATEGY.md)
- [Performance Monitoring](performance-monitoring.md)
