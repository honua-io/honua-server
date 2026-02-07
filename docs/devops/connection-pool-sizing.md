# Connection Pool Sizing

Configure PostgreSQL connection pooling for reliable Honua Server performance in production deployments.

**Scope**: Set pool limits, validate database headroom, and diagnose pool exhaustion.

---

## Quick Configuration

Use these settings as a starting point and adjust based on observed load.

```bash
# Honua connection and concurrency limits
Limits__Connections__MaxConnectionPoolSize=100
Limits__Connections__MaxConcurrentQueries=100
Limits__Connections__RequestTimeout=00:02:00

# Npgsql connection string parameters
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=yourpassword;Minimum Pool Size=10;Maximum Pool Size=100;Connection Idle Lifetime=300"
```

---

## Sizing Guidance

- Keep `MaxConnectionPoolSize x replica_count` below the database `max_connections` minus headroom.
- Reserve headroom for migrations, maintenance, and admin access.
- Prefer scaling read throughput by adding replicas instead of endlessly raising pool size.

**Simple rule**:
```
max_connections >= (pool_size x app_replicas) + headroom
```

---

## Monitoring

- Check Honua metrics: `GET /api/v1/metrics/database`
- Check Postgres activity:
  ```sql
  SELECT count(*) AS active_connections
  FROM pg_stat_activity
  WHERE datname = 'honua' AND state = 'active';
  ```

---

## Troubleshooting

**Symptoms**: timeouts, spikes in latency, "connection pool exhausted" logs.

**Fixes**:
- Increase `MaxConnectionPoolSize` and `MaxConcurrentQueries` in small steps.
- Add application replicas and lower per-replica pool size.
- Reduce slow queries and oversized result sets.
- Ensure Postgres `max_connections` can handle your total pool size.

---

## Related Docs

- [Performance Monitoring](performance-monitoring.md)
- [Query Optimization](query-optimization.md)
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
