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
| `GET /metrics` | Admin auth | Native Prometheus text exposition |
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

## Job Orchestration Observability

Worker hosts running `AddJobWorker()` emit log entries from the
`JobReconciliationService` background sweep:

| Level | Signal |
|-------|--------|
| Debug | Routine sweep results (reconciled count out of active total) |
| Warning | Heartbeat expired — job requeued for retry |
| Error | Heartbeat expired with no retries remaining, timeout expiry, or sweep failure |

Monitor for `JobReconciliationService` entries in worker hosts to detect
stale heartbeats, abandoned jobs, and retry exhaustion. For lifecycle
details and tuning, see [Operations — Job Orchestration](operations.md#job-orchestration).

---

## OpenTelemetry

Honua uses standard OpenTelemetry APIs.

- Configure OTLP export via `OTEL_*` environment variables to send metrics and traces to your telemetry backend.
- Prometheus can scrape native text metrics at `GET /metrics` by presenting admin credentials such as an API key.
- Optional path override: `Observability__Prometheus__Path=/custom-metrics`.
- Use `/api/v1/admin/observability/telemetry` to confirm tracing status.
- In multi-node environments, deploy rollback detection should query a Prometheus-compatible metrics backend. OTLP is the export path, not the rollback-decision API.

---

## Cloud-Native Alerting (OTLP → Collector → Managed Prometheus)

The recommended alerting path uses managed cloud services rather than self-hosted Prometheus/Grafana.

### Architecture

1. Honua emits OTLP telemetry.
2. OpenTelemetry Collector receives OTLP and batches data.
3. Collector exports metrics to managed Prometheus via `remote_write`.
4. Alert policies use a shared PromQL rules file: `docs/alerting/rules/honua-core.yaml`
5. The Honua control plane can query that Prometheus-compatible backend for canary settle/rollback decisions.

For self-hosted multi-node environments, Prometheus can also scrape `GET /metrics` directly with admin credentials and act as both the collector and the query backend for deploy rollback gates.

### Deploy Telemetry Presets

Deploy targets can point at a Prometheus-compatible query backend by setting `telemetry.connection` in the control-plane target parameters.

- Default Kubernetes preset: when a Kubernetes deploy target sets `telemetry.connection`, Honua uses the built-in `kubernetes-honua-http` policy unless you override it.
- AWS ALB canary preset: set `telemetry.policy=aws-alb-canary` and expose a distinct Prometheus scrape lane for the canary tasks, typically via a separate scrape job such as `honua-canary`.
- Azure Container Apps canary preset: auto-selected when a canary Prometheus selector or canary job is configured, or set explicitly with `telemetry.policy=azure-aca-canary`. Uses 3-minute warmup and 10 minimum samples (lower thresholds due to reduced canary traffic volume). Requires a canary Prometheus selector or canary job.
- Generic Honua HTTP preset: serverless and Azure Container Apps targets can use `telemetry.policy=honua-http` when they expose the standard Honua HTTP metrics without a distinct canary scrape lane. Azure Functions and Azure Container Apps immediate-cutover deploys default to this policy when `telemetry.connection` is set.

Useful target parameters:

- `telemetry.connection`: named telemetry connection from `ControlPlane:TelemetryConnections`
- `telemetry.policy`: optional preset, currently `kubernetes-honua-http`, `aws-alb-canary`, `azure-aca-canary`, or `honua-http`
- `telemetry.prometheus.job`: Prometheus `job` label for the stable or aggregated Honua scrape target
- `telemetry.prometheus.selector`: raw PromQL label selector fragment when `job=...` is not enough
- `telemetry.prometheus.canary_job`: Prometheus `job` label for the canary scrape target
- `telemetry.prometheus.canary_selector`: raw PromQL label selector fragment for canary-only metrics
- `telemetry.prometheus.extra_selector`: additional label matchers appended to the generated selector
- `lambda.canary_weight_percentage`: optional AWS Lambda alias canary percentage for deploy targets; requires `telemetry.connection` because Honua only promotes or rolls back the alias after telemetry settles
- `containerapp.canary_weight_percentage`: optional Azure Container Apps canary traffic percentage (1–99) for the `honua-azure-container-apps-revision` backend; requires `telemetry.connection`

Explicit query overrides still win:

- `telemetry.error_rate.query`
- `telemetry.latency_p95.query`
- `telemetry.sample_count.query`

To render a starter `ControlPlane` config fragment from Terraform outputs, use:

```bash
terraform output -json > terraform-output.json
./scripts/render-control-plane-config-from-terraform.sh \
  --terraform-output-json terraform-output.json
```

The renderer also consumes provider-specific identity hints when Terraform exposes them, including:

- `control_plane_target_id`
- `control_plane_target_name`
- `control_plane_target_resource_id`
- `control_plane_current_revision`
- `control_plane_namespace`
- `aws_region`
- `lambda_alias_name`
- `lambda_alias_arn`
- `lambda_alias_invoke_arn`
- `lambda_current_version`
- `lambda_function_name`
- `function_app_name`
- `container_app_name`

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
