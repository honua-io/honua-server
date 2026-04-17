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
`JobExecutionService` claim loop and the `JobReconciliationService`
background sweep.

**JobExecutionService** (claim and execution):

| Level | Signal |
|-------|--------|
| Information | Worker started/stopped, job execution started, job completed, operator cancellation, durable cancellation signal honoured during abandon |
| Warning | Job not found after claim, no executor for job kind, job abandoned for retry, no executors registered at startup, job timeout, state transition skipped (job cancelled or reclaimed between claim and Running, or reconciler intervention), heartbeat pump faulted (finalization still proceeds), transient heartbeat write failure (pump retries on next interval), per-attempt warning log append failed (requeue/terminal transition still proceeds) |
| Error | Job execution exception, claim loop error, pre-execution shutdown requeue failed (stale-claim reconciliation will recover) |

**JobReconciliationService** (liveness sweep):

| Level | Signal |
|-------|--------|
| Information | Service started/stopped, reconciliation skipped — job already terminal or claim owner changed since sweep snapshot; heartbeat refreshed since sweep snapshot (worker still alive); timeout no longer expired since sweep snapshot (job reclaimed with fresh claim time) |
| Debug | Sweep results when at least one job was reconciled (count out of active total) |
| Warning | Heartbeat expired — job requeued for retry |
| Error | Heartbeat expired with no retries remaining, timeout expiry, or sweep failure |

**RedisJobQueue** (queue operations and claim recovery):

| Level | Signal |
|-------|--------|
| Information | Job enqueued, job claimed, job requeued |
| Warning | Orphaned claim requeued (claim succeeded but store update failed), claim rolled back after store failure, claim scan traverse threshold exceeded |
| Error | Claim rollback failed (orphaned claim will be caught by reconciliation) |

Monitor for `JobExecutionService`, `JobReconciliationService`, and
`RedisJobQueue` entries in worker hosts to detect execution failures,
stale heartbeats, abandoned jobs, retry exhaustion, and claim-recovery
events. For lifecycle details and tuning, see
[Operations — Job Orchestration](operations.md#job-orchestration).

**Recommended alerts:**

| Condition | Suggested threshold | Signal source |
|-----------|---------------------|---------------|
| Repeated heartbeat expiry | > 2 expiry events in 10 min | `JobReconciliationService` Warning/Error |
| Retry exhaustion spike | > 1 exhaustion event in 5 min | `JobReconciliationService` Error |
| Claim rollback failures | Any occurrence | `RedisJobQueue` Error (`ClaimRollbackFailed`) |
| Worker with no executors | Any occurrence at startup | `JobExecutionService` Warning |
| Sustained claim-loop errors | > 3 errors in 5 min | `JobExecutionService` Error |
| Claim scan traverse threshold exceeded | > 1 occurrence in 5 min | `RedisJobQueue` Warning (`ClaimScanTraverseThresholdExceeded`) |

Queue depth is available via `IJobQueue.GetQueueDepthAsync` but is not yet
exposed through a public metrics endpoint. Operators requiring queue depth
alerting can query the Redis sorted set `controlplane:jobqueue:pending`
directly until a metrics projection is added.

---

## Workflow Orchestration Observability

The declarative workflow engine (`WorkflowOrchestrationEngine`), its
reconciler loop, and the cron scheduler emit structured logs in the
`8100-8199` event-id band via `OrchestrationLog`.

**Key log events:**

| EventId | Name | Level | Signal |
|---------|------|-------|--------|
| 8100 | WorkflowRunCreated | Information | A new workflow run was created |
| 8101 | WorkflowRunCompleted | Information | Run reached a terminal state (succeeded, failed, or cancelled) |
| 8102 | WorkflowStepSubmitted | Information | Step job submitted to the execution substrate |
| 8103 | WorkflowStepCompleted | Information | Step reached a terminal state |
| 8104 | WorkflowStepRetrying | Warning | Step failed and is being retried per its retry policy |
| 8105 | WorkflowStepSkipped | Information | Step was skipped because its dependency used a `Skip` failure policy |
| 8107 | InputBindingFailed | Warning | Artifact-to-input binding resolution failed for a step |
| 8108 | SchedulerTriggered | Information | Cron scheduler created a run for a scheduled workflow |
| 8110 | ReconciliationFailed | Error | Reconciliation loop encountered an unhandled error |
| 8116 | SchedulerDefinitionInvalid | Warning | Scheduled workflow has an invalid cron expression or time zone |
| 8117 | WorkflowStepCancelJobFailed | Warning | Best-effort cascade cancel of a child job failed |
| 8119 | WorkflowCancelLeaseContention | Warning | Cancel request could not acquire reconcile lease (409 returned) |
| 8120 | WorkflowStepArtifactsUnavailableForBoundDependents | Error | Step artifact retrieval failed; bound dependents marked Failed |

**Activities and metrics** (under `honua.orchestration.*`):

| Type | Name |
|------|------|
| Activity | `honua.orchestration.reconcile_run` |
| Activity | `honua.orchestration.execute_step` |
| Activity | `honua.orchestration.resolve_bindings` |
| Activity | `honua.orchestration.scheduler_tick` |
| Metric | `honua.orchestration.runs_created_total` |
| Metric | `honua.orchestration.runs_completed_total` |
| Metric | `honua.orchestration.steps_completed_total` |
| Metric | `honua.orchestration.steps_retried_total` |
| Metric | `honua.orchestration.run_duration_ms` |
| Metric | `honua.orchestration.step_duration_ms` |

**Recommended alerts:**

| Condition | Suggested threshold | Signal source |
|-----------|---------------------|---------------|
| Reconciliation failures | Any occurrence | `OrchestrationLog` Error (8110) |
| Sustained step retries | > 3 retry events in 5 min | `OrchestrationLog` Warning (8104) |
| Artifact binding failures | Any occurrence | `OrchestrationLog` Warning (8107) / Error (8120) |
| Scheduler definition invalid | Any occurrence | `OrchestrationLog` Warning (8116) |
| Cancel lease contention | > 2 in 5 min | `OrchestrationLog` Warning (8119) |

For lifecycle details, scheduler semantics, and policy tuning, see
[Operations — Workflow Orchestration](operations.md#workflow-orchestration).

---

## Workspace Lifecycle Observability

Background cleanup and lifecycle operations emit log entries from
`WorkspaceCleanupService` and `WorkspaceLifecycleService`.

**WorkspaceCleanupService** (periodic sweep):

| Level | Signal |
|-------|--------|
| Information | Service started/stopped, cleanup disabled, sweep results (expired/deleted/artifact counts) |
| Debug | Sweep started |
| Warning | Partial error during cleanup (individual workspace failure) |
| Error | Sweep failed |

**WorkspaceLifecycleService** (workspace and artifact operations):

| Level | Signal |
|-------|--------|
| Information | Workspace created, workspace expired, workspace deleted, artifact promoted |
| Warning | Artifact addition rejected (wrong state or expired), promotion source transition failed, cleanup skipped (orphan risk) |
| Error | Cleanup error for individual workspace, promotion rollback failed (duplicate artifact may exist) |

Monitor for these entries to detect cleanup failures, quota-related
rejections, and promotion errors. For configuration and retention
details, see [Operations — Workspace Lifecycle](operations.md#workspace-lifecycle).

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
