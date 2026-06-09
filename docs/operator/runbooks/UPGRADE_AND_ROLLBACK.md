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
| `target.resource_id` | Optional | Lambda function ARN. Used to derive the AWS region only; the function name is **not** parsed from this ARN. Aliases: `lambda.function_arn`, `lambda.alias_arn`. |
| `aws.lambda.function_name` / `lambda.function_name` / `lambda.alias_function_name` | Optional | Explicit Lambda function name. Falls back to `TargetName` when omitted, so an ARN-only configuration must set `TargetName` to the function name. |
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

## AWS ECS + ALB Canary Rollout

AWS ECS deploys behind an Application Load Balancer use ALB listener-rule weights to shift traffic between a stable and a canary ECS service, managed through the deploy controller backend `honua-aws-ecs-alb`. The backend manages two-target-group canary traffic shifting end-to-end without requiring an external GitOps agent. It coexists with the GitOps passthrough backend `honua-gitops-aws-ecs`; targets pick by `Backend` name.

### Resource pre-conditions

The operator (typically Terraform) must provision **before** submitting any deploy operation:

- **Stable ECS service** registered to a stable ALB target group.
- **Canary ECS service** registered to a canary ALB target group. The two services share the same load balancer.
- **ALB listener rule** whose first action is `Type=forward` with both target groups attached as `TargetGroupTuple` entries with integer weights. The backend mutates the weights of this existing rule; it does not create or delete rules.
- **IAM credentials** that grant the server `ecs:DescribeServices`, `ecs:UpdateService`, `elasticloadbalancing:DescribeRules`, and `elasticloadbalancing:ModifyRule` against the listed ARNs.

The backend reads and writes only the listener rule and the canary ECS service; the stable ECS service is read-only from its perspective. Rotate the stable service through your CI/CD pipeline once a promotion is confirmed.

### Lifecycle

1. **Preflight**: `PlanAsync` validates that the cluster, canary service, listener rule ARN, both target group ARNs, and a non-empty `desiredRevision` (canary task definition ARN) are present. When `deployment.canary_weight_percentage` is set, it requires `telemetry.connection` so the rollout can be promoted or rolled back automatically.
2. **Start (immediate cutover)**: Without canary configuration, the controller calls `UpdateService` on the canary ECS service to register the new task definition, then sets the listener rule to `canary=100, stable=0`. The listener rule's existing forward-action stickiness configuration and any sibling actions (for example authenticate-cognito) are preserved — only the target group weights change. Operators typically retire the stable service post-promotion.
3. **Start (canary)**: With `deployment.canary_weight_percentage` set, the controller registers the new task definition on the canary service and sets the listener rule to `canary=N, stable=100-N` where N is the configured percentage.
4. **Observe**: Each reconciliation reads the listener-rule weights (`DescribeRules`) and the canary ECS service state (`DescribeServices`). The observation only reports `PromotionRecommended=true` (and only returns `Succeeded` at 100% canary weight) once **all** of the following are true:
    - **Listener-rule weights normalize**: the canary target group weight equals the configured target percentage, the stable target group weight equals 100 minus that percentage, and no other target group on the rule carries a non-zero weight. An unnormalized rule (for example `canary=100/stable=100` or a stray third target group) keeps the operation in `Reconciling` because relative weights would still split traffic.
    - **PRIMARY deployment matches**: the ECS service's `PRIMARY` deployment is on `desiredRevision`, has `RolloutState=COMPLETED` (where ECS reports it), and is at deployment-level steady state (`RunningCount >= DesiredCount`, `PendingCount == 0`).
    - **No old ACTIVE deployment is still serving**: any `ACTIVE` deployment from a previous task definition has drained to zero running tasks. ECS aggregate counts can satisfy `RunningCount >= DesiredCount` while a previous deployment is still draining; deployment-level state is the source of truth.
    
    If any of those conditions is not met, the operation stays `Reconciling` and the message describes the specific gate that is still pending.
5. **Promote**: After the telemetry gate passes, the controller sets the listener rule to `canary=100, stable=0` (preserving stickiness and sibling actions as in step 2). Returns `Succeeded` with the desired task definition arn.
6. **Rollback**: The controller sets the listener rule to `canary=0, stable=100` and reports `RollbackRequested`. Subsequent observations return `RolledBack` once the listener rule weights are at `stable=100/canary=0` and the canary ECS deployment has settled (`PendingCount == 0` or the service is `INACTIVE`). Warm canary tasks are expected to remain running for the next rollout — no traffic flows to them once the listener rule shifts.

### Configuration (immediate cutover)

```json
{
  "ControlPlane": {
    "DeployTargets": [
      {
        "TargetId": "prod-ecs",
        "TargetKind": "AwsEcs",
        "Backend": "honua-aws-ecs-alb",
        "Environment": "production",
        "TargetName": "honua-prod-canary",
        "Parameters": {
          "aws.region": "us-east-1",
          "aws.ecs.cluster": "honua-prod-ecs",
          "aws.ecs.canary_service": "honua-prod-canary",
          "aws.alb.listener_rule_arn": "arn:aws:elasticloadbalancing:us-east-1:<acct>:listener-rule/app/honua-prod/<lb-id>/<listener-id>/<rule-id>",
          "aws.alb.canary_target_group_arn": "arn:aws:elasticloadbalancing:us-east-1:<acct>:targetgroup/honua-canary/<id>",
          "aws.alb.stable_target_group_arn": "arn:aws:elasticloadbalancing:us-east-1:<acct>:targetgroup/honua-stable/<id>"
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
        "TargetId": "prod-ecs",
        "TargetKind": "AwsEcs",
        "Backend": "honua-aws-ecs-alb",
        "Environment": "production",
        "TargetName": "honua-prod-canary",
        "Parameters": {
          "aws.region": "us-east-1",
          "aws.ecs.cluster": "honua-prod-ecs",
          "aws.ecs.canary_service": "honua-prod-canary",
          "aws.alb.listener_rule_arn": "arn:aws:elasticloadbalancing:us-east-1:<acct>:listener-rule/app/honua-prod/<lb-id>/<listener-id>/<rule-id>",
          "aws.alb.canary_target_group_arn": "arn:aws:elasticloadbalancing:us-east-1:<acct>:targetgroup/honua-canary/<id>",
          "aws.alb.stable_target_group_arn": "arn:aws:elasticloadbalancing:us-east-1:<acct>:targetgroup/honua-stable/<id>",
          "deployment.canary_weight_percentage": "10",
          "telemetry.connection": "prod-prom",
          "telemetry.policy": "aws-alb-canary"
        }
      }
    ]
  }
}
```

`desiredRevision` on each deploy operation must be the ARN of the published task definition revision (e.g. `arn:aws:ecs:us-east-1:<acct>:task-definition/honua-app:42`). The CI/CD pipeline is responsible for registering the task definition before submitting the operation.

### Health-gate behavior

The deploy controller's telemetry evaluator selects the `aws-alb-canary` Prometheus preset for `AwsEcs` targets when `telemetry.policy` is omitted and any of `aws.ecs.canary_weight_percentage`, `deployment.canary_weight_percentage`, `telemetry.prometheus.canary_selector`, or `telemetry.prometheus.canary_job` is set, or when `telemetry.policy` is explicitly set to `aws-alb-canary`. The preset evaluates the canary's HTTP traffic against:

- **Error rate threshold**: 5%
- **P95 latency threshold**: 2000ms
- **Warmup duration**: 3 minutes (canary service rolling-update settling time)
- **Minimum sample count**: 10 requests (lower volume canary)

Configure the canary's Prometheus selector through `telemetry.prometheus.canary_selector` or `telemetry.prometheus.canary_job` so the gate scopes to canary-only traffic.

### Limitations

- The backend mutates listener-rule weights in place. It performs a `DescribeRules` + `ModifyRule` round-trip so the existing forward action's stickiness configuration, action ordering, and sibling action types (for example authenticate-cognito chained before the forward) are preserved across weight shifts. A rule with no forward action returns a sanitised state-lookup error.
- The canary ECS service must be pre-existing and pre-attached to the canary target group; the backend does not create services or target groups.
- The stable ECS service task definition is not changed by this backend. Operators promote the stable service through their normal CI/CD path after a `Succeeded` observation.
- `SupportsCancellation` is `false`; in-flight deploys are settled by promotion or rollback, not cancellation.
- `desiredRevision` must be the full task-definition ARN that ECS will return on subsequent `DescribeServices` calls; the convergence check uses an exact-match comparison so a `family:revision` shorthand will hold the operation in `Reconciling` indefinitely.
- The listener rule must contain only the configured canary and stable target groups in its weighted forward action. A third target group with a non-zero weight is treated as a contract violation and keeps the operation in `Reconciling`; zero-weighted strays are tolerated.
- Promotion and success require the ECS service to expose a `PRIMARY` deployment for the desired task definition. Services that do not return per-deployment state (for example when ECS hides `Deployments` for some service shapes) fall back to aggregate counts plus the service-level task-definition match — that is best-effort and operators should prefer rolling-update services so deployment-level signal is available.

### Manual intervention scenarios

- **Listener rule has no forward action**: The runtime returns a sanitised `Failed` observation; the structured log carries the underlying AWS error. Inspect the rule with `aws elbv2 describe-rules --rule-arns <arn>` and reattach a forward action with both target groups before retrying.
- **Canary task definition fails to roll**: ECS rolling update keeps `PendingCount > 0` or `RunningCount < DesiredCount`, or the `PRIMARY` deployment reports `RolloutState=IN_PROGRESS` or `FAILED`. The reconciler keeps the operation in `Reconciling` until the service converges; investigate ECS service events with `aws ecs describe-services --cluster <cluster> --services <service>`. The observation message echoes the rollout state and any reason ECS supplies.
- **Old ACTIVE deployment lingering**: An `ACTIVE` deployment from a previous task definition is still serving running tasks. The reconciler treats this as not-yet-converged because traffic could still land on the previous revision. Allow ECS to drain the old deployment, or correct the service out-of-band, before retrying.
- **External task-definition rollback**: If something else (a manual `UpdateService`, a CI/CD job, an operator running `aws ecs deploy`) reverts the canary service to a previous task definition while the rollout is in flight, the next observation reports the mismatch and stays in `Reconciling` rather than promoting or declaring `Succeeded`. Re-run the deploy workflow with the desired revision or correct the service out-of-band before retrying.
- **Listener-rule weights drifted**: Manual edits, an external GitOps controller, or an unrelated automation can leave the listener rule with weights that do not normalize to `canary + stable = 100` or with a third target group at non-zero weight. The reconciler keeps the operation in `Reconciling`; correct the rule or run `RollbackAsync` to drive the weights back to the controller's contract.
- **AWS submission errors**: Submission, observation, promotion, and rollback paths sanitise both AWS service exceptions (ECS / ALB API errors) and AWS SDK client-side failures (credential resolution, profile lookup, instance metadata, network errors before reaching AWS). Operators see a stable `ECS state lookup failed…`, `ALB state lookup failed…`, or generic `AWS state lookup failed…` message on the operation record; the underlying AWS error (request id, ARNs, account hints) is in the structured log.

### GitOps passthrough alternative

Operators who manage ECS through an external GitOps controller can use the `honua-gitops-aws-ecs` backend instead. That backend delegates state observation to the external controller and returns `ManualInterventionRequired` for observation.

### Live validation after Terraform apply

The repository ships gated live-validation tests in `tests/dotnet/Honua.Server.Tests/Features/Infrastructure/ControlPlane/AwsEcsAlbDeployBackendLiveTests.cs`. They exercise the real `AwsSdkAlbClient` and `AwsSdkEcsClient` against an environment provisioned by Terraform. Set the following environment variables before running `dotnet test --filter FullyQualifiedName~AwsEcsAlbDeployBackendLiveTests`:

| Variable | Required | Notes |
|---|---|---|
| `HONUA_LIVE_ECS_ALB_CLUSTER` | yes | ECS cluster name or ARN |
| `HONUA_LIVE_ECS_ALB_CANARY_SERVICE` | yes | Canary ECS service name |
| `HONUA_LIVE_ECS_ALB_LISTENER_RULE_ARN` | yes | ALB listener rule with weighted forward action |
| `HONUA_LIVE_ECS_ALB_CANARY_TARGET_GROUP_ARN` | yes | Target group attached to the canary service |
| `HONUA_LIVE_ECS_ALB_STABLE_TARGET_GROUP_ARN` | yes | Target group attached to the stable service |
| `HONUA_LIVE_ECS_ALB_TASK_DEFINITION_ARN` | yes | Published task definition ARN to roll out |
| `HONUA_LIVE_ECS_ALB_REGION` | no | AWS region; SDK credential-chain default used otherwise |
| `HONUA_LIVE_ECS_ALB_TELEMETRY_CONNECTION` | no | Prometheus connection id (enables the canary plan test) |

Tests skip automatically when any required variable is missing. AWS credentials are resolved through the standard AWS SDK credential chain.

---

## Kubernetes Argo Rollouts Canary

Kubernetes deploys use an external [Argo Rollouts](https://argo-rollouts.readthedocs.io/) controller for weighted canary traffic shifting and automatic rollback, managed through the deploy controller backend `honua-kubernetes-argo-rollouts`. Honua initiates the rollout (sets the canary image), observes the controller-reported rollout status, and drives promotion or rollback through the shared telemetry-gated reconciler — the operation lifecycle reflects real rollout state instead of `ManualInterventionRequired`. The stepped canary ramp itself (`5→25→50→100`, pauses, analysis) is owned by the `Rollout` resource's `spec.strategy.canary.steps` on the cluster. This backend coexists with the GitOps passthrough backend `honua-gitops-kubernetes`; targets pick by `Backend` name. The decision is recorded in [ADR-0052](../../contributor/adr/0052-kubernetes-closed-loop-promotion-via-argo-rollouts.md).

### Resource pre-conditions

The operator (typically Helm/Terraform) must provision **before** submitting any deploy operation:

- **Argo Rollouts controller** installed in the cluster.
- A **`Rollout` custom resource** (`argoproj.io/v1alpha1`) for the workload — not a plain `Deployment` — with its `spec.strategy.canary.steps` defining the weighted ramp and any analysis templates. The container Honua shifts must be named in `kubernetes.argo.container_name`.
- **Kubernetes API access** for the Honua server: in-cluster service-account RBAC (or an out-of-cluster `ControlPlane:Kubernetes:ApiServerUrl` + bearer token / CA bundle) granting `get` and `patch` on `rollouts` and `rollouts/status` in the target namespace. Honua reuses the same auth/CA path as the Kubernetes Job batch backend (`ControlPlane:Kubernetes:*`).

Honua reads and writes only the named `Rollout`; the controller owns ReplicaSet management, traffic splitting, and analysis.

### Lifecycle

1. **Preflight**: `PlanAsync` validates that the namespace, rollout name, container name, and a non-empty `desiredRevision` (container image) are present. When a canary weight is set (`deployment.canary_weight_percentage` or `kubernetes.argo.canary_weight_percentage`), it requires `telemetry.connection` so the rollout can be promoted or rolled back automatically.
2. **Start**: The controller backend reads the current pod-template image (captured for rollback baseline), then strategic-merge-patches `spec.template.spec.containers[name].image` to `desiredRevision`. Argo Rollouts begins the configured canary steps. The patch is keyed by container name so sibling containers and pod-template fields are preserved.
3. **Observe**: Each reconciliation reads the `Rollout` `.status`. The observation maps controller state onto the operation lifecycle:
    - **Paused at the configured canary weight** (`status.canary.weights.canary.weight` equals the target percentage): `Reconciling` with `PromotionRecommended=true`. The reconciler runs the telemetry gate and, on pass, promotes.
    - **Healthy and fully promoted** (`status.currentPodHash == status.stableRS`): `Succeeded`.
    - **Healthy but mid-ramp** (`currentPodHash != stableRS`): stays `Reconciling`.
    - **Degraded or aborted** (`status.phase == Degraded` or `status.abort == true`): `Reconciling` with `RollbackRecommended=true`, which drives automatic rollback.
    - **Rollout resource not found**: `Failed` (provision the `Rollout` before deploying).
4. **Promote**: After the telemetry gate passes, the controller backend clears the rollout's pause conditions on the `status` subresource and unsets `spec.paused` (mirrors `kubectl-argo-rollouts promote`). Argo advances to the next step or to 100%. The operation stays `Reconciling` until a later observation reports `Healthy` + stable, then `Succeeded`.
5. **Rollback**: The controller backend sets `status.abort=true` (mirrors `kubectl-argo-rollouts abort`) and reports `RollbackRequested`. Subsequent observations return `RolledBack` once the controller has reverted to the stable revision (`Healthy`, `currentPodHash == stableRS`).

### Configuration (canary traffic shifting)

```json
{
  "ControlPlane": {
    "Kubernetes": {
      "InClusterAutoDetect": true
    },
    "DeployTargets": [
      {
        "TargetId": "prod-k8s",
        "TargetKind": "Kubernetes",
        "Backend": "honua-kubernetes-argo-rollouts",
        "Environment": "production",
        "TargetName": "honua-server",
        "Parameters": {
          "kubernetes.namespace": "honua-prod",
          "kubernetes.argo.rollout_name": "honua-server",
          "kubernetes.argo.container_name": "honua",
          "deployment.canary_weight_percentage": "25",
          "telemetry.connection": "prod-prom"
        }
      }
    ]
  }
}
```

`desiredRevision` on each deploy operation must be the container image reference to roll out (for example `ghcr.io/honua/honua-server:sha-42`). Omit `deployment.canary_weight_percentage` for a rollout that pauses once for manual/telemetry-gated promotion without a weighted ramp.

### Limitations

- The workload must be modeled as an Argo `Rollout` resource; a plain `Deployment` is not driven by this backend.
- The stepped canary ramp and analysis templates are authored cluster-side on the `Rollout`. Honua observes the step weight and gates promotion/rollback; it does not define the steps.
- `SupportsCancellation` is `false`; in-flight rollouts are settled by promotion or rollback (abort), not cancellation.
- When the controller does not report both `currentPodHash` and `stableRS` (older Argo versions), a `Healthy` phase is treated as fully converged so the operation still terminates.

### Manual intervention scenarios

- **Rollout not found**: `StartAsync`/`ObserveAsync` return a `Failed` observation naming the missing rollout/namespace. Provision the `Rollout` resource (and the Argo Rollouts controller) before retrying.
- **Kubernetes API errors**: submission, observation, promotion, and rollback paths sanitise Kubernetes/Argo API errors. Operators see a stable `Argo Rollouts state lookup failed…` message on the operation record; the underlying error (resource names, namespaces, request detail) is in the structured log.
- **Controller stalled mid-ramp**: if Argo holds the rollout `Progressing` without pausing or completing (for example a stuck analysis run), the operation stays `Reconciling`. Inspect with `kubectl argo rollouts get rollout <name> -n <ns>` and correct the rollout or analysis out-of-band.

### GitOps passthrough alternative

Operators who manage Kubernetes through a pure out-of-band GitOps hand-off (Flux/Argo CD, or Flagger) can use the `honua-gitops-kubernetes` backend instead. That backend delegates all state observation to the external controller and returns `ManualInterventionRequired` for observation.

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
