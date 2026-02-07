# Honua Server

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.6-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-ready-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

**Cloud-native geospatial feature server.** Publish, query, and edit spatial data through industry-standard protocols — GeoServices REST, OGC API, OData v4, and vector tiles — backed by PostGIS.

## Why Honua

- **Multi-protocol** — one server speaks GeoServices REST (Esri-compatible), OGC API Features/Tiles, OData v4, and MVT. Connect ArcGIS Pro, QGIS, MapLibre, Power BI, and Excel to the same data.
- **Cloud-native** — container-first, auto-scaling, OpenTelemetry observability, and IaC templates for Kubernetes, ECS, Lambda, Azure Container Apps, and Azure Functions.
- **No GDAL dependency** — import GeoJSON, Shapefile, GeoPackage, CSV, KML/KMZ, and WKT directly. Import from live Esri REST services for migration.
- **Enterprise data access** — OData v4 with spatial functions (`geo.distance`, `geo.intersects`), `$search`, `$apply`, and `$batch` puts your spatial data in Excel, Power BI, Tableau, and any OData client.

## Quick Start

**Docker Compose** (requires Docker and Compose v2+):

```bash
git clone https://github.com/honua-io/honua-server.git && cd honua-server
docker compose up -d
curl http://localhost:8080/healthz/ready
```

PostGIS starts automatically. Migrations run on first boot. The server is at `http://localhost:8080`.

**Pre-built image** (bring your own PostGIS):

```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  honuaio/honua-server:latest
```

**Kubernetes**:

```bash
helm dependency update infrastructure/helm/honua
helm install honua infrastructure/helm/honua \
  --set secret.env.ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=honua;Password=honua" \
  --set secret.env.HONUA_ADMIN_PASSWORD="change-me"
```

**.NET Aspire** (local dev with dashboard for traces, logs, metrics):

```bash
dotnet run --project src/Honua.AppHost
```

## Protocols

| Protocol | Endpoint | Clients |
|---|---|---|
| GeoServices REST FeatureServer | `/rest/services/{id}/FeatureServer` | ArcGIS Pro, Esri SDKs, ArcGIS Online |
| OGC API Features | `/ogc/features` | QGIS, MapLibre, any OGC client |
| OGC API Tiles | `/ogc/tiles` | QGIS, MapLibre |
| OData v4 | `/odata` | Excel, Power BI, Tableau, SAP |
| Vector Tiles (MVT) | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | MapLibre, Leaflet, Mapbox GL |
| TileJSON | `/tiles/{layerId}/tile.json` | MapLibre |
| MapLibre Styles | `/api/styles/{layerId}.json` | MapLibre |
| Admin API | `/api/v1/admin` | Admin UI, automation scripts |
| OpenAPI | `/openapi.json` | Any HTTP client |
| Health | `/healthz/live`, `/healthz/ready` | Load balancers, orchestrators |

## Capabilities

**Query and edit** — FeatureServer query, applyEdits, attachments, and related records. OGC transactions (POST/PUT/DELETE). OData CRUD with spatial functions.

**Vector tiles** — PostGIS-native `ST_AsMVT` generation with TileJSON metadata and auto-generated MapLibre styles.

**File import** — GeoJSON, Shapefile, GeoPackage, CSV (lat/lon or WKT), KML/KMZ, and WKT. CRS auto-detection and PostGIS-based reprojection.

**Service import** — Migrate existing Esri feature and map services, preserving structure and metadata.

**Admin** — REST API and Blazor WASM UI (`/admin`) for managing connections, services, layers, relationships, styles, and import jobs.

**Caching** — Multi-layer: output cache, Redis, in-memory fallback.

**Auth** — API key authentication, OIDC (server-side plumbing), and optional Redis metadata cache.

**Observability** — OpenTelemetry traces and metrics, structured logging, health endpoints.

## Configuration

All settings use environment variables. Copy [`.env.example`](.env.example) for a full reference.

**Required:**
```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
HONUA_ADMIN_PASSWORD="change-me"
```

**Common options:**
```bash
HONUA_ADMIN_UI=true                       # Web admin UI at /admin
HONUA_OBSERVABILITY=true                  # Metrics and health endpoints
HONUA_OPENTELEMETRY=true                  # Distributed tracing
ConnectionStrings__Redis="localhost:6379"  # Redis cache
Cors__AllowedOrigins__0="https://app.example.com"
```

Invalid configuration causes a startup failure with a detailed error message.

## Project Structure

```
src/
  Honua.Core/         Domain models and abstractions
  Honua.Postgres/     PostGIS implementation
  Honua.Server/       HTTP host (Minimal APIs, vertical slices)
  Honua.Admin/        Blazor WASM admin UI
  Honua.AppHost/      .NET Aspire orchestration
  Honua.ServiceDefaults/  Shared service configuration

infrastructure/
  docker-compose/     Compose reference configs
  helm/               Helm chart with PostGIS subchart
  terraform/          Modules for AWS ECS, AWS Lambda, Azure Container Apps, Azure Functions
```

## Documentation

| I want to... | Go to |
|---|---|
| Set up a dev environment | [Getting Started](docs/contributor/development/getting-started.md) |
| Deploy to production | [Infrastructure](infrastructure/README.md) |
| Call the API | [Standards APIs](docs/user/STANDARDS_APIS.md) / [API Examples](docs/user/API_EXAMPLES.md) |
| Manage services and layers | [Control Plane API](docs/user/CONTROL_PLANE_API.md) |
| Understand the architecture | [Architecture](docs/contributor/ARCHITECTURE.md) / [ADRs](docs/contributor/adr/README.md) |
| Configure security | [Security Configuration](docs/devops/SECURITY_CONFIGURATION.md) |
| Troubleshoot issues | [Troubleshooting](docs/devops/TROUBLESHOOTING.md) / [Runbooks](docs/devops/runbooks/README.md) |
| Contribute code | [Contributing](docs/contributor/development/contributing.md) |

Full documentation index: [`docs/README.md`](docs/README.md)

## License

[Elastic License 2.0 (ELv2)](LICENSE) — free to use, deploy, and modify.
