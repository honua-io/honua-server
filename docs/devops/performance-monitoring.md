# Performance Monitoring

This guide covers the monitoring surfaces Honua exposes and the signals worth tracking in production.

**Scope**: Endpoints and operational signals, not implementation details.

---

## Health Endpoints

- `GET /healthz/live`
- `GET /healthz/ready`

Use readiness for load balancer checks and liveness for container restarts.

---

## Metrics Endpoints

Honua exposes snapshot metrics endpoints (authentication may be required in production):

- `GET /api/v1/metrics/health`
- `GET /api/v1/metrics/performance`
- `GET /api/v1/metrics/database`
- `GET /api/v1/metrics/cache`
- `GET /api/v1/metrics/memory`

---

## Admin Observability

Admin-only endpoints for operational diagnostics:

- `GET /api/v1/admin/observability/errors`
- `GET /api/v1/admin/observability/telemetry`

---

## Recommended Signals

Track these consistently across environments:

- Request latency (p50/p95/p99)
- Error rates (4xx vs 5xx)
- Database query time and connection saturation
- Cache hit ratios and evictions
- Memory pressure and GC activity

---

## OpenTelemetry

Honua uses standard OpenTelemetry APIs. If you configure OTLP export, your telemetry backend can ingest Honua metrics and traces. Use `/api/v1/admin/observability/telemetry` to confirm tracing status.

---

## Security Notes

- Treat metrics as sensitive operational data.
- Restrict access to metrics endpoints in production.
- Use TLS and a secure network boundary for observability traffic.

---

## Related Docs

- [Connection Pool Sizing](connection-pool-sizing.md)
- [Query Optimization](query-optimization.md)
- [Security Configuration](SECURITY_CONFIGURATION.md)
