# Pilot Onboarding Runbook

This runbook covers the minimum steps to get a self-hosted Honua Server instance running from a clean state, and documents the failure modes most commonly encountered in the first hour of operation.

---

## Prerequisites

### Runtime requirements

| Requirement | Minimum | Notes |
|-------------|---------|-------|
| Docker + Compose v2 | Docker 24+ | `docker compose version` must show v2 |
| PostgreSQL + PostGIS | PostgreSQL 16, PostGIS 3.4 | PostgreSQL 17 + PostGIS 3.5 used in the bundled compose file |
| Redis | Redis 7 | **Required** for durable job orchestration and workflow scheduling. Without Redis, any call to a geoprocessing, OGC Processes, or workflow endpoint returns `503 Service Unavailable`. Read/query-only deployments that never submit jobs can operate without Redis; the server falls back to in-memory caching. |
| .NET 10 SDK | Only needed for Aspire local dev (`dotnet run --project src/Honua.AppHost`) | Not required when using Docker |

### Required environment variables (PostgreSQL provider)

| Variable | Example | Notes |
|----------|---------|-------|
| `ConnectionStrings__DefaultConnection` | `Host=postgres;Database=honua;Username=postgres;Password=postgres` | Postgres connection string. Required in non-development environments; startup fails with a configuration error if absent. |
| `HONUA_ADMIN_PASSWORD` | `change-me` | API key for all `/api/v1/admin/*` endpoints. Required in non-development environments (the validator rejects startup without it). In development the server warns and leaves admin endpoints inaccessible rather than failing. Passed as `X-API-Key: <value>` or HTTP Basic username. |

### Optional but commonly needed variables

| Variable | Default | Notes |
|----------|---------|-------|
| `ConnectionStrings__Redis` | *(unset)* | Redis connection string. Set to enable durable job orchestration, workflow scheduling, and Redis-backed output caching. Without this, job/workflow endpoints return 503. Format: `localhost:6379` or `redis:6379`. Also accepted as `HONUA_REDIS_URL` for Docker Compose convenience. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Set to `Development` for relaxed startup validation and verbose logging. |
| `HONUA_OBSERVABILITY` | `false` | Enable Prometheus metrics and health endpoint detail at `/metrics`. |
| `HONUA_SKIP_MIGRATIONS` | `false` | Set to `true` to bypass automatic database migrations on startup. Not recommended for first boot. |

See `.env.example` for the full variable reference.

---

## First-boot sequence

### Option A — Docker Compose (recommended for pilots)

The bundled compose file starts PostGIS automatically. Redis is in the `redis` profile and must be included explicitly if you need durable job orchestration.

**Without Redis (query/serve only):**

```bash
git clone https://github.com/honua-io/honua-server.git
cd honua-server
docker compose up -d
```

**With Redis (required for jobs/workflows):**

```bash
docker compose --profile redis up -d
```

Migrations run automatically on first boot. The application waits for PostGIS to pass its healthcheck before starting.

### Option B — Pre-built image with external PostGIS

```bash
docker run -p 8080:8080 -p 8081:8081 \
  -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  honuaio/honua-server:latest
```

Add `-e ConnectionStrings__Redis="host.docker.internal:6379"` if you have a Redis instance.

### Migration behavior

Migrations run on every startup unless `HONUA_SKIP_MIGRATIONS=true`. They are idempotent; re-running against a migrated database is safe and a no-op. The readiness probe reports `Not Ready` (503) until migrations complete. On a fresh database this typically takes under 10 seconds.

If migrations fail (e.g. due to insufficient database permissions), the readiness probe reports `Not Ready` with the reason `Database migrations failed`. Liveness (`/healthz/live`) still returns `200` because the process is alive.

### Verifying readiness

```bash
# Liveness — is the process running?
curl http://localhost:8080/healthz/live
# Expected: 200 "Healthy"

# Readiness — is the server ready to accept traffic?
curl http://localhost:8080/healthz/ready
# Expected: 200 "Ready"
# Returns: 503 "Not Ready" until database + migrations are healthy
```

The readiness probe checks database connectivity, migration state, and (when Redis is configured) the cache layer. The cache check is present only when a cache health checker is registered; Redis absence without `ConnectionStrings__Redis` does not cause a readiness failure — only job/workflow endpoints surface the 503 at call time.

Once ready, verify the catalog endpoint:

```bash
curl http://localhost:8080/rest/services
# Expected: JSON catalog (empty object if no services are published yet)
```

---

## Metadata-v2 snapshot activation

### What it is

Honua stores its service and layer catalog as a typed, revision-stamped graph document called a **metadata-v2 snapshot**. This snapshot is keyed per environment (e.g. `Production`, `Development`). Every protocol endpoint — FeatureServer, OGC API Features, WMS, WCS, MapServer, STAC, WFS, and others — reads the active snapshot for the configured environment to discover which services and layers exist.

### When 500s happen without an activated snapshot

If no snapshot has been activated for the environment **and** the legacy V1 catalog (pre-v2 tables) is also empty, any data request returns `HTTP 500 Internal Server Error`. The root exception message is:

```
No Metadata v2 snapshot has been activated for environment '<environment-name>'.
```

This surfaces to the caller as a generic 500. It does **not** appear in `/healthz/ready` because readiness only checks database connectivity and migration state, not whether catalog data exists.

**When does this happen in practice?** On a fresh database after first boot: migrations have run, the server is healthy, but no connections or layers have been published through the admin API yet. The V1 catalog and the V2 snapshot tables are both empty. Any attempt to query `/rest/services/{id}/FeatureServer/...` or `/ogc/features/collections/...` before publishing at least one layer triggers this 500.

**The compat fallback:** When the V1 catalog (legacy `honua.services` / `honua.layers` tables) has published data, the server synthesizes a read-only snapshot from it automatically, without requiring an explicit activation step. This fallback is transparent to the operator and is used by existing databases migrated from a pre-v2 deployment. It does not write to the metadata-v2 tables; the first real layer publish via the admin API takes precedence on the next read.

### How the snapshot is created and activated

There is no separate "activate snapshot" admin command. The snapshot is **created and activated automatically** when you publish a layer through the admin API. The publish pipeline writes the updated graph into `metadata_v2_snapshots` and sets it as the current revision in `metadata_v2_current` for the configured environment. There is no manual snapshot promotion step for a standard single-environment deployment.

**The operator steps to get a valid snapshot:**

1. Register a database connection (admin credentials required for all admin endpoints):

```bash
# Create a connection pointing to your PostGIS database
curl -X POST http://localhost:8080/api/v1/admin/connections \
  -H "X-API-Key: <HONUA_ADMIN_PASSWORD>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "main",
    "connectionString": "Host=postgres;Database=honua;Username=postgres;Password=postgres"
  }'
# Response includes: { "connectionId": "<uuid>", ... }
```

2. Publish at least one layer from a PostGIS table:

```bash
# Publish a layer from an existing PostGIS table
curl -X POST "http://localhost:8080/api/v1/admin/connections/<connectionId>/layers" \
  -H "X-API-Key: <HONUA_ADMIN_PASSWORD>" \
  -H "Content-Type: application/json" \
  -d '{
    "schema": "public",
    "table": "my_table",
    "layerName": "My Layer",
    "serviceName": "default"
  }'
# Response: 201 Created with layer summary including layerId
```

3. Verify the snapshot is active:

```bash
# Check the semantic inventory for the active environment
curl -H "X-API-Key: <HONUA_ADMIN_PASSWORD>" \
  "http://localhost:8080/api/v1/admin/metadata/environments/Production/inventory"
# Response: 200 with resource/service inventory
# Response 404: "Metadata v2 environment 'Production' does not have an active revision"
#   -> means no layer has been published yet
```

4. Confirm data endpoints are serving:

```bash
# GeoServices REST catalog should now list published services
curl http://localhost:8080/rest/services

# OGC API Features collections
curl http://localhost:8080/ogc/features/collections
```

---

## Symptom — cause — fix

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `GET /healthz/ready` returns `503 Not Ready` with body `Database unavailable` | PostGIS is not reachable or the connection string is wrong | Verify `ConnectionStrings__DefaultConnection`; confirm PostGIS is running and accepting connections. Check container logs for Npgsql connection errors. |
| `GET /healthz/ready` returns `503 Not Ready` with body `Database migrations failed` | Migrations failed on startup, usually due to insufficient database permissions | Check server logs for migration error details. The Honua database user needs `CREATE TABLE`, `CREATE INDEX`, and `ALTER TABLE` rights on the `honua` schema. |
| `GET /healthz/ready` returns `503 Not Ready` with body `Database migrations in progress` | Migrations are still running | Wait; migrations on a fresh database take under 10 seconds. If this persists, check for a blocked migration transaction in `pg_stat_activity`. |
| `GET /healthz/ready` returns `503 Not Ready` with body `Cache unavailable` | Redis was configured (`ConnectionStrings__Redis` is set) but the connection failed | Check that the Redis server is running and reachable from the server container. Remove `ConnectionStrings__Redis` / `HONUA_REDIS_URL` if you do not need Redis-backed caching; the server falls back to in-memory cache and readiness will pass. |
| `GET /rest/services/{id}/FeatureServer/...` or any data endpoint returns `500 Internal Server Error` | No metadata-v2 snapshot has been activated and the V1 catalog is also empty (fresh database) | Publish at least one layer via `POST /api/v1/admin/connections/{id}/layers`. See [Metadata-v2 snapshot activation](#metadata-v2-snapshot-activation). |
| `POST /ogc/processes/{id}/execution`, `POST /rest/services/{id}/GPServer/{task}/submitJob`, or any workflow/job endpoint returns `503 Service Unavailable` with message `Job operations require Redis-backed durable storage` | Redis is not configured | Set `ConnectionStrings__Redis` to a reachable Redis instance and restart the server. Redis is required for the durable job store (`RedisJobQueue`, `RedisExecutionLogStore`) and workflow orchestration. Without it, workflow and geoprocessing submission endpoints return 503. |
| Admin endpoints return `401 Unauthorized` | `HONUA_ADMIN_PASSWORD` is not set or the wrong key is being sent | Set `HONUA_ADMIN_PASSWORD` and pass it as `X-API-Key: <value>` in the request header. HTTP Basic auth is also accepted: `Authorization: Basic <base64(password:)>`. |
| Server exits immediately at startup with `HONUA_ADMIN_PASSWORD is required in non-development environments` | `HONUA_ADMIN_PASSWORD` is absent and `ASPNETCORE_ENVIRONMENT` is not `Development` | Set `HONUA_ADMIN_PASSWORD`. For a non-production eval, set `ASPNETCORE_ENVIRONMENT=Development` to relax this check. |
| Server exits immediately at startup with `ConnectionStrings__DefaultConnection is required` | No PostgreSQL connection string provided and environment is not `Development` | Set `ConnectionStrings__DefaultConnection`. |
| Server exits immediately with `Redis durable coordination is required in this environment, but...` | `ConnectionStrings__Redis` is set and the connection fails, and the environment requires durable distributed events (feature-change streaming enabled) | Verify the Redis address. If you do not need feature-change streaming, remove the Redis connection string. |

---

## Diagnostic reference

During any first-hour issue, use these endpoints:

```bash
# Liveness — process alive?
curl http://localhost:8080/healthz/live

# Readiness — all dependencies healthy?
curl http://localhost:8080/healthz/ready

# Effective configuration (requires admin auth)
curl -H "X-API-Key: <HONUA_ADMIN_PASSWORD>" http://localhost:8080/api/v1/admin/config

# Migration history (requires admin auth)
curl -H "X-API-Key: <HONUA_ADMIN_PASSWORD>" http://localhost:8080/api/v1/admin/observability/migrations

# Metadata snapshot inventory for environment (requires admin auth)
curl -H "X-API-Key: <HONUA_ADMIN_PASSWORD>" \
  "http://localhost:8080/api/v1/admin/metadata/environments/Production/inventory"

# Recent errors (requires admin auth)
curl -H "X-API-Key: <HONUA_ADMIN_PASSWORD>" http://localhost:8080/api/v1/admin/observability/errors
```

HTTP: `http://localhost:8080`. gRPC (native h2c, for SDK/mobile clients): `http://localhost:8081`.
