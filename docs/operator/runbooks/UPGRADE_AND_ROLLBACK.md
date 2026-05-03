# Upgrade and Rollback Runbook

Use this runbook for Honua application upgrades. The goal is to keep deployments boring: preflight first, roll forward with backward-compatible schema changes, and roll back the app before considering database restore.

---

## Core Policy

1. Zero-downtime upgrades are supported only for backward-compatible migrations.
2. The default recovery strategy is to roll back the application deployment first.
3. Database restore is the last resort and should be used only when a destructive migration or data corruption makes the previous application version unusable.

---

## Preflight Checklist

Run these checks before any production rollout:

```bash
# Instance-local deploy readiness (includes database compatibility, migration state)
curl -H "X-API-Key: <admin-key>" \
  "https://<host>/api/v1/admin/deploy/preflight?includeDiagnostics=true"

# Migration visibility
curl -H "X-API-Key: <admin-key>" \
  https://<host>/api/v1/admin/observability/migrations

# Baseline health
curl -f https://<host>/healthz/live
curl -f https://<host>/healthz/ready
```

Verify:
- `readyForCoordinatedDeploy=true`
- no unexpected pending migrations
- backups or snapshots are current
- the target release uses expand-contract schema changes

If a migration contains an explicit compatibility-review marker, treat it as a gated rollout and document the rollback path before proceeding.

---

## Docker or Single-Instance Upgrade

Single-instance deployments are not zero-downtime. Use a maintenance window or shift traffic before restarting the container.

1. Pull the target image.
2. Run preflight checks against the current instance.
3. Stop the old container and start the new one with the same configuration and database.
4. Wait for `/healthz/ready` to return `200`.
5. Run post-deploy verification:

```bash
BASE_URL=https://<host> ADMIN_API_KEY=<admin-key> ./scripts/cloud/post-deployment-verification.sh
```

If the deployment uses OIDC rather than API keys, pass a full auth header instead:

```bash
BASE_URL=https://<host> \
  ADMIN_AUTH_HEADER="Authorization: Bearer <token>" \
  ./scripts/cloud/post-deployment-verification.sh
```

If the new container does not become ready, restart the previous image immediately.

---

## Helm and Kubernetes Upgrade

Production Kubernetes rollouts should use the chart defaults added for `#388`:
- rolling update strategy
- `minReadySeconds`
- optional `PodDisruptionBudget`
- readiness gates on migration completion

Recommended flow:

```bash
# Use the chart from the separate honua-helm repository:
# https://github.com/honua-io/honua-helm
#
# Review the rendered upgrade
helm upgrade --install honua <chart-from-honua-helm> \
  --namespace honua \
  --dry-run

# Apply the upgrade
helm upgrade --install honua <chart-from-honua-helm> \
  --namespace honua

# Watch rollout progress
kubectl rollout status deployment/honua-server --namespace honua --timeout=600s
```

Optional scripted path:

```bash
ADMIN_API_KEY=<admin-key> ./scripts/cloud/deploy-rolling.sh ghcr.io/honua-io/honua-server:<tag>
```

After rollout:
- verify `/healthz/live` and `/healthz/ready`
- check `/api/v1/admin/deploy/preflight`
- run `./scripts/cloud/post-deployment-verification.sh`

`deploy-rolling.sh` port-forwards the upgraded deployment and runs the same verification suite against the instance-local control-plane APIs. Use `ADMIN_AUTH_HEADER` instead of `ADMIN_API_KEY` when Bearer auth is enabled.

For a validated canary rehearsal, use the scale-test Nginx edge:

```bash
./scripts/scale/scale-test.sh --test canary
```

That rehearsal:
- starts a dedicated `honua_canary` slice
- routes a weighted subset of traffic through Nginx
- supports a forced canary header (`X-Honua-Canary: always`) for targeted verification
- triggers rollback automatically when the canary lane degrades
- can use a configured Prometheus-compatible telemetry connection as the real rollback signal source in non-local environments

`./scripts/cloud/deploy-canary.sh` remains a cluster-specific helper for environments that already have an external traffic-splitting substrate, but the scale-test path above is the canary flow validated in-repo for `#388`.

---

## Azure Functions Slot Swap Rollout

Azure Functions deploys use atomic deployment slot swapping managed through the deploy controller backend `honua-gitops-azure-functions`.

### Lifecycle

1. **Preflight**: Terraform provisions the staging slot with the desired image. The deploy controller validates production and slot images match expected topology.
2. **Swap**: The controller calls the ARM API to swap the staging slot with production. This is an atomic operation — there is no canary traffic splitting.
3. **Health gate**: After the swap completes, the telemetry evaluator checks error rate and p95 latency against the `honua-http` policy preset. If signals breach thresholds, the reconciler triggers a reverse swap via `RollbackAsync`.
4. **Rollback**: A reverse slot swap restores the previous production image.

### Configuration

```json
{
  "ControlPlane": {
    "DeployTargets": [
      {
        "TargetId": "prod-functions",
        "TargetKind": "AzureFunctions",
        "Backend": "honua-gitops-azure-functions",
        "Environment": "production",
        "TargetName": "honua-prod-functions",
        "Parameters": {
          "target.resource_id": "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Web/sites/<app>",
          "functions.current_image": "ghcr.io/honua-io/honua-server:current",
          "functions.desired_image": "ghcr.io/honua-io/honua-server:next",
          "telemetry.connection": "prod-prom"
        }
      }
    ]
  }
}
```

### Health-gate behavior

When `telemetry.connection` is set, the deploy controller evaluates the `honua-http` telemetry preset after the swap completes:
- **Error rate threshold**: 5% (5xx / total requests over 5-minute window)
- **P95 latency threshold**: 2000ms
- **Warmup duration**: 2 minutes before evaluation begins
- **Minimum sample count**: 20 requests

If any threshold is breached, the controller initiates an automatic reverse slot swap.

---

## Azure Container Apps Revision Traffic Rollout

Azure Container Apps deploys use revision-based traffic splitting managed through the deploy controller backend `honua-azure-container-apps-revision`. This backend supports both immediate cutover and canary traffic shifting.

### Lifecycle

1. **Preflight**: CI/CD pipeline creates the new revision in the Container App. The deploy controller validates subscription, resource group, and app name.
2. **Start (immediate cutover)**: Without canary configuration, the controller sets 100% traffic to the desired revision immediately.
3. **Start (canary)**: With `canary_weight_percentage` set, the controller activates the desired revision (if inactive), then splits traffic between the current primary revision and the canary revision.
4. **Observe**: The reconciler polls traffic weights. For canary deploys, it reports `PromotionRecommended` when the canary split matches the target percentage.
5. **Promote**: After telemetry gates pass, the controller sets 100% traffic to the desired revision.
6. **Rollback**: The controller sets 100% traffic back to the original primary revision. If no original revision was captured, it reports `ManualInterventionRequired`.

### Configuration (immediate cutover)

```json
{
  "ControlPlane": {
    "DeployTargets": [
      {
        "TargetId": "prod-aca",
        "TargetKind": "AzureContainerApps",
        "Backend": "honua-azure-container-apps-revision",
        "Environment": "production",
        "TargetName": "honua-prod-aca",
        "Parameters": {
          "target.resource_id": "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.App/containerApps/<app>",
          "telemetry.connection": "prod-prom"
        }
      }
    ]
  }
}
```

### Configuration (canary traffic shifting)

```json
{
  "ControlPlane": {
    "DeployTargets": [
      {
        "TargetId": "prod-aca",
        "TargetKind": "AzureContainerApps",
        "Backend": "honua-azure-container-apps-revision",
        "Environment": "production",
        "TargetName": "honua-prod-aca",
        "Parameters": {
          "target.resource_id": "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.App/containerApps/<app>",
          "containerapp.canary_weight_percentage": "10",
          "telemetry.connection": "prod-prom",
          "telemetry.policy": "azure-aca-canary"
        }
      }
    ]
  }
}
```

### Health-gate behavior

**Default (`honua-http` preset):** Used for immediate cutover deploys. Same thresholds as Azure Functions (5% error rate, 2000ms p95 latency, 2-minute warmup, 20 minimum samples).

**Canary (`azure-aca-canary` preset):** Used when `telemetry.policy: "azure-aca-canary"` is set. Evaluates the canary revision's traffic against:
- **Error rate threshold**: 5%
- **P95 latency threshold**: 2000ms
- **Warmup duration**: 3 minutes (longer to allow canary revision to stabilize)
- **Minimum sample count**: 10 requests (lower due to reduced canary traffic volume)

### Manual intervention scenarios

- **Missing current revision**: If the controller did not capture the original primary revision during `StartAsync`, rollback returns `ManualInterventionRequired`. Operator must manually set traffic weights through the Azure portal or CLI.
- **Revision not found**: If the desired revision does not exist in the Container App when `StartAsync` runs, the ARM API will return an error. Ensure the CI/CD pipeline has created the revision before submitting the deploy operation.

### GitOps passthrough alternative

Operators who manage ACA through an external GitOps controller (Flux/ArgoCD) can use the `honua-gitops-azure-container-apps` backend instead. This backend delegates all state observation to the external controller and returns `ManualInterventionRequired` for observation.

---

## AWS Lambda Alias and Version Rollout

AWS Lambda deploys use alias-based weighted traffic shifting between published numeric function versions, managed through the deploy controller backend `honua-gitops-aws-lambda`. The backend supports both immediate cutover (alias re-pointing) and canary traffic shifting via `RoutingConfig.AdditionalVersionWeights`.

### Lifecycle

1. **Preflight**: CI/CD pipeline publishes a new Lambda function version (e.g. `42`). The deploy controller validates that `desiredRevision` is a positive numeric version (not `$LATEST`), the alias name resolves, and the function name resolves. When `lambda.canary_weight_percentage` is set, `telemetry.connection` is also required.
2. **Start (immediate cutover)**: Without canary configuration, the controller calls `UpdateAlias` with the desired version and no additional weights, moving 100% of traffic to the desired version.
3. **Start (canary)**: With `lambda.canary_weight_percentage` set, the controller holds the alias on the current stable version and routes the configured percentage of traffic to the desired version through `AdditionalVersionWeights`. The previous version is captured at submit time so rollback can target it.
4. **Observe**: The reconciler polls alias state. While the alias points at the stable version with the desired canary weight, observation reports `Reconciling` with `PromotionRecommended=true`. Once the alias points to the desired version with no weighted traffic, observation reports `Succeeded`.
5. **Promote**: After the telemetry gate passes, the controller calls `UpdateAlias` again with the desired version and clears the additional weights, completing the rollout.
6. **Rollback**: The controller re-points the alias to the previously captured `CurrentRevision` and clears any additional weights. If `CurrentRevision` was not captured, rollback returns `ManualInterventionRequired`.

### Weighted traffic state in the operation record

The `WorkflowOperationRecord.CurrentPhase` field reports the canary weight as a human-readable phrase, e.g. `Lambda alias 'live' is holding stable version '41' while routing 10% of traffic to canary version '42'.` Promotion, rollback, and failure phases all flow through the same field so operators see the full rollout history without consulting Lambda directly. The terminal `Succeeded` or `RolledBack` status, the `ObservedState` (alias's current version), and `ErrorMessage` (rollback or failure reason) round out the operation state model.

### Configuration (immediate cutover)

```json
{
  "ControlPlane": {
    "DeployTargets": [
      {
        "TargetId": "prod-lambda",
        "TargetKind": "AwsLambda",
        "Backend": "honua-gitops-aws-lambda",
        "Environment": "production",
        "TargetName": "honua-prod-lambda",
        "Parameters": {
          "target.resource_id": "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda",
          "lambda.alias_name": "live"
        }
      }
    ]
  }
}
```

### Configuration (canary traffic shifting with health gate)

```json
{
  "ControlPlane": {
    "DeployTargets": [
      {
        "TargetId": "prod-lambda",
        "TargetKind": "AwsLambda",
        "Backend": "honua-gitops-aws-lambda",
        "Environment": "production",
        "TargetName": "honua-prod-lambda",
        "Parameters": {
          "target.resource_id": "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda",
          "lambda.alias_name": "live",
          "lambda.canary_weight_percentage": "10",
          "telemetry.connection": "prod-prom",
          "telemetry.prometheus.canary_selector": "service=\"honua-prod-lambda\",version=\"canary\""
        }
      }
    ]
  }
}
```

### Parameter reference

| Parameter | Required? | Purpose |
|---|---|---|
| `target.resource_id` | One of the function-name keys is required | Lambda function ARN. Used to derive both function name and region. Aliases: `lambda.function_arn`, `lambda.alias_arn`. |
| `aws.lambda.function_name` / `lambda.function_name` | Required if `target.resource_id` does not encode the function name | Explicit function name. Falls back to `TargetName` when omitted. |
| `aws.lambda.alias_name` / `lambda.alias_name` / `lambda.alias` | Optional (default `live`) | Lambda alias managed by the rollout. |
| `aws.region` / `lambda.region` | Optional | Explicit AWS region. Derived from the resource ARN when omitted. |
| `aws.lambda.canary_weight_percentage` / `lambda.canary_weight_percentage` / `deployment.canary_weight_percentage` | Optional (1-99) | Enables weighted canary rollout. When set, `telemetry.connection` is required. |
| `telemetry.connection` | Required for canary rollouts | Reference to a `ControlPlane.TelemetryConnections` entry. |
| `telemetry.policy` | Optional | Override the default preset (e.g. force `aws-lambda-canary`). |
| `telemetry.prometheus.canary_selector` / `telemetry.prometheus.canary_job` | Optional | Selector or job label for the canary metric stream. Presence triggers the `aws-lambda-canary` preset by default. |
| `telemetry.error_rate.query` / `telemetry.latency_p95.query` / `telemetry.sample_count.query` | Optional | Override individual preset queries. Required when running event-driven Lambdas without HTTP metrics. |

### Health-gate behavior

**Default (`honua-http` preset)**: Used when no canary selector or canary job is configured. Same thresholds as Azure Functions (5% error rate, 2000ms p95 latency, 2-minute warmup, 20 minimum samples).

**Canary (`aws-lambda-canary` preset)**: Selected automatically when `telemetry.prometheus.canary_selector` or `telemetry.prometheus.canary_job` is set, or explicitly via `telemetry.policy: "aws-lambda-canary"`. Reuses the canary preset shape from `aws-alb-canary` and `azure-aca-canary`:
- **Error rate threshold**: 5% (5xx / total over 5-minute window)
- **P95 latency threshold**: 2000ms
- **Warmup duration**: 3 minutes (longer to allow the canary version to warm up after cold start)
- **Minimum sample count**: 10 requests (lower due to reduced canary traffic volume)

Telemetry breach drives the reconciler to call `RollbackAsync` on the Lambda backend, which re-points the alias to `CurrentRevision` and clears `AdditionalVersionWeights`. Telemetry warmup keeps the operation `Reconciling` without rolling back so the canary has time to accumulate samples.

### Canary rollout walkthrough

1. Submit a deploy operation with `desiredRevision="42"`, the previous live version observable as `CurrentRevision="41"`, and `lambda.canary_weight_percentage="10"`. The operation moves to `Submitted`.
2. The backend's `StartAsync` calls `UpdateAlias` with `FunctionVersion=41` and `AdditionalVersionWeights={"42":0.10}`. The operation phase reads "Lambda alias 'live' is routing 10% of traffic to published version '42'."
3. The reconciler polls `ObserveAsync`. While the alias still routes 10% to version 42, the observation returns `Reconciling` with `PromotionRecommended=true` so the controller can run the telemetry gate.
4. Telemetry warmup elapses. The evaluator returns no breach, no waiting. The reconciler calls `PromoteAsync`, which clears the weights and pins the alias to version 42. The operation transitions to `Succeeded`.
5. If telemetry detects degradation at any point, the reconciler calls `RollbackAsync` instead. The alias re-points to version 41 with no weights, and the operation transitions to `RollbackRequested` then `RolledBack` once the next `ObserveAsync` confirms the alias state.

### Manual intervention scenarios

- **Missing previous revision**: If the controller did not capture `CurrentRevision` when the canary started, `RollbackAsync` returns `ManualInterventionRequired`. Operator must manually re-point the alias through the AWS console or `aws lambda update-alias`.
- **Function version not published**: `desiredRevision` must be a positive numeric version published via `PublishVersion`. `$LATEST` and non-numeric values are rejected by `PlanAsync`.
- **Event-driven Lambdas**: The `aws-lambda-canary` preset queries Honua HTTP metrics. Lambdas that do not emit `honua_http_request_total` and `honua_http_request_duration_ms_bucket` should either omit the canary selector (skipping the preset and falling back to operator-supplied queries) or set explicit `telemetry.error_rate.query` / `telemetry.latency_p95.query` overrides.
- **Backend unavailable**: When `GetAlias` raises `ResourceNotFoundException`, the operation transitions to `Failed` with the SDK message preserved in `CurrentPhase` and `ErrorMessage`.

---

## Rollback Procedure

### Application Rollback First

Use application rollback whenever:
- the new version fails readiness or health checks
- latency or error rate regresses after rollout
- migrations were additive and the previous version can still run against the expanded schema

Kubernetes rollback:

```bash
ADMIN_API_KEY=<admin-key> ./scripts/cloud/rollback-deployment.sh

# Or target a specific revision
ADMIN_API_KEY=<admin-key> ./scripts/cloud/rollback-deployment.sh <revision>
```

`rollback-deployment.sh` verifies health and reruns post-deploy checks through a local port-forward before declaring recovery complete.

Helm-native rollback:

```bash
helm rollback honua <revision> --namespace honua
kubectl rollout status deployment/honua-server --namespace honua --timeout=600s
```

### When Database Restore Is Required

Only restore the database when one of these is true:
- a destructive migration already ran and the previous app version cannot operate on the new schema
- a migration corrupted or deleted required data
- the release introduced irreversible state changes outside the expand-contract window

If database restore is required:
1. Roll back the application first to stop further writes.
2. Restore the last known-good database snapshot.
3. Validate schema/version alignment before reintroducing traffic.

---

## Rehearsal and Validation

Use the scale-test environment to rehearse multi-instance behavior before production changes:

```bash
./scripts/scale/scale-test.sh --test rollback
./scripts/scale/scale-test.sh --test canary
```

This environment is the right place to validate:
- multi-instance readiness behavior
- distributed cache behavior during rollout
- rollback recovery after a failed rollout
- weighted Nginx canary routing plus automatic rollback on canary health degradation

For release rehearsal, capture:
- deploy preflight output before and after rollout
- readiness and metrics responses
- canary lane verification using `X-Honua-Canary: always`
- rollback steps used and time to recovery

---

## Exit Criteria After Upgrade

An upgrade is complete when:
- all intended replicas are ready
- deploy preflight reports the instance as ready
- no unexpected pending migrations remain
- post-deployment verification passes
- rollback steps are documented for the release in case of later regression
