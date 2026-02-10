# Serverless Deployments

Serverless can work for light, read-heavy workloads but is not ideal for high-throughput geospatial workloads.

---

## When It Makes Sense

- Low traffic, bursty workloads
- Read-heavy APIs
- Teams without long-lived infrastructure

---

## Caveats

- Cold starts increase latency.
- Long-running queries may hit platform timeouts.
- Database connections must be managed carefully.

---

## Recommendation

Use containers or Kubernetes for sustained production workloads.
