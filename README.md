# Honua Server

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![Security Nightly](https://github.com/honua-io/honua-server/actions/workflows/security-nightly.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/security-nightly.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.4--3.6-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-honuaio%2Fhonua--server-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

**Cloud-native geospatial server.** One container exposes the same PostGIS-backed data through every major GIS protocol — GeoServices REST (FeatureServer, MapServer, ImageServer, Geometry, GPServer), OGC API (Features, Maps, Tiles, Coverages, Processes), classic OGC WMS/WFS/WMTS/WCS, STAC, OData v4, vector tiles (MVT/TileJSON), Terrain-RGB and elevation APIs, 3D Tiles, MCP for AI agents, and gRPC. Honua provides protocol-level compatibility for selected, operation-scoped Esri client workflows; support is bounded by the published [GeoServices parity matrix](docs/reference/compatibility/geoservices-parity.md) and [cross-client certification matrix](docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md). QGIS, MapLibre, Excel, and Power BI use the same layers — no ETL, no duplication, no GDAL toolchain to install.

## Status

Honua Server is source-available under the [Fair Core License, Version 1.0, ALv2 Future License](LICENSE) (FCL-1.0-ALv2); each version converts to Apache-2.0 two years after release. Current source and nightly images are **evaluation builds**. A supported versioned server release has not yet been published; 2026.1 release qualification is in progress. See [server releases](https://github.com/honua-io/honua-server/releases) for published artifacts and release notes, and [versioning and support](docs/reference/versioning-and-support.md) for the compatibility and recovery policy. Feature requests and priorities go in [issues](https://github.com/honua-io/honua-server/issues).

For a reproducible evaluation, retain the server image digest and source revision, exact client package versions, deployment configuration, and the evidence for that combination. Historical test results and capability maturity labels do not qualify a newer nightly image. Use the release-specific manifest and evidence when a supported release is published.

The documented **2026.1 deployment posture disables licensing** with `Licensing__Mode=Disabled`: no license file is required and catalog entitlements are granted, while authorization, runtime dependencies, and Preview/Experimental boundaries still apply. The server default remains `Enabled`, with Community when no license is configured; **(Pro)** / **(Enterprise)** labels below describe that enabled mode. See [Editions and licensing](docs/concepts/editions-and-licensing.md) for the version-specific configuration and source-license restrictions.

## Quick start

**Start with an agent:** “Set up Honua in Docker on this machine.” Follow
[Get started with AI](docs/get-started/with-ai.md) from prerequisite checks to
a verified local dataset, realtime maps/apps, and publication back to Honua.

**Docker Compose** (requires Docker with Compose v2 and Python 3):

```bash
git clone https://github.com/honua-io/honua-server.git && cd honua-server
python3 scripts/docker/quickstart.py
docker compose ps
```

Open <http://localhost:8080/healthz/ready> in a browser and wait for `Ready`.

The bootstrap generates per-install datastore passwords in a private `.env` file; retain it with your volumes. All published ports default to loopback. The default `docker-compose.yml` builds the server image from source on first run, so expect the first `up` to take a few minutes; for an instant start use the pre-built image below. PostGIS, Redis, and Honua Server start automatically; migrations run on first boot. HTTP/1 REST and gRPC-Web are at `http://localhost:8080`, native h2c gRPC at `http://localhost:8081`. Use the [package quickstart](docs/get-started/quickstart.md) for an isolated install, import, publish, and verified SDK query; continue this source installation with the [source quickstart](docs/internal/developer/source-quickstart.md) to see a browser map. To add Console and its embedded Studio editor, run `docker compose --profile console up -d` and open <http://localhost:5174>. Compose defaults to the release-manifest Console image pinned by digest; `HONUA_CONSOLE_IMAGE` can select another compatible image. See the [source quickstart](docs/internal/developer/source-quickstart.md) for operator sign-in and callback configuration.

**Pre-built image** (bring your own PostGIS):

```bash
docker run -p 8080:8080 -p 8081:8081 \
  -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  -e Security__ConnectionEncryption__MasterKey="change-me-random-string-32-plus-characters" \
  honuaio/honua-server:nightly
```

`:nightly` and `:trunk` are moving evaluation tags. Pin the registry image digest for a repeatable installation and record its source revision; a dated tag or a successful historical test run does not establish release qualification. Consult the published release manifest and notes for a supported combination when one becomes available.

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
| GeoServices ImageServer **(Preview)** | `/rest/services/{id}/ImageServer` | ArcGIS raster workflows |
| GeoServices Geometry Service | `/rest/services/Utilities/Geometry/GeometryServer` | Esri SDKs (buffer, project, intersect, …) |
| GeoServices GPServer | `/rest/services/{id}/GPServer` | ArcGIS Pro, async geoprocessing clients |
| GeoServices GeocodeServer | `/rest/services/{locator}/GeocodeServer` | Esri geocoding clients (`findAddressCandidates`) |
| GeoServices VectorTileServer | `/rest/services/{id}/VectorTileServer` | Esri vector-tile clients, ArcGIS SDKs |
| GeoServices NAServer **(Pro)** | `/rest/services/{id}/NAServer` | Esri routing / network-analysis clients |
| GeoServices VersionManagementServer **(Pro, experimental)** | `/rest/services/{id}/VersionManagementServer` | Esri branch-versioning editing workflows |
| Portal token issuance | `/sharing/rest/generateToken` | Esri clients using username/password tokens |
| OGC API Features | `/ogc/features` | QGIS, GDAL, OpenLayers, any OGC client |
| OGC API Maps | `/ogc/maps` | OGC map clients |
| OGC API Tiles | `/ogc/tiles` | QGIS, MapLibre |
| OGC API Coverages **(Preview)** | `/ogc/coverages` | Science and raster tooling |
| OGC API Processes | `/ogc/processes` | OGC processing clients |
| OGC API Records | `/ogc/records` | Catalog / metadata search clients |
| OGC API Environmental Data Retrieval (EDR) **(Preview)** | `/edr` | Environmental and scientific data clients |
| OGC API Styles | `/ogc/styles` | Style-aware map clients |
| OGC SensorThings v1.1 | `/sta/v1.1` | IoT / observations clients |
| WMS 1.3 / 1.1.1 | `/ogc/services/{id}/wms`, `/rest/services/{id}/MapServer/WMS` | QGIS, legacy OGC clients |
| WFS 2.0 / 1.1.0 / 1.0.0 | `/wfs` | QGIS, GDAL/OGR, legacy stacks |
| WCS 2.0.1 | `/ogc/services/{id}/wcs`, `/ogc/wcs/{id}` (ArcGIS Pro), `/rest/services/{id}/ImageServer/WCS` | Science, elevation, coverage clients |
| WMTS 1.0 **(Preview)** | `/ogc/services/{id}/wmts`, `/rest/services/{id}/MapServer/WMTS` | QGIS, legacy tile clients |
| WPS 2.0 | `/wps` | Classic OGC processing clients |
| OData v4 | `/odata` | Excel, Power BI, Tableau, SAP |
| STAC API | `/stac` | STAC browsers, catalog/search tooling |
| Vector tiles (MVT) + TileJSON | `/tiles/{layerId}/{z}/{x}/{y}.mvt`, `/tiles/{layerId}/tile.json` | MapLibre, OpenLayers, Leaflet |
| Terrain-RGB + elevation API | `/terrain/{datasetId}/…`, `/elevation/{datasetId}/…` | MapLibre `raster-dem`, field apps |
| 3D Tiles scenes | `/scenes/{sceneId}/tileset.json` | CesiumJS, 3D Tiles clients |
| PMTiles | `/api/v1/tiles/pmtiles/{artifactId}` | MapLibre, serverless/CDN tile hosting |
| MCP (JSON-RPC) | `/mcp` | AI agents, operator automation, MCP clients |
| gRPC (`geospatial.v1`) | port `8081` (h2c), gRPC-Web on `8080` | Honua SDKs, mobile, services |

Path shapes differ on purpose: each protocol carries exactly the scope its own specification makes the addressable unit. WMS, WMTS and WCS render a composition, so a service is in the path; WFS returns features that `TYPENAMES` already identifies, so it is served unscoped at `/wfs` with no service-scoped form and no GeoServices alias. The rule, and why adding one for symmetry would be a regression, is [ADR-0079](docs/internal/contributor/adr/0079-protocol-route-scoping.md).

With licensing **Enabled**, surfaces marked **(Pro)** / **(Enterprise)** require the corresponding signed entitlements; everything unmarked is Community. The 2026.1 **Disabled** deployment posture grants these entitlements while preserving maturity and runtime requirements. The machine-readable capability vocabulary behind this table is [`docs/gis/data/capability-keys.v1.json`](docs/gis/data/capability-keys.v1.json) (generated from `CapabilityKeyCatalog.cs`) — keep the table in sync with it.

Plus operational surfaces: health probes (`/healthz/live`, `/healthz/ready`), OpenAPI documents per OGC API, an interactive API explorer at `/docs` (dev mode or `HONUA_SERVE_API_DOCS=true`), the admin API (`/api/v1/admin`), and a capability manifest (`/api/v1/capabilities/manifest`) for clients to discover what a deployment supports.

## Compliance

- **OGC CITE historical evidence:** the 2026-09-18 run at source `80e23bedfe8ff7b43362c8d8ea22bfae1756df7d` passed 1138 / 1138 assertions across 14 suites, including WFS 2.0 `basic` at 167/167. This result covers that source and those profiles; newer images need their own evidence. See the [authoritative snapshot and run receipt](docs/cite-status.md) and [OGC conformance evidence](docs/reference/compatibility/ogc-conformance.md).
- **Client compatibility:** the supported client x protocol matrix — including known limitations — is the [compatibility contract](docs/reference/compatibility/clients.md); Esri-side parity is tracked in [GeoServices parity](docs/reference/compatibility/geoservices-parity.md).
- **gRPC stability:** gRPC remains supported for 2026.1. `Geospatial.Grpc` is on nuget.org (`dotnet add package Geospatial.Grpc`). See the [gRPC reference](docs/reference/protocols/grpc.md#net-package-installation) for versioning and stability guarantees.
- **Control plane stability:** admin/control-plane API versioning is governed by [versioning and support](docs/reference/versioning-and-support.md).

## Key capabilities

- **Query and edit** — FeatureServer query/applyEdits/attachments/related records, OGC API Features CRUD with CQL2, WFS 2.0 transactions, OData CRUD with spatial functions (`geo.distance`, `geo.intersects`, `$batch`). FeatureServer applyEdits is **(Pro)**; edits through the open protocols (OGC API Features, WFS-T, OData, gRPC) stay Community. Output as JSON, GeoJSON, PBF, FlatGeobuf, GeoParquet, and GeoArrow.
- **Esri migration and coexistence** — Honua provides protocol-level compatibility for selected, operation-scoped ArcGIS Pro and Esri SDK workflows, bounded by the published [GeoServices parity matrix](docs/reference/compatibility/geoservices-parity.md) and [cross-client certification matrix](docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md). Import public ArcGIS REST and GeoServer services into PostGIS (service imports are **(Enterprise)**); scan ArcGIS Server and GeoServer for deterministic migration inventories. See [Migrate from ArcGIS Server](docs/guides/migrate/from-arcgis-server.md) and [from GeoServer](docs/guides/migrate/from-geoserver.md).
- **No GDAL required on the server** — import GeoJSON, Shapefile (zip), GeoPackage, GPX, KML, WKT, FlatGeobuf, File Geodatabase (`.gdb.zip`), and GeoParquet directly, with CRS auto-detection and PostGIS reprojection; the serving container ships no GDAL, while optional geoprocessing worker images bundle it separately.
- **Rendering and rasters** — MapServer export/identify/legend, OGC API Maps, WCS, and server-generated Terrain-RGB elevation tiles (Community); ImageServer and OGC API Coverages **(Preview)**. Cloud-optimized GeoTIFFs registered in place from S3/Azure are served under **(Pro)**.
- **Geoprocessing and workflows** — one canonical async job runtime behind GPServer, OGC API Processes, gRPC, and MCP; declarative multi-step DAG workflows with retries and cron scheduling (Redis required for durable jobs).
- **AI-operable** — the `/mcp` surface implements the open [geospatial-mcp](https://github.com/honua-io/geospatial-mcp) standard so agents can validate plans, dry-run, execute, and read results with the same authorization as any other client. MCP discovery/query and spec artifacts are Community; agent operations and spec-apply execution are **(Pro)**; approval workflows are **(Enterprise)**. See [Connect AI agents](docs/guides/connect/ai-agents-mcp.md).
- **Cloud-native operations** — container-first and stateless; multi-layer caching with in-memory fallback (output cache and Redis caching are **(Pro)**); OpenTelemetry traces and metrics; API-key auth, OIDC SSO **(Pro)** — multi-provider OIDC, SAML 2.0, and SCIM 2.0 are **(Enterprise)** — and experimental mTLS client-certificate auth **(Enterprise)** (off by default, not on the GA path); a server-computed operate loop for humans, Console, and agents ([Operating Honua](docs/guides/operate/README.md)).

- **Spatial analytics** — viewshed, line-of-sight, and sun/shadow analysis; H3 aggregation; density, clustering, spatial join, buffer-aggregate, and slice, all **(Pro)**; analysis content and reporting stay Community. These are **synchronous** — the elevation analytics are `POST /elevation/{datasetId}/…` calls and the vector ones are FeatureServer query extensions, not async jobs. The long-running GDAL-worker operations are catalogued separately in [geoprocessing operations](docs/reference/geoprocessing-operations.md).
- **Geocoding** — forward and reverse geocoding against a [local PostGIS-backed geocoder](docs/reference/geocoding/local-postgis-geocoder.md) (Community), batch geocoding **(Enterprise)**, and provider failover **(Pro)**, served to Esri clients through GeocodeServer ([provider parity](docs/reference/geocoding/geocode-server-parity.md)).
- **Routing and network analysis** — routes, service areas, closest facility, origin-destination cost matrices, and location-allocation through NAServer **(Pro)**.
- **Time** — temporal extent discovery and time filtering (Community), temporal histograms, time-series tiles, and an SDK-side animation API **(Pro)**, so time-aware layers answer "when" as well as "where". See [Work with time](docs/guides/query-analyze/work-with-time.md).
- **Real-time, geofencing and alerting** — feature subscriptions for change streams **(Pro)**; an alert-rule evaluation engine **(Pro, Preview)** whose trigger types are enter/exit geofences **(Pro, Preview)** and dwell and threshold triggers **(Enterprise, Preview)**; delivery to a webhook **(Pro, Preview)** or to Slack, Teams, email, AWS SNS, Azure EventGrid, or a digest **(Enterprise, Preview)**. See [React to changes](docs/guides/edit/react-to-changes.md).
- **Field operations** — [form packages](docs/reference/admin-api/forms.md) for data collection (Community) and offline sync for disconnected field work **(Pro, Preview)**; the mobile SDK and collection app live in [honua-mobile](https://github.com/honua-io/honua-mobile) and [honua-collect](https://github.com/honua-io/honua-collect).
- **Collaboration** — multi-user saved-map sessions with an [operation log](docs/reference/saved-map-collaboration-op-log.md) (Community), and [feature locks](docs/reference/collaboration/feature-locks.md) for concurrent editing (Community) — note that the shipped lock authorizer **denies every claim** until you register your own `IFeatureLockAuthorizer`, so the manifest reports `available: false` out of the box. Leases are also single-node.
- **Data enrichment** — enrich your features with attributes from a registered reference dataset by spatial join ([data enrichment](docs/operator/data-enrichment.md), Community).
- **Static maps** — server-rendered map images (Community) for reports, tiles-free embeds and notifications; high-DPI output, large dimensions and rich overlays are **(Pro)**.
- **Raster configuration** — temporal mosaics **(Pro)** on top of the COG serving above. Cloud-storage provider selection (S3 / Azure Blob) is startup configuration (`HONUA_STORAGE_PROVIDER`), not a licensed gate — paid enforcement for cloud-served rasters happens at request time through COG serving.
- **Print and export** — server-rendered PDF output **(Pro)** from configurable layout templates **(Pro)**.
- **3D (not on the 2026.1 path)** — owner decision D0.2 moved 3D wholesale to 2026.2. The scene catalog is **deferred**, OGC 3D Tiles serving to CesiumJS is **experimental**, I3S scene serving is **(Enterprise)** and **experimental**, and BIM and point-cloud ingest are **(Enterprise)** and **experimental**. Treat all of it as preview. See [Publish 3D scenes](docs/guides/publish/publish-3d-scenes.md).

The admin API (`/api/v1/admin`) manages connections, services, layers, styles, and import jobs; the web admin UI lives in [honua-console](https://github.com/honua-io/honua-console). The admin API is also the substrate for self-managed control-plane workflows: change management and instance lifecycle automation build on it rather than on a third-party GitOps controller.

## Data providers

PostGIS is the primary read/write backend. Additional providers serve data in place, read/query-only, through the same protocol surfaces:

| Provider | Access |
|---|---|
| [PostGIS](docs/reference/configuration/data-sources/postgis.md) | Full read/write (default) |
| [DuckDB](docs/reference/configuration/data-sources/duckdb.md) | Read-only, embedded — analytics and reference layers, no external database |
| [SQL Server](docs/reference/configuration/data-sources/sql-server.md) | Read/query-only (`geometry`/`geography` tables) |
| [Oracle](docs/reference/configuration/data-sources/oracle.md) | Read/query-only (standard `SDO_GEOMETRY`) |
| [MySQL / MariaDB](docs/reference/configuration/data-sources/mysql-mariadb.md) | Read/query-only (MySQL 8.0.12+, MariaDB 10.6+) |
| [Amazon Redshift](docs/reference/configuration/data-sources/redshift.md) | Read/query-only — **Enterprise, experimental**, off by default (native Redshift spatial) |
| [Snowflake](docs/reference/configuration/data-sources/snowflake.md) | Read/query-only — **Enterprise, experimental**, off by default (`GEOGRAPHY`/`GEOMETRY`) |
| [Databricks](docs/reference/configuration/data-sources/databricks.md) | Read/query-only — **Enterprise, experimental**, off by default (SQL Warehouse, best-effort) |

Per-provider capabilities, selection variables, and limitations are in the [data sources reference](docs/reference/configuration/data-sources/README.md).

## Configuration

All settings are environment variables. Copy [`.env.example`](.env.example) for a full annotated reference, or see the [environment variable reference](docs/reference/configuration/environment-variables.md).

**Required (PostgreSQL provider — default):**

```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
HONUA_ADMIN_PASSWORD="change-me"
# Encrypts stored data-connection credentials (>=32 chars). Required outside
# Development — startup fails without it; in dev, registering a data
# connection fails until it is set.
Security__ConnectionEncryption__MasterKey="change-me-random-string-32-plus-characters"
```

**Common options:**

```bash
ConnectionStrings__Redis="localhost:6379"   # optional; shared caches, and required for durable jobs/workflows
HONUA_OBSERVABILITY=true                    # metrics endpoints (health probes are always on)
HONUA_OPENTELEMETRY=true                    # distributed tracing
Cors__AllowedOrigins__0="https://app.example.com"
```

PostGIS is mandatory; Redis is optional. A single-node install with no `ConnectionStrings__Redis` starts, reports `Ready`, and serves the full read path with an in-memory cache — it refuses durable jobs, workflows, Studio drafts, and governed operation proposals and approvals with a typed `dependency-unavailable` error. Redis is therefore required for the governed control plane; the capability manifest reports this as `operations.proposals` and `jobs.runner` with `available: false`. See [Redis is optional; PostGIS is not](docs/guides/deploy/docker-compose.md#redis-is-optional-postgis-is-not).

**Production tuning** — bounded database admission is the default production posture: keep `Limits__Connections__MaxConcurrentQueries` aligned with the pool size and size from the shared database budget across replicas (small 4-vCPU nodes profile best in the 4–6 active-query range; larger pools can overfeed PostGIS and worsen tail latency). Adaptive admission (`Limits__Connections__AdaptiveConcurrencyEnabled`) is an explicit tuning profile, not the default — monitor `/monitoring/metrics/connection-pool` and keep fixed-cap results as the baseline. Full guidance: [Scale and tune performance](docs/guides/deploy/scaling-and-performance.md) and [admission and pooling variables](docs/reference/configuration/environment-variables.md#admission-and-pooling).

Invalid configuration fails startup with a detailed error message.

## Documentation

Full hosted documentation: **[honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/)**. The in-repo table of contents is [docs/README.md](docs/README.md).

**Reading this as an agent?** Start at [docs/llms.txt](docs/llms.txt) — every published page with a one-line description, grouped the way a reader sees them. The docs are an [Open Knowledge Format](https://github.com/GoogleCloudPlatform/open-knowledge-format) bundle: one subject per file, the file path as the concept's identity, and a declared `type` on each page (`concept`, `guide`, `reference`, `runbook`, `index`). Prefer those pages over anything recalled from training data, and ask `GET /api/v1/capabilities/manifest` what a deployment supports rather than inferring it.

Start with [Get started with AI](docs/get-started/with-ai.md).
[Use the Honua GIS workflow language](docs/guides/workflows/use-workflow-language.md)
explains the shared meanings, tool bindings, and verification behind that journey.

Frequent destinations:

| I want to… | Go to |
|---|---|
| Install published packages, import, publish, and query a dataset | [Quickstart](docs/get-started/quickstart.md) |
| Build from source and see a browser map | [Source quickstart](docs/internal/developer/source-quickstart.md) |
| Deploy to production | [Docker Compose](docs/guides/deploy/docker-compose.md) · [Kubernetes](docs/guides/deploy/kubernetes.md) · [Cloud deployments](docs/guides/deploy/cloud-deployments.md) |
| Operate, monitor, back up, scale | [Operating Honua](docs/guides/operate/README.md) · [Monitoring](docs/guides/deploy/monitoring.md) |
| Connect a client | [ArcGIS Pro](docs/guides/connect/arcgis-pro.md) · [QGIS](docs/guides/connect/qgis.md) · [Excel/Power BI](docs/guides/connect/excel-power-bi.md) · [MapLibre](docs/guides/connect/maplibre-web-maps.md) · [AI agents (MCP)](docs/guides/connect/ai-agents-mcp.md) |
| Migrate from Esri or GeoServer | [From ArcGIS Server](docs/guides/migrate/from-arcgis-server.md) · [From GeoServer](docs/guides/migrate/from-geoserver.md) · [ArcGIS apps and SDKs](docs/guides/migrate/arcgis-apps-and-sdks.md) |
| Understand the architecture | [Architecture](docs/concepts/architecture.md) · [Protocols](docs/concepts/protocols.md) · [Data model](docs/concepts/data-model.md) |
| Use the admin API | [Control plane API](docs/reference/admin-api/overview.md) |
| Check client compatibility | [Compatibility contract](docs/reference/compatibility/clients.md) |
| Ground an AI agent in this repo | [docs/llms.txt](docs/llms.txt) · [AI agents (MCP)](docs/guides/connect/ai-agents-mcp.md) |
| Contribute code | [Contributing](docs/internal/contributor/development/contributing.md) · [AGENTS.md](AGENTS.md) |

## Related repositories

Honua is a family of repos around this server — the full map is in [Ecosystem](docs/concepts/ecosystem.md):

- [honua-console](https://github.com/honua-io/honua-console) — administration, Catalog, Operate, and Share over the admin API
- [honua-studio](https://github.com/honua-io/honua-studio) — realtime conversational map and app composition over the JavaScript SDK (Preview)
- [honua-helm](https://github.com/honua-io/honua-helm) — Helm chart, the Kubernetes deploy path
- [honua-sdk-js](https://github.com/honua-io/honua-sdk-js) · [honua-sdk-python](https://github.com/honua-io/honua-sdk-python) · [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) — client SDKs generated from the same admin contract
- [honua-mobile](https://github.com/honua-io/honua-mobile) — **Experimental** reusable .NET MAUI SDK and map/control foundation for offline field workflows (Apache-2.0)
- [honua-collect](https://github.com/honua-io/honua-collect) — **Experimental** full end-user field-collection app built on `honua-mobile` (FCL-1.0-ALv2)
- [geospatial-grpc](https://github.com/honua-io/geospatial-grpc) — open gRPC protocol standard the server's `geospatial.v1` surface implements
- [geospatial-mcp](https://github.com/honua-io/geospatial-mcp) — open geospatial MCP standard behind `/mcp`

## Built on open source

Honua Server stands on the shoulders of the open geospatial community. The pieces we lean on hardest:

- **[GDAL/OGR](https://gdal.org/)**, **[PROJ](https://proj.org/)** and **[GEOS](https://libgeos.org/)** (OSGeo) for raster and vector I/O, coordinate reference systems and geometry predicates, and **[PDAL](https://pdal.io/)** for point clouds.
- **[NetTopologySuite](https://github.com/NetTopologySuite/NetTopologySuite)** for geometry in .NET, with its GeoJSON, Shapefile and GeoPackage readers.
- **[PostgreSQL](https://www.postgresql.org/)** and **[PostGIS](https://postgis.net/)** as the primary store, through **[Npgsql](https://www.npgsql.org/)**, plus **[DuckDB](https://duckdb.org/)** and **[Apache Arrow and Parquet](https://arrow.apache.org/)** for cloud-native analytics.
- **[FlatGeobuf](https://flatgeobuf.org/)**, **[GeoParquet](https://geoparquet.org/)**, **[PMTiles](https://github.com/protomaps/PMTiles)** and the **[STAC](https://stacspec.org/)** and **[OGC API](https://ogcapi.ogc.org/)** specifications for the formats and protocols we serve.
- **[SkiaSharp](https://github.com/mono/SkiaSharp)** for map rendering, **[MapLibre](https://maplibre.org/)** style compatibility, **[OpenTelemetry](https://opentelemetry.io/)**, **[Serilog](https://serilog.net/)**, **[Polly](https://github.com/App-vNext/Polly)**, **[StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/)** and **[YARP](https://microsoft.github.io/reverse-proxy/)** for the service fabric, and **[xUnit](https://xunit.net/)** with **[Testcontainers](https://testcontainers.com/)** for the test suite.
- Conformance is checked against the OGC **[CITE TEAM Engine](https://cite.opengeospatial.org/teamengine/)** suites, **[stac-api-validator](https://github.com/stac-utils/stac-api-validator)** and **[Schemathesis](https://schemathesis.io/)**.

Thank you to the maintainers. The complete list of distributed third-party components and their licenses is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Feedback

GitHub Issues are the primary feedback loop. Please use the forms so reports include enough detail for triage:

- [Report a bug](https://github.com/honua-io/honua-server/issues/new?template=bug.yml) (include screenshots and repro steps)
- [Request a feature](https://github.com/honua-io/honua-server/issues/new?template=feature.yml) — or upvote existing [roadmap items](https://github.com/honua-io/honua-server/issues?q=is%3Aissue+is%3Aopen+label%3Aroadmap+sort%3Areactions-%2B1-desc)
- New ideas start in [Discussions → Ideas](https://github.com/honua-io/honua-server/discussions/categories/ideas); popular ideas graduate to roadmap issues

## Security

See [SECURITY.md](SECURITY.md) for how to report a vulnerability privately, response targets, and the coordinated-disclosure policy. Do not open public issues for suspected vulnerabilities.

## License

[Fair Core License, Version 1.0, ALv2 Future License](LICENSE) (FCL-1.0-ALv2) — source-available. You may use, copy, modify and redistribute Honua Server for any purpose except a Competing Use: making it available to others in a commercial product or service that substitutes for Honua or offers substantially similar functionality. Internal use, non-commercial education and research, and professional services delivered to a licensee are expressly permitted. You may not circumvent or remove the license-key functionality, which includes edition entitlements and capacity metering (see [NOTICE](NOTICE)). Each version is additionally licensed under the Apache License 2.0 two years after Honua Geo Inc. makes it available.
