# Docker Compose Sample

This sample runs Honua Server with PostGIS, Redis, and optional pgAdmin using Docker Compose.

## Requirements
- Docker
- Docker Compose (v2+)

## Quick Start (from repo root)
```bash
docker compose -f infrastructure/samples/docker-compose/docker-compose.yml up -d
```

By default this pulls `ghcr.io/honua-io/honua-server:nightly`. Override with:
```bash
HONUA_IMAGE=honuaio/honua-server:nightly \
  docker compose -f infrastructure/samples/docker-compose/docker-compose.yml up -d
```

## Optional pgAdmin
```bash
docker compose -f infrastructure/samples/docker-compose/docker-compose.yml --profile admin up -d
```

pgAdmin will be available at `http://localhost:5050` (admin@honua.local / admin).

## Health Check
```bash
curl http://localhost:8080/healthz/live
```

## Shutdown
```bash
docker compose -f infrastructure/samples/docker-compose/docker-compose.yml down --remove-orphans --volumes
```

## Notes
- Auth is disabled for dev with `HONUA_DEV_AUTH=true`. Set `HONUA_ADMIN_PASSWORD` and remove `HONUA_DEV_AUTH` if you want admin auth in dev.
- The image can be overridden via `HONUA_IMAGE` if you want `latest`, a SHA tag, or Docker Hub.
- Redis is enabled by default and wired to `ConnectionStrings__Redis`.
- Optional port overrides: `POSTGRES_PORT`, `REDIS_PORT`, `HONUA_HTTP_PORT`, `PGADMIN_PORT`.

## Validate the Sample
```bash
bash scripts/verify-docker-compose-sample.sh
```
