# Deploy with Docker Compose

You'll run a production-shaped Honua stack on a single host: pinned image, secrets in an env file, persistent PostGIS and Redis volumes, optional Console, and TLS terminated by a reverse proxy. For the build-from-source dev stack with the profiled Console service, use the [quickstart](../../get-started/quickstart.md) instead.

**Prerequisites:** Docker with Compose v2, a DNS name pointing at the host (for TLS), and outbound access to Docker Hub or GHCR.

## Steps

1. Create the env file. The master key must be at least 32 characters; production compose files must pass secrets explicitly rather than relying on the repo-root development defaults.

```bash
mkdir -p /opt/honua && cd /opt/honua
cat > .env <<'EOF'
HONUA_TAG=latest
POSTGRES_PASSWORD=replace-with-strong-db-password
HONUA_ADMIN_PASSWORD=replace-with-strong-admin-password
HONUA_MASTER_KEY=replace-with-random-string-of-32-plus-characters
HONUA_CORS_ORIGIN=https://app.example.com
HONUA_STORAGE_VOLUME_NAME=honua_storage
EOF
chmod 600 .env
```

2. Create the compose file. The server process is replaceable, while PostGIS, Redis durable-control-plane state, and local file storage live on persistent volumes. The server binds to localhost only, so the proxy is the sole public entrypoint.

```bash
cat > docker-compose.yml <<'EOF'
services:
  honua:
    image: honuaio/honua-server:${HONUA_TAG}
    ports:
      - "127.0.0.1:8080:8080"
      - "127.0.0.1:8081:8081"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=honua;Password=${POSTGRES_PASSWORD}"
      HONUA_ADMIN_PASSWORD: ${HONUA_ADMIN_PASSWORD}
      Security__ConnectionEncryption__MasterKey: ${HONUA_MASTER_KEY}
      Cors__AllowedOrigins__0: ${HONUA_CORS_ORIGIN}
      HONUA_OBSERVABILITY: "true"
      ConnectionStrings__Redis: "redis:6379"
      Database__MigrationSafety__ContractApplyPolicy: Gate
      FileStorage__Provider: Local
      FileStorage__LocalStorage__BasePath: /var/lib/honua/storage
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "wget", "--no-verbose", "--tries=1", "--spider", "http://localhost:8080/healthz/live"]
      interval: 30s
      timeout: 10s
      retries: 3
    restart: unless-stopped
    read_only: true
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    tmpfs:
      - /tmp:noexec,nosuid,size=100m
    volumes:
      - honua_storage:/var/lib/honua/storage
    deploy:
      resources:
        limits:
          cpus: "2.0"
          memory: 2g

  postgres:
    image: pgrouting/pgrouting:17-3.5-3.7.3
    environment:
      POSTGRES_DB: honua
      POSTGRES_USER: honua
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - postgres_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U honua -d honua"]
      interval: 10s
      timeout: 5s
      retries: 5
    restart: unless-stopped

  redis:
    image: redis:7.4-alpine
    command: redis-server --appendonly yes --maxmemory 64mb --maxmemory-policy noeviction
    volumes:
      - redis_data:/data
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 5
    restart: unless-stopped

volumes:
  postgres_data:
  redis_data:
  honua_storage:
    name: ${HONUA_STORAGE_VOLUME_NAME}
EOF
```

3. Put a TLS-terminating reverse proxy in front — Honua does not terminate TLS. Any proxy works; Caddy on the host is the shortest path (port 8080 carries HTTP/1 REST, port 8081 carries h2c gRPC for native SDK clients).

```bash
HONUA_HOST=honua.example.com
printf '%s {\n  reverse_proxy 127.0.0.1:8080\n}\n' "$HONUA_HOST" | sudo tee /etc/caddy/Caddyfile && sudo systemctl reload caddy
```

4. Start the stack. Redis is part of the production-shaped baseline because durable jobs, queued imports, workflows, operation proposals, and Console approval flows need it.

```bash
docker compose up -d
```

For headless deployments, keep Redis unless every durable control-plane feature is intentionally disabled — see [Redis is optional; PostGIS is not](#redis-is-optional-postgis-is-not) for exactly what you give up. To add Console in production, use a published Console image tag that is compatible with the server ops-health contract and add this service:

```yaml
  console:
    image: ghcr.io/honua-io/honua-console:replace-with-compatible-tag
    ports:
      - "127.0.0.1:5174:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: "http://+:8080"
      HONUA_SERVER_BASE_URL: "http://honua:8080"
      HONUA_ADMIN_API_KEY: ${HONUA_ADMIN_PASSWORD}
    depends_on:
      honua:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "wget", "--no-verbose", "--tries=1", "--spider", "http://localhost:8080/operate"]
      interval: 30s
      timeout: 10s
      retries: 5
    restart: unless-stopped
```

Then start it with `docker compose up -d console`, or disable it with `docker compose up -d --scale console=0`.

## Verify

> Open `http://127.0.0.1:8080/healthz/ready` in a browser.

Expected output: `Ready`. Then confirm admin auth works:

From an operator workstation with Python 3, install the pinned admin SDK and read
the effective configuration through its authenticated client:

```bash
set -a
. ./.env
set +a
python3 -m pip install \
  "honua-admin @ git+https://github.com/honua-io/honua-sdk-python.git@python-sdk-v0.1.9#subdirectory=packages/honua-admin"
python3 - <<'PY'
import os
from honua_admin import HonuaAdminClient

with HonuaAdminClient(
    "http://127.0.0.1:8080",
    api_key=os.environ["HONUA_ADMIN_PASSWORD"],
) as admin:
    print(admin.get_config())
PY
```

If Console is enabled, confirm the ops dashboard responds:

> Open `http://127.0.0.1:5174/operate`, `http://127.0.0.1:5174/operate/health`, `http://127.0.0.1:5174/operate/copilot` in a browser.

CI can run the same root-compose smoke through `scripts/ci/smoke-quickstart-console.sh`.
Set the repository variable `HONUA_CONSOLE_IMAGE` or pass the `console_image`
workflow-dispatch input once a compatible Console image tag is published. The
smoke starts Redis, Honua, and Console, creates a Redis-backed operation proposal
through the platform-release converge API, and verifies the Operate routes.

## Redis is optional; PostGIS is not

The 2026.1 install topology is deliberate and applies to every deployment shape:

- **PostGIS is mandatory.** The server connects to `ConnectionStrings__DefaultConnection` on boot and runs its migrations there. All catalog, service/layer, style, and metadata state lives in PostGIS. There is no alternative metadata store, and the server will not start without one.
- **Redis is optional.** A single-node install with no `ConnectionStrings__Redis` starts cleanly, reports `Ready` on `/healthz/ready`, and serves the whole read path — connections, imports, service/layer publication, styles, tiles, and rendering.

### What you lose without Redis

| Subsystem | Without Redis |
|---|---|
| Multi-layer cache | Falls back to the in-memory provider. Reads still serve correctly; the cache is process-local and not shared across replicas. |
| Durable geoprocessing jobs (GPServer, OGC API Processes, WPS, MCP, gRPC) | **Unavailable.** No durable job store is composed and no worker loop runs, so submission is refused up front. |
| Workflows / orchestration | **Unavailable.** The orchestration engine is not registered; workflow definitions and runs are not persisted. |
| Operation proposals and the Console approval flow | **Unavailable.** Proposal state has no PostGIS path — it is Redis-only. |
| Queued imports | Development/Test fall back to an in-process queue (non-durable). Outside those environments the import routes refuse with `503`. |
| Multi-replica coordination | Not available. Cross-replica cache invalidation, feature-change event durability, and shared streaming fan-out all degrade to node-local behaviour, so run **one** server instance. |

Nothing in that list fails silently: every affected surface refuses with a typed error instead of accepting work it cannot finish.

### The typed refusal

Refusals on a Redis-less install carry a machine-readable *capability-unavailable receipt* so an agent or script can branch on it without parsing prose. On the problem+json surfaces (OGC API Processes, admin API) it looks like this:

```json
{
  "type": "https://honua.io/problems/capability-unavailable",
  "title": "Capability unavailable",
  "status": 503,
  "detail": "Durable geoprocessing jobs and workflows require a Redis-backed job store. This server was started without a Redis connection, so the request is refused up front instead of being queued and never run.",
  "code": "dependency-unavailable",
  "capability": "jobs.runner",
  "missingDependency": "redis",
  "remediation": "Set ConnectionStrings__Redis to a reachable Redis instance and restart the server. ...",
  "remediationRef": "https://docs.honua.io/guides/deploy/docker-compose#redis-is-optional-postgis-is-not"
}
```

The same receipt is projected onto the other envelopes: GeoServices returns its usual `{"error":{"code":503,…}}` body with `code:`, `missingDependency:`, `capability:`, `remediation:`, and `remediationRef:` entries in `error.details[]`, and MCP returns `isError: true` with `code: "unavailable"`, `retryable: false`, and the same four fields in `structuredContent`.

`retryable` is `false` and `capability` is omitted where no manifest capability covers the refused surface — today that is the proposal/approval control plane, which otherwise carries the identical receipt.

The capabilities manifest agrees with the refusal rather than over-claiming. `GET /api/v1/capabilities/manifest` reports:

```json
{ "id": "jobs.runner", "category": "jobs", "supported": true, "available": false,
  "reasonCode": "dependency-unavailable", "messageKey": "capabilities.jobs.runner.dependency-unavailable" }
```

and `limits.job.durableJobStoreAvailable` is `false`. The manifest is computed per request and served `no-store`, so no stale "available" claim survives.

### Running the no-Redis variant

The repository ships an override that composes the root quickstart stack as PostGIS + Honua Server only, with `ConnectionStrings__Redis` unset:

```bash
docker compose -f docker-compose.yml -f docker-compose.no-redis.yml up -d
```

### Adding Redis later

Drop the override and start again:

```bash
docker compose up -d
```

Redis holds only job, workflow, proposal, and cache state, none of which is durable across an install that never had it — so nothing is lost by adding it. PostGIS-backed metadata state is untouched by the change, and durable jobs and workflows become available on the next boot.

## Troubleshoot

- **Admin calls return 401** — `HONUA_ADMIN_PASSWORD` was not passed into the container; check your production compose file and secret source.
- **Startup fails with "Master key must be at least 32 characters"** — lengthen `Security__ConnectionEncryption__MasterKey`.
- **Browser requests blocked by CORS** — the permissive dev CORS policy is force-disabled inside containers; set `Cors__AllowedOrigins__0` to your app's exact origin.
- **`503` on OGC Processes, import job routes, or proposal flows** — those need Redis; check the `redis` container health and `ConnectionStrings__Redis`. A `503` whose body carries `"code": "dependency-unavailable"` means the server was started with no Redis at all — see [Redis is optional; PostGIS is not](#redis-is-optional-postgis-is-not).
- **Console shows a missing server binding** — set `HONUA_SERVER_BASE_URL` to the server origin reachable from the Console container and pass `HONUA_ADMIN_API_KEY`.
- **Container restarts in a loop** — check `docker compose logs honua` for migration failures; the server applies database migrations at startup.

## Upgrade & Rollback

A single-node compose deployment cannot upgrade with zero downtime — there is one server process, so stopping the old container and starting the new one always leaves a brief gap. Plan the short outage rather than expecting a seamless roll. The safe order is **preflight → backup → pull → verify**, with rollback to the previous tag first and a database restore only if a schema-narrowing migration actually ran. See [Upgrade and roll back](upgrade-and-rollback.md) for the full policy (and Kubernetes/cloud rollouts).

1. Preflight against the running instance before pulling anything. Proceed only when `readyForCoordinatedDeploy` is `true` and no unexpected migrations are pending.

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/deploy/preflight?includeDiagnostics=true` and `GET /api/v1/admin/observability/migrations`.

2. Back up the database (this is your rollback floor for any destructive migration).

```bash
docker compose exec -T postgres pg_dump -U honua -d honua -Fc > "honua-$(date +%F).dump"
```

3. Pull the new tag and recreate the server. Migrations run automatically when the new container starts.

```bash
sed -i 's/^HONUA_TAG=.*/HONUA_TAG=vX.Y.Z/' .env   # pin the new version
docker compose pull honua
docker compose up -d honua
```

4. Verify readiness, then confirm no migration failure in the logs.

> Open `http://127.0.0.1:8080/healthz/ready` in a browser.

**Roll back:** re-pin `HONUA_TAG` to the previous version and `docker compose up -d honua`. Additive (expand) migrations leave the schema backward-compatible, so the previous image runs against it unchanged. Restore the database (`pg_restore` from your dump) **only** when a contract-phase (schema-narrowing) migration ran and made the old version unusable — stop the container first, restore, then start the previous tag.

### Gating contract-phase migrations (optional)

The root quickstart uses the journal-scoped Gate policy by default. For production compose, keep `Database__MigrationSafety__ContractApplyPolicy: Gate` when you want a schema-narrowing upgrade to be a deliberate, approved step on an existing database. It applies only to an already-migrated database, so a first install still provisions fully with no extra config:

```yaml
    environment:
      # ... existing env ...
      Database__MigrationSafety__ContractApplyPolicy: Gate
      # Optional: run a backup automatically, just before any contract-phase script applies.
      # A non-zero exit aborts the upgrade (fail closed). This value is read only from
      # configuration/env — it is never settable through the admin API or database.
      Database__MigrationSafety__BackupCommand: "pg_dump -h postgres -U honua -d honua -Fc -f /tmp/honua-pre-migrate.dump"
```

Under `Gate`, a pending reviewed contract migration blocks startup with a message naming the scripts and the preflight endpoints above. Approve it for one upgrade by starting the container with `HONUA_APPROVE_CONTRACT_MIGRATIONS=true`; unset it again afterward. Setting `HONUA_SKIP_MIGRATIONS=true` bypasses migrations entirely (for out-of-band migration flows such as serverless) and is **outside** this policy — those paths own their own upgrade safety.

## Next steps

- [Upgrade and roll back](upgrade-and-rollback.md)
- [Monitor Honua Server](monitoring.md)
- [Back up and restore](backup-and-restore.md)
- [Production checklist](../secure/production-checklist.md)
