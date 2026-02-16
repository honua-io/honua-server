# Honua Server

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![Container Security](https://github.com/honua-io/honua-server/actions/workflows/container-security.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/container-security.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.6-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-ready-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

**Cloud-native geospatial feature server.** Publish, query, edit, and render spatial data through industry-standard protocols — GeoServices REST (catalog + FeatureServer + MapServer + ImageServer + Geometry Service), OGC API (Features, Maps, Tiles), OData v4, and vector tiles — backed by PostGIS.

## Why Honua

- **Multi-protocol** — one server speaks GeoServices REST (catalog, FeatureServer, MapServer, ImageServer, Geometry Service), OGC API Features/Maps/Tiles, OData v4, and MVT. Connect ArcGIS Pro, QGIS, MapLibre, Power BI, and Excel to the same data.
- **Cloud-native** — container-first, auto-scaling, OpenTelemetry observability, and IaC templates for Kubernetes, ECS, Lambda, Azure Container Apps, and Azure Functions.
- **No GDAL dependency** — import GeoJSON, Shapefile (zip), GeoPackage, GPX, KML, and WKT directly. Import from live Esri REST services for migration.
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

## Feedback

We use GitHub Issues as the primary feedback loop for the open-core MVP.

- Report bugs (include screenshots + repro steps): [Open bug report](https://github.com/honua-io/honua-server/issues/new?template=bug.yml)
- Request features or share product feedback: [Open feature/feedback request](https://github.com/honua-io/honua-server/issues/new?template=feature.yml)

Please use these forms instead of blank issues so reports include enough detail for triage.

## Protocols

| Protocol | Endpoint | Clients |
|---|---|---|
| GeoServices REST Catalog | `/rest/services` and `/rest/info` | ArcGIS clients, service discovery tooling |
| GeoServices REST FeatureServer | `/rest/services/{id}/FeatureServer` | ArcGIS Pro, Esri SDKs, ArcGIS Online |
| GeoServices REST MapServer | `/rest/services/{id}/MapServer` | ArcGIS Pro, Esri map clients |
| GeoServices REST ImageServer | `/rest/services/{id}/ImageServer` | ArcGIS raster/image workflows |
| GeoServices REST Geometry Service | `/rest/services/geometry` | Esri-compatible geometry operations |
| OGC API Features | `/ogc/features` | QGIS, MapLibre, any OGC client |
| OGC API Maps | `/ogc/maps` | OGC map clients, custom web apps |
| OGC API Tiles | `/ogc/tiles` | QGIS, MapLibre |
| OData v4 | `/odata` | Excel, Power BI, Tableau, SAP |
| Vector Tiles (MVT) | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | MapLibre, Leaflet, Mapbox GL |
| TileJSON | `/tiles/{layerId}/tile.json` | MapLibre |
| MapLibre Styles | `/api/styles/{layerId}.json` | MapLibre |
| Admin API | `/api/v1/admin` | Admin UI, automation scripts |
| OpenAPI (OGC Features) | `/openapi.json` | Any HTTP client |
| OpenAPI (OGC Tiles) | `/ogc/tiles/openapi.json` | Any HTTP client |
| Health | `/healthz/live`, `/healthz/ready` | Load balancers, orchestrators |

## Capabilities

**Query and edit** — FeatureServer query, applyEdits, attachments, and related records. OGC transactions (POST/PUT/DELETE). OData CRUD with spatial functions.

**Map rendering** — MapServer (export/identify/legend/find/query) plus OGC API Maps endpoints for rendered map images.

**Geometry operations** — GeoServices Geometry Service endpoints for `buffer`, `simplify`, and `project`.

**Vector tiles** — PostGIS-native `ST_AsMVT` generation with TileJSON metadata and auto-generated MapLibre styles.

**File import** — GeoJSON, Shapefile (zip), GeoPackage, GPX, KML, and WKT. CRS auto-detection and PostGIS-based reprojection.

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
HONUA_SERVE_ADMIN_UI=true                 # Serve admin UI at /admin
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
| Configure security | [Security Configuration](docs/devops/security.md) |
| Troubleshoot issues | [Troubleshooting](docs/devops/troubleshooting.md) / [Runbooks](docs/devops/runbooks/README.md) |
| Contribute code | [Contributing](docs/contributor/development/contributing.md) |

Full documentation index: [`docs/README.md`](docs/README.md)

## License

[Elastic License 2.0 (ELv2)](LICENSE) — free to use, deploy, and modify.
