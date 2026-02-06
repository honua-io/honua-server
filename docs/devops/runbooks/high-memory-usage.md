# High Memory Usage Runbook

**Alert**: HonuaHighMemoryUsage
**Severity**: Warning
**Response Time**: < 1 hour

## Symptoms
- Memory usage near limits
- OOM kills or frequent restarts
- Increased GC pauses

## Immediate Actions

### 1. Check Memory Metrics
```bash
curl -s https://api.honua.example.com/api/v1/metrics/performance | jq '{allocated_mb: (.memory.allocatedBytes / 1024 / 1024), working_set_mb: (.systemInfo.workingSet / 1024 / 1024)}'
```

### 2. Check Pod Limits
```bash
kubectl top pods -n honua-production
kubectl describe pod <pod-name> -n honua-production | grep -i "memory"
```

## Diagnostics

### Cache Size
```bash
curl -s https://api.honua.example.com/api/v1/metrics/cache | jq '{total_requests: .totalRequests, hit_ratio: (.hitRatio * 100)}'
```

### GC Pressure
- Review logs for GC-related warnings
- Inspect memory pressure percentage from `/api/v1/metrics/health`

## Common Causes & Fixes

### Cause 1: Unbounded Cache Growth
**Fix**:
- Reduce cache TTL
- Lower fallback cache max entries

### Cause 2: Large Responses
**Fix**:
- Reduce `Limits:Query:MaxRecordCount`
- Encourage pagination

### Cause 3: Memory Leak
**Fix**:
- Capture heap dumps
- Roll back recent changes

## Escalation

Escalate if:
- Memory usage continues to grow after mitigation
- OOM kills persist

## Recovery

```bash
# Scale out to reduce per-pod load
kubectl scale deployment honua-server --replicas=5 -n honua-production

# Restart pods to clear memory pressure
kubectl rollout restart deployment/honua-server -n honua-production
```
