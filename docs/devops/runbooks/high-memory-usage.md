# High Memory Usage Runbook

**Alert**: HonuaHighMemory
**Severity**: Medium
**Goal**: Reduce memory pressure and avoid OOM restarts.

---

## Immediate Checks

- Check memory usage per pod/container.
- Review recent traffic spikes or large imports.

---

## Diagnose

- Large query responses (no paging)
- High concurrency with large payloads
- Memory pressure from big imports

---

## Mitigate

- Reduce query limits and payload sizes.
- Scale replicas or increase memory limits.
- Split large imports into smaller batches.

---

## Escalate

Escalate if OOM events persist after mitigation.
