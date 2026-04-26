# Docker Compose

The supported local compose entrypoint is the repo-root [`docker-compose.yml`](../../docker-compose.yml). It builds Honua from source and starts PostGIS, with optional Redis and MinIO profiles.

Container layout is intentional:

- Repo root holds the canonical web runtime entrypoints: [`Dockerfile`](../../Dockerfile) and [`docker-compose.yml`](../../docker-compose.yml).
- [`docker/`](../../docker/) holds specialized variants and support assets such as AOT/Lambda/Functions Dockerfiles, emulator compose, CITE stacks, the scale-test stack, nginx, and Prometheus files.
- [`docker/cloud/`](../../docker/cloud/) contains cloud-provider host shims copied into Lambda and Azure Functions images.
- [`docker/cite/`](../../docker/cite/) contains CITE compose files, suite configs, seed data, and shared CITE runner assets.
- [`docker/scale-test/`](../../docker/scale-test/) contains the multi-node scale-test compose stack and scale-test-specific Nginx/Prometheus assets.
- [`docker/monitoring/`](../../docker/monitoring/) contains reusable self-hosted Prometheus/Grafana assets used by the scale-test stack and operator docs.

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
