# Query Optimization Guide

This document describes query execution plan analysis and index usage verification for Honua Server.

## Overview

Honua Server uses PostgreSQL with PostGIS for geospatial data storage. Optimal query performance depends on:

1. **Proper index usage** for spatial and attribute queries
2. **Efficient execution plans** avoiding sequential scans on large tables
3. **Window function optimization** for count + fetch operations
4. **Connection pool efficiency** for high concurrency

## Index Strategy

### Required Indexes

The Honua schema includes these performance-critical indexes:

```sql
-- Spatial index for geometry operations (GIST)
CREATE INDEX idx_features_geometry ON features USING GIST(geometry);

-- JSONB index for attribute queries (GIN)
CREATE INDEX idx_features_attributes ON features USING GIN(attributes);

-- Layer filtering index
CREATE INDEX idx_features_layer_id ON features(layer_id);
```

### Index Type Selection

| Query Type | Index Type | Use Case |
|------------|------------|----------|
| Spatial intersection (`ST_Intersects`) | GIST | Bbox queries, spatial relationships |
| Attribute containment (`@>`) | GIN | JSONB field filtering |
| Equality filter (`=`) | B-tree | layer_id, objectid lookups |
| Range queries (`>`, `<`, `BETWEEN`) | B-tree | Numeric/date filtering |

## Query Execution Plan Analysis

### Analyzing Query Plans

Use `EXPLAIN ANALYZE` to examine query execution:

```sql
EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
SELECT objectid, ST_AsGeoJSON(geometry), attributes
FROM honua.features
WHERE layer_id = 1
  AND ST_Intersects(geometry, ST_MakeEnvelope(-122.5, 37.5, -122.0, 38.0, 4326))
LIMIT 100;
```

### Interpreting Results

**Good execution plan indicators:**
- `Index Scan` or `Bitmap Index Scan` instead of `Seq Scan`
- Low `actual time` values (< 10ms for index scans)
- `Buffers: shared hit` (data in cache) vs `shared read` (disk I/O)

**Warning signs:**
- `Seq Scan` on large tables (> 10,000 rows)
- High `actual time` values (> 100ms)
- `Sort` operations on large result sets without index

### Example: Optimal Spatial Query Plan

```
Limit  (cost=4.42..50.32 rows=100 width=256) (actual time=0.045..0.523 rows=100 loops=1)
  ->  Index Scan using idx_features_geometry on features
        (cost=4.42..458.82 rows=1000 width=256)
        (actual time=0.044..0.498 rows=100 loops=1)
        Index Cond: (geometry && ST_MakeEnvelope(...))
        Filter: ST_Intersects(geometry, ST_MakeEnvelope(...))
Planning Time: 0.215 ms
Execution Time: 0.562 ms
```

## Performance Targets

### Latency Requirements (Issue #46)

| Query Type | p50 Target | p95 Target | p99 Target |
|------------|------------|------------|------------|
| Simple WHERE (100 features) | < 30ms | < 100ms | < 200ms |
| Spatial bbox (100 features) | < 30ms | < 100ms | < 200ms |
| Combined WHERE + spatial | < 50ms | < 100ms | < 300ms |
| Paginated queries | < 20ms | < 50ms | < 100ms |
| Count-only queries | < 10ms | < 30ms | < 50ms |
| Large result (1000 features) | < 100ms | < 300ms | < 500ms |

### Throughput Targets

| Scenario | Target RPS | Max Concurrent |
|----------|------------|----------------|
| Simple queries | > 1000 | 100 |
| Spatial queries | > 500 | 50 |
| Mixed workload | > 800 | 100 |

## Window Function Optimization

Honua uses PostgreSQL window functions to combine count and data retrieval in a single query, achieving 30-50% latency reduction:

```sql
-- Optimized: Single query with window function
SELECT
    objectid,
    geometry,
    attributes,
    COUNT(*) OVER() as total_count
FROM features
WHERE layer_id = $1
  AND ST_Intersects(geometry, $2)
ORDER BY objectid
LIMIT $3 OFFSET $4;

-- vs. Traditional: Two separate queries
SELECT COUNT(*) FROM features WHERE layer_id = $1 AND ST_Intersects(geometry, $2);
SELECT * FROM features WHERE layer_id = $1 AND ST_Intersects(geometry, $2) LIMIT $3 OFFSET $4;
```

## Index Usage Verification

### Checking Index Statistics

```sql
-- View index usage statistics
SELECT
    schemaname,
    tablename,
    indexname,
    idx_scan,           -- Number of index scans
    idx_tup_read,       -- Tuples read via index
    idx_tup_fetch       -- Tuples fetched
FROM pg_stat_user_indexes
WHERE schemaname = 'honua'
ORDER BY idx_scan DESC;
```

### Identifying Missing Indexes

```sql
-- Find sequential scans on large tables
SELECT
    schemaname,
    relname,
    seq_scan,
    seq_tup_read,
    idx_scan,
    n_live_tup
FROM pg_stat_user_tables
WHERE schemaname = 'honua'
  AND seq_scan > idx_scan
  AND n_live_tup > 1000
ORDER BY seq_tup_read DESC;
```

### Verifying Spatial Index Usage

```sql
-- Check if spatial index is being used
EXPLAIN (FORMAT TEXT)
SELECT * FROM honua.features
WHERE ST_Intersects(geometry, ST_MakeEnvelope(-122, 37, -121, 38, 4326));

-- Should show: Index Scan using idx_features_geometry
-- NOT: Seq Scan on features
```

## Troubleshooting

### Slow Queries

1. **Check for missing indexes:**
   ```sql
   EXPLAIN ANALYZE <slow_query>;
   ```
   Look for `Seq Scan` on tables with > 1000 rows.

2. **Verify statistics are current:**
   ```sql
   ANALYZE honua.features;
   ```

3. **Check for lock contention:**
   ```sql
   SELECT * FROM pg_stat_activity
   WHERE wait_event_type = 'Lock';
   ```

### Index Not Being Used

1. **Table too small:** PostgreSQL may choose Seq Scan for small tables (< 1000 rows)

2. **Statistics stale:** Run `ANALYZE` after bulk inserts

3. **Query planner cost estimates:** Adjust `random_page_cost` for SSD storage:
   ```sql
   SET random_page_cost = 1.1;  -- Default is 4.0, lower for SSD
   ```

### Memory Pressure

1. **Check work_mem for sorts:**
   ```sql
   SHOW work_mem;  -- Default 4MB, increase for large sorts
   SET work_mem = '64MB';
   ```

2. **Monitor shared buffer usage:**
   ```sql
   SELECT
       c.relname,
       count(*) AS buffers,
       pg_size_pretty(count(*) * 8192) AS size
   FROM pg_buffercache b
   JOIN pg_class c ON b.relfilenode = c.relfilenode
   WHERE c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = 'honua')
   GROUP BY c.relname
   ORDER BY buffers DESC;
   ```

## Automated Verification

The test suite includes query plan verification tests:

- `QueryPlanVerificationTests.SpatialQuery_UsesGistIndex` - Verifies GIST index usage
- `QueryPlanVerificationTests.JsonbQuery_UsesGinIndex` - Verifies GIN index usage
- `QueryPlanVerificationTests.LayerIdQuery_UsesIndex` - Verifies B-tree index usage
- `QueryPlanVerificationTests.CombinedQuery_UsesMultipleIndexes` - Verifies multi-index queries
- `QueryPlanVerificationTests.QueryExecution_MeetsPerformanceBaseline` - Documents execution times

Run these tests to verify index usage:

```bash
dotnet test --filter "Category=Performance&Operation=QueryPlan"
```

## Related Resources

- [PostgreSQL EXPLAIN Documentation](https://www.postgresql.org/docs/current/sql-explain.html)
- [PostGIS Performance Tips](https://postgis.net/docs/performance_tips.html)
- [Connection Pool Sizing Guide](./connection-pool-sizing.md)
- [Performance Testing Guide](./performance-testing.md)
