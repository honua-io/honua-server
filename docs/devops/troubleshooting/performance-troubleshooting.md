# Honua Performance Troubleshooting

Diagnose and resolve performance issues in Honua Server deployments.

## Quick Honua Diagnostics

```bash
# Check Honua performance metrics
curl -s http://localhost:8080/api/v1/admin/performance/summary

# Monitor Honua container resources
docker stats honua-server

# Check for slow queries in Honua logs
docker logs honua-server | grep -E "(slow|timeout|performance|exceeded)"

# Test Honua API response times
curl -w "Total: %{time_total}s\n" -o /dev/null -s http://localhost:8080/rest/services

# Check Honua database health
curl -s http://localhost:8080/api/v1/admin/database/health
```

## Slow Honua API Responses

### Symptom: Feature queries taking >500ms

**Troubleshooting**:

1. **Check Honua query performance**:
   ```bash
   # Check Honua's built-in performance monitoring
   curl http://localhost:8080/api/v1/admin/performance/queries

   # Test specific layer performance
   time curl "http://localhost:8080/rest/services/1/FeatureServer/0/query?f=json&resultRecordCount=100"

   # Check if spatial indexes exist (Honua creates these automatically)
   curl http://localhost:8080/api/v1/admin/database/indexes
   ```

**Solutions**:

1. **Tune Honua query limits**:
   ```bash
   # Reduce default record counts for faster responses
   LIMITS__QUERY__DEFAULTRECORDCOUNT=500
   LIMITS__QUERY__MAXRECORDCOUNT=2000
   LIMITS__QUERY__MAXEXECUTIONTIME=30s
   ```

2. **Enable Honua query caching**:
   ```bash
   # Enable response caching
   HONUA_DATABASE__QUERYCACHE__MAXCACHEDSTATEMENTS=200
   HONUA_DATABASE__QUERYCACHE__ENABLEAUTOMATICCACHING=true

   # Configure Redis for distributed caching
   ConnectionStrings__Redis=redis:6379
   ```

3. **Check data size and complexity**:
   ```sql
   -- Update PostgreSQL statistics
   ANALYZE honua.features;
   ANALYZE honua.layers;

   -- Check statistics age
   SELECT schemaname, tablename, last_analyze, last_autoanalyze
   FROM pg_stat_user_tables
   WHERE schemaname = 'honua';
   ```

### Symptom: Large Geometry Processing Timeouts

**Root Cause**: Complex geometries with too many vertices.

**Solutions**:

1. **Configure Geometry Limits**:
   ```bash
   # Reduce geometry complexity limits
   export Limits__Geometry__MaxVertices=5000
   export Limits__Geometry__MaxPolygons=50
   export Limits__Geometry__MaxCoordinatePrecision=6
   ```

2. **Simplify Complex Geometries**:
   ```sql
   -- Check geometry complexity
   SELECT
       layer_id,
       AVG(ST_NPoints(geometry)) as avg_vertices,
       MAX(ST_NPoints(geometry)) as max_vertices,
       COUNT(*) as feature_count
   FROM honua.features
   GROUP BY layer_id
   ORDER BY max_vertices DESC;

   -- Simplify overly complex geometries
   UPDATE honua.features
   SET geometry = ST_SimplifyPreserveTopology(geometry, 0.0001)
   WHERE ST_NPoints(geometry) > 10000;
   ```

3. **Use Geometry Validation and Repair**:
   ```sql
   -- Find invalid geometries
   SELECT feature_id, layer_id, ST_IsValidReason(geometry)
   FROM honua.features
   WHERE NOT ST_IsValid(geometry);

   -- Repair invalid geometries
   UPDATE honua.features
   SET geometry = ST_MakeValid(geometry)
   WHERE NOT ST_IsValid(geometry);
   ```

## Caching Performance Issues

### Symptom: Low Cache Hit Ratio (<80%)

**Diagnostic Steps**:

1. **Check Cache Metrics**:
   ```bash
   # View current cache statistics
   curl -s http://localhost:8080/api/v1/metrics/cache | jq '{
     cache_hits: ([.types[]?.hits] | add // 0),
     cache_misses: ([.types[]?.misses] | add // 0),
     hit_ratio: (.hitRatio * 100)
   }'

   # Monitor cache usage over time
   watch -n 5 'curl -s http://localhost:8080/api/v1/metrics/cache | jq .types'
   ```

2. **Check Redis Connectivity**:
   ```bash
   # Test Redis connection (if using Redis)
   redis-cli ping

   # Check Redis memory usage
   redis-cli info memory

   # Monitor Redis operations
   redis-cli monitor
   ```

**Solutions**:

1. **Optimize Cache TTL Settings**:
   ```bash
   # Adjust cache timeouts based on data change frequency
   export Cache__LayerCatalog__TimeToLive="01:00:00"     # 1 hour for layer metadata
   export Cache__FeatureCounts__TimeToLive="00:15:00"    # 15 minutes for counts
   export Cache__ServiceCatalog__TimeToLive="04:00:00"   # 4 hours for service info
   ```

2. **Increase Cache Size Limits**:
   ```bash
   # Increase memory limits for in-memory fallback cache
   export Cache__InMemory__MaxSizeBytes=104857600  # 100MB
   export Cache__InMemory__MaxEntries=10000
   ```

3. **Configure Redis Memory Policy**:
   ```bash
   # Connect to Redis
   redis-cli

   # Set memory policy for cache eviction
   CONFIG SET maxmemory-policy allkeys-lru

   # Set memory limit
   CONFIG SET maxmemory 256mb

   # Check current configuration
   CONFIG GET maxmemory*
   ```

### Symptom: Redis Connection Failures

**Root Cause**: Redis server unavailable, network issues, or configuration problems.

**Diagnostic Steps**:
```bash
# Check Redis service status
systemctl status redis-server
# or for Docker
docker logs redis-container

# Test Redis connectivity
redis-cli -h localhost -p 6379 ping

# Check Redis logs for errors
tail -f /var/log/redis/redis-server.log
```

**Solutions**:

1. **Configure Fallback Behavior**:
   ```bash
   # Enable graceful degradation to in-memory cache
   export Cache__FallbackEnabled=true
   export Cache__Redis__ConnectionRetryAttempts=3
   export Cache__Redis__RetryDelayMilliseconds=1000
   ```

2. **Redis Service Recovery**:
   ```bash
   # Restart Redis service
   sudo systemctl restart redis-server

   # For Docker environments
   docker restart redis-container

   # Check Redis configuration
   redis-cli CONFIG GET "*"
   ```

## Database Connection Pool Issues

### Symptom: Connection Pool Exhaustion

**Error**: `Timeout expired. Pool is full`

**Diagnostic Steps**:

1. **Monitor Connection Usage**:
   ```sql
   -- Check active connections
   SELECT
       state,
       COUNT(*) as connection_count,
       MAX(now() - query_start) as longest_query
   FROM pg_stat_activity
   WHERE datname = 'honua'
   GROUP BY state;

   -- Check connection limits
   SHOW max_connections;

   -- Check current connection count
   SELECT count(*) FROM pg_stat_activity;
   ```

2. **Identify Connection Leaks**:
   ```sql
   -- Find long-running connections
   SELECT
       pid,
       usename,
       application_name,
       client_addr,
       backend_start,
       state,
       query
   FROM pg_stat_activity
   WHERE backend_start < now() - interval '1 hour'
   AND state != 'idle';
   ```

**Solutions**:

1. **Optimize Connection Pool Settings**:
   ```bash
   # Adjust connection pool configuration
   export ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres;Maximum Pool Size=50;Connection Idle Lifetime=300;Connection Pruning Interval=10"
   ```

2. **Configure PostgreSQL Limits**:
   ```bash
   # Edit postgresql.conf
   sudo nano /etc/postgresql/14/main/postgresql.conf

   # Increase connection limit
   max_connections = 200

   # Configure shared buffers
   shared_buffers = 256MB

   # Restart PostgreSQL
   sudo systemctl restart postgresql
   ```

3. **Monitor and Kill Long-Running Queries**:
   ```sql
   -- Kill specific problematic query
   SELECT pg_terminate_backend(pid) FROM pg_stat_activity
   WHERE query_start < now() - interval '10 minutes'
   AND state = 'active';

   -- Configure automatic statement timeout
   ALTER DATABASE honua SET statement_timeout = '5min';
   ```

## Memory Usage Issues

### Symptom: Honua container using too much memory

**Solutions**:

1. **Set Honua memory limits**:
   ```bash
   # Configure .NET memory limits for containers
   DOTNET_GCConserveMemory=3
   DOTNET_GCHeapHardLimit=1073741824  # 1GB limit
   DOTNET_GCHighMemPercent=75
   ```

2. **Limit Honua geometry processing**:
   ```bash
   # Reduce memory usage for large geometries
   LIMITS__GEOMETRY__MAXVERTICES=5000
   LIMITS__QUERY__MAXRECORDCOUNT=1000
   MEMORYMANAGEMENT__MAXCACHEENTRIES=5000
   ```

3. **Monitor container memory**:
   ```bash
   # Monitor Honua container memory usage
   docker stats honua-server

   # Set Docker memory limits
   docker run --memory=2g honuaio/honua-server:latest

   # In Docker Compose:
   services:
     honua-server:
       deploy:
         resources:
           limits:
             memory: 2G
   ```

## Network and I/O Performance

### Symptom: Slow API Response Times

**Solutions**:

1. **Test Honua API performance**:
   ```bash
   # Test feature server response times
   curl -w "Total: %{time_total}s\n" -o /dev/null -s \
     "http://localhost:8080/rest/services/1/FeatureServer/0/query?f=json&resultRecordCount=100"

   # Test OGC API Features
   curl -w "Total: %{time_total}s\n" -o /dev/null -s \
     "http://localhost:8080/collections/layer1/items?limit=100"

   # Verify response compression
   curl -H "Accept-Encoding: gzip" -v http://localhost:8080/rest/services
   ```

2. **Optimize Honua connection pooling**:
   ```bash
   # Tune connection pool for better performance
   HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE=100
   HONUA__LIMITS__CONNECTIONS__MAXCONCURRENT=100
   HONUA__LIMITS__CONNECTIONS__REQUESTTIMEOUT=30s

   # Use larger connection pool for high load
   HONUA__LIMITS__CONNECTIONS__MAXCONNECTIONPOOLSIZE=200
   ```

3. **Monitor PostgreSQL container**:
   ```bash
   # Monitor PostgreSQL container performance
   docker stats postgres

   # Check PostgreSQL data volume usage
   docker exec postgres df -h /var/lib/postgresql/data

   # Monitor slow queries in PostgreSQL logs
   docker logs postgres | grep -E "(duration|slow)"
   ```

## Monitoring and Alerting

### Set Up Performance Monitoring

1. **Application Metrics Collection**:
   ```bash
   # Monitor metrics endpoints (HTTP rates/latency are in OpenTelemetry exports)
   curl -s http://localhost:8080/api/v1/metrics/performance | jq '
   {
     memory_usage_mb: (.memory.allocatedBytes / 1024 / 1024),
     working_set_mb: (.systemInfo.workingSet / 1024 / 1024)
   }'

   curl -s http://localhost:8080/api/v1/metrics/cache | jq '
   {
     cache_hit_ratio: (.hitRatio * 100),
     total_requests: .totalRequests
   }'

   curl -s http://localhost:8080/api/v1/metrics/database | jq '
   {
     cache_hit_rate: (.cacheHitRate * 100),
     operations: (.operations | keys)
   }'
   ```

2. **Database Performance Monitoring**:
   ```sql
   -- Create monitoring view
   CREATE OR REPLACE VIEW honua.performance_summary AS
   SELECT
       'connections' as metric,
       count(*)::text as value,
       'active_connections' as description
   FROM pg_stat_activity WHERE state = 'active'
   UNION ALL
   SELECT
       'cache_hit_ratio' as metric,
       round(100.0 * sum(blks_hit) / (sum(blks_hit) + sum(blks_read)), 2)::text as value,
       'buffer_cache_percentage' as description
   FROM pg_stat_database
   UNION ALL
   SELECT
       'slow_queries' as metric,
       count(*)::text as value,
       'queries_running_over_1_minute' as description
   FROM pg_stat_activity
   WHERE state = 'active' AND query_start < now() - interval '1 minute';

   -- Query performance summary
   SELECT * FROM honua.performance_summary;
   ```

3. **Automated Alerts**:
   ```bash
   #!/bin/bash
   # performance-check.sh - Run via cron every 5 minutes

   # Check API response time
   RESPONSE_TIME=$(curl -w %{time_total} -o /dev/null -s http://localhost:8080/health)
   if (( $(echo "$RESPONSE_TIME > 1.0" | bc -l) )); then
       echo "ALERT: API response time is ${RESPONSE_TIME}s" | mail -s "Honua Performance Alert" admin@example.com
   fi

   # Check cache hit ratio
   HIT_RATIO=$(curl -s http://localhost:8080/api/v1/metrics/cache | jq '.hitRatio * 100')
   if (( $(echo "$HIT_RATIO < 80" | bc -l) )); then
       echo "ALERT: Cache hit ratio is ${HIT_RATIO}%" | mail -s "Honua Cache Alert" admin@example.com
   fi
   ```

## Performance Tuning Checklist

### Database Optimization
- [ ] Spatial indexes on geometry columns
- [ ] B-tree indexes on frequently queried columns
- [ ] Updated table statistics (ANALYZE)
- [ ] Appropriate work_mem settings
- [ ] Connection pooling configured
- [ ] Query timeouts set appropriately

### Application Optimization
- [ ] Cache TTL values tuned for data change frequency
- [ ] Response compression enabled
- [ ] Geometry complexity limits configured
- [ ] Memory limits set for .NET runtime
- [ ] Connection string optimized

### System Optimization
- [ ] Adequate RAM for PostgreSQL shared_buffers
- [ ] Fast storage (SSD) for database files
- [ ] Network latency minimized
- [ ] Monitoring and alerting configured
- [ ] Log rotation configured

### Capacity Planning
- [ ] Peak concurrent users estimated
- [ ] Database growth rate calculated
- [ ] Backup and recovery tested
- [ ] Disaster recovery plan documented

## Getting Help

For complex performance issues:

1. **Collect diagnostic data**:
   ```bash
   # Performance snapshot script
   #!/bin/bash
   echo "=== System Resources ===" > performance-report.txt
   free -h >> performance-report.txt
   df -h >> performance-report.txt

   echo "=== Database Stats ===" >> performance-report.txt
   psql -h localhost -U postgres -d honua -c "SELECT * FROM honua.performance_summary;" >> performance-report.txt

   echo "=== Application Metrics ===" >> performance-report.txt
   curl -s http://localhost:8080/api/v1/metrics/performance >> performance-report.txt
   curl -s http://localhost:8080/api/v1/metrics/cache >> performance-report.txt
   curl -s http://localhost:8080/api/v1/metrics/database >> performance-report.txt
   ```

2. **Share performance metrics and query execution plans**
3. **Include system specifications and load characteristics**
4. **Provide application logs with timestamps**
