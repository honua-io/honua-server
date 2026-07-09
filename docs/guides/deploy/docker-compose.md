# Deploy with Docker Compose

You'll run a production-shaped Honua stack on a single host: pinned image, secrets in an env file, persistent PostGIS and Redis volumes, optional Console, and TLS terminated by a reverse proxy. For the build-from-source dev stack with the profiled Console service, use the [quickstart](../../get-started/quickstart.md) instead.

**Prerequisites:** Docker with Compose v2, a DNS name pointing at the host (for TLS), and outbound access to Docker Hub or GHCR.

## Steps

1. Create the env file. The master key must be at least 32 characters; production compose files must pass secrets explicitly rather than relying on the repo-root development defaults.

```bash
mkdir -p /opt/honua && cd /opt/honua
cat > .env <<'EOF'
HONUA_TAG=latest-aot
POSTGRES_PASSWORD=replace-with-strong-db-password
HONUA_ADMIN_PASSWORD=replace-with-strong-admin-password
HONUA_MASTER_KEY=replace-with-random-string-of-32-plus-characters
HONUA_CORS_ORIGIN=https://app.example.com
EOF
chmod 600 .env
```

2. Create the compose file. Honua is stateless — only PostGIS (and Redis, if enabled) need volumes; the server binds to localhost only, so the proxy is the sole public entrypoint.

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

For headless deployments, keep Redis unless every durable control-plane feature is intentionally disabled. To add Console in production, use a published Console image tag that is compatible with the server ops-health contract and add this service:

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

```bash
curl -s http://127.0.0.1:8080/healthz/ready
```

Expected output: `Ready`. Then confirm admin auth works:

```bash
source .env && curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" http://127.0.0.1:8080/api/v1/admin/config | head -c 200
```

If Console is enabled, confirm the ops dashboard responds:

```bash
curl -fsS http://127.0.0.1:5174/operate >/dev/null
curl -fsS http://127.0.0.1:5174/operate/health >/dev/null
curl -fsS http://127.0.0.1:5174/operate/copilot >/dev/null
```

CI can run the same root-compose smoke through `scripts/ci/smoke-quickstart-console.sh`.
Set the repository variable `HONUA_CONSOLE_IMAGE` or pass the `console_image`
workflow-dispatch input once a compatible Console image tag is published. The
smoke starts Redis, Honua, and Console, creates a Redis-backed operation proposal
through the platform-release converge API, and verifies the Operate routes.

## Troubleshoot

- **Admin calls return 401** — `HONUA_ADMIN_PASSWORD` was not passed into the container; check your production compose file and secret source.
- **Startup fails with "Master key must be at least 32 characters"** — lengthen `Security__ConnectionEncryption__MasterKey`.
- **Browser requests blocked by CORS** — the permissive dev CORS policy is force-disabled inside containers; set `Cors__AllowedOrigins__0` to your app's exact origin.
- **`503` on OGC Processes, import job routes, or proposal flows** — those need Redis; check the `redis` container health and `ConnectionStrings__Redis`.
- **Console shows a missing server binding** — set `HONUA_SERVER_BASE_URL` to the server origin reachable from the Console container and pass `HONUA_ADMIN_API_KEY`.
- **Container restarts in a loop** — check `docker compose logs honua` for migration failures; the server applies database migrations at startup.

## Upgrade & Rollback

A single-node compose deployment cannot upgrade with zero downtime — there is one server process, so stopping the old container and starting the new one always leaves a brief gap. Plan the short outage rather than expecting a seamless roll. The safe order is **preflight → backup → pull → verify**, with rollback to the previous tag first and a database restore only if a schema-narrowing migration actually ran. See [Upgrade and roll back](upgrade-and-rollback.md) for the full policy (and Kubernetes/cloud rollouts).

1. Preflight against the running instance before pulling anything. Proceed only when `readyForCoordinatedDeploy` is `true` and no unexpected migrations are pending.

```bash
source .env
curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" http://127.0.0.1:8080/api/v1/admin/deploy/preflight?includeDiagnostics=true
curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" http://127.0.0.1:8080/api/v1/admin/observability/migrations
```

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

```bash
curl -s http://127.0.0.1:8080/healthz/ready   # expect: Ready
docker compose logs --tail=50 honua
```

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
