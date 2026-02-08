# High CPU Usage Runbook

**Alert**: HonuaHighCpu
**Severity**: Medium
**Goal**: Reduce CPU saturation and prevent cascading latency.

---

## Immediate Checks

- Identify which nodes or pods are maxed.
- Correlate CPU spikes with traffic or specific endpoints.

---

## Diagnose

- Expensive spatial queries or large result sets.
- Missing indexes causing sequential scans.
- High concurrency with insufficient limits.

---

## Mitigate

- Scale replicas temporarily.
- Tighten query limits to reduce heavy requests.
- Add or rebuild spatial indexes.

---

## Escalate

Escalate if CPU remains >90% for 30 minutes or causes errors.
