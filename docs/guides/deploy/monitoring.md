# Monitor Honua Server

You'll wire up health probes, Prometheus metrics, OpenTelemetry export, and the pinned alert rules so a degraded deployment pages you before users notice. For the higher-level operate story - the loop, Console and MCP seats, autonomy ladder, rollback taxonomy, and when Grafana is optional depth - start with [Operating Honua](../operate/README.md). The profiled Console Operate service in the Docker quickstart is the first local ops view once a compatible Console image is published; Grafana and Prometheus are optional depth for teams that want an external metrics backend.

**Prerequisites:** A running deployment and the admin password (admin endpoints authenticate with the `X-API-Key` header). A metrics backend (managed Prometheus, self-hosted Prometheus, or any OTLP-compatible stack) is needed for long-retention metrics and deep traces/logs, but the built-in operate status, ops-health, findings, and timeline surfaces do not require Grafana or Prometheus.

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
| `GET /api/v1/operate/status` | ops-reader or admin | **Server-authoritative aggregated status** — one server-computed verdict plus per-domain rollups and the availability SLO |

## Aggregated operational status

Instead of stitching the endpoints above and inventing your own "is the system healthy" verdict, call the one aggregated surface:

```bash
curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" http://localhost:8080/api/v1/operate/status
```

It returns a server-computed `status` (`healthy` / `degraded` / `unhealthy`) with the machine-readable `reasons` that drove it, per-domain rollups (`deploys`, `jobs`, `alerts`, `migrations`, `findings`, `telemetryBackends`) each carrying a `source` hint you can drill down to, and a `schemaVersion` + `generatedAt` so a consumer can version its parsing. The verdict rules are fixed and documented server-side: the health-check roll-up being `Unhealthy` ⇒ `unhealthy`; a `Critical` finding, a deploy parked in manual intervention, dead-lettered alerts, an impaired dispatcher, or an exhausted SLO error budget ⇒ `degraded`; otherwise `healthy`.

With the quickstart `console` profile enabled, open <http://localhost:5174/operate/health> for this status plus the Console health dashboard, and <http://localhost:5174/operate/copilot> for deterministic findings and proposal entry points. Those pages read the same server-owned APIs described here.

### Availability SLO / error budget

Configure an availability target and the `slo` block evaluates a burn rate and remaining error budget from the in-process, GIS-protocol-partitioned serving-latency window (no metrics database required):

```bash
Slo__Availability__Target=0.995          # fraction in (0,1); omit to leave the SLO "not configured"
Slo__Availability__RollingWindowSeconds=300
```

When no target is set, `slo.configured` is `false` with an explicit reason rather than an invented number. v1 evaluates HTTP-5xx serving availability over the aggregator window; the in-band GeoServices error-envelope signal (2xx error envelopes) is not yet folded into the window.

### Read-only ops credential

Provision an ops-reader credential so a status dashboard or copilot can read the ops posture without holding a key that could `POST /rollback`. Mint an admin API key scoped to the `ops:read` grant (distinct from `admin:read`, which can also read the broader admin surfaces):

```bash
# With a full-admin key, mint an ops-reader key:
curl -s -X POST http://localhost:8080/api/v1/admin/api-keys \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" -H 'Content-Type: application/json' \
  -d '{"name":"ops-dashboard","permissions":["ops:read"]}'
```

The returned key authorizes the read-only ops surfaces — `GET /api/v1/operate/status`, `GET /api/v1/admin/observability/{ops-health,findings}`, and `GET /api/v1/admin/observability/alerts` — but is rejected with a `403` on every mutating ops operation (deploy rollback/promote/submit, `findings/{id}/propose`, alert `acknowledge`/`suppress`/`resolve`) and on non-ops admin surfaces such as key management. Full-admin keys and client-certificate admins are unaffected.

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

5. (Optional one-command Docker depth) For local or single-node deployments that also need Grafana and Prometheus, bring up the curated bundle - provisioned datasource plus the Serving, GP/Jobs, and Ops/Alerts dashboards - against a running server:

```bash
docker compose -f docker/monitoring/compose.yml up -d
# Grafana http://localhost:3000 (admin/admin), Prometheus http://localhost:9090
```

Prometheus scrapes `/metrics` with `basic_auth` (the API-key handler accepts the admin key as the Basic password). Edit [`docker/monitoring/prometheus/prometheus.yml`](../../../docker/monitoring/prometheus/prometheus.yml) to set the scrape target/credentials, and see [`docker/monitoring/README.md`](../../../docker/monitoring/README.md) for details.

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
- **`/healthz/ready` flaps** — usually database connectivity; check `GET /monitoring/health/comprehensive` for the failing dependency. If Redis is configured, an unreachable Redis also fails readiness (durable feature-change event storage); a deployment with no Redis configured runs events in single-node in-memory mode and stays `Ready`.

## Next steps

- [Operating Honua](../operate/README.md)
- [Scale and tune performance](scaling-and-performance.md)
- [Troubleshoot Honua Server](troubleshooting.md)
- [Upgrade and roll back](upgrade-and-rollback.md)
