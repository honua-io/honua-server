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
| `POST /api/v1/admin/deploy/operations/{operationId}/rollback` | Roll the operation back |

Deploy targets are configured under `ControlPlane__DeployTargets__*` with a backend per platform: `honua-kubernetes-argo-rollouts` (Argo Rollouts canary), `honua-aws-ecs-alb` (ALB weighted target groups), `honua-gitops-aws-lambda` (alias weights), `honua-azure-container-apps-revision` (revision traffic split), `honua-gitops-azure-functions` (slot swap), plus GitOps passthrough variants. When a target sets `telemetry.connection` (a `ControlPlane__TelemetryConnections` entry, Prometheus or CloudWatch), the reconciler gates promotion on error rate and p95 latency and triggers automatic rollback on breach. Keep environment-specific target metadata in [honua-terraform](https://github.com/honua-io/honua-terraform).

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
- **Deploy operation stuck in `Reconciling`** — the platform hasn't converged (ECS draining, Argo mid-ramp) or telemetry hasn't accumulated minimum samples; check `GET .../operations/{operationId}` for the specific pending gate.
- **Rollback returns `ManualInterventionRequired`** — the backend never captured the previous revision; re-point the alias/weights/slot manually through the cloud console or CLI.

## Next steps

- [Back up and restore](backup-and-restore.md)
- [Monitor Honua Server](monitoring.md)
- [Deploy on AWS and Azure](cloud-deployments.md)
