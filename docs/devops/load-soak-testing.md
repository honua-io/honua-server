# Load and Soak Testing

Use soak tests to catch memory leaks, cache churn, and performance degradation over time.

---

## Recommended Process

- Run sustained traffic for multiple hours.
- Use a realistic mix of queries and payload sizes.
- Monitor memory, CPU, and error rates continuously.

---

## What to Watch

- Memory growth over time
- Rising latency percentiles
- Increased cache miss rates
- Database connection saturation

---

## Related Docs

- [Performance Monitoring](performance-monitoring.md)
- [Memory Optimizations](MEMORY_OPTIMIZATIONS_REPORT.md)
