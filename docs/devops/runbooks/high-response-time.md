# High Response Time Runbook

**Alert**: HonuaHighLatency
**Severity**: High
**Goal**: Restore latency to acceptable levels.

---

## Immediate Checks

- Check `/api/v1/metrics/performance` and `/api/v1/metrics/database`.
- Identify the slowest endpoints.
- Confirm database health and index usage.

---

## Diagnose

- Large queries (no filters, huge bbox, large offsets)
- Missing spatial indexes
- Connection pool saturation
- CPU or memory pressure on database

---

## Mitigate

- Tighten query limits temporarily.
- Scale app replicas.
- Add or rebuild spatial indexes if missing.

---

## Escalate

Escalate if latency remains above SLO for 30 minutes.
