# Production Operations Runbook

This runbook provides step-by-step procedures for diagnosing and resolving common production issues in the Honua server.

## Table of Contents

- [Monitoring & Alerting](#monitoring--alerting)
- [Database Connection Issues](#database-connection-issues)
- [Cache Performance Problems](#cache-performance-problems)
- [Memory and GC Issues](#memory-and-gc-issues)
- [Rate Limiting Violations](#rate-limiting-violations)
- [File Upload Problems](#file-upload-problems)
- [Circuit Breaker Incidents](#circuit-breaker-incidents)
- [General Troubleshooting](#general-troubleshooting)

## Monitoring & Alerting

### Health Check Endpoints

- **Production Health**: `GET /monitoring/health/production`
- **Connection Pool**: `GET /monitoring/metrics/connection-pool`
- **Cache Metrics**: `GET /monitoring/metrics/cache`
- **Resource Usage**: `GET /monitoring/metrics/resources`
- **Active Alerts**: `GET /monitoring/alerts`

### Key Metrics to Monitor

#### Critical Thresholds

| Metric | Warning | Critical | Action Required |
|--------|---------|----------|-----------------|
| Cache Hit Ratio | < 80% | < 50% | Review cache configuration |
| DB Pool Utilization | > 80% | > 95% | Scale database connections |
| Memory Usage | > 1.5GB | > 2GB | Investigate memory leaks |
| Error Rate | > 2% | > 5% | Check application logs |
| Query P95 Latency | > 1000ms | > 5000ms | Optimize queries |

#### Prometheus Metrics

```promql
# Cache hit ratio
honua_cache_hit_ratio

# Database connection pool utilization
honua_db_pool_utilization_ratio

# Memory usage
honua_memory_usage_bytes

# Query latency (95th percentile)
histogram_quantile(0.95, honua_query_duration_ms)

# Error rate
rate(honua_errors_total[5m]) / rate(honua_queries_total[5m])
```

## Database Connection Issues

### Symptoms
- High connection pool utilization (>80%)
- Connection acquisition failures
- Query timeouts
- Application returning 503 Service Unavailable

### Diagnosis Steps

1. **Check connection pool metrics**:
   ```bash
   curl -H "Authorization: Bearer $ADMIN_TOKEN" \
        https://honua-server/monitoring/metrics/connection-pool
   ```

2. **Review database connection configuration**:
   ```bash
   echo $HONUA__LIMITS__CONNECTIONS__MAXPOOLSIZE
   echo $HONUA__LIMITS__CONNECTIONS__COMMANDTIMEOUTSECONDS
   ```

3. **Check active connections in PostgreSQL**:
   ```sql
   SELECT count(*) as active_connections, max_conn, max_conn-count(*) as free_connections
   FROM pg_stat_activity
   CROSS JOIN (SELECT setting::int as max_conn FROM pg_settings WHERE name='max_connections') mc;
   ```

### Resolution Steps

#### Immediate Actions (High Severity)
1. **Scale connection pool** (if under-provisioned):
   ```bash
   export HONUA__LIMITS__CONNECTIONS__MAXPOOLSIZE=50
   kubectl rollout restart deployment/honua-server
   ```

2. **Kill long-running queries** (if blocking connections):
   ```sql
   SELECT pg_terminate_backend(pid)
   FROM pg_stat_activity
   WHERE state = 'active' AND query_start < now() - interval '5 minutes';
   ```

#### Long-term Solutions
1. **Optimize query patterns** - Review slow query log
2. **Implement connection pooling at database level** (PgBouncer)
3. **Add read replicas** for read-heavy workloads
4. **Configure connection lifetime** to prevent stale connections

## Cache Performance Problems

### Symptoms
- Low cache hit ratio (<80%)
- High query latency
- Increased database load

### Diagnosis Steps

1. **Check cache metrics**:
   ```bash
   curl -H "Authorization: Bearer $ADMIN_TOKEN" \
        https://honua-server/monitoring/metrics/cache
   ```

2. **Review cache configuration**:
   ```bash
   echo $HONUA__CACHE__DEFAULTTTLSECONDS
   echo $HONUA__CACHE__ENABLED
   ```

3. **Check Redis connectivity** (if using Redis):
   ```bash
   redis-cli ping
   redis-cli info memory
   ```

### Resolution Steps

#### Immediate Actions
1. **Restart cache** (if connectivity issues):
   ```bash
   kubectl rollout restart deployment/redis
   ```

2. **Clear corrupted cache entries**:
   ```bash
   redis-cli --scan --pattern "honua:*" | head -100 | xargs redis-cli del
   ```

#### Configuration Adjustments
1. **Increase TTL** for stable data:
   ```bash
   export HONUA__CACHE__DEFAULTTTLSECONDS=3600  # 1 hour
   export HONUA__CACHE__LAYERTTLSECONDS=7200    # 2 hours
   ```

2. **Tune cache size limits**:
   ```bash
   export HONUA__CACHE__FALLBACKMAXENTRIES=5000
   export HONUA__CACHE__RESPONSECACHEMAXENTRIES=50000
   ```

## Memory and GC Issues

### Symptoms
- High memory usage (>2GB)
- Frequent Gen2 garbage collections
- Application slowdowns
- Out of memory errors

### Diagnosis Steps

1. **Check memory metrics**:
   ```bash
   curl -H "Authorization: Bearer $ADMIN_TOKEN" \
        https://honua-server/monitoring/metrics/resources
   ```

2. **Review GC statistics**:
   ```bash
   dotnet-counters monitor --process-id $PID \
     --counters System.Runtime[gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,alloc-rate]
   ```

### Resolution Steps

#### Immediate Actions
1. **Force garbage collection** (temporary relief):
   ```bash
   # Use admin endpoint to trigger GC (if implemented)
   curl -X POST -H "Authorization: Bearer $ADMIN_TOKEN" \
        https://honua-server/admin/gc/collect
   ```

2. **Restart application** (if memory leak suspected):
   ```bash
   kubectl rollout restart deployment/honua-server
   ```

#### Long-term Solutions
1. **Enable memory profiling**:
   ```bash
   export DOTNET_EnableEventPipe=1
   export DOTNET_EventPipeConfig="Microsoft-DotNETCore-SampleProfiler:0x1:5"
   ```

2. **Optimize caching strategy** - reduce cache entry sizes
3. **Review file upload handling** - ensure streaming is working
4. **Configure memory limits**:
   ```yaml
   resources:
     limits:
       memory: "2Gi"
     requests:
       memory: "1Gi"
   ```

## Rate Limiting Violations

### Symptoms
- 429 Too Many Requests responses
- Rate limit violation metrics increasing
- Client complaints about blocked requests

### Diagnosis Steps

1. **Check rate limiting metrics**:
   ```bash
   curl -H "Authorization: Bearer $ADMIN_TOKEN" \
        https://honua-server/monitoring/alerts
   ```

2. **Review rate limiting configuration**:
   ```bash
   echo $HONUA__RATELIMITING__GLOBALREQUESTSPERMINUTE
   echo $HONUA__RATELIMITING__ENABLED
   ```

3. **Identify top offenders** (if using Redis):
   ```bash
   redis-cli --scan --pattern "honua:ratelimit:*" | head -20
   ```

### Resolution Steps

#### Immediate Actions
1. **Temporarily increase limits** (if legitimate traffic):
   ```bash
   export HONUA__RATELIMITING__GLOBALREQUESTSPERMINUTE=2000
   kubectl rollout restart deployment/honua-server
   ```

2. **Block specific IPs** (if abuse detected):
   ```bash
   # Use your ingress controller/WAF to block IPs
   kubectl patch configmap nginx-config \
     --patch '{"data":{"blocked-ips":"1.2.3.4,5.6.7.8"}}'
   ```

#### Long-term Solutions
1. **Implement API key-based rate limiting**
2. **Add rate limiting at CDN/WAF level**
3. **Configure different limits per endpoint type**

## File Upload Problems

### Symptoms
- Upload timeouts
- High queue depth
- Memory exhaustion during uploads
- Failed file processing

### Diagnosis Steps

1. **Check upload queue metrics**:
   ```bash
   curl -H "Authorization: Bearer $ADMIN_TOKEN" \
        https://honua-server/monitoring/metrics/upload-queue
   ```

2. **Review file upload configuration**:
   ```bash
   echo $HONUA__FILEUPLOAD__MAXFILESIZEBYTES
   echo $HONUA__FILEUPLOAD__MAXCONCURRENTUPLOADS
   ```

### Resolution Steps

#### Immediate Actions
1. **Clear upload queue** (if stuck):
   ```bash
   # Restart pods to clear in-memory queue
   kubectl rollout restart deployment/honua-server
   ```

2. **Increase upload limits temporarily**:
   ```bash
   export HONUA__FILEUPLOAD__MAXCONCURRENTUPLOADS=10
   export HONUA__FILEUPLOAD__MAXQUEUEDUPLOADS=50
   ```

#### Configuration Tuning
1. **Adjust upload timeouts**:
   ```bash
   export HONUA__LIMITS__IMPORTS__UPLOADSIZEVALIDATIONTIMEOUTSECONDS=300
   ```

2. **Configure streaming settings**:
   ```bash
   export HONUA__FILEUPLOAD__MAXFILESIZEBYTES=524288000  # 500MB
   ```

## Circuit Breaker Incidents

### Symptoms
- External service integration failures
- Circuit breaker open alerts
- Degraded functionality for external features

### Diagnosis Steps

1. **Check circuit breaker status** (in application logs):
   ```bash
   kubectl logs deployment/honua-server | grep "Circuit breaker"
   ```

2. **Test external service connectivity**:
   ```bash
   # Test from pod
   kubectl exec deployment/honua-server -- curl -v https://external-service/health
   ```

### Resolution Steps

#### Immediate Actions
1. **Manual circuit breaker reset** (if service is healthy):
   ```bash
   # Restart application to reset circuit breakers
   kubectl rollout restart deployment/honua-server
   ```

2. **Disable external service features** (if extended outage):
   ```bash
   export HONUA__EXTERNALSERVICES__ENABLED=false
   ```

#### Configuration Adjustments
1. **Increase failure threshold** (if too sensitive):
   ```bash
   export HONUA__EXTERNALSERVICES__CIRCUITBREAKER__ARCGISREST__FAILURETHRESHOLD=10
   ```

2. **Adjust timeout settings**:
   ```bash
   export HONUA__EXTERNALSERVICES__CIRCUITBREAKER__ARCGISREST__TIMEOUTSECONDS=60
   ```

## General Troubleshooting

### Application Logs

#### Key Log Patterns to Search For
```bash
# Error patterns
kubectl logs deployment/honua-server | grep -E "(ERROR|FATAL|Exception)"

# Performance issues
kubectl logs deployment/honua-server | grep -E "(timeout|slow|latency)"

# Database issues
kubectl logs deployment/honua-server | grep -E "(connection|database|pool)"

# Memory issues
kubectl logs deployment/honua-server | grep -E "(memory|GC|OutOfMemory)"
```

#### Structured Logging Queries
```bash
# Query errors by severity
kubectl logs deployment/honua-server --since=1h | \
  jq 'select(.Level == "Error") | {Timestamp, Message, Exception}'

# Query database operations
kubectl logs deployment/honua-server --since=30m | \
  jq 'select(.Properties.Category == "Database") | {Timestamp, Duration, Operation}'
```

### Performance Investigation

#### Query Performance
```sql
-- Find slow queries
SELECT query, mean_exec_time, calls, total_exec_time
FROM pg_stat_statements
WHERE mean_exec_time > 1000
ORDER BY mean_exec_time DESC
LIMIT 20;
```

#### Connection Analysis
```sql
-- Analyze connection patterns
SELECT client_addr, state, count(*), max(query_start) as latest_query
FROM pg_stat_activity
WHERE application_name LIKE '%honua%'
GROUP BY client_addr, state;
```

### Emergency Procedures

#### Complete Service Restart
```bash
# Graceful restart
kubectl rollout restart deployment/honua-server
kubectl rollout status deployment/honua-server --timeout=300s

# Emergency restart (if pods are stuck)
kubectl delete pods -l app=honua-server
```

#### Database Emergency Procedures
```bash
# Kill all connections (EMERGENCY ONLY)
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE datname = 'honua' AND pid <> pg_backend_pid();

# Enable read-only mode (maintenance)
ALTER DATABASE honua SET default_transaction_read_only = on;
```

### Escalation Procedures

#### Severity Levels
- **P0 (Critical)**: Service down, data loss risk
- **P1 (High)**: Significant performance degradation
- **P2 (Medium)**: Minor performance issues, feature degradation
- **P3 (Low)**: Monitoring alerts, no user impact

#### Contact Information
```bash
# Notification commands
curl -X POST $SLACK_WEBHOOK_URL \
  -d '{"text":"Production Issue: [Description] - Severity: [Level]"}'

# PagerDuty integration
curl -X POST https://api.pagerduty.com/incidents \
  -H "Authorization: Token $PD_TOKEN" \
  -d '{"incident":{"type":"incident","title":"Honua Server Issue"}}'
```

## Preventive Measures

### Regular Maintenance
- **Daily**: Review error rates and performance metrics
- **Weekly**: Analyze slow query reports and connection patterns
- **Monthly**: Review and update alert thresholds
- **Quarterly**: Load testing and capacity planning

### Automated Monitoring
```bash
# Set up monitoring alerts in your observability platform
# Example Prometheus alerting rules:

# High error rate
alert: HonuaHighErrorRate
expr: rate(honua_errors_total[5m]) / rate(honua_queries_total[5m]) > 0.05
for: 5m

# High memory usage
alert: HonuaHighMemoryUsage
expr: honua_memory_usage_bytes > 2e9  # 2GB
for: 10m

# Database pool exhaustion
alert: HonuaDbPoolExhaustion
expr: honua_db_pool_utilization_ratio > 0.9
for: 2m
```

---

**Last Updated**: 2026-04-06  
**Version**: 1.0  
**Maintained By**: Honua Operations Team