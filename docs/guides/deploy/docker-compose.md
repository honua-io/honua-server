# Deploy with Docker Compose

You'll run a production-shaped Honua stack on a single host: pinned image, secrets in an env file, persistent PostGIS volume, optional Redis, and TLS terminated by a reverse proxy. For the build-from-source dev stack, use the [quickstart](../../get-started/quickstart.md) instead.

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
      ConnectionStrings__Redis: ""
    depends_on:
      postgres:
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
    image: redis:7-alpine
    profiles: [redis]
    command: redis-server --appendonly yes
    volumes:
      - redis_data:/data
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

4. Start the stack. Add `--profile redis` and set `ConnectionStrings__Redis: "redis:6379"` in the compose file when you need durable jobs, queued imports, or workflows — Redis is required for those, optional otherwise.

```bash
docker compose up -d
```

## Verify

```bash
curl -s http://127.0.0.1:8080/healthz/ready
```

Expected output: `Ready`. Then confirm admin auth works:

```bash
source .env && curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" http://127.0.0.1:8080/api/v1/admin/config | head -c 200
```

## Troubleshoot

- **Admin calls return 401** — `HONUA_ADMIN_PASSWORD` was not passed into the container; check your production compose file and secret source.
- **Startup fails with "Master key must be at least 32 characters"** — lengthen `Security__ConnectionEncryption__MasterKey`.
- **Browser requests blocked by CORS** — the permissive dev CORS policy is force-disabled inside containers; set `Cors__AllowedOrigins__0` to your app's exact origin.
- **`503` on OGC Processes or import job routes** — those need Redis; enable the `redis` profile and set `ConnectionStrings__Redis`.
- **Container restarts in a loop** — check `docker compose logs honua` for migration failures; the server applies database migrations at startup.

## Next steps

- [Monitor Honua Server](monitoring.md)
- [Back up and restore](backup-and-restore.md)
- [Production checklist](../secure/production-checklist.md)
