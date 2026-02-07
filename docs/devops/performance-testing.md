# Performance Testing

Use performance tests to validate throughput, latency, and stability before production.

---

## Recommended Process

1. Load representative data (size, geometry complexity).
2. Define a realistic query mix (bbox, attribute filters, paging).
3. Capture latency percentiles and error rates.
4. Compare results to your SLOs.

---

## What to Measure

- p50/p95/p99 latency
- Error rate (4xx vs 5xx)
- Database CPU and connection saturation
- Cache hit ratio

---

## Related Docs

- [Performance Monitoring](performance-monitoring.md)
- [Query Optimization](query-optimization.md)
- [Connection Pool Sizing](connection-pool-sizing.md)
