# High CPU Usage Runbook

**Alert**: HonuaHighCpuUsage
**Severity**: Warning
**Response Time**: < 1 hour

## Symptoms
- CPU utilization consistently above threshold
- Increased latency or throttling

## Immediate Actions

### 1. Check CPU Utilization
```bash
kubectl top pods -n honua-production
kubectl top nodes
```

### 2. Review Recent Changes
- Confirm whether a deploy correlates with CPU spike

## Diagnostics

### Identify Hot Endpoints
- Inspect logs for high-frequency endpoints
- Check for large spatial queries or heavy filters

### Database Load
```sql
SELECT query, mean_time, calls
FROM pg_stat_statements
ORDER BY calls DESC
LIMIT 10;
```

## Common Causes & Fixes

### Cause 1: Expensive Queries
**Fix**:
- Add indexes
- Tighten query limits

### Cause 2: Insufficient Resources
**Fix**:
- Scale out pods
- Increase CPU limits

### Cause 3: Abusive Traffic
**Fix**:
- Tighten rate limits
- Apply IP blocks

## Escalation

Escalate if:
- CPU remains high after scaling
- User impact exceeds SLO

## Recovery

```bash
# Scale out
kubectl scale deployment honua-server --replicas=5 -n honua-production

# Roll back if correlated with recent release
kubectl rollout undo deployment/honua-server -n honua-production
```
