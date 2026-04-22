# Docker Compose

The supported local compose entrypoint is the repo-root [`docker-compose.yml`](../../docker-compose.yml). It builds Honua from source and starts PostGIS, with optional Redis and MinIO profiles.

## Requirements

- Docker
- Docker Compose (v2+)

## Quick Start

```bash
docker compose up -d
```

The default local database credentials are:

- database: `honua_dev`
- username: `honua_user`
- password: `honua_password`

## Optional Profiles

Enable Redis:

```bash
HONUA_REDIS_URL=redis:6379 docker compose --profile redis up -d
```

Enable MinIO:

```bash
HONUA_STORAGE_PROVIDER=AwsS3 \
HONUA_S3_BUCKET=honua-dev \
HONUA_S3_REGION=us-east-1 \
HONUA_S3_SERVICE_URL=http://minio:9000 \
HONUA_S3_ACCESS_KEY_ID=minioadmin \
HONUA_S3_SECRET_ACCESS_KEY=minioadmin \
docker compose --profile minio up -d
```

## Health Check

```bash
curl http://localhost:8080/healthz/live
```

## Shutdown

```bash
docker compose down --remove-orphans --volumes
```
