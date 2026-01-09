# Health Check Failure Runbook

**Alert**: HonuaHealthCheckFailing
**Severity**: Warning
**Response Time**: < 15 minutes

## Symptoms
- `/healthz/ready` returns non-200
- `/healthz/live` intermittently fails
- Readiness flapping during deploys

## Immediate Actions

### 1. Confirm Impact (0-2 minutes)
```bash
curl -f https://api.honua.example.com/healthz/live
curl -f https://api.honua.example.com/healthz/ready
curl -f https://api.honua.example.com/healthz/metrics
```

### 2. Check Recent Changes (2-5 minutes)
- Review latest deployments
- Check for config changes (DB, cache, auth)

## Diagnostics

### Database Readiness
Readiness fails if migrations fail or the database is unhealthy.

```bash
# Application logs
kubectl logs --since=30m -l app=honua-server -n honua-production | grep -i "migration\|database"

# Database connectivity
kubectl exec -it deployment/honua-server -n honua-production -- \
  psql -h postgres.honua.internal -U honua_prod -c "SELECT 1;"
```

### Cache Health
Cache failures do not block readiness, but note if fallback is active.

```bash
# Redis health (if enabled)
redis-cli ping
```

### Resource Constraints
```bash
kubectl top pods -n honua-production
kubectl describe pod <pod-name> -n honua-production | grep -i "oom\|memory\|cpu"
```

## Common Causes & Fixes

### Cause 1: Migration Failure
**Symptoms**: Readiness fails immediately after deploy, migration errors in logs

**Fix**:
- Validate migration scripts
- Re-run migration in a single instance
- Use `HONUA_SKIP_MIGRATIONS=true` on other instances

### Cause 2: Database Unavailable
**Symptoms**: Connection errors, timeouts

**Fix**:
- Verify DB pods/instances
- Check credentials and network policies
- Restart database or failover

### Cause 3: Misconfiguration
**Symptoms**: Config validation errors in logs

**Fix**:
- Validate environment variables and config maps
- Roll back last change if needed

## Escalation

Escalate if:
- Readiness remains failing for > 30 minutes
- Multiple regions impacted
- Data corruption suspected

## Recovery

```bash
# Restart deployment
kubectl rollout restart deployment/honua-server -n honua-production

# Roll back if failure aligns with a recent deploy
kubectl rollout undo deployment/honua-server -n honua-production
```
