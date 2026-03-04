# Monitoring and Alerting

This guide covers health endpoints, metrics, tracing, and alerting setup for Honua Server.

---

## Health Endpoints

- `GET /healthz/live` — liveness probe (container restarts)
- `GET /healthz/ready` — readiness probe (load balancer checks)

---

## Metrics Endpoints

Honua exposes native Prometheus metrics and JSON snapshot endpoints:

| Endpoint | Access | What it shows |
|----------|--------|---------------|
| `GET /metrics` | Prometheus/network access (restrict at edge) | Native Prometheus text exposition |
| `GET /api/v1/metrics/health` | Admin auth | Health summary |
| `GET /api/v1/metrics/performance` | Admin auth | Request latency and throughput |
| `GET /api/v1/metrics/database` | Admin auth | Connection pool and query stats |
| `GET /api/v1/metrics/cache` | Admin auth | Output cache hit/miss rates |
| `GET /api/v1/metrics/memory` | Admin auth | Memory usage and GC stats |
| `GET /healthz/metrics` | Admin auth | Lightweight health/performance snapshot |

Admin-only diagnostics:

| Endpoint | What it shows |
|----------|---------------|
| `GET /api/v1/admin/observability/errors` | Recent error history |
| `GET /api/v1/admin/observability/telemetry` | Tracing status |
| `GET /api/v1/admin/performance/database/query-cache/statistics` | Prepared statement cache health |

---

## OpenTelemetry

Honua uses standard OpenTelemetry APIs.

- Configure OTLP export via `OTEL_*` environment variables to send metrics and traces to your telemetry backend.
- Prometheus can scrape native text metrics at `GET /metrics`.
- Optional path override: `Observability__Prometheus__Path=/custom-metrics`.
- Use `/api/v1/admin/observability/telemetry` to confirm tracing status.

---

## Cloud-Native Alerting (OTLP → Collector → Managed Prometheus)

The recommended alerting path uses managed cloud services rather than self-hosted Prometheus/Grafana.

### Architecture

1. Honua emits OTLP telemetry.
2. OpenTelemetry Collector receives OTLP and batches data.
3. Collector exports metrics to managed Prometheus via `remote_write`.
4. Alert policies use a shared PromQL rules file: `docs/alerting/rules/honua-core.yaml`

### AWS (Amazon Managed Prometheus)

**Collector overlay**: `docs/alerting/aws/collector-overlay.yaml`

```bash
aws amp put-rule-groups-namespace \
  --workspace-id "$AMP_WORKSPACE_ID" \
  --name honua-core \
  --data fileb://docs/alerting/rules/honua-core.yaml
```

Bind AMP alertmanager routes to SNS or managed Grafana notification policies.

### Azure (Azure Monitor Managed Prometheus)

**Collector overlay**: `docs/alerting/azure/collector-overlay.yaml`

Authentication: Managed Identity with Azure Monitor workspace permissions. Define rule group ARM/Bicep/Terraform resources using expressions from `honua-core.yaml`. Use Action Groups for notification routing.

---

## Optional Self-Hosted Stack (Prometheus + Grafana)

Use this only when you want self-hosted dashboards and alerts in Kubernetes. If you are on AWS/Azure managed monitoring, prefer the cloud-native path above.

Recommended approach:

- Deploy `kube-prometheus-stack` (Prometheus + Grafana) via Helm.
- Configure a scrape target for Honua `GET /metrics`.
- Import dashboard JSON from `docker/grafana/dashboards/honua-overview.json`.
- Apply alert rules from `docker/prometheus/alerts.yml`.

If you use Terraform for observability, use the separate `honua-terraform` repository.
