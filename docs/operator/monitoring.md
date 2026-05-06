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
| `GET /api/v1/metrics/health` | Admin auth | Basic health summary; does not include license state in the #338 baseline |
| `GET /api/v1/metrics/performance` | Admin auth | Request latency and throughput |
| `GET /api/v1/metrics/database` | Admin auth | Connection pool and query stats |
| `GET /api/v1/metrics/cache` | Admin auth | Output cache hit/miss rates |
| `GET /api/v1/metrics/memory` | Admin auth | Memory usage and GC stats |
| `GET /healthz/metrics` | Admin auth | Lightweight health/performance snapshot, including active license state |

Admin-only diagnostics:

| Endpoint | What it shows |
|----------|---------------|
| `GET /api/v1/admin/observability/errors` | Recent error history |
| `GET /api/v1/admin/observability/telemetry` | Tracing status |
| `GET /api/v1/admin/performance/database/query-cache/statistics` | Prepared statement cache health |

Detailed operational diagnostics (also admin-authenticated):

| Endpoint | What it shows |
|----------|---------------|
| `GET /monitoring/health/production` | Combined health snapshot built from live request/cache/connection telemetry, including active license state |
| `GET /monitoring/health/comprehensive` | Sanitized ASP.NET health check report for critical dependencies |
| `GET /monitoring/metrics/connection-pool` | Connection pool utilization, failures, and timeouts |
| `GET /monitoring/metrics/cache` | Cache health summary with hit ratio |
| `GET /monitoring/metrics/resources` | Process memory and GC snapshot |
| `GET /monitoring/metrics/upload-queue` | Upload queue depth and utilization |
| `GET /monitoring/metrics/database-resilience` | Database resilience summary and active alerts |
| `GET /monitoring/alerts` | Current alert conditions derived from production thresholds |

---

## License Runtime Observability

The ticket #338 license loader publishes an in-memory snapshot at startup and
after successful admin upload. License state is visible through:

- `GET /api/v1/admin/license` and `GET /api/v1/admin/license/status`
- `GET /healthz/metrics` under `license`
- `GET /monitoring/health/production` under `license`
- runtime logs in the `10000`-`10009` event-id range

Validation states are `NoLicenseConfigured`, `Valid`, `MissingFile`,
`Malformed`, `UnknownKey`, `InvalidSignature`, and `Expired`. A missing
configured path, malformed file, unknown key, invalid signature, or expired file
falls back to Community mode without blocking startup.

The health summary intentionally exposes only operator-safe fields: edition,
validation state, validity, expiry, days until expiry, license id/licensee when
present, and active entitlement keys. `/healthz/metrics` serializes these as
snake_case (`validation_state`, `active_entitlements`); `/monitoring/health/production`
uses the monitoring API's camelCase policy (`validationState`,
`activeEntitlements`). Neither surface returns the license path, trusted keys,
signature, or raw payload.

| EventId | Level | Signal |
|---------|-------|--------|
| 10000 | Information | No license path configured; Community mode is active. |
| 10001 | Warning | Configured license file is missing. |
| 10002 | Warning | License file is malformed. |
| 10003 | Warning | License references an unknown signing key. |
| 10004 | Warning | Ed25519 signature validation failed. |
| 10005 | Warning | License is expired. |
| 10006 | Information | License loaded successfully. |
| 10007 | Warning | Admin upload was rejected. |
| 10008 | Error | Admin upload validated but could not be saved. |
| 10009 | Warning | Entitlement gate denied a paid feature request. |

Prometheus license counters are reserved by ADR-0033 but are not emitted by the
#338 runtime baseline.

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

## Execution Job Reconciler Observability

The `ExecutionJobReconciler` and its background service
(`ExecutionJobReconcilerBackgroundService`) reconcile active execution jobs
against pluggable `IBatchComputeBackend` adapters, bridging status and
progress into `IUniversalProgressStore`. The reconciler runs on every
Redis-enabled host and emits structured logs in the `9030-9036` event-id
band.

**ExecutionJobReconciler** (per-job reconciliation):

| EventId | Level | Signal |
|---------|-------|--------|
| 9030 | Debug | Execution job reconciled to new status and percent complete |
| 9031 | Warning | Execution-job reconciliation failed for a specific operation |
| 9033 | Debug | Reconciliation lease was lost; another node may continue |
| 9034 | Warning | No batch compute backend registered for the job's `(Backend, TargetKind)` |
| 9035 | Warning | Failed to bridge execution-job progress into `IUniversalProgressStore` |

**ExecutionJobReconcilerBackgroundService** (poll loop):

| EventId | Level | Signal |
|---------|-------|--------|
| 9032 | Warning | Reconciliation poll loop failed |
| 9036 | Information | Background service started |

**Recommended alerts:**

| Condition | Suggested threshold | Signal source |
|-----------|---------------------|---------------|
| Missing backend registrations | Any occurrence | `ExecutionJobReconciler` Warning (9034) |
| Sustained reconciliation failures | > 3 in 5 min | `ExecutionJobReconciler` Warning (9031) |
| Poll loop failures | Any occurrence | `ExecutionJobReconciler` Warning (9032) |
| Progress bridge failures | Sustained volume | `ExecutionJobReconciler` Warning (9035) |

For lifecycle details and backend configuration, see
[Operations — Job Orchestration](operations.md#job-orchestration).

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
| 8102 | WorkflowStepSubmitted | Debug | Step job submitted to the execution substrate |
| 8103 | WorkflowStepCompleted | Debug | Step reached a terminal state |
| 8104 | WorkflowStepRetrying | Information | Step failed and is being retried per its retry policy |
| 8105 | WorkflowStepSkipped | Information | Step was skipped because its dependency used a `Skip` failure policy |
| 8107 | InputBindingFailed | Warning | Artifact-to-input binding resolution failed for a step |
| 8108 | SchedulerTriggered | Information | Cron scheduler created a run for a scheduled workflow |
| 8110 | ReconciliationFailed | Warning | Reconciliation loop encountered an unhandled error |
| 8111 | PollLoopFailed | Warning | Reconciler background service poll loop failed |
| 8114 | SchedulerTickFailed | Warning | Scheduler background service tick failed |
| 8115 | WorkflowStepFailed | Warning | A workflow step failed (exhausted retries or no retry policy) |
| 8116 | SchedulerDefinitionInvalid | Warning | Scheduled workflow has an invalid cron expression or time zone |
| 8117 | WorkflowStepCancelJobFailed | Warning | Best-effort cascade cancel of a child job failed |
| 8118 | WorkflowStepObservationTransientFailure | Warning | Transient job-observation failure; step preserved for retry on next reconcile tick |
| 8119 | WorkflowCancelLeaseContention | Information | Cancel request could not acquire reconcile lease (409 returned) |
| 8120 | WorkflowStepArtifactsUnavailableForBoundDependents | Warning | Step artifact retrieval failed; bound dependents marked Failed |
| 8121 | DefinitionStepSetMismatch | Error | Definition step-set changed during active run; run failed deterministically |
| 8122 | ProgressProjectionFailed | Warning | Progress store write failed after authoritative run state was durable; progress view may be stale |

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
| Reconciliation failures | Any occurrence | `OrchestrationLog` Warning (8110) / Warning (8111) |
| Sustained step retries | > 3 retry events in 5 min | `OrchestrationLog` Information (8104) |
| Artifact binding failures | Any occurrence | `OrchestrationLog` Warning (8107) / Warning (8120) |
| Scheduler tick failures | Any occurrence | `OrchestrationLog` Warning (8114) |
| Scheduler definition invalid | Any occurrence | `OrchestrationLog` Warning (8116) |
| Workflow step failures | Sustained volume | `OrchestrationLog` Warning (8115) |
| Observation transport failures | Sustained volume | `OrchestrationLog` Warning (8118) |
| Cancel lease contention | > 2 in 5 min | `OrchestrationLog` Information (8119) |
| Definition step-set mismatch | Any occurrence | `OrchestrationLog` Error (8121) |
| Progress projection failures | Sustained volume | `OrchestrationLog` Warning (8122) |

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
- AWS Lambda canary preset: auto-selected for `AwsLambda` targets when a canary Prometheus selector or canary job is configured, or set explicitly with `telemetry.policy=aws-lambda-canary`. Reuses the canary preset shape (3-minute warmup, 10 minimum samples). Required when `lambda.canary_weight_percentage` is set; for event-driven Lambdas without HTTP metrics, override `telemetry.error_rate.query` / `telemetry.latency_p95.query` instead.
- Generic Honua HTTP preset: serverless and Azure Container Apps targets can use `telemetry.policy=honua-http` when they expose the standard Honua HTTP metrics without a distinct canary scrape lane. Azure Functions, AWS Lambda (without canary signal config), and Azure Container Apps immediate-cutover deploys default to this policy when `telemetry.connection` is set.

Useful target parameters:

- `telemetry.connection`: named telemetry connection from `ControlPlane:TelemetryConnections`
- `telemetry.policy`: optional preset, currently `kubernetes-honua-http`, `aws-alb-canary`, `aws-lambda-canary`, `azure-aca-canary`, or `honua-http`
- `telemetry.prometheus.job`: Prometheus `job` label for the stable or aggregated Honua scrape target
- `telemetry.prometheus.selector`: raw PromQL label selector fragment when `job=...` is not enough
- `telemetry.prometheus.canary_job`: Prometheus `job` label for the canary scrape target
- `telemetry.prometheus.canary_selector`: raw PromQL label selector fragment for canary-only metrics
- `telemetry.prometheus.extra_selector`: additional label matchers appended to the generated selector
- `lambda.canary_weight_percentage`: optional AWS Lambda alias canary percentage for deploy targets; requires `telemetry.connection` because Honua only promotes or rolls back the alias after telemetry settles
- `containerapp.canary_weight_percentage`: optional Azure Container Apps canary traffic percentage (1–99) for the `honua-azure-container-apps-revision` backend; requires `telemetry.connection`
- `aws.ecs.canary_weight_percentage`: optional AWS ECS canary traffic percentage (1–99) for the `honua-aws-ecs-alb` backend; requires `telemetry.connection`. Equivalent to the generic `deployment.canary_weight_percentage` key

Explicit query overrides still win:

- `telemetry.error_rate.query`
- `telemetry.latency_p95.query`
- `telemetry.sample_count.query`

Keep environment-specific `ControlPlane` target metadata in the infrastructure repository that owns deployment state. For Honua-managed environments, that now means `honua-terraform`. In this repository, document and maintain the shape of the config, but do not rely on a local Terraform-output renderer.

```json
{
  "ControlPlane": {
    "TelemetryConnections": [
      {
        "ConnectionId": "prometheus-default",
        "ConnectionType": "Prometheus",
        "BaseUrl": "https://prometheus.example.com"
      }
    ],
    "DeployTargets": [
      {
        "TargetId": "prod-honua",
        "TargetKind": "Kubernetes",
        "BackendName": "honua-gitops-kubernetes",
        "TelemetryConnectionId": "prometheus-default"
      }
    ]
  }
}
```

Populate provider-specific identity hints from your infrastructure source of truth, including:

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
- Import dashboard JSON from `docker/monitoring/grafana/dashboards/honua-overview.json`.
- Apply alert rules from `docker/monitoring/prometheus/alerts.yml`.
- Use `docs/operator/examples/production-monitoring.json` as a baseline app
  configuration for monitoring, resilience, and rate-limiting thresholds.
- Use `docs/operator/examples/prometheus-alerts.yml` as a broader standalone
  Prometheus ruleset reference when you are not using the bundled Docker
  example stack.

If you use Terraform for observability, use the separate `honua-terraform` repository.
