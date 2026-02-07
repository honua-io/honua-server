# Honua Server

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.6-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-ready-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

Honua Server is a modern, cloud-native, open GIS server designed for interoperability, performance, and long-term flexibility.

Modern GIS infrastructure for the cloud era:
- **Modernize without rip-and-replace** — import existing services, keep legacy clients running, and move to open standards incrementally.
- **Open standards everywhere** — GeoServices REST (FeatureServer), OGC API Features/Tiles, OData v4, and vector tiles (MVT).
- **Enterprise data access** — OData v4 for Excel/Power BI with spatial queries.
- **Cloud-native by default** — containers, Helm/Terraform templates, and serverless-friendly images.

Protocols:
- **GeoServices REST FeatureServer** — GeoServices REST compatible queries + full editing (applyEdits, attachments, related records).
- **OGC API Features** — Modern REST/JSON for GIS apps with transaction support.
- **OData v4** — Full CRUD access for Excel/Power BI with spatial queries.
- **Vector Tiles (MVT)** — PostGIS-native tile generation.

Includes **file import** APIs (GeoJSON, Shapefile, GeoPackage, CSV, KML) and **Esri service import endpoints** for migration. Deployment templates (Helm + AWS/Azure Terraform) are available under `infrastructure/`, including serverless options (Lambda + Functions). The Admin UI is available at `/admin` when enabled (`HONUA_SERVE_ADMIN_UI=true`).

## Entrypoints

- `/healthz/live`
- `/healthz/ready`
- `/api/v1/admin`
- `/rest/services/{service}/FeatureServer`
- `/ogc/features`
- `/ogc/tiles`
- `/odata`
- `/tiles/{layerId}/{z}/{x}/{y}.mvt`
- `/tiles/{layerId}/tile.json`
- `/api/styles/{layerId}.json`
- `/openapi.json`

## Quick Start

You need **Docker** and **Docker Compose v2+**. Nothing else.

```bash
git clone https://github.com/honua-io/honua-server.git
cd honua-server

# Start PostGIS + Honua Server
docker compose up -d

# Wait for healthy, then verify
curl http://localhost:8080/healthz/ready
```

The root `docker-compose.yml` starts PostGIS and builds the server from source. Migrations run automatically on first boot. Once ready, the server is available at `http://localhost:8080`.

Optional services (add as needed):
```bash
# Redis (caching)
HONUA_REDIS_URL=redis:6379 docker compose --profile redis up -d

# MinIO (S3-compatible file storage for imports)
HONUA_STORAGE_PROVIDER=AwsS3 HONUA_S3_BUCKET=honua-dev \
  HONUA_S3_SERVICE_URL=http://minio:9000 \
  HONUA_S3_ACCESS_KEY_ID=minioadmin HONUA_S3_SECRET_ACCESS_KEY=minioadmin \
  docker compose --profile minio up -d
```

For manual .NET development (without Docker), see the [Getting Started guide](docs/contributor/development/getting-started.md).

### .NET Aspire (alternative)

If you have the .NET Aspire workload installed, you can use the AppHost for local orchestration with a dashboard (traces, logs, metrics):
```bash
dotnet run --project src/Honua.AppHost
```

### Run a pre-built image

```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  honuaio/honua-server:latest
```

This requires an existing PostGIS database. For image tags and registries, see [Container Images](docs/devops/CONTAINER_IMAGES.md).

## Capabilities

- PostGIS-only data source.
- FeatureServer: query, applyEdits, attachments, related records.
- OGC API Features: collections/items, filters, bbox/geometry, POST/PUT/DELETE transactions.
- OGC API Tiles: tilesets metadata + vector tiles.
- OData v4: CRUD with spatial functions (`geo.distance`, `geo.intersects`), `$search`, `$apply`, and `$batch`.
- Vector tiles (MVT): PostGIS `ST_AsMVT` via `/tiles/{layerId}/{z}/{x}/{y}.mvt`.
- TileJSON metadata: `/tiles/{layerId}/tile.json` with MapLibre style discovery.
- Public MapLibre styles: `/api/styles/{layerId}.json`.
- File import: GeoJSON, Shapefile, GeoPackage, CSV (lat/lon or WKT), KML/KMZ — no GDAL required.
- CRS support: PostGIS-based reprojection, EPSG via `spatial_ref_sys`, auto-detect from source files.
- Admin APIs: connections, services/layers/relationships/styles, import jobs, operations progress.
- Admin UI (Blazor WASM) served at `/admin` when enabled.
- OIDC authentication (server-side plumbing) and optional Redis metadata cache.
- .NET Aspire local dev orchestration with dashboard (traces, logs, metrics, health).

## Configuration

Every setting is controlled via environment variables. Copy `.env.example` for a full reference.

**Required** (all deployments):
```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
HONUA_ADMIN_PASSWORD="change-me"
```

**Common options:**
```bash
# Feature flags
HONUA_ADMIN_UI=true                       # Enable web admin UI at /admin
HONUA_OBSERVABILITY=true                  # Enable metrics endpoints
HONUA_SKIP_MIGRATIONS=false               # Skip auto-migrations (set true for serverless)

# Cache (Redis)
ConnectionStrings__Redis="localhost:6379"  # Or use HONUA_REDIS_URL in Docker Compose

# Query limits (shared across all protocols)
Limits__Query__MaxRecordCount=2000
Limits__Query__DefaultRecordCount=1000
Limits__Query__QueryTimeout=00:00:30

# CORS
Cors__AllowedOrigins__0="https://myapp.example.com"
```

Invalid configuration causes a startup failure with a detailed error message. See [`.env.example`](.env.example) for every available variable and [`docs/contributor/adr/0008-env-var-configuration.md`](docs/contributor/adr/0008-env-var-configuration.md) for design rationale.

## Documentation

**Getting started:**
- **[Developer Setup](docs/contributor/development/getting-started.md)** - Full development environment guide
- **[Deploying to Production](infrastructure/README.md)** - Docker Compose, Helm, Terraform, and serverless options

**API reference:**
- **[Standards APIs](docs/user/STANDARDS_APIS.md)** - FeatureServer, OGC, OData, MVT
- **[Control Plane API](docs/user/CONTROL_PLANE_API.md)** - Admin and automation endpoints
- **[API Examples](docs/user/API_EXAMPLES.md)** - Request/response examples for all protocols

**Architecture and contributing:**
- **[Architecture](docs/contributor/ARCHITECTURE.md)** - System design and component interaction
- **[ADRs](docs/contributor/adr/README.md)** - Architecture Decision Records
- **[Contributing](docs/contributor/development/contributing.md)** - Code style, testing, and PR process

**Operations:**
- **[Security Configuration](docs/devops/SECURITY_CONFIGURATION.md)** - OIDC, secrets, and proxy hardening
- **[Troubleshooting](docs/devops/TROUBLESHOOTING.md)** - Common issues and debugging
- **[Runbooks](docs/devops/runbooks/README.md)** - Operational playbooks
