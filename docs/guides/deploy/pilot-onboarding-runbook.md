# Pilot onboarding runbook

Use this runbook before handing a self-hosted pilot to an operator or customer team. It focuses on the first-hour failures that look like generic server errors but usually come from missing runtime prerequisites, Redis, or an inactive Metadata v2 snapshot.

**Prerequisites:** shell access to the deployment, the admin password, access to the PostGIS database, and either `redis-cli` or the managed Redis provider's connectivity test.

## Runtime prerequisites

### Local or disposable pilot

The repository Docker Compose stack supplies PostGIS and runs migrations. A local pilot still needs explicit runtime secrets and Redis when it exercises asynchronous work:

```bash
HONUA_ADMIN_PASSWORD=replace-with-admin-password
Security__ConnectionEncryption__MasterKey=replace-with-32-plus-character-secret
HONUA_REDIS_URL=redis:6379
```

Start Redis with the Compose profile when durable jobs, queued imports, OGC Processes, GPServer async jobs, tile-operation jobs, or workflow runs are in scope:

```bash
HONUA_REDIS_URL=redis:6379 docker compose --profile redis up -d
```

The stock Compose file maps host `HONUA_REDIS_URL` into the container's `ConnectionStrings__Redis`. If you use a `docker-compose.override.yml`, setting `ConnectionStrings__Redis: redis:6379` on the `honua` service is equivalent.

For a disposable fresh-stack smoke test only, clear volumes first:

```bash
docker compose down -v
HONUA_REDIS_URL=redis:6379 docker compose --profile redis up -d
curl -fsS http://localhost:8080/healthz/ready
```

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
| `ConnectionStrings__Redis` | Required for durable jobs/workflows, queued imports, geoprocessing jobs, workflow definitions/runs, execution logs, and cross-replica job coordination. |

Redis cache fallback only protects cache reads. It is not a durable job/workflow substitute. If a production pilot includes background imports, OGC Processes, GPServer async jobs, tile jobs, or workflow orchestration, run Redis with persistence enabled, for example managed Redis persistence or Redis AOF.

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
| Process liveness | `curl -fsS "$HONUA_URL/healthz/live"` | `Healthy` |
| Readiness, migrations, database, configured cache | `curl -fsS "$HONUA_URL/healthz/ready"` | `Ready` |
| PostGIS | `psql "$ConnectionStrings__DefaultConnection" -c "select postgis_full_version();"` | PostGIS version text |
| Compose PostGIS | `docker compose exec postgres pg_isready -U honua_user -d honua_dev` | `accepting connections` |
| Redis from Compose | `docker compose exec redis redis-cli ping` | `PONG` |
| Honua cache/Redis view | `curl -fsS -H "X-API-Key: $HONUA_ADMIN_PASSWORD" "$HONUA_URL/api/v1/admin/cache/status"` | `isHealthy: true`; `isUsingFallback: false` when Redis is required |
| Runtime config | `curl -fsS -H "X-API-Key: $HONUA_ADMIN_PASSWORD" "$HONUA_URL/api/v1/admin/config"` | Effective env-derived config is visible |
| Performance and license health | `curl -fsS -H "X-API-Key: $HONUA_ADMIN_PASSWORD" "$HONUA_URL/healthz/metrics"` | JSON status is `healthy` |

If `/healthz/ready` returns `503` and the body is `Not Ready`, use `/api/v1/admin/cache/status`, database logs, and server logs to identify whether the failing check is migrations, PostGIS, or cache/Redis.

## Activate Metadata v2

Honua serves layer/service metadata from the active Metadata v2 graph for the configured environment. The active graph is the row in `honua.metadata_v2_current` that points at a revision in `honua.metadata_v2_snapshots`.

Supported activation path:

1. Set the deployment environment id with `Metadata__Environment` before the server starts. Keep it stable across restarts and replicas.
2. Register or import a dataset.
3. Publish at least one layer through the admin publish API, for example the quickstart's `POST /api/v1/admin/connections/{connectionId}/layers` flow.
4. Any successful publish or later metadata authoring update saves a new Metadata v2 graph revision and makes it current for that environment.

Do not hand-edit `metadata_v2_current` for normal onboarding. If you restore a database backup, restore `metadata_v2_snapshots`, `metadata_v2_current`, and the sidecar index tables with the catalog data. If you only restored older v1 catalog tables, run a publish or metadata update in the same `Metadata__Environment` to persist and activate the v2 graph.

Detect the active snapshot through the admin API:

```bash
curl -i -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  "$HONUA_URL/api/v1/admin/metadata/environments/$METADATA_ENV/inventory"
```

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
| OGC Processes, GPServer async job routes, workflow routes, queued imports, or tile-operation jobs return `503` | Redis-backed durable queue/store is missing or unavailable. | `/api/v1/admin/cache/status` reports fallback or unhealthy; Redis `PING` fails; logs mention job queue/store or distributed import coordination. | Configure `ConnectionStrings__Redis`, restore Redis, restart affected replicas, then resubmit the job. |
| `503 Distributed import coordination is unavailable` | Queued ArcGIS/GeoServer import coordination cannot reach Redis. | Redis `PING` fails or import logs contain Redis connection failures. | Restore Redis and retry the import; queued import requests are not safely durable without Redis. |
| `/healthz/ready` returns `503` with body `Not Ready` | Migrations, PostGIS, configured cache, or feature-change storage failed readiness. | Check server logs, database health, and `/api/v1/admin/cache/status`. | Fix the named dependency and restart only if configuration changed. |
| Admin calls return `401` in a fresh pilot | `HONUA_ADMIN_PASSWORD` was not set in the running container or the request omitted `X-API-Key`. | Check effective deployment env and request headers. | Set the admin password through the deployment secret store and send `X-API-Key: <password>`. |
| Creating a saved connection fails with `Master key not configured` | `Security__ConnectionEncryption__MasterKey` is absent or shorter than 32 characters. | Check env configuration. | Set a 32+ character master key and restart before storing encrypted connection credentials. |

## Before handoff

- `curl -fsS "$HONUA_URL/healthz/ready"` returns `Ready`.
- Redis `PING` returns `PONG` when jobs/workflows are in scope.
- `/api/v1/admin/cache/status` is healthy and not using fallback when Redis is required.
- The metadata inventory endpoint for `Metadata__Environment` returns `200 OK`.
- At least one published layer can be fetched through the expected public protocol.
- Server logs contain no startup validation errors, migration failures, Redis connection loops, or `No Metadata v2 snapshot has been activated` messages.

## Related pages

- [Quickstart](../../get-started/quickstart.md)
- [Deploy with Docker Compose](docker-compose.md)
- [Monitoring](monitoring.md)
- [Troubleshooting](troubleshooting.md)
- [Scale and tune performance](scaling-and-performance.md)
