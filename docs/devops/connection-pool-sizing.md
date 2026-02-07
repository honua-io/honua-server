# Connection Pool Sizing Guide

Configure PostgreSQL connection pooling for optimal Honua Server performance in production deployments.

## Quick Configuration

Set these environment variables in your deployment:

```bash
# Connection pool settings
HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE=100
HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT=100
HONUA__LIMITS__CONNECTIONS__REQUESTTIMEOUT=120s

# Database connection with pooling parameters
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=yourpassword;Minimum Pool Size=10;Maximum Pool Size=100;Connection Idle Lifetime=300"
```

## Environment Variables Reference

| Variable | Default | Description | Example |
|----------|---------|-------------|---------|
| `HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE` | 100 | Maximum connections in pool | `50` for small, `200` for high load |
| `HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT` | 100 | Maximum concurrent queries | `50` for small, `500` for high load |
| `HONUA__LIMITS__CONNECTIONS__REQUESTTIMEOUT` | 120s | Request timeout | `30s`, `5min` |

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

## Deployment Examples

### Docker Compose
```yaml
services:
  honua:
    image: honuaio/honua-server:latest
    environment:
      # Small production (50 concurrent users)
      - HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE=50
      - HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT=50
      - HONUA__LIMITS__CONNECTIONS__REQUESTTIMEOUT=60s
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=honua;Username=honua;Password=yourpass;Maximum Pool Size=50;Connection Idle Lifetime=300
```

### Kubernetes
```yaml
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
      - name: honua
        env:
        # High load production (500+ concurrent users)
        - name: HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE
          value: "200"
        - name: HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT
          value: "200"
        - name: HONUA__LIMITS__CONNECTIONS__REQUESTTIMEOUT
          value: "120s"
```

### Recommended Settings by Workload

| Workload | Pool Size | Max Concurrent | Environment Variables |
|----------|-----------|----------------|----------------------|
| **Development** | 10-20 | 20 | `MAXCONNECTIONPOOLSIZE=20 MAXCONCURRENT=20` |
| **Small Production** | 20-50 | 50 | `MAXCONNECTIONPOOLSIZE=50 MAXCONCURRENT=50` |
| **Medium Production** | 50-100 | 100 | `MAXCONNECTIONPOOLSIZE=100 MAXCONCURRENT=100` |
| **High Load** | 100-200 | 200 | `MAXCONNECTIONPOOLSIZE=200 MAXCONCURRENT=200` |

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
- High response times

**Solutions:**
1. **Increase pool size:**
   ```bash
   # Double the pool size
   HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE=200
   HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT=200
   ```

2. **Scale horizontally:**
   ```bash
   # Kubernetes: increase replicas
   kubectl scale deployment honua --replicas=3

   # Docker Compose: add more instances
   docker-compose up --scale honua=3
   ```

3. **Check PostgreSQL limits:**
   ```sql
   -- Ensure PostgreSQL can handle your pool size × replicas
   -- Example: 100 pool × 3 replicas = 300 + 50 overhead = 350
   ALTER SYSTEM SET max_connections = 350;
   SELECT pg_reload_conf();
   ```

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

### 1. Start Small and Scale Up
```bash
# Start with conservative settings
HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE=50
HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT=50

# Monitor and increase based on usage patterns
```

### 2. Match PostgreSQL Settings
```bash
# Calculate PostgreSQL max_connections
# Formula: (pool_size × replicas) + 50 overhead
# Example: (100 × 3 replicas) + 50 = 350

# In PostgreSQL:
# ALTER SYSTEM SET max_connections = 350;
```

### 3. Use Load Testing
```bash
# Test your configuration before production
# Use tools like k6, wrk, or Artillery to validate pool sizing

# Example test command:
# k6 run --vus 100 --duration 60s load-test.js
```

### 4. Monitor Key Metrics
- Pool utilization (keep < 80% average)
- Connection wait times (keep < 10ms)
- Query duration (watch for > 1s queries)

### 5. Essential Connection String Settings
```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=pass;Maximum Pool Size=100;Connection Idle Lifetime=300;Connection Pruning Interval=10"
```

## Related Resources

- [Npgsql Connection Pooling](https://www.npgsql.org/doc/connection-string-parameters.html)
- [PostgreSQL Connection Management](https://www.postgresql.org/docs/current/runtime-config-connection.html)
- [Honua Performance Testing Guide](./performance-testing.md)
