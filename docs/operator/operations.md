# Operations

Database management, connection tuning, query optimization, caching, and memory for Honua Server.

---

## Backups and Restores

### Restore Checklist

1. Restore the database snapshot.
2. Verify PostGIS extensions: `SELECT PostGIS_Version();`
3. Validate a known feature query to confirm data integrity.

---

## Zero-Downtime Migrations

Honua uses DbUp for migrations (`src/Honua.Postgres/Migrations/`).

1. **Add, don't drop**: make backward-compatible schema changes first (add columns, create tables).
2. **Deploy the new application** after the schema is in place.
3. **Remove old columns** in a later release, once no running instance depends on them.

### Compatibility Review Marker

Potentially breaking migrations must declare an explicit compatibility review in the SQL file so the rollout risk is visible in code review and CI.

Add a comment near the top of the migration:

```sql
-- honua:compatibility-review reason=removes v1-only column after two release windows
```

Use the marker when a migration performs top-level changes such as:
- `ALTER TABLE ... DROP COLUMN`
- `ALTER TABLE ... RENAME COLUMN`
- `ALTER TABLE ... ALTER COLUMN ... TYPE`
- `ALTER TABLE ... ALTER COLUMN ... SET NOT NULL`
- `DROP TABLE`, `DROP SCHEMA`, or `DROP SEQUENCE`

The compatibility marker does not make a migration safe by itself. It signals that the change needs an explicit rollout plan, backward-compatibility review, and recovery path.

### Rollout Checklist

1. Apply migrations in a rolling fashion.
2. Verify `/healthz/ready` and a critical query after each step.
3. Monitor error rates and latency during rollout.

---

## Connection Pool Sizing

### Quick Configuration

```bash
Limits__Connections__MaxConnectionPoolSize=100
Limits__Connections__MaxConcurrentQueries=100
Limits__Connections__RequestTimeout=00:02:00

ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=yourpassword;Minimum Pool Size=10;Maximum Pool Size=100;Connection Idle Lifetime=300"
```

### Sizing Rule

Keep `MaxConnectionPoolSize x replica_count` below the database `max_connections` minus headroom:

```
max_connections >= (pool_size x app_replicas) + headroom
```

### Monitoring

- Prometheus scrape endpoint: `GET /metrics` with admin credentials
- Honua metrics: `GET /api/v1/metrics/database`
- Postgres activity:
  ```sql
  SELECT count(*) AS active_connections
  FROM pg_stat_activity
  WHERE datname = 'honua' AND state = 'active';
  ```

### Pool Exhaustion

**Symptoms**: timeouts, latency spikes, "connection pool exhausted" logs.

**Fixes**:
- Increase `MaxConnectionPoolSize` and `MaxConcurrentQueries` in small steps.
- Add application replicas and lower per-replica pool size.
- Reduce slow queries and oversized result sets.
- Ensure Postgres `max_connections` can handle your total pool size.

---

## Query Optimization

### Honua Query Limits

These limits apply across protocols:

- `Limits__Query__MaxRecordCount`
- `Limits__Query__DefaultRecordCount`
- `Limits__Query__MaxOffset`
- `Limits__Query__MaxBboxAreaSqKm`
- `Limits__Query__QueryTimeout`
- `Limits__Connections__RequestTimeout`

Use `/api/v1/admin/config` to confirm effective values.

### Database Checks

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

### If Queries Are Slow

- Check for missing indexes or sequential scans.
- Confirm statistics are current after imports.
- Reduce geometry complexity or simplify where possible.
- Tighten limits if users are requesting large result sets.

---

## Caching

Honua uses a layered caching approach:

| Layer | Surface | Notes |
|-------|---------|-------|
| **Edge / CDN** | Tile traffic, public read endpoints | Recommended for production |
| **Server output cache** | OGC / FeatureServer metadata | Short TTLs; invalidated on writes |
| **Response cache** | Query responses | Only safe for anonymous GETs |
| **Database query cache** | Prepared statements | Npgsql internal cache |

### Cache Metrics

| Endpoint | What it shows |
|----------|---------------|
| `GET /api/v1/metrics/cache` | Output cache hit/miss rates |
| `GET /api/v1/admin/performance/database/query-cache/statistics` | Prepared statement cache health |

**Investigate when**: miss rate spikes after deployments, high latency despite stable app metrics, or sudden drops in cache utilization.

---

## Memory Optimizations

Key memory-management patterns in Honua:

- **Array pooling** for large buffers in geometry and streaming paths (`MemoryPool.cs`)
- **Streaming APIs** for large result sets (`IStreamingFeatureStore`)
- **Geometry processing optimizations** for coordinate handling
- **Response and metadata caching** to reduce repeated allocations (`MemoryResponseCache.cs`)
