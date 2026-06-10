# Monitor Honua Server

You'll wire up health probes, Prometheus metrics, OpenTelemetry export, and the pinned alert rules so a degraded deployment pages you before users notice.

**Prerequisites:** A running deployment, the admin password (admin endpoints authenticate with the `X-API-Key` header), and a metrics backend (managed Prometheus, self-hosted Prometheus, or any OTLP-compatible stack).

## Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /healthz/live` | none | Liveness probe; returns `Healthy` |
| `GET /healthz/ready` | none | Readiness probe; returns `Ready` |
| `GET /metrics` | admin | Native Prometheus text exposition |
| `GET /healthz/metrics` | admin | Lightweight health/performance snapshot incl. license state |
| `GET /api/v1/metrics/{health,performance,database,cache,memory,streaming}` | admin | JSON metric snapshots |
| `GET /monitoring/health/production` | admin | Combined health from live request/cache/connection telemetry |
| `GET /monitoring/health/comprehensive` | admin | Sanitized ASP.NET health-check report |
| `GET /monitoring/metrics/{connection-pool,cache,resources,upload-queue,database-resilience}` | admin | Focused operational diagnostics |
| `GET /monitoring/alerts` | admin | Current alert conditions from production thresholds |
| `GET /api/v1/admin/observability/{errors,telemetry,events,migrations}` | admin | Error history, tracing status, Operate events, migration state |

## Steps

1. Enable the observability surfaces. `HONUA_OBSERVABILITY=true` turns on metrics; `HONUA_OPENTELEMETRY=true` adds distributed tracing; the scrape path is configurable.

```bash
HONUA_OBSERVABILITY=true
HONUA_OPENTELEMETRY=true
Observability__Prometheus__Path=/metrics
```

2. Point your scraper or collector at the server. `/metrics` requires admin authorization, so the scraper must send the `X-API-Key` header. For OTLP export instead of scraping, set the standard `OTEL_*` variables (for example `OTEL_EXPORTER_OTLP_ENDPOINT`) and metrics/traces/logs flow to your collector.

```bash
HONUA_ADMIN_PASSWORD=replace-with-admin-password
curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" http://localhost:8080/metrics | head -n 5
```

3. Load the pinned alert rules. The canonical PromQL ruleset is [`honua-core.yaml`](../../alerting/rules/honua-core.yaml); collector overlays for managed backends are at [`aws/collector-overlay.yaml`](../../alerting/aws/collector-overlay.yaml) and [`azure/collector-overlay.yaml`](../../alerting/azure/collector-overlay.yaml). On Amazon Managed Prometheus:

```bash
aws amp put-rule-groups-namespace \
  --workspace-id "$AMP_WORKSPACE_ID" \
  --name honua-core \
  --data fileb://docs/alerting/rules/honua-core.yaml
```

On Azure Monitor managed Prometheus, define rule-group resources (ARM/Bicep/Terraform) using the same expressions and route notifications through Action Groups.

4. (Self-hosted option) Deploy `kube-prometheus-stack` and import the bundled assets: the Grafana dashboard at [`docker/monitoring/grafana/dashboards/honua-overview.json`](../../../docker/monitoring/grafana/dashboards/honua-overview.json) and alert rules at [`docker/monitoring/prometheus/alerts.yml`](../../../docker/monitoring/prometheus/alerts.yml). A broader standalone ruleset is in [`examples/prometheus-alerts.yml`](examples/prometheus-alerts.yml), and [`examples/production-monitoring.json`](examples/production-monitoring.json) is a baseline app configuration for monitoring, resilience, and rate-limit thresholds.

```bash
helm install monitoring prometheus-community/kube-prometheus-stack --namespace monitoring --create-namespace
```

## Verify

```bash
curl -s http://localhost:8080/healthz/live && echo && \
curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" http://localhost:8080/monitoring/health/production | head -c 300
```

Expected: `Healthy` followed by a JSON health snapshot with status fields.

## What to watch

- **Request health**: error rate and p95 latency from `/metrics` — these same signals gate automatic deploy rollback (see [Upgrade and roll back](upgrade-and-rollback.md)).
- **Connection pool**: `GET /monitoring/metrics/connection-pool` for utilization, failures, and timeouts; tune with [Scale and tune performance](scaling-and-performance.md).
- **Jobs and workflows**: durable job history at `GET /api/v1/admin/jobs`, per-job logs at `/api/v1/admin/jobs/{jobId}/logs`, and the Operate event timeline at `GET /api/v1/admin/observability/events?kind=job`. Workflow runs are covered in [Automate workflows](../query-analyze/automate-workflows.md).
- **License state**: surfaced under `license` in `/healthz/metrics` and `/monitoring/health/production`.

## Troubleshoot

- **`/metrics` returns 404** — `HONUA_OBSERVABILITY` is not `true`, or the path was moved with `Observability__Prometheus__Path`.
- **`/metrics` returns 401** — the scrape request is missing the `X-API-Key` header (or OIDC bearer token).
- **No traces in your backend** — set `HONUA_OPENTELEMETRY=true` and an `OTEL_EXPORTER_OTLP_ENDPOINT`; confirm status via `GET /api/v1/admin/observability/telemetry`.
- **`/healthz/ready` flaps** — usually database connectivity; check `GET /monitoring/health/comprehensive` for the failing dependency.

## Next steps

- [Scale and tune performance](scaling-and-performance.md)
- [Troubleshoot Honua Server](troubleshooting.md)
- [Upgrade and roll back](upgrade-and-rollback.md)
