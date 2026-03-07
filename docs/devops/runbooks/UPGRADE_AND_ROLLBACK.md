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
BASE_URL=https://<host> ./scripts/post-deployment-verification.sh
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
./scripts/deploy-rolling.sh ghcr.io/honua-io/honua-server:<tag>
```

After rollout:
- verify `/healthz/live` and `/healthz/ready`
- check `/api/v1/admin/deploy/preflight`
- run `./scripts/post-deployment-verification.sh`

For canary-style rehearsals, use `./scripts/deploy-canary.sh` only where the cluster has the required traffic-splitting substrate.

---

## Rollback Procedure

### Application Rollback First

Use application rollback whenever:
- the new version fails readiness or health checks
- latency or error rate regresses after rollout
- migrations were additive and the previous version can still run against the expanded schema

Kubernetes rollback:

```bash
./scripts/rollback-deployment.sh

# Or target a specific revision
./scripts/rollback-deployment.sh <revision>
```

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
```

This environment is the right place to validate:
- multi-instance readiness behavior
- distributed cache behavior during rollout
- rollback recovery after a failed rollout

For release rehearsal, capture:
- deploy preflight output before and after rollout
- readiness and metrics responses
- rollback steps used and time to recovery

---

## Exit Criteria After Upgrade

An upgrade is complete when:
- all intended replicas are ready
- deploy preflight reports the instance as ready
- no unexpected pending migrations remain
- post-deployment verification passes
- rollback steps are documented for the release in case of later regression
