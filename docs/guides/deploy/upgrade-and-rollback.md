# Upgrade and roll back

You'll roll a new Honua version forward safely — preflight first, backward-compatible migrations, app rollback before database restore.

**Prerequisites:** Admin access (`X-API-Key` header), a current database backup ([Back up and restore](backup-and-restore.md)), and the target image tag.

## Policy

1. Zero-downtime upgrades are supported only for backward-compatible (expand-contract) migrations: add columns/tables first, deploy, drop old columns in a later release. Potentially breaking migrations carry an explicit `-- honua:compatibility-review` marker in the SQL — treat those as gated rollouts with a documented rollback path.
2. The default recovery is rolling back the application image; the previous version keeps working against the expanded schema.
3. Database restore is the last resort, only when a destructive migration or data corruption makes the previous version unusable.

## Steps

1. Preflight against the running instance. The deploy API reports database compatibility, migration state, and coordinated-deploy readiness.

```bash
HOST=https://honua.example.com
ADMIN_KEY=replace-with-admin-password
curl -s -H "X-API-Key: $ADMIN_KEY" "$HOST/api/v1/admin/deploy/preflight?includeDiagnostics=true"
curl -s -H "X-API-Key: $ADMIN_KEY" "$HOST/api/v1/admin/observability/migrations"
```

Proceed only when `readyForCoordinatedDeploy` is `true`, no unexpected migrations are pending, and backups are current.

2. Roll out the new image. Migrations run automatically when the new version starts (skip with `HONUA_SKIP_MIGRATIONS=true` if you run them out-of-band — mandatory on serverless).

```bash
# Kubernetes / Helm
helm upgrade --install honua "$CHART_PATH" --namespace honua --values honua-values.yaml
kubectl rollout status deployment/honua-server --namespace honua --timeout=600s
```

Single-instance Docker is not zero-downtime: pull the new tag, stop the old container, start the new one with the same env and database, and wait for `/healthz/ready`.

3. Verify the rollout.

```bash
BASE_URL=$HOST ADMIN_API_KEY=$ADMIN_KEY ./scripts/cloud/post-deployment-verification.sh
```

Use `ADMIN_AUTH_HEADER="Authorization: Bearer ..."` instead of `ADMIN_API_KEY` for OIDC deployments.

## Coordinated rollouts via the deploy API

For canary/gated rollouts, Honua's control plane drives the deployment through admin endpoints (all `X-API-Key`-authenticated):

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/admin/deploy/preflight` | Readiness, migration state, database compatibility |
| `POST /api/v1/admin/deploy/plan` | Plan a rollout against a configured deploy target |
| `POST /api/v1/admin/deploy/operations` | Create a deploy operation |
| `GET /api/v1/admin/deploy/operations/{operationId}` | Observe operation status and phase |
| `POST /api/v1/admin/deploy/operations/{operationId}/submit` | Start the rollout |
| `POST /api/v1/admin/deploy/operations/{operationId}/promote` | Force the cutover of a rollout parked awaiting promotion |
| `POST /api/v1/admin/deploy/operations/{operationId}/rollback` | Roll the operation back |

Deploy targets are configured under `ControlPlane__DeployTargets__*` with a backend per platform: `honua-kubernetes-argo-rollouts` (Argo Rollouts canary), `honua-aws-ecs-alb` (ALB weighted target groups), `honua-gitops-aws-lambda` (alias weights), `honua-azure-container-apps-revision` (revision traffic split), `honua-gitops-azure-functions` (slot swap), plus GitOps passthrough variants. When a target sets `telemetry.connection` (a `ControlPlane__TelemetryConnections` entry, Prometheus or CloudWatch), the reconciler gates promotion on error rate and p95 latency and triggers automatic rollback on breach. Keep environment-specific target metadata in your infrastructure-as-code repository (Honua's Terraform modules are available to customers through support).

### Synthetic health-probe gate

Beyond metric thresholds, a deploy target can declare a synthetic `/healthz/ready` probe so an *unhealthy* canary is a first-class rollback trigger during the bake window — inherited by every backend and change class, independent of the metrics provider. Set these parameters on the deploy target (alongside or instead of the metric queries):

| Parameter | Default | Purpose |
|---|---|---|
| `telemetry.healthz.url` | — | Absolute HTTPS URL of the canary health endpoint (e.g. `https://canary.example.com/healthz/ready`). Enables the gate. |
| `telemetry.healthz.failure_threshold` | `1` | Failing checks (within one scrape) that trigger rollback. |
| `telemetry.healthz.samples` | `3` | Sequential checks issued per scrape. |
| `telemetry.healthz.expected_status` | `200` | HTTP status a healthy check returns. |
| `telemetry.healthz.timeout_seconds` | `5` | Per-check timeout. |

A failing probe drives the **same** automatic-rollback path as an error-rate/latency breach and respects the anti-flap debounce (`telemetry.rollback.consecutive_breaches`). The probe URL is validated (HTTPS-only, no private/loopback destinations). A target may gate purely on health (`telemetry.healthz.url` with no metric queries) or combine the probe with the metric gate.

## Promotion requirements

A rollout does not auto-promote (cut over to the new revision) until its **promotion gate** is satisfied. The gate is chosen with the `deployment.promotion_gate` parameter and defaults by target kind. This is independent of the automatic-rollback signals above — rollback still fires on a telemetry breach or an unhealthy probe regardless of the promotion gate.

| Gate (`deployment.promotion_gate`) | Default for | What must pass to promote | Telemetry backend required? |
|---|---|---|---|
| `telemetry` | all cloud backends (ECS/ALB, Lambda, ACA, Functions, Argo Rollouts) | The configured metrics gate (`telemetry.connection` + error-rate/latency thresholds) must clear after warmup. | Yes — a reachable Prometheus/CloudWatch connection. |
| `health` | `honua-yarp-rolling` (self-hosted rolling, on-prem/air-gapped) | The backend's own health gate. The rolling backend health-probes the new standby replica locally and cuts over once it is healthy. A metrics gate is optional; if `telemetry.connection` is also set its rollback signals and waits still apply. | No. |
| `manual` | — (opt-in) | Nothing automatic. The rollout bakes and holds until an operator calls the promote endpoint. | No. |

Notes:

- **On-prem / air-gapped:** the self-hosted rolling backend defaults to the `health` gate, so a cutover needs **no** external telemetry backend — the standby replica's `/healthz/ready` is the gate. You do not need a cloud-style Prometheus for the promotion to complete.
- **Using a private/on-prem Prometheus for the telemetry gate:** the outbound URL guard rejects private/loopback endpoints by default (SSRF hardening). To point a telemetry connection at an on-prem Prometheus (for example `http://prometheus.internal:9090` or a `10.x`/`192.168.x` address), set `AllowPrivateNetworks: true` on that `ControlPlane__TelemetryConnections` entry. This is a per-connection opt-in; the default posture (HTTPS-only, no private destinations) is unchanged for every other connection. Only enable it for a trusted endpoint inside your own network.
- **Manual promotion (escape hatch):** `POST /api/v1/admin/deploy/operations/{operationId}/promote` forces the cutover on a rollout parked in `Reconciling` (or `Submitted`). It requires admin authorization, records the operator on the audit trail, and returns `409 Conflict` with a reason when the operation cannot be promoted (not yet submitted, already promoted, rolling back, or terminal). Use it when a gate never clears (for example a metrics connection is down) or when you deliberately run the `manual` gate.

## Rollback

Application rollback first — whenever readiness fails, errors or latency regress, and migrations were additive:

```bash
# Kubernetes helper (verifies health and reruns post-deploy checks)
ADMIN_API_KEY=$ADMIN_KEY ./scripts/cloud/rollback-deployment.sh

# Or Helm-native
helm rollback honua --namespace honua
kubectl rollout status deployment/honua-server --namespace honua --timeout=600s
```

For deploy-API-managed rollouts, `POST .../operations/{operationId}/rollback` drives the platform backend (re-point alias, shift ALB weights back, reverse slot swap). Single-instance Docker: restart the previous image tag.

Restore the database only when a destructive migration already ran or data was corrupted: roll the app back first to stop writes, restore the last known-good snapshot, validate schema/version alignment, then readmit traffic — see [Back up and restore](backup-and-restore.md).

## Verify

```bash
curl -s "$HOST/healthz/ready" && \
curl -s -H "X-API-Key: $ADMIN_KEY" "$HOST/api/v1/admin/deploy/preflight" | head -c 200
```

Expected: `Ready`, then a preflight payload with `readyForCoordinatedDeploy: true` and no pending migrations. Rehearse rollback/canary behavior before production with `./scripts/scale/scale-test.sh --test rollback` and `--test canary`.

## Troubleshoot

- **New pods never ready after upgrade** — check logs for migration failure; roll back the image (migrations that already applied are additive by policy, so the old version still runs).
- **Preflight reports pending migrations you didn't expect** — the image tag is newer than intended, or a prior out-of-band migration run was skipped; reconcile via `GET /api/v1/admin/observability/migrations` before proceeding.
- **Deploy operation stuck in `Reconciling`** — the platform hasn't converged (ECS draining, Argo mid-ramp) or telemetry hasn't accumulated minimum samples; check `GET .../operations/{operationId}` for the specific pending gate. If the promotion gate can never clear (a `telemetry`-gated target whose metrics connection is unreachable, or a `manual`-gated rollout), force the cutover with `POST .../operations/{operationId}/promote`. On-prem rolling targets promote on the health gate by default and need no telemetry backend — see [Promotion requirements](#promotion-requirements).
- **Rollback returns `ManualInterventionRequired`** — the backend never captured the previous revision; re-point the alias/weights/slot manually through the cloud console or CLI.

## Next steps

- [Back up and restore](backup-and-restore.md)
- [Monitor Honua Server](monitoring.md)
- [Deploy on AWS and Azure](cloud-deployments.md)
