# Database Issues Runbook

**Alert**: HonuaDatabaseIssues
**Severity**: High
**Goal**: Restore database availability and performance.

---

## Immediate Checks

- Confirm database is reachable.
- Check active connections and replication status (if applicable).
- Review recent migrations or schema changes.

---

## Diagnose

- Connection pool exhaustion
- Slow queries or missing indexes
- Disk saturation or replication lag

---

## Mitigate

- Restart or fail over database if required.
- Reduce app concurrency temporarily.
- Roll back recent schema changes if they correlate with the issue.

---

## Escalate

Escalate if data integrity or corruption is suspected.
