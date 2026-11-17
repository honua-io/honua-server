---
type: guide
title: "Pilot onboarding runbook"
description: "Use this runbook before handing a self-hosted pilot to an operator or customer team."
---
# Pilot onboarding runbook

Use this runbook before handing a self-hosted pilot to an operator or customer team. It focuses on the first-hour failures that look like generic server errors but usually come from missing database prerequisites, incompatible worker configuration, or an inactive Metadata v2 snapshot.

**Prerequisites:** shell access to the deployment, the admin password, access to the PostGIS database, and `psql`.

## Runtime prerequisites

### Local or disposable pilot

The repository Docker Compose stack supplies PostGIS and migrations. A local pilot still needs explicit runtime secrets:

```bash
HONUA_ADMIN_PASSWORD=replace-with-admin-password
Security__ConnectionEncryption__MasterKey=replace-with-32-plus-character-secret
```

Start the stack normally. PostgreSQL holds shared durable job, queue, workflow, and coordination state:

```bash
docker compose up -d
```

The stock Compose stack uses its PostgreSQL database for application and system state. Each replica keeps its cache in memory.

For a disposable fresh-stack smoke test only, clear volumes first:

```bash
docker compose down -v
docker compose up -d
```

Then open `http://localhost:8080/healthz/ready` in a browser and expect `Ready`.

Do not use `docker compose down -v` against a pilot that contains customer data.

### Production pilot

Production pilots need these prerequisites before traffic is routed:

| Prerequisite | Required value |
|---|---|
| PostGIS | PostgreSQL with PostGIS enabled, reachable from every Honua replica. |
| `ConnectionStrings__DefaultConnection` | Primary PostGIS connection string. Migrations run at startup unless `HONUA_SKIP_MIGRATIONS=true`; do not skip them for a new pilot. |
| `HONUA_ADMIN_PASSWORD` | Strong admin secret delivered through the platform secret store. Admin endpoints refuse to operate without it in production. |
| `Security__ConnectionEncryption__MasterKey` | 32+ character secret when storing encrypted connection credentials. |
| `Metadata__Environment` | Stable environment id for this deployment, for example `dev`, `staging`, or `prod`. Defaults to `default` if unset. |
| Durable job prerequisites | All participating replicas and compatible workers must reach the shared system PostgreSQL database. Retain output artifacts in storage accessible to the workers and clients that need them. |

Include system state and retained job outputs in the backup and recovery plan. A healthy per-replica cache does not prove that durable queues, workers, or output storage are working.

## Verify dependencies

Set these once for the commands below:

```bash
HONUA_URL=http://localhost:8080
HONUA_ADMIN_PASSWORD=replace-with-admin-password
METADATA_ENV=default
```

Use the environment id from `Metadata__Environment` for `METADATA_ENV`; if neither `Metadata__Environment` nor `Environment` is set, use `default`.

| Dependency | Check | Healthy result |
|---|---|---|
| Process liveness | Open `/healthz/live` at the deployment origin in a browser | `Healthy` |
| Readiness, migrations, database, per-replica cache | Open `/healthz/ready` at the deployment origin in a browser | `Ready` |
| PostGIS | `psql "$ConnectionStrings__DefaultConnection" -c "select postgis_full_version();"` | PostGIS version text |
| Compose PostGIS | `docker compose exec postgres pg_isready -U honua_user -d honua_dev` | `accepting connections` |
| Honua per-replica cache | Run `GET /api/v1/admin/cache/status` in the authorized [API explorer](../../reference/openapi-and-explorer.md) | `isHealthy: true`; `isUsingFallback: false` |
| Runtime config | Run `GET /api/v1/admin/config` in the authorized API explorer | Effective env-derived config is visible |
| Performance and license health | Open Honua Console's **Operate → Health** view (backed by `/healthz/metrics`) | JSON status is `healthy` |

If `/healthz/ready` returns `503` and the body is `Not Ready`, use `/api/v1/admin/cache/status`, database logs, and server logs to identify whether the failing check is migrations, PostGIS, the local cache, or feature-change storage.

## Activate Metadata v2

Honua serves layer/service metadata from the active Metadata v2 graph for the configured environment. The active graph is the row in `honua.metadata_v2_current` that points at a revision in `honua.metadata_v2_snapshots`.

Supported activation path:

1. Set the deployment environment id with `Metadata__Environment` before the server starts. Keep it stable across restarts and replicas.
2. Register or import a dataset.
3. Publish at least one layer through the admin publish API, for example the quickstart's `POST /api/v1/admin/connections/{connectionId}/layers` flow.
4. Any successful publish or later metadata authoring update saves a new Metadata v2 graph revision and makes it current for that environment.

Do not hand-edit `metadata_v2_current` for normal onboarding. If you restore a database backup, restore `metadata_v2_snapshots`, `metadata_v2_current`, and the sidecar index tables with the catalog data. If you only restored older v1 catalog tables, run a publish or metadata update in the same `Metadata__Environment` to persist and activate the v2 graph.

Detect the active snapshot through the admin API:

Run `GET /api/v1/admin/metadata/environments/{environment}/inventory` in the authorized [API explorer](../../reference/openapi-and-explorer.md), substituting the configured `Metadata__Environment` value.

Expected: `200 OK` with `environment`, `revision`, `eTag`, and `entries`. A `404` with `Metadata v2 environment '<env>' does not have an active revision.` means no snapshot is active for that environment.

Detect it directly in PostGIS:

```bash
psql "$ConnectionStrings__DefaultConnection" \
  -v env="$METADATA_ENV" \
  -c "select c.environment, c.revision, c.activated_at, s.etag
      from honua.metadata_v2_current c
      join honua.metadata_v2_snapshots s
        on s.environment = c.environment and s.revision = c.revision
      where c.environment = :'env';"
```

No rows means no active snapshot for that environment. A row in another environment usually means `Metadata__Environment` is wrong for the running deployment.

## Failure modes

| Symptom | Likely cause | Confirm | Fix |
|---|---|---|---|
| Runtime metadata routes return `500`; logs contain `No Metadata v2 snapshot has been activated for environment '<env>'.` | The running `Metadata__Environment` has no active Metadata v2 snapshot and no compatible legacy catalog to synthesize from. | Inventory endpoint returns `404`; SQL query against `metadata_v2_current` returns no row. | Publish a layer or make a metadata authoring update in the same environment. If this is a restore, restore the metadata-v2 tables with the catalog. |
| `GET /api/v1/admin/metadata/environments/<env>/inventory` returns `404` with `does not have an active revision` | No active snapshot for the requested environment, or the request used the wrong environment id. | Compare `Metadata__Environment` with rows in `honua.metadata_v2_current`. | Use the correct environment id or activate a snapshot by publishing/updating metadata. |
| OGC Processes, GPServer async job routes, workflow routes, queued imports, or tile-operation jobs return `503` | A durable PostgreSQL dependency or compatible worker configuration is unavailable. | Inspect server and worker logs, database connectivity, migrations, and the existing job status. | Restore the named dependency or worker configuration. Inspect the previous job outcome before resubmitting. |
| `503 Distributed import coordination is unavailable` | Queued GeoServices REST/GeoServer import coordination is unavailable. | Inspect import logs, shared PostgreSQL connectivity, and migrations. | Restore the named dependency and inspect the import status before retrying. |
| `/healthz/ready` returns `503` with body `Not Ready` | Migrations, PostGIS, configured cache, or feature-change storage failed readiness. | Check server logs, database health, and `/api/v1/admin/cache/status`. | Fix the named dependency and restart only if configuration changed. |
| Admin calls return `401` in a fresh pilot | `HONUA_ADMIN_PASSWORD` was not set in the running container or the request omitted `X-API-Key`. | Check effective deployment env and request headers. | Set the admin password through the deployment secret store and send `X-API-Key: <password>`. |
| Creating a saved connection fails with `Master key not configured` | `Security__ConnectionEncryption__MasterKey` is absent or shorter than 32 characters. | Check env configuration. | Set a 32+ character master key and restart before storing encrypted connection credentials. |

## Before handoff

- `/healthz/ready` at the deployment origin returns `Ready` in a browser.
- A representative job reaches its terminal outcome and its retained output can be retrieved when jobs/workflows are in scope.
- `/api/v1/admin/cache/status` is healthy and reports `isUsingFallback: false`.
- The metadata inventory endpoint for `Metadata__Environment` returns `200 OK`.
- At least one published layer can be fetched through the expected public protocol.
- Server logs contain no startup validation errors, migration failures, shared-state connection failures, or `No Metadata v2 snapshot has been activated` messages.

## Related pages

- [Quickstart](../../get-started/quickstart.md)
- [Deploy with Docker Compose](docker-compose.md)
- [Monitoring](monitoring.md)
- [Troubleshooting](troubleshooting.md)
- [Scale and tune performance](scaling-and-performance.md)
