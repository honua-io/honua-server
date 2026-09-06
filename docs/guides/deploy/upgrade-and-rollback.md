# Upgrade and roll back

You'll roll a new Honua version forward safely — preflight first, backward-compatible migrations, app rollback before database restore.

**Prerequisites:** Admin access (`X-API-Key` header), a current database backup ([Back up and restore](backup-and-restore.md)), and the target image tag.

## Policy

1. Zero-downtime upgrades are supported only for backward-compatible (expand-contract) migrations: add columns/tables first, deploy, drop old columns in a later release. Potentially breaking migrations carry an explicit `-- honua:compatibility-review` marker in the SQL — treat those as gated rollouts with a documented rollback path. On an existing database you can require explicit approval before those apply: set `Database__MigrationSafety__ContractApplyPolicy=Gate` (fresh installs are unaffected), set `HONUA_APPROVE_CONTRACT_MIGRATIONS` to the nonce printed by the migration safety error, and optionally run a pre-migration backup hook via `Database__MigrationSafety__BackupCommand` — see [Deploy with Docker Compose — Upgrade & Rollback](docker-compose.md#upgrade--rollback).
2. The default recovery is rolling back the application image; the previous version keeps working against the expanded schema.
3. Database restore is the last resort, only when a destructive migration or data corruption makes the previous version unusable.

### Configuration binding correction (#3055)

This release corrects source-generated binding for options that previously used
`init`-only properties. Explicit values under the following sections now take effect
instead of silently retaining their defaults: `Alerts` (including delivery channels),
`AuditLog:ChainVerification`, `AuditLog:Export:Dispatch`, `Deployment`, `Federation`,
`Limits:Validation`, `Database:MigrationSafety`, `Database:StartupResilience`,
`Database:QueryCache`, `Scenes` access policies, `SecureConfiguration`,
`Spec:CostEstimator`, `TemporaryFiles`, `TileOptions`, and
`Geoprocessing:Workspace`.

Review those values before upgrading, especially environment variables already present
in deployment manifests. Defaults are unchanged when a key is absent. Existing
`Alerts:Enabled` / `Alerts__Enabled` behavior is unchanged; this correction applies to
the nested evaluation, dispatch, delivery-channel, and operations settings.

### Tenant schema isolation correction

This section applies only to explicitly labelled **Preview/trial** environments in 2026.1. GA is single-tenant; no production multi-tenant deployment or customer production data is permitted. The isolation correction retains full security severity. See [commercial boundaries](../../concepts/editions-and-licensing.md#commercial-boundaries-for-20261).

Schema routing remains opt-in (`MultiTenancy:SchemaRouting:Enabled`, default
`false`). When enabled, tenant IDs are now matched exactly. Default derivation
preserves existing lowercase ASCII letters, digits and underscores, such as
`acme` → `tenant_acme`. IDs that previously needed case folding or punctuation
replacement now require an explicit `SchemaMap` entry and otherwise receive
HTTP 503 **before any data handler runs**. Names longer than PostgreSQL's
63-byte identifier limit are rejected rather than truncated. An invalid mapping
never falls back to derivation or the default database schema.

Before upgrading a deployment with schema routing enabled:

1. Inventory **all** tenant IDs and their actual schemas, including aliases from
   the old lowercase/punctuation-to-underscore derivation. Back up the database.
2. Assign each existing schema to exactly one tenant. If tenants already share a
   schema, stop their traffic and reconcile data ownership before splitting it;
   renaming a shared schema cannot separate its rows. Do not deploy an older
   vulnerable router alongside the corrected router.
3. Pin existing tenants to their verified schemas using `SchemaMap`. Keys are
   exact tenant IDs; schema values must be safe ASCII identifiers of at most 63
   bytes. Duplicate schema targets (including case variants) are rejected for
   both owners. Mapped schemas are reserved: another tenant cannot derive the
   same target. Use `SchemaMappings` entries with `TenantId` and `SchemaName`
   values for IDs containing colons (configuration uses colons as key delimiters).
   For example, mapping `acme-east` to `tenant_acme_east` preserves
   that tenant's live schema and blocks an unmapped `acme_east` tenant from it.
4. Deploy identical configuration to every instance, then verify distinct tenant
   principals against their known data. Keep the mapping with the database
   backup and restore it with the application configuration.

Example configuration preserving two **verified, separate** existing schemas:

```json
{
  "MultiTenancy": {
    "SchemaRouting": {
      "Enabled": true,
      "SchemaMap": { "acme-east": "tenant_acme_east" },
      "SchemaMappings": [
        { "TenantId": "acme:east", "SchemaName": "legacy_acme_colon" }
      ]
    }
  }
}
```

For new installations, or after an explicit schema migration, set
`MultiTenancy:SchemaRouting:UseEncodedSchemaNames=true`. Encoding preserves
lowercase ASCII letters and digits and escapes every other UTF-16 code unit as
`_` plus four lowercase hexadecimal digits (including underscore itself).
Thus `acme-east` → `tenant_acme_002deast` and `acme_east` →
`tenant_acme_005feast`. This is reversible; no hash or truncation is used. If the
encoded result exceeds 63 bytes, provision a unique shorter schema and add an
explicit mapping. Mappings take precedence in either mode.

**Changing this option does not rename or move any schema.** Keep all existing
tenants pinned before enabling it, or stop traffic, migrate each verified schema
and update its mapping before restarting. To roll back an encoding migration,
stop traffic and restore the previous names and matching configuration together.
Do not restore the vulnerable normalization behavior as an isolation workaround.
Single-tenant deployments and the explicitly excluded `public` tenant retain
their configured default schema behavior.

### Preview raster and coverage surfaces

ImageServer, WMTS, OGC API Coverages, and EDR are Preview in 2026.1. Their
capability manifests and evidence catalogs now agree on that lifecycle.
ImageServer, WMTS, and Coverages retain their existing default availability;
Preview is a release maturity statement and does not itself disable their routes.
EDR retains its existing explicit opt-in through
`Capabilities:Experimental:serve.ogc-api-edr:Enabled=true` (or the global experimental switch).
Existing authorization and entitlement checks still apply. CITE and functional
parity results describe the tested operations and do not promote these surfaces to GA.

### SensorThings API

The OGC SensorThings API remains experimental and is not registered unless
`Capabilities__Experimental__serve.sensorthings__Enabled=true` is set. SensorThings write routes now
require API-key/admin authentication by default. Deployments that intentionally
accepted unauthenticated STA ingestion must explicitly set
`SensorThings__AllowAnonymousWritesDangerously=true`; use that only behind a
trusted ingress or disposable test boundary.

## Steps

1. Preflight against the running instance. The deploy API reports database compatibility, migration state, and coordinated-deploy readiness.

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/deploy/preflight?includeDiagnostics=true`; `GET /api/v1/admin/observability/migrations`.

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
| `POST /api/v1/admin/platform-release/converge` | Actuate the declared platform release across all serving targets in one call |

Deploy targets are configured under `ControlPlane__DeployTargets__*` with a backend per platform: `honua-kubernetes-argo-rollouts` (Argo Rollouts canary), `honua-aws-ecs-alb` (ALB weighted target groups), `honua-gitops-aws-lambda` (alias weights), `honua-azure-container-apps-revision` (revision traffic split), `honua-gitops-azure-functions` (slot swap), plus GitOps passthrough variants. When a target sets `telemetry.connection` (a `ControlPlane__TelemetryConnections` entry, Prometheus or CloudWatch), the reconciler gates promotion on error rate and p95 latency and triggers automatic rollback on breach. Keep environment-specific target metadata in your infrastructure-as-code repository (Honua's Terraform modules are available to customers through support).

### Converge the whole platform release in one call

When you declare a versioned platform release under `ControlPlane__PlatformRelease__*` (a `Version`, a `ServingArtifactReference`, and one or more `Workers`; ADR-0060 WS2), `POST /api/v1/admin/platform-release/converge` actuates the serving plane onto it in a single call — no per-target scripting. It takes **no version argument**: it always converges to the currently declared release, and it validates co-versioning first (a release must bind both planes), returning `400` if the declaration is missing or one-sided.

Per-target behaviour follows a fixed divergence contract:

- **Last-applied revision** for a target is the `DesiredRevision` of its most recent terminal-**Succeeded** deploy operation. A target with **no** such operation is *unknown* and treated as divergent.
- **`already-converged`** — last-applied already equals the declared serving artifact. Converge never actuates a target that is already at the declared release (a no-op stays a no-op).
- **`operation-created`** — last-applied differs from the declared artifact; converge creates one deploy operation with `DesiredRevision` = the declared serving artifact.
- **`unknown-treated-divergent`** — no terminal-Succeeded operation exists; converge creates a deploy operation. On a **first converge of a pre-existing install** the terminal index is empty, so every unpinned target deploys.
- **`skipped-pinned`** — the target pins an explicit artifact that diverges from the release. Config-derived skew cannot be cleared at runtime, so the target is skipped; change its configuration instead.

Every created deploy is routed through the same guardrail gateway and approval flow as any other deploy (Enterprise editions land the deploys as approval proposals in the console inbox, not a direct execute), and each is keyed by `converge:{version}:{targetId}` so re-invoking converge folds onto the in-flight operations rather than creating duplicates. The response lists the per-target outcome (with the created operation or proposal id).

**Workers are not deployed by converge.** The geoprocessing worker images converge on the next GP dispatch via the release projection; the converge response states this explicitly (`workersDeferred: true`).

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

> Open `/healthz/ready` in a browser, then run `GET /api/v1/admin/deploy/preflight` in the authorized [API explorer](../../reference/openapi-and-explorer.md).

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
