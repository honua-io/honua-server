# Incident Runbooks

Stabilize first, diagnose second, document after. Prefer safe, reversible changes.

---

## Release Runbooks

- [Upgrade and Rollback](UPGRADE_AND_ROLLBACK.md) — production upgrade flow, recovery policy, and rollout checks

---

## Licensing Runbooks

- [License Migration](LICENSE_MIGRATION.md) — move from any pre-existing license format to the unified Ed25519 / JWS envelope (ADR-0033) without forced re-issuance
- [License Key Rotation](LICENSE_KEY_ROTATION.md) — additive Ed25519 signing key rotation, including the smoke test required by the acceptance criteria
- [Marketplace Operations](MARKETPLACE_OPERATIONS.md) — AWS and Azure marketplace adapter health: webhook 10s SLA, metering reconciliation, lifecycle failures

---

## Honua Diagnostic Endpoints

Use these during any incident:

```bash
# Health
curl -f https://<host>/healthz/live
curl -f https://<host>/healthz/ready

# Performance & database (admin)
curl https://<host>/api/v1/metrics/performance
curl https://<host>/api/v1/metrics/database
curl https://<host>/api/v1/metrics/cache
curl https://<host>/api/v1/metrics/memory

# Error history and tracing status (admin)
curl https://<host>/api/v1/admin/observability/errors
curl https://<host>/api/v1/admin/observability/telemetry

# Effective configuration
curl https://<host>/api/v1/admin/config
```

---

## Server Down

**Severity**: Critical

1. Hit `/healthz/live` — if it fails, the process is dead or unreachable.
2. Check for recent deploys or config changes.
3. Inspect container/pod status for crash loops or OOM kills.
4. Restart the service or roll back the last deployment.
5. Escalate if the outage persists beyond 15 minutes.

---

## Health Check Failure

**Severity**: High

- `/healthz/live` failing → process crash. Check container logs for startup exceptions.
- `/healthz/ready` failing → dependency is down (PostGIS, Redis, config). Verify database connectivity and credentials.
- Roll back recent deploys if failures correlate with a release.

---

## High Error Rate

**Severity**: High

1. Check `/api/v1/admin/observability/errors` for the top error causes.
2. Determine if errors are isolated to one protocol (`/rest/`, `/ogc/`, `/odata/`) or global.
3. Look for connection pool exhaustion (EventId 7300-7399) or query timeouts (EventId 1000-1999) in logs.
4. Roll back if errors correlate with a deploy; scale replicas if load-related.

---

## High Response Time

**Severity**: High

1. Check `/api/v1/metrics/performance` for p95/p99 latency and `/api/v1/metrics/database` for slow queries.
2. Common causes:
   - Unfiltered queries (no `bbox`, no `limit`, huge offsets)
   - Missing spatial indexes → `EXPLAIN (ANALYZE, BUFFERS)` on slow queries
   - Connection pool saturation → check pool sizing vs replica count
3. Tighten `Limits__Query__QueryTimeout` temporarily; scale replicas.

---

## High CPU Usage

**Severity**: Medium

1. Correlate CPU spikes with traffic on `/api/v1/metrics/performance`.
2. Common causes:
   - Complex spatial queries or MapServer export (`/rest/services/*/MapServer/export`)
   - Missing GiST indexes causing sequential scans
   - Tile generation bursts on `/ogc/tiles/collections/*/tiles`
3. Tighten query limits; add or rebuild spatial indexes; scale replicas.

---

## High Memory Usage

**Severity**: Medium

1. Check `/api/v1/metrics/memory` and container memory stats.
2. Common causes:
   - Large unpaged feature responses (check `Limits__Query__MaxRecordCount`)
   - File import jobs (`/api/v1/admin/import/jobs`) processing large datasets
   - Concurrent MapServer/ImageServer export rendering
3. Reduce query limits and payload sizes; split large imports.

---

## Database Issues

**Severity**: High

1. Confirm PostGIS is reachable: `SELECT PostGIS_Version();`
2. Check `/api/v1/metrics/database` for connection pool health.
3. Look for EventId 7300-7399 (connection/transaction issues) in logs.
4. Common causes:
   - Pool exhaustion (`MaxConnectionPoolSize x replicas` exceeds `max_connections`)
   - Slow queries holding connections — check `pg_stat_activity`
   - Disk saturation or replication lag on managed Postgres
5. Reduce app concurrency temporarily; roll back recent migrations.

---

## Security Incident

**Severity**: Critical

1. Restrict access immediately (network rules, WAF, or firewall).
2. Rotate exposed credentials and admin API keys.
3. Preserve logs — check `/api/v1/admin/observability/errors` for the attack surface.
4. Identify affected endpoints: Admin API (`/api/v1/admin/*`), data APIs (`/rest/`, `/ogc/`, `/odata/`), or infrastructure.
5. Escalate immediately to security leadership.
