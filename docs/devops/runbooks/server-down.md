# Server Down Runbook

**Alert**: HonuaServerDown
**Severity**: Critical
**Goal**: Restore service quickly, then investigate root cause.

---

## Immediate Checks (first 5 minutes)

1. **Health endpoints**
   ```bash
   curl -f https://<host>/healthz/live
   curl -f https://<host>/healthz/ready
   ```

2. **Recent deployments**
   - Identify any deploys or config changes in the last hour.

3. **Service status**
   - Kubernetes: check pod status and restarts.
   - Docker: check container status and logs.

---

## Diagnose

- Review recent logs for startup failures or crash loops.
- Verify database connectivity and DNS resolution.
- Check resource limits (CPU/memory) and OOM kills.

---

## Mitigate

- Restart the service.
- Roll back the most recent deployment.
- Scale replicas if the outage is load-related.

---

## Escalate

Escalate if:
- Outage persists beyond 15 minutes.
- Database corruption or security incident is suspected.
