# High Response Time Runbook

**Alert**: HonuaHighResponseTime
**Severity**: Warning
**Response Time**: < 30 minutes

## Symptoms
- P95/P99 latency above SLO
- Slow query responses across protocols
- Timeouts reported by clients

## Immediate Actions

### 1. Verify Scope (0-5 minutes)
```bash
# Check health
curl -f https://api.honua.example.com/healthz/ready

# Check performance metrics
curl -s https://api.honua.example.com/api/v1/metrics/performance | jq '.memory'
curl -s https://api.honua.example.com/api/v1/metrics/database | jq '.operations | keys'
```

### 2. Identify Hot Paths (5-10 minutes)
- Review request logs for slow endpoints
- Check DB slow query logs

## Diagnostics

### Database Query Performance
```sql
-- Top slow queries
SELECT query, mean_time, calls
FROM pg_stat_statements
ORDER BY mean_time DESC
LIMIT 10;
```

### Cache Effectiveness
```bash
curl -s https://api.honua.example.com/api/v1/metrics/cache | jq '{hit_ratio: (.hitRatio * 100), total_requests: .totalRequests}'
```

### Resource Saturation
```bash
kubectl top pods -n honua-production
kubectl top nodes
```

## Common Causes & Fixes

### Cause 1: Cache Miss Spike
**Fix**:
- Increase cache TTL for hot metadata
- Verify cache is enabled and Redis is healthy

### Cause 2: Slow Database Queries
**Fix**:
- Add missing indexes
- Reduce `Limits:Query:MaxRecordCount`
- Optimize filters (bbox, spatial indexes)

### Cause 3: CPU Saturation
**Fix**:
- Scale out application replicas
- Identify expensive endpoints and rate-limit

## Escalation

Escalate if:
- Latency remains above SLO for > 60 minutes
- Timeouts exceed error budget

## Recovery

```bash
# Scale out
kubectl scale deployment honua-server --replicas=5 -n honua-production

# Roll back recent deploy if latency spike aligns with release
kubectl rollout undo deployment/honua-server -n honua-production
```
