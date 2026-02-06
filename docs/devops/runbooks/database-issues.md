# Database Issues Runbook

**Alert**: HonuaDatabaseIssues
**Severity**: Critical
**Response Time**: < 5 minutes

## Symptoms
- Readiness fails with database unavailable
- Connection timeouts or authentication errors
- Elevated query latency

## Immediate Actions

### 1. Validate Connectivity (0-3 minutes)
```bash
kubectl exec -it deployment/honua-server -n honua-production -- \
  psql -h postgres.honua.internal -U honua_prod -c "SELECT 1;"
```

### 2. Check Database Health (3-5 minutes)
```bash
kubectl get pods -n postgres
kubectl logs deployment/postgres -n postgres --since=30m
```

## Diagnostics

### Connection Pool Saturation
```sql
SELECT count(*) FROM pg_stat_activity;
SELECT * FROM pg_stat_activity WHERE state = 'active';
```

### Slow Queries
```sql
SELECT query, mean_time, calls
FROM pg_stat_statements
ORDER BY mean_time DESC
LIMIT 10;
```

### Migration State
- Review application logs for migration failures
- Confirm latest schema version in `schema_versions`

## Common Causes & Fixes

### Cause 1: Database Down
**Fix**:
- Restart DB pod/instance
- Fail over to standby

### Cause 2: Credentials/Secret Errors
**Fix**:
- Validate `ConnectionStrings__DefaultConnection`
- Verify secret references resolve correctly

### Cause 3: Connection Exhaustion
**Fix**:
- Increase connection pool
- Scale application horizontally
- Terminate runaway queries

## Escalation

Escalate if:
- Data loss or corruption suspected
- Failover required

## Recovery

```bash
# Restart database deployment
kubectl rollout restart deployment/postgres -n postgres

# Restart application after DB recovery
kubectl rollout restart deployment/honua-server -n honua-production
```
