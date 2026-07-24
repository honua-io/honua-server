# Honua Server

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![Security Nightly](https://github.com/honua-io/honua-server/actions/workflows/security-nightly.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/security-nightly.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.5-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-honuaio%2Fhonua--server-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

**Cloud-native geospatial server.** One container exposes the same PostGIS-backed data through every major GIS protocol — GeoServices REST (FeatureServer, MapServer, ImageServer, Geometry, GPServer), OGC API (Features, Maps, Tiles, Coverages, Processes), classic OGC WMS/WFS/WMTS/WCS, STAC, OData v4, vector tiles (MVT/TileJSON), Terrain-RGB and elevation APIs, 3D Tiles, MCP for AI agents, and gRPC. ArcGIS Pro and Esri SDK clients connect unmodified; QGIS, MapLibre, Excel, and Power BI hit the same layers — no ETL, no duplication, no GDAL toolchain to install.

## Status

Honua Server is open core under the [Elastic License 2.0](LICENSE), heading to a v1.0 GA. The GA-tier core (protocol surfaces, editing, imports, auth, operations) is tracked on the [public roadmap](ROADMAP.md) — upvote what you want next. The server runs in Community mode with no license file; paid Pro/Enterprise features activate only via signed entitlements (see [Editions and licensing](docs/concepts/editions-and-licensing.md)).

## Quick start

**Docker Compose** (requires Docker with Compose v2):

```bash
git clone https://github.com/honua-io/honua-server.git && cd honua-server
docker compose up -d
docker compose ps
```

Open <http://localhost:8080/healthz/ready> in a browser and wait for `Ready`.

PostGIS, Redis, and Honua Server start automatically; migrations run on first boot. HTTP/1 REST and gRPC-Web are at `http://localhost:8080`, native h2c gRPC at `http://localhost:8081`. Continue with the [quickstart](docs/get-started/quickstart.md) to import a dataset and see it on a map, or add the web Console with `docker compose --profile console up -d` (set `HONUA_CONSOLE_IMAGE` to a published [honua-console](https://github.com/honua-io/honua-console) image; Operate serves at `http://localhost:5174/operate`).

**Pre-built image** (bring your own PostGIS):

```bash
docker run -p 8080:8080 -p 8081:8081 \
  -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  honuaio/honua-server:latest
```

**Kubernetes** — deploy with the Helm chart in [honua-helm](https://github.com/honua-io/honua-helm); see the [Kubernetes guide](docs/guides/deploy/kubernetes.md).

**Local development** — .NET Aspire with a dashboard for traces, logs, and metrics:

```bash
dotnet run --project src/Honua.AppHost
```

To run geoprocessing jobs locally (in-process, no cloud), use the [local GP dev quickstart](docs/guides/query-analyze/gp-local-dev-quickstart.md). For self-hosted pilots, run the [pilot onboarding runbook](docs/guides/deploy/pilot-onboarding-runbook.md) before handing a deployment to another team.

## Protocols

Every published layer is reachable through every protocol its service enables. The canonical matrix with examples lives in [Protocols](docs/concepts/protocols.md).

| Protocol | Endpoint | Typical clients |
|---|---|---|
| GeoServices FeatureServer | `/rest/services/{id}/FeatureServer` | ArcGIS Pro, Esri SDKs, Esri Leaflet |
| GeoServices MapServer | `/rest/services/{id}/MapServer` | ArcGIS Pro, Esri map clients |
| GeoServices ImageServer | `/rest/services/{id}/ImageServer` | ArcGIS raster workflows |
| GeoServices Geometry Service | `/rest/services/Utilities/Geometry/GeometryServer` | Esri SDKs (buffer, project, intersect, …) |
| GeoServices GPServer | `/rest/services/{id}/GPServer` | ArcGIS Pro, async geoprocessing clients |
| Portal token issuance | `/sharing/rest/generateToken` | Esri clients using username/password tokens |
| OGC API Features | `/ogc/features` | QGIS, GDAL, OpenLayers, any OGC client |
| OGC API Maps | `/ogc/maps` | OGC map clients |
| OGC API Tiles | `/ogc/tiles` | QGIS, MapLibre |
| OGC API Coverages | `/ogc/coverages` | Science and raster tooling |
| OGC API Processes | `/ogc/processes` | OGC processing clients |
| WMS 1.3 / 1.1.1 | `/ogc/services/{id}/wms`, `/rest/services/{id}/MapServer/WMS` | QGIS, legacy OGC clients |
| WFS 2.0 / 1.1.0 / 1.0.0 | `/wfs` | QGIS, GDAL/OGR, legacy stacks |
| WCS 2.0.1 | `/ogc/services/{id}/wcs`, `/rest/services/{id}/ImageServer/WCS` | Science, elevation, coverage clients |
| WMTS 1.0 | `/ogc/services/{id}/wmts`, `/rest/services/{id}/MapServer/WMTS` | QGIS, legacy tile clients |
| OData v4 | `/odata` | Excel, Power BI, Tableau, SAP |
| STAC API | `/stac` | STAC browsers, catalog/search tooling |
| Vector tiles (MVT) + TileJSON | `/tiles/{layerId}/{z}/{x}/{y}.mvt`, `/tiles/{layerId}/tile.json` | MapLibre, OpenLayers, Leaflet |
| Terrain-RGB + elevation API | `/terrain/{datasetId}/…`, `/elevation/{datasetId}/…` | MapLibre `raster-dem`, field apps |
| 3D Tiles scenes | `/scenes/{sceneId}/tileset.json` | CesiumJS, 3D Tiles clients |
| PMTiles | `/api/v1/tiles/pmtiles/{artifactId}` | MapLibre, serverless/CDN tile hosting |
| MCP (JSON-RPC) | `/mcp` | AI agents, operator automation, MCP clients |
| gRPC (`geospatial.v1`) | port `8081` (h2c), gRPC-Web on `8080` | Honua SDKs, mobile, services |

Plus operational surfaces: health probes (`/healthz/live`, `/healthz/ready`), OpenAPI documents per OGC API, an interactive API explorer at `/docs` (dev mode or `HONUA_SERVE_API_DOCS=true`), the admin API (`/api/v1/admin`), and a capability manifest (`/api/v1/capabilities/manifest`) for clients to discover what a deployment supports.

## Compliance

- **OGC CITE:** 952 / 952 passing across 11 conformance suites (OGC API Features 1.0, OGC API Tiles 1.0, GeoPackage 1.2, GML 3.2, KML 2.2, WFS 1.0/1.1/2.0, WCS 2.0, WMS 1.3, WMTS 1.0) on `trunk` — see [docs/cite-status.md](docs/cite-status.md) for the authoritative snapshot and [OGC conformance evidence](docs/reference/compatibility/ogc-conformance.md) for suite-by-suite evidence.
- **Client compatibility:** the supported client x protocol matrix is the [compatibility contract](docs/reference/compatibility/clients.md); Esri-side parity is tracked in [GeoServices parity](docs/reference/compatibility/geoservices-parity.md).
- **gRPC stability:** versioning, deprecation, and stability guarantees for the `geospatial.v1` surface are defined in the [gRPC reference](docs/reference/protocols/grpc.md).
- **Control plane stability:** admin/control-plane API versioning is governed by [versioning and support](docs/reference/versioning-and-support.md).

## Key capabilities

- **Query and edit** — FeatureServer query/applyEdits/attachments/related records, OGC API Features CRUD with CQL2, WFS 2.0 transactions, OData CRUD with spatial functions (`geo.distance`, `geo.intersects`, `$batch`). Output as JSON, GeoJSON, PBF, FlatGeobuf, GeoParquet, and GeoArrow.
- **Esri migration and coexistence** — ArcGIS Pro and Esri SDK clients connect unmodified. Import public ArcGIS REST services into PostGIS; scan ArcGIS Server and GeoServer for deterministic migration inventories. See [Migrate from ArcGIS Server](docs/guides/migrate/from-arcgis-server.md) and [from GeoServer](docs/guides/migrate/from-geoserver.md).
- **No GDAL dependency** — import GeoJSON, Shapefile (zip), GeoPackage, GPX, KML, WKT, FlatGeobuf, File Geodatabase (`.gdb.zip`), and GeoParquet directly, with CRS auto-detection and PostGIS reprojection.
- **Rendering and rasters** — MapServer export/identify/legend, OGC API Maps, ImageServer, WCS, OGC API Coverages, cloud-optimized GeoTIFFs registered in place from S3/Azure, and server-generated Terrain-RGB elevation tiles.
- **Geoprocessing and workflows** — one canonical async job runtime behind GPServer, OGC API Processes, gRPC, and MCP; declarative multi-step DAG workflows with retries and cron scheduling (Redis required for durable jobs).
- **AI-operable** — the `/mcp` surface implements the open [geospatial-mcp](https://github.com/honua-io/geospatial-mcp) standard so agents can validate plans, dry-run, execute, and read results with the same authorization as any other client. See [Connect AI agents](docs/guides/connect/ai-agents-mcp.md).
- **Cloud-native operations** — container-first and stateless; multi-layer caching (output cache, Redis, in-memory fallback); OpenTelemetry traces and metrics; API-key, OIDC, and optional mTLS auth; a server-computed operate loop for humans, Console, and agents ([Operating Honua](docs/guides/operate/README.md)).

The admin API (`/api/v1/admin`) manages connections, services, layers, styles, and import jobs; the web admin UI lives in [honua-console](https://github.com/honua-io/honua-console). The admin API is also the substrate for Honua's managed control-plane direction — change management and instance lifecycle workflows build on it rather than on a third-party GitOps controller.

## Data providers

PostGIS is the primary read/write backend. Additional providers serve data in place, read/query-only, through the same protocol surfaces:

| Provider | Access |
|---|---|
| [PostGIS](docs/reference/configuration/data-sources/postgis.md) | Full read/write (default) |
| [DuckDB](docs/reference/configuration/data-sources/duckdb.md) | Read-only, embedded — analytics and reference layers, no external database |
| [SQL Server](docs/reference/configuration/data-sources/sql-server.md) | Read/query-only (`geometry`/`geography` tables) |
| [Oracle](docs/reference/configuration/data-sources/oracle.md) | Read/query-only (standard `SDO_GEOMETRY`) |
| [MySQL / MariaDB](docs/reference/configuration/data-sources/mysql-mariadb.md) | Read/query-only (MySQL 8.0.11+, MariaDB 10.6+) |
| [Amazon Redshift](docs/reference/configuration/data-sources/redshift.md) | Read/query-only (native Redshift spatial) |
| [Snowflake](docs/reference/configuration/data-sources/snowflake.md) | Read/query-only (`GEOGRAPHY`/`GEOMETRY`) |
| [Databricks](docs/reference/configuration/data-sources/databricks.md) | Read/query-only (SQL Warehouse, best-effort) |

Per-provider capabilities, selection variables, and limitations are in the [data sources reference](docs/reference/configuration/data-sources/README.md).

## Configuration

All settings are environment variables. Copy [`.env.example`](.env.example) for a full annotated reference, or see the [environment variable reference](docs/reference/configuration/environment-variables.md).

**Required (PostgreSQL provider — default):**

```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
HONUA_ADMIN_PASSWORD="change-me"
```

**Common options:**

```bash
ConnectionStrings__Redis="localhost:6379"   # shared caches; required for durable jobs/workflows
HONUA_OBSERVABILITY=true                    # metrics and health endpoints
HONUA_OPENTELEMETRY=true                    # distributed tracing
Cors__AllowedOrigins__0="https://app.example.com"
```

**Production tuning** — bounded database admission is the default production posture: keep `Limits__Connections__MaxConcurrentQueries` aligned with the pool size and size from the shared database budget across replicas (small 4-vCPU nodes profile best in the 4–6 active-query range; larger pools can overfeed PostGIS and worsen tail latency). Adaptive admission (`AdaptiveConcurrencyEnabled`) is an explicit tuning profile, not the default — monitor `/monitoring/metrics/connection-pool` and keep fixed-cap results as the baseline. Full guidance: [Scale and tune performance](docs/guides/deploy/scaling-and-performance.md) and [admission and pooling variables](docs/reference/configuration/environment-variables.md#admission-and-pooling).

Invalid configuration fails startup with a detailed error message.

## Documentation

Full hosted documentation: **[honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/)**. The in-repo table of contents is [docs/README.md](docs/README.md). Frequent destinations:

| I want to… | Go to |
|---|---|
| Import a dataset and see a map in 10 minutes | [Quickstart](docs/get-started/quickstart.md) |
| Deploy to production | [Docker Compose](docs/guides/deploy/docker-compose.md) · [Kubernetes](docs/guides/deploy/kubernetes.md) · [Cloud deployments](docs/guides/deploy/cloud-deployments.md) |
| Operate, monitor, back up, scale | [Operating Honua](docs/guides/operate/README.md) · [Monitoring](docs/guides/deploy/monitoring.md) |
| Connect a client | [ArcGIS Pro](docs/guides/connect/arcgis-pro.md) · [QGIS](docs/guides/connect/qgis.md) · [Excel/Power BI](docs/guides/connect/excel-power-bi.md) · [MapLibre](docs/guides/connect/maplibre-web-maps.md) · [AI agents (MCP)](docs/guides/connect/ai-agents-mcp.md) |
| Migrate from Esri or GeoServer | [From ArcGIS Server](docs/guides/migrate/from-arcgis-server.md) · [From GeoServer](docs/guides/migrate/from-geoserver.md) · [ArcGIS apps and SDKs](docs/guides/migrate/arcgis-apps-and-sdks.md) |
| Understand the architecture | [Architecture](docs/concepts/architecture.md) · [Protocols](docs/concepts/protocols.md) · [Data model](docs/concepts/data-model.md) |
| Use the admin API | [Control plane API](docs/reference/admin-api/overview.md) |
| Check client compatibility | [Compatibility contract](docs/reference/compatibility/clients.md) |
| Contribute code | [Contributing](docs/internal/contributor/development/contributing.md) · [AGENTS.md](AGENTS.md) |

## Related repositories

Honua is a family of repos around this server — the full map is in [Ecosystem](docs/concepts/ecosystem.md):

- [honua-console](https://github.com/honua-io/honua-console) — web console (Studio, Catalog, Operate, Share) over the admin API; the admin/UI home
- [honua-helm](https://github.com/honua-io/honua-helm) — Helm chart, the Kubernetes deploy path
- [honua-sdk-js](https://github.com/honua-io/honua-sdk-js) · [honua-sdk-python](https://github.com/honua-io/honua-sdk-python) · [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) — client SDKs generated from the same admin contract
- [honua-mobile](https://github.com/honua-io/honua-mobile) — **Experimental** reusable .NET MAUI SDK and map/control foundation for offline field workflows (Apache-2.0)
- [honua-collect](https://github.com/honua-io/honua-collect) — **Experimental** full end-user field-collection app built on `honua-mobile` (ELv2)
- [geospatial-grpc](https://github.com/honua-io/geospatial-grpc) — open gRPC protocol standard the server's `geospatial.v1` surface implements
- [geospatial-mcp](https://github.com/honua-io/geospatial-mcp) — open geospatial MCP standard behind `/mcp`

## Feedback

GitHub Issues are the primary feedback loop for the open-core MVP. Please use the forms so reports include enough detail for triage:

- [Report a bug](https://github.com/honua-io/honua-server/issues/new?template=bug.yml) (include screenshots and repro steps)
- [Request a feature](https://github.com/honua-io/honua-server/issues/new?template=feature.yml) — or upvote existing [roadmap items](ROADMAP.md)

## Security

See [SECURITY.md](SECURITY.md) for supported versions and how to report vulnerabilities (security@honua.io).

## License

[Elastic License 2.0 (ELv2)](LICENSE) — free to use, deploy, and modify.
