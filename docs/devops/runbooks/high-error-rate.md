# High Error Rate Runbook

**Alert**: HonuaHighErrorRate
**Severity**: High
**Goal**: Reduce 5xx errors and stabilize traffic.

---

## Immediate Checks

- Check error rate in your metrics dashboard.
- Review recent logs for top error causes.
- Verify database connectivity and timeouts.

---

## Diagnose

- Is the error rate isolated to a specific endpoint or protocol?
- Do errors correlate with traffic spikes or a recent deploy?
- Are timeouts or pool exhaustion errors appearing?

---

## Mitigate

- Roll back recent changes if errors began after deploy.
- Scale replicas to relieve load.
- Tighten query limits temporarily if requests are heavy.

---

## Escalate

Escalate if errors persist beyond 30 minutes or affect critical customers.
