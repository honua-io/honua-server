# High Error Rate Runbook

**Alert**: HonuaHighErrorRate
**Severity**: Critical
**Response Time**: < 10 minutes

## Symptoms
- 5xx rate above threshold
- Spike in 4xx/5xx from clients
- Errors across multiple endpoints

## Immediate Actions

### 1. Confirm Impact (0-3 minutes)
```bash
curl -f https://api.honua.example.com/healthz/ready
curl -s https://api.honua.example.com/api/v1/metrics/health | jq '.status'
```

### 2. Inspect Logs (3-7 minutes)
```bash
kubectl logs --since=15m -l app=honua-server -n honua-production | grep -E "ERROR|Exception"
```

## Diagnostics

### Check Recent Deployments
- Review release timeline
- Compare error spike with deploy time

### Validate Dependencies
```bash
# Database connectivity
kubectl exec -it deployment/honua-server -n honua-production -- \
  psql -h postgres.honua.internal -U honua_prod -c "SELECT 1;"

# Cache
redis-cli ping
```

### Identify Endpoint Hotspots
- Inspect logs for repeated endpoint paths
- Check for malformed client requests or auth failures

## Common Causes & Fixes

### Cause 1: Breaking Change Deployed
**Fix**:
- Roll back to previous version
- Confirm schema compatibility

### Cause 2: Dependency Outage
**Fix**:
- Failover database if available
- Restore cache connectivity

### Cause 3: Invalid Client Traffic
**Fix**:
- Rate limit offending IPs
- Validate input validation failures

## Escalation

Escalate if:
- Errors persist > 30 minutes
- Data integrity is at risk

## Recovery

```bash
# Roll back
kubectl rollout undo deployment/honua-server -n honua-production

# Restart pods
kubectl rollout restart deployment/honua-server -n honua-production
```
