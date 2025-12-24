# Connection Pool Sizing Guide

This document provides guidance for configuring PostgreSQL connection pool settings in Honua Server for optimal performance.

## Overview

Honua Server uses Npgsql's built-in connection pooling with NpgsqlDataSource. Connection pooling reduces the overhead of establishing database connections by reusing existing connections from a pool.

## Configuration Options

### Environment Variables

| Variable | Default | Range | Description |
|----------|---------|-------|-------------|
| `HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE` | 100 | 10-500 | Maximum connections in the pool |
| `HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT` | 100 | 10-1000 | Maximum concurrent queries |
| `HONUA__LIMITS__CONNECTIONS__REQUESTTIMEOUT` | 120s | 10s-10min | Request timeout duration |

### Configuration in appsettings.json

```json
{
  "Limits": {
    "Connections": {
      "MaxConnectionPoolSize": 100,
      "MaxConcurrent": 100,
      "RequestTimeout": "00:02:00"
    }
  }
}
```

## Sizing Guidelines

### Calculating Pool Size

The optimal connection pool size depends on:

1. **Expected concurrent users**: How many simultaneous requests you expect
2. **Query duration**: Average time spent per database query
3. **PostgreSQL max_connections**: Server-side connection limit

**Formula:**

```
Pool Size = (Expected Concurrent Requests) × (Avg Query Duration in seconds) + Buffer
```

**Example:**
- Expected concurrent requests: 100
- Average query duration: 50ms (0.05s)
- Buffer: 20 connections

```
Pool Size = 100 × 0.05 + 20 = 25 connections
```

### Recommended Settings by Workload

| Workload Type | Pool Size | Max Concurrent | Notes |
|---------------|-----------|----------------|-------|
| **Development** | 10-20 | 20 | Minimal resources |
| **Small Production** | 20-50 | 50 | <100 concurrent users |
| **Medium Production** | 50-100 | 100 | 100-500 concurrent users |
| **High Load Production** | 100-200 | 200 | 500+ concurrent users |
| **High Concurrency** | 200-500 | 500 | Heavy spatial queries |

### PostgreSQL Server Considerations

Ensure PostgreSQL `max_connections` is appropriately configured:

```sql
-- Check current setting
SHOW max_connections;

-- Recommended: Set to at least (pool_size × app_instances) + overhead
-- For 100 pool size with 2 app instances:
-- max_connections = 100 × 2 + 20 = 220
```

## Performance Targets

Based on benchmarking, Honua Server achieves these targets with default settings:

| Metric | Target | Notes |
|--------|--------|-------|
| p50 query latency | <50ms | 100 features |
| p95 query latency | <150ms | 100 features |
| p99 query latency | <300ms | 100 features |
| Throughput (simple queries) | >1000 RPS | With 50 concurrent clients |
| Throughput (spatial queries) | >500 RPS | Bbox intersection |
| Connection reuse ratio | >90% | Pool efficiency |

## Monitoring

### Key Metrics to Monitor

1. **Pool Utilization**: `connections_in_use / pool_size`
   - Target: <80% average, <95% peak

2. **Connection Wait Time**: Time waiting for available connection
   - Target: <10ms average

3. **Query Duration**: Time spent executing queries
   - Monitor for slow queries (>1s)

### PostgreSQL Monitoring Queries

```sql
-- Active connections by application
SELECT application_name, count(*)
FROM pg_stat_activity
WHERE datname = 'honua'
GROUP BY application_name;

-- Connection state distribution
SELECT state, count(*)
FROM pg_stat_activity
WHERE datname = 'honua'
GROUP BY state;

-- Long-running queries
SELECT pid, now() - pg_stat_activity.query_start AS duration, query
FROM pg_stat_activity
WHERE datname = 'honua'
  AND state != 'idle'
  AND now() - pg_stat_activity.query_start > interval '5 seconds'
ORDER BY duration DESC;
```

## Troubleshooting

### Pool Exhaustion

**Symptoms:**
- Requests timing out
- Errors: "connection pool exhausted"
- Increasing queue depth

**Solutions:**
1. Increase `MaxConnectionPoolSize`
2. Reduce query duration (add indexes, optimize queries)
3. Scale horizontally (add more app instances)
4. Implement query caching for repeated requests

### Connection Leaks

**Symptoms:**
- Pool size grows over time
- Connections in "idle" state for long periods
- Memory growth in application

**Detection:**
```sql
-- Check for abandoned connections
SELECT pid, state, state_change, query
FROM pg_stat_activity
WHERE datname = 'honua'
  AND state = 'idle'
  AND now() - state_change > interval '5 minutes';
```

**Prevention:**
- Always use `await using` with connections
- Set connection idle lifetime in connection string
- Run connection pool tests regularly

### High Latency

**Symptoms:**
- p95/p99 latency spikes
- Inconsistent query performance

**Solutions:**
1. Check for missing indexes:
   ```sql
   EXPLAIN ANALYZE <your_query>;
   ```

2. Verify spatial index usage:
   ```sql
   SELECT indexname, idx_tup_read, idx_tup_fetch
   FROM pg_stat_user_indexes
   WHERE schemaname = 'honua';
   ```

3. Consider connection pooler (PgBouncer) for very high concurrency

## Best Practices

1. **Start Conservative**: Begin with lower pool sizes and scale up based on monitoring

2. **Match Pool to Workload**: Spatial queries need larger pools than simple CRUD

3. **Monitor Before Tuning**: Collect baseline metrics before making changes

4. **Test Under Load**: Use load testing to validate settings before production

5. **Set Timeouts**: Always configure request timeouts to prevent runaway queries

6. **Use Connection Strings Wisely**: Include essential settings:
   ```
   Server=localhost;Database=honua;
   Minimum Pool Size=10;
   Maximum Pool Size=100;
   Connection Idle Lifetime=300;
   Connection Pruning Interval=10;
   ```

## Related Resources

- [Npgsql Connection Pooling](https://www.npgsql.org/doc/connection-string-parameters.html)
- [PostgreSQL Connection Management](https://www.postgresql.org/docs/current/runtime-config-connection.html)
- [Honua Performance Testing Guide](./performance-testing.md)
