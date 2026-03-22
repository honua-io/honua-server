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
# Instance-local deploy readiness
curl -H "X-API-Key: <admin-key>" \
  https://<host>/api/v1/admin/deploy/preflight

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
BASE_URL=https://<host> ADMIN_API_KEY=<admin-key> ./scripts/post-deployment-verification.sh
```

If the deployment uses OIDC rather than API keys, pass a full auth header instead:

```bash
BASE_URL=https://<host> \
  ADMIN_AUTH_HEADER="Authorization: Bearer <token>" \
  ./scripts/post-deployment-verification.sh
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
# Review the rendered upgrade
helm upgrade --install honua ./infrastructure/helm/honua \
  --namespace honua \
  --dry-run

# Apply the upgrade
helm upgrade --install honua ./infrastructure/helm/honua \
  --namespace honua

# Watch rollout progress
kubectl rollout status deployment/honua-server --namespace honua --timeout=600s
```

Optional scripted path:

```bash
ADMIN_API_KEY=<admin-key> ./scripts/deploy-rolling.sh ghcr.io/honua-io/honua-server:<tag>
```

After rollout:
- verify `/healthz/live` and `/healthz/ready`
- check `/api/v1/admin/deploy/preflight`
- run `./scripts/post-deployment-verification.sh`

`deploy-rolling.sh` port-forwards the upgraded deployment and runs the same verification suite against the instance-local control-plane APIs. Use `ADMIN_AUTH_HEADER` instead of `ADMIN_API_KEY` when Bearer auth is enabled.

For a validated canary rehearsal, use the scale-test Nginx edge:

```bash
./scripts/scale-test.sh --test canary
```

That rehearsal:
- starts a dedicated `honua_canary` slice
- routes a weighted subset of traffic through Nginx
- supports a forced canary header (`X-Honua-Canary: always`) for targeted verification
- triggers rollback automatically when the canary lane degrades
- can use a configured Prometheus-compatible telemetry connection as the real rollback signal source in non-local environments

`./scripts/deploy-canary.sh` remains a cluster-specific helper for environments that already have an external traffic-splitting substrate, but the scale-test path above is the canary flow validated in-repo for `#388`.

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

## Rollback Procedure

### Application Rollback First

Use application rollback whenever:
- the new version fails readiness or health checks
- latency or error rate regresses after rollout
- migrations were additive and the previous version can still run against the expanded schema

Kubernetes rollback:

```bash
ADMIN_API_KEY=<admin-key> ./scripts/rollback-deployment.sh

# Or target a specific revision
ADMIN_API_KEY=<admin-key> ./scripts/rollback-deployment.sh <revision>
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
./scripts/scale-test.sh --test rollback
./scripts/scale-test.sh --test canary
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
