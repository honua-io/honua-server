# Docker Compose Sample

This sample runs Honua Server with PostGIS and optional Redis/MinIO using Docker Compose.

## Requirements
- Docker
- Docker Compose (v2+)

## Quick Start (from repo root)
```bash
docker compose -f infrastructure/samples/docker-compose/docker-compose.yml up -d
```

By default this pulls `honuaio/honua-server:latest`. Override with nightly tags if needed:
```bash
HONUA_IMAGE=honuaio/honua-server:nightly \
  docker compose -f infrastructure/samples/docker-compose/docker-compose.yml up -d

HONUA_IMAGE=ghcr.io/honua-io/honua-server:nightly-aot \
  docker compose -f infrastructure/samples/docker-compose/docker-compose.yml up -d
```

## Enable Redis (optional)
```bash
HONUA_REDIS_URL=redis:6379 \
  docker compose -f infrastructure/samples/docker-compose/docker-compose.yml --profile redis up -d
```

## Enable MinIO (optional S3-compatible storage)
```bash
HONUA_STORAGE_PROVIDER=AwsS3 \
HONUA_S3_BUCKET=honua-dev \
HONUA_S3_REGION=us-east-1 \
HONUA_S3_SERVICE_URL=http://minio:9000 \
HONUA_S3_ACCESS_KEY_ID=minioadmin \
HONUA_S3_SECRET_ACCESS_KEY=minioadmin \
  docker compose -f infrastructure/samples/docker-compose/docker-compose.yml --profile minio up -d
```

Create the bucket once MinIO is up (console at `http://localhost:9001`, credentials default to `minioadmin` / `minioadmin`).

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
- The image can be overridden via `HONUA_IMAGE` if you want `nightly-aot`, a SHA tag, or Docker Hub.
- Redis is optional; set `HONUA_REDIS_URL=redis:6379` and use the `redis` profile to enable it.
- MinIO is optional; use the `minio` profile and set `HONUA_STORAGE_PROVIDER=AwsS3` with the S3 env vars above.
- Optional port overrides: `POSTGRES_PORT`, `REDIS_PORT`, `HONUA_HTTP_PORT`, `PGADMIN_PORT`.

## Validate the Sample
```bash
bash scripts/verify-docker-compose-sample.sh
```
