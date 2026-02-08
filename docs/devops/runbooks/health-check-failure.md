# Health Check Failure Runbook

**Alert**: HonuaHealthCheckFailed
**Severity**: High
**Goal**: Restore readiness and liveness checks.

---

## Immediate Checks

```bash
curl -f https://<host>/healthz/live
curl -f https://<host>/healthz/ready
```

---

## Diagnose

- If `/live` fails: process is down or crashed.
- If `/ready` fails: dependencies are unhealthy (DB, cache, config).
- Check recent deploys or config changes.

---

## Mitigate

- Restart the service.
- Verify database connectivity and credentials.
- Roll back recent changes if health failures started after deploy.

---

## Escalate

Escalate if readiness doesn't recover within 15 minutes.
