# Honua Server

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![Container Security](https://github.com/honua-io/honua-server/actions/workflows/container-security.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/container-security.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.5-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-ready-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

**Cloud-native geospatial feature server.** Publish, query, edit, and render spatial data through industry-standard protocols — GeoServices REST (catalog + FeatureServer + MapServer + ImageServer + Geometry Service + GPServer), classic OGC WMS/WFS/WCS/WMTS, STAC API, OGC API (Features, Maps, Tiles, Processes), OData v4, vector tiles, and Terrain-RGB elevation tiles — backed by PostGIS, with an embedded DuckDB provider for read-only analytical and reference workloads.

## Why Honua

- **Multi-protocol** — one server speaks GeoServices REST (catalog, FeatureServer, MapServer, ImageServer, Geometry Service, GPServer), classic OGC WMS/WFS/WCS/WMTS compatibility, STAC API, OGC API Features/Maps/Tiles/Processes, OData v4, MVT, and Terrain-RGB. Connect ArcGIS Pro, QGIS, MapLibre, STAC tooling, Power BI, and Excel to the same data.
- **Cloud-native** — container-first, auto-scaling, OpenTelemetry observability, and IaC templates for Kubernetes, ECS, Lambda, Azure Container Apps, and Azure Functions.
- **No GDAL dependency** — import GeoJSON, Shapefile (zip), GeoPackage, GPX, KML, WKT, FlatGeobuf (`.fgb`), File Geodatabase (`.gdb.zip`), and GeoParquet (`.parquet`, `.geoparquet`) directly. Import from live Esri REST services or public object URLs for migration.
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
# Helm charts live in the separate honua-helm repository:
# https://github.com/honua-io/honua-helm
#
# Follow that repository's chart README for the current chart path and values.
```

**.NET Aspire** (local dev with dashboard for traces, logs, metrics):

```bash
dotnet run --project src/Honua.AppHost
```

## Feedback

We use GitHub Issues as the primary feedback loop for the open-core MVP.

- Report bugs (include screenshots + repro steps): [Open bug report](https://github.com/honua-io/honua-server/issues/new?template=bug.yml)
- Request features or share feedback: [Open feature request](https://github.com/honua-io/honua-server/issues/new?template=feature.yml)

Please use these forms instead of blank issues so reports include enough detail for triage.

## Protocols

| Protocol | Endpoint | Clients |
|---|---|---|
| GeoServices REST Catalog | `/rest/services` and `/rest/info` | ArcGIS clients, service discovery tooling |
| GeoServices REST FeatureServer | `/rest/services/{id}/FeatureServer` | ArcGIS Pro, Esri Leaflet, Esri SDKs, ArcGIS Online |
| GeoServices REST MapServer | `/rest/services/{id}/MapServer` | ArcGIS Pro, Esri Leaflet, Esri map clients |
| GeoServices REST ImageServer | `/rest/services/{id}/ImageServer` | ArcGIS raster/image workflows |
| OGC WCS 2.0.1 | `/rest/services/{id}/ImageServer/WCS`, `/ogc/services/{serviceId}/wcs` | Science, elevation, and coverage clients |
| OGC API Coverages | `/ogc/coverages` | Modern OGC raster/coverage clients |
| GeoServices REST Geometry Service | `/rest/services/geometry` | Esri-compatible geometry operations |
| GeoServices REST GPServer | `/rest/services/{id}/GPServer` | ArcGIS Pro, Esri geoprocessing SDKs (async submit, job status, cancel; synchronous execute pending) |
| MCP Operator JSON-RPC | `/mcp` | AI agents, operator automation, MCP clients |
| STAC API | `/stac`, `/stac/collections`, `/stac/search` | STAC browsers, catalog/search tooling |
| OGC API Features | `/ogc/features` | QGIS, OpenLayers, MapLibre, any OGC client |
| OGC API Maps | `/ogc/maps` | OGC map clients, custom web apps |
| OGC API Tiles | `/ogc/tiles` | QGIS, OpenLayers, MapLibre |
| OGC API Processes | `/ogc/processes` | OGC-compliant process clients |
| OData v4 | `/odata` | Excel, Power BI, Tableau, SAP |
| Vector Tiles (MVT) | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | MapLibre, OpenLayers, Leaflet, Mapbox GL |
| TileJSON | `/tiles/{layerId}/tile.json` | MapLibre |
| Terrain-RGB Elevation Tiles | `/terrain/{datasetId}/tile.json`, `/terrain/{datasetId}/{z}/{x}/{y}.png` | MapLibre/Mapbox `raster-dem` clients |
| MapLibre Styles | `/api/styles/{layerId}.json` | MapLibre |
| Admin API | `/api/v1/admin` | Standalone Admin UI, automation scripts |
| STAC Ops Demo | `/samples/stac-ops` or `/samples/stac-ops/` | Browser *(Development/Test or `HONUA_SERVE_STAC_DEMO=true`; custom images also need demo assets)* |
| OpenAPI (OGC Features) | `/openapi.json` | Any HTTP client |
| OpenAPI (OGC Tiles) | `/ogc/tiles/openapi.json` | Any HTTP client |
| OpenAPI (OGC Coverages) | `/ogc/coverages/openapi.json` | Any HTTP client |
| OpenAPI (OGC Processes) | `/ogc/processes/openapi.json` | Any HTTP client |
| API Explorer (Scalar) | `/docs` | Browser *(dev mode or `HONUA_SERVE_API_DOCS=true`)* |
| Health | `/healthz/live`, `/healthz/ready` | Load balancers, orchestrators |

## Capabilities

**Query and edit** — FeatureServer query, applyEdits, attachments, and related records. OGC transactions (POST/PUT/DELETE). OData CRUD with spatial functions. Query output in JSON, GeoJSON, PBF, FlatGeobuf, GeoParquet, and GeoArrow (Arrow IPC) formats, plus GeoBuf when the configured feature store supports native GeoBuf output, with Accept-header content negotiation.

**Map rendering** — MapServer (export/identify/legend/find/query) plus OGC API Maps endpoints for rendered map images.

**Raster and coverage access** — ImageServer export/identify/tile/catalog/statistics/legend routes, WCS 2.0.1 `GetCapabilities`, `DescribeCoverage`, and `GetCoverage`, plus OGC API Coverages discovery/schema/coverage retrieval over enabled raster layers.

**Terrain elevation tiles** — server-generated Terrain-RGB metadata and 256x256 WebMercator XYZ PNG tiles from registered single-band DEM/raster sources. TileJSON is available at `/terrain/{datasetId}/tile.json`; no-data and uncovered pixels encode as `[0,0,0]` (`-10000m`).

**Geometry operations** — GeoServices Geometry Service endpoints for buffer, simplify, project, intersect, union, clip, difference, area, and length.

**Async geoprocessing** — OGC API Processes landing/conformance, process discovery, async execution, job polling, dismiss, and job results over the canonical geoprocessing runtime. `/ogc/processes/jobs/{jobId}/results` returns `200 OK` with a document-mode JSON body on success (empty `{}` until the canonical process declares value-typed outputs and result storage is wired).

**AI operator workflows** — MCP JSON-RPC on `/mcp` exposes plan validation, dry runs, execution submission, cancellation, and job/result resource reads over the same canonical geoprocessing runtime used by gRPC and GPServer. Natural-language grounding and clarification (`honua_ground_candidates`, `honua_clarify_intent`) are functional over the built-in process and layer catalogs; remaining planning, workspace, and catalog contracts are discoverable as authenticated `not_implemented` placeholders so clients can bind before the upstream services land.

**Workflow orchestration** — Declarative multi-step DAG workflows compose canonical analysis plans into chained, scheduled, and dependency-aware runs. Steps wire upstream artifacts to downstream inputs, support per-step retry policies and failure propagation, and execute over the durable job orchestration substrate. A cron scheduler fires time-triggered workflows with replica-safe deduplication. Requires Redis.

**Catalog discovery** — STAC catalog, collections, items, and item-search with extension-aware metadata, collection license defaults, cross-protocol links to OGC API Features, and conditional GET support on catalog metadata routes.

**Vector tiles** — PostGIS-native `ST_AsMVT` generation with TileJSON metadata and auto-generated MapLibre styles.

**File import** — GeoJSON, Shapefile (zip), GeoPackage, GPX, KML, WKT, FlatGeobuf (`.fgb`), File Geodatabase (`.gdb.zip`), and GeoParquet (`.parquet`, `.geoparquet`). CRS auto-detection and PostGIS-based reprojection.

**Service import** — Migrate existing Esri feature and map services, preserving structure and metadata.

**Admin** — REST API for managing connections, services, layers, relationships, styles (with auto-cartographic suggestions), and import jobs. The Blazor admin UI lives in the separate `honua-server-admin` repo and is deployed as a standalone static app.

**Caching** — Multi-layer: output cache, Redis, in-memory fallback.

**Auth** — API key authentication, OIDC (server-side plumbing), and optional Redis metadata cache.

**Observability** — OpenTelemetry traces and metrics, structured logging, health endpoints.

## Configuration

All settings use environment variables. Copy [`.env.example`](.env.example) for a full reference.

**Required (PostgreSQL provider — default):**
```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
HONUA_ADMIN_PASSWORD="change-me"
```

**DuckDB provider** (no external database):
```bash
DataSource__Provider=duckdb
DuckDB__DatabasePath="/data/layers.duckdb"
```
See the [DuckDB Provider Guide](docs/operator/duckdb-provider.md) for layer and service configuration.

**Common options:**
```bash
HONUA_SERVE_STAC_DEMO=true               # Serve the STAC operations demo at /samples/stac-ops/ (default: on in Development/Test; Docker production builds also require --build-arg HONUA_INCLUDE_STAC_OPS_DEMO=true)
HONUA_SERVE_API_DOCS=true                # Interactive API explorer at /docs (default: on in Development)
HONUA_OBSERVABILITY=true                  # Metrics and health endpoints
HONUA_OPENTELEMETRY=true                  # Distributed tracing
ConnectionStrings__Redis="localhost:6379"  # Redis cache
Cors__AllowedOrigins__0="https://app.example.com"
```

**Database admission tuning:**
```bash
Limits__Connections__MaxConcurrentQueries=6
Limits__Connections__MaxConnectionPoolSize=6
Limits__Connections__MinConnectionPoolSize=2
Limits__Connections__AdaptiveConcurrencyEnabled=false
```

Use bounded database admission as the default production posture. `MaxConcurrentQueries` limits
active database work; `MaxConnectionPoolSize` is the Npgsql pool ceiling. Keep them aligned unless a
measured profile proves that a larger idle pool helps without increasing active PostGIS pressure.
Start with the smallest cap that keeps throughput stable and p95/p99 acceptable, then scale
deliberately with node size and database capacity. Small 4-vCPU benchmark nodes have shown useful
caps in the 4-6 active-query range; larger pools can overfeed PostGIS and make tail latency worse.

Adaptive admission should be treated as an explicit tuning profile, not the default. When enabled,
set `AdaptiveConcurrencyMinQueries`, `AdaptiveConcurrencyInitialQueries`,
`AdaptiveConcurrencyMaxQueries`, `AdaptiveConcurrencyTargetDurationMs`, and
`AdaptiveConcurrencyUpdateIntervalMs`, then monitor `/monitoring/metrics/connection-pool` for the
current limit, queued waiters, duration EWMA, queue-wait EWMA when available, and adjustment count.
Fixed-cap results remain the baseline until adaptive mode repeatedly beats them under the same
workload and machine state.

For multi-node deployments, size from the shared database budget first and divide that budget across
nodes. Redis is useful for metadata caching and required for durable job/workflow orchestration, but
Redis-coordinated query admission is still a research direction; do not add it to the request path
unless multi-node testing shows local fixed or adaptive caps cannot protect the shared PostGIS
budget.

Invalid configuration causes a startup failure with a detailed error message.

## Project Structure

```
src/
  Honua.Core/         Domain models and abstractions
  Honua.Postgres/     PostGIS implementation
  Honua.DuckDB/       DuckDB read-only provider (analytics, GeoParquet, edge)
  Honua.Server/       HTTP host (Minimal APIs, vertical slices)
  Honua.AppHost/      .NET Aspire orchestration
  Honua.ServiceDefaults/  Shared service configuration
```

## Control Plane Direction

Honua's admin UI and admin API are intended to become the foundation of a Honua-managed GitOps control plane.

- Honua is not standardizing on Flux or Argo CD as its primary rollout controller.
- Helm and Terraform remain packaging and infrastructure surfaces.
- Change management, deploy coordination, and instance lifecycle workflows are expected to live in the Honua control plane.
- The public admin API is the substrate for those workflows; operator-grade AI DevOps/copilot tooling may be delivered through private enterprise surfaces on top of it rather than through the open-core server repository.

## Documentation

Full documentation: **[honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/)**

| I am a... | Start here |
|---|---|
| **Server Operator** | [Operator Guide](docs/operator/README.md) — deploy, configure, monitor |
| **GIS Professional** | [GIS User Guide](docs/gis/README.md) — connect desktop apps, consume services |
| **Developer** | [Developer Guide](docs/developer/README.md) — APIs, SDKs, integrations |
| **Contributor** | [Contributor Guide](docs/contributor/README.md) — architecture, testing, PRs |

| I want to... | Go to |
|---|---|
| Deploy to production | [Infrastructure](docs/operator/infrastructure.md) |
| Connect QGIS | [QGIS Tutorial](docs/gis/tutorials/qgis-getting-started.md) |
| See protocol coverage | [Protocols Overview](docs/gis/STANDARDS_APIS.md) |
| Use the admin API | [Control Plane API](docs/operator/CONTROL_PLANE_API.md) |
| Check compatibility | [MVP Compatibility Contract](docs/gis/MVP_COMPATIBILITY_CONTRACT.md) |
| Run the STAC ops sample | [STAC Ops Demo](samples/Honua.StacOpsDemo/README.md) |
| Contribute code | [Contributing](docs/contributor/development/contributing.md) |

## License

[Elastic License 2.0 (ELv2)](LICENSE) — free to use, deploy, and modify.
