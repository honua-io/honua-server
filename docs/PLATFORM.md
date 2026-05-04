# Honua Platform Overview

Honua is a cloud-native geospatial feature server. It publishes, queries, edits, and renders spatial data through industry-standard protocols — enabling ArcGIS Pro, QGIS, MapLibre, Power BI, Excel, and custom applications to connect to the same data source simultaneously. The primary provider is PostgreSQL/PostGIS (full read/write). An embedded DuckDB provider supports read-only analytical and reference workloads without external database infrastructure. Additional read-only providers serve enterprise/user-managed spatial tables from SQL Server (`geometry`/`geography`) and MySQL/MariaDB (MySQL 8.0.11+ / MariaDB 10.6+).

## Architecture

```
                     +-----------------------+
                     |      GIS Clients      |
                     +-----------+-----------+
                                 |
            +--------------------+---------------------+
            |                    |                      |
     ArcGIS Pro/SDKs      QGIS/MapLibre         Excel/Power BI
     (GeoServices REST)   (OGC API/MVT)         (OData v4)
            |                    |                      |
            +--------------------+---------------------+
                                 |
                    +------------+------------+
                    |     Honua Server        |
                    |                         |
                    |  FeatureServer          |
                    |  MapServer + OGC WMS/WMTS|
                    |  ImageServer            |
                    |  OGC Features/Maps/Tiles|
                    |  OData v4              |
                    |  Vector Tiles (MVT)     |
                    |  Geometry Service       |
                    |  GPServer               |
                    |  Admin API              |
                    |  gRPC (internal)        |
                    +------+-----+------------+
                           |     |
       +-------------------+--------------------+--------------------+
       |                   |                    |                    |
 +-----+---------------+   +-------------+   +--+---------------+   +--+-----------------+
 | PostgreSQL + PostGIS|   |   DuckDB    |   |   SQL Server     |   |  MySQL / MariaDB   |
 | (full read/write)   |   | (read-only, |   | (read-only,      |   | (read/query-only,  |
 |                     |   |  analytics) |   |  geometry/geog.) |   |  user tables)      |
 +---------------------+   +-------------+   +------------------+   +--------------------+
```

## Protocols at a Glance

Honua serves multiple protocols from a single dataset. No ETL, no data duplication. The PostGIS, DuckDB, SQL Server, and MySQL/MariaDB providers each expose the same protocol surface for the operations they support. Only PostGIS supports writes today; read-only providers report `false` on capabilities they do not implement (edits, native MVT, statistics) and the protocol layer surfaces those limitations as `NotSupportedException` or HTTP 501.

| Protocol | Primary Clients | Use Case |
|---|---|---|
| **GeoServices REST FeatureServer** | ArcGIS Pro, Esri SDKs, ArcGIS Online | Feature query, editing, attachments, related records |
| **GeoServices REST MapServer** | ArcGIS Pro, Esri map clients | Server-rendered map images, identify, legends |
| **GeoServices REST ImageServer** | ArcGIS raster workflows | Raster/image export, identify, tiles, raster catalog query, per-band statistics & histograms, legend swatches, raster function chain validation |
| **GeoServices Geometry Service** | Esri geometry operations | Buffer, project, intersect, union, clip, difference |
| **GeoServices GPServer** | ArcGIS Pro, Esri geoprocessing SDKs | Catalog-backed task discovery and task metadata, async job submission, job status polling, cancellation, and per-parameter result routes over the canonical runtime. Internal `IProcessCatalog` seeds 34 built-in processes across seven families (`geometry.*`, `analytics.*`, `surface.*`, `raster.*`, `conversion.*`, `generalization.*`, `data-management.*`) used for plan validation; destructive `data-management.*` ids route through the operator approval gate. Generic built-in tasks are currently async-only; heavyweight `surface.*` and `raster.*` operations stay on the canonical worker boundary, with per-task projection into the GPServer surface and execution-engine result delivery remaining pending. |
| **MCP Operator JSON-RPC** | AI agents, operator automation | JSON-RPC tool/resource surface on `/mcp` for plan validation, dry runs, execution submission, cancellation, and job/result inspection over the canonical geoprocessing runtime. Natural-language grounding and clarification (`honua_ground_candidates`, `honua_clarify_intent`) are functional over the built-in process catalog and layer catalog. Promotion-surface resources (published services, deployments, map/app packages) with provenance edges are functional handlers wired to the publishing/deployment store interfaces; published-service and deployment reads additionally carry monotonic ETags, while map/app package views and list-root envelopes expose provenance without their own ETag. The promotion surface is opt-in via `AddMcpPromotionSurface` and is not advertised by the default composition until canonical `IPublishedServiceStore`/`IDeploymentStore` persistence is registered. Remaining planning tools and workspace/catalog resources are stable contract stubs pending their upstream services. |
| **OGC API Features** | QGIS, MapLibre, any OGC client | Feature CRUD with CQL2 filtering |
| **OGC API Maps** | OGC map clients | Standards-based rendered map images |
| **OGC API Tiles** | QGIS, MapLibre | Vector and raster tile access |
| **WMS 1.3 / 1.1.1, WMTS 1.0** | Legacy OGC clients (modern + 1.1.1-pinned) | Map image and tile services; WMS 1.1.1 is read-only and uses `SRS` / `X`/`Y` and lon/lat `EPSG:4326` BBOX order |
| **WFS 2.0 / 1.1.0 / 1.0.0** | Legacy OGC clients (QGIS legacy, ArcGIS Desktop, GDAL/OGR, WFS 1.0.0-pinned stacks) | Feature query with GML output; 1.1.0 emits GML 3.1.1 / OWS 1.0 exceptions, 1.0.0 emits GML 2.1.2 / `ServiceExceptionReport`. Read-only on legacy versions. |
| **OData v4** | Excel, Power BI, Tableau, SAP | BI integration with spatial functions |
| **Vector Tiles (MVT)** | MapLibre, Leaflet, Mapbox GL | Client-side rendered maps |
| **TileJSON + MapLibre Styles** | MapLibre | Auto-generated styles and tile metadata |

## Data Flow

```
  Import                    Serve                      Consume
  ------                    -----                      -------

  GeoJSON  ──┐                                    ┌── ArcGIS Pro
  Shapefile ─┤                                    ├── QGIS
  GeoPackage ┤          ┌──────────────┐          ├── MapLibre
  KML ───────┤          │              │          ├── Power BI
  FileGDB ───┼──Import──▶   PostGIS    ├──Serve──▶├── Excel
  FlatGeobuf ┤          │              │          ├── Tableau
  GeoParquet ┤          └──────────────┘          ├── Leaflet
  GPX ───────┤                                    ├── Custom apps
  WKT/CSV ───┤          ┌──────────────┐          └── Mobile SDKs
  Esri REST ─┤          │   DuckDB     │
  GeoServer ─┘          │  (read-only) ├──Serve──▶ Same clients
                         └──────────────┘          (query only)

                         ┌──────────────┐
                         │ SQL Server   ├──Serve──▶ Same clients
                         │ (read-only)  │          (query only)
                         └──────────────┘

                         ┌──────────────┐
                         │ MySQL/MariaDB├──Serve──▶ Same clients
                         │ (read/query) │          (query only)
                         └──────────────┘
```

The DuckDB provider serves pre-built `.duckdb` files containing data prepared offline (e.g. from GeoParquet, Shapefile, or CSV imports). It supports feature queries, spatial filters, statistics, and GeoJSON/streaming export, but not editing, MVT, H3, native WFS GML output, or replica/extract workflows. See the [DuckDB Provider Guide](operator/duckdb-provider.md) for configuration and limitations.

The SQL Server provider exposes existing `geometry`/`geography` tables as read-only feature layers without copying data into PostGIS. It supports feature query, count, extent, and pagination, but not editing, statistics, top-features, date/value/H3 bins, temporal extents, or native MVT/FlatGeobuf/Geobuf/GML output (which fall back to the in-process formatter). See the [SQL Server Provider Guide](operator/sqlserver-provider.md) for supported versions, configuration, and limitations.

The MySQL/MariaDB provider serves user-managed tables in MySQL 8.0.11+ or MariaDB 10.6+. It supports feature query, count, pagination, attribute filters, OGC spatial relationships (Intersects, Within, Contains, etc.), and buffered/paged `IStreamingFeatureStore` iteration; it does **not** support edits, statistics, native MVT/FlatGeobuf/Geobuf/GML, streaming GeoJSON, KNN/nearest-neighbor, cross-SRID `ST_Transform`, or temporal (`datetime`) filters. `GetExtentAsync` is supported for Point and Polygon/MultiPolygon layers only — other geometry types raise `NotSupportedException` (the slice avoids the MySQL-only 2-arg `ST_SRID` retag and the point-only `ST_X`/`ST_Y` patterns to keep the emitted SQL portable across both engines). Invalid `GeometryType` configuration values are rejected at startup. See the [MySQL/MariaDB Provider Guide](operator/mysql-provider.md) for layer mapping, version floors, spatial filter mapping, and cloud-hosted deployment notes.

## Key Capabilities

### Multi-Protocol Query
Every published layer is automatically available through the layer-scoped protocols that the service enables. A single dataset published from PostGIS can be exposed simultaneously via FeatureServer REST, OGC API Features, OData, WFS, and vector tiles, while service-scoped families such as WMS, WMTS, and OGC API Maps remain service-level surfaces.

### Feature Editing
Full CRUD support across protocols:
- **FeatureServer**: `addFeatures`, `updateFeatures`, `deleteFeatures`, `applyEdits`
- **OGC API Features**: POST, PUT, PATCH, DELETE on `/items`
- **OData v4**: POST, PATCH, DELETE with `$batch` support

### Map Rendering
Server-side rendering via SkiaSharp:
- **MapServer**: export, identify, legend, find
- **OGC API Maps**: collection and dataset maps with CRS/scaling support
- **WMS 1.3 / 1.1.1 / WMTS 1.0**: GetMap, GetFeatureInfo, GetTile (WMS 1.1.1 is read-only)

### File & Service Import
Import from 10+ file formats and live services:
- Automatic CRS detection and PostGIS-based reprojection
- Streaming import for large datasets
- Esri REST service migration (preserves structure and metadata)
- GeoServer REST catalog import

### Admin Control Plane
REST API and GitOps-ready management:
- Database connection management with encrypted credentials
- Layer publishing from PostGIS tables
- Protocol enablement per service
- Access policies (anonymous, role-based)
- MapLibre style editing with deterministic theme variants (`dark`, `colorblind-safe`, `print`), versioned revision metadata, and stable-code reporting for unsupported GeoServices symbolizers (see [Style Engine: Cross-Protocol Consumption](gis/style-engine-protocol-consumption.md))
- Metadata manifest export/apply with approval workflows

### Spec Plan/Apply Engine
Terraform-style plan/apply semantics for canonical spec documents ([Spec Engine reference](developer/SPEC_ENGINE.md)):
- `POST /v1/spec/validate` parses spec DSL or canonical JSON and returns structured diagnostics for workspace linting
- `POST /v1/spec/plan` returns a DAG with per-node cost estimates and structured warnings (catalog/metadata only — side-effect-free)
- `POST /v1/spec/apply` streams per-node progress events over SSE, with a mirrored `geospatial.v1.SpecService/ApplySpec` gRPC server-streaming surface
- `POST /v1/spec/cancel` cooperatively cancels an in-flight run; `GET /v1/spec/artifact/{hash}` retrieves cached outputs
- Content-hash artifact cache: re-applying an unchanged spec completes with zero compute invocations; mutating one node invalidates only its transitive closure
- S1 scope: `compute` and `report` kinds execute; `dataset` / `service` / `app` slots are declared and reject apply with `spec-kind-not-in-s1`; the apply-token registry is in-process and does not survive restart

## Deployment

Honua runs as a single container in combined mode. The job orchestration substrate ([ADR-0031](contributor/adr/0031-durable-job-orchestration-substrate.md)) is designed to support separate API and worker hosts for enterprise scale-out; dedicated worker-mode hosting (`AddJobWorker()`) for queue-based claim/execute on separate hosts is not yet wired. Pluggable batch-compute backends implement `IBatchComputeBackend` and are resolved by `(BackendName, TargetKind)` at runtime; the `LocalBatchComputeBackend` observes in-process worker progress in the combined host (actual local execution requires `AddJobWorker()` wiring on a worker host), and the execution-job reconciler bridges backend state into the canonical job store on every Redis-enabled host. A declarative workflow orchestration layer ([ADR-0032](contributor/adr/0032-workflow-orchestration-layer.md)) composes canonical process steps into chained, scheduled, and DAG-style runs on top of the same substrate. See [Operations — Job Orchestration](operator/operations.md#job-orchestration), [Operations — Workflow Orchestration](operator/operations.md#workflow-orchestration), and [Deployment Scenarios](operator/DEPLOYMENT_SCENARIOS.md#apiworker-host-separation) for details.

```
  PostgreSQL (default):         DuckDB (embedded):
  ---------------------         ------------------
  1x Honua container            1x Honua container
  1x PostgreSQL/PostGIS         .duckdb file (bundled or mounted)
  Redis (optional; required     Reverse proxy (TLS)
    for job and workflow
    orchestration)
  Reverse proxy (TLS)
```

**Deployment options:**
- Docker Compose (evaluation and development)
- Kubernetes via Helm ([honua-helm](https://github.com/honua-io/honua-helm))
- Terraform modules for AWS ECS/Lambda, Azure ACA/Functions, EKS/AKS ([honua-terraform](https://github.com/honua-io/honua-terraform))
- .NET Aspire (local development with dashboard)

## Observability

- **Health**: `/healthz/live`, `/healthz/ready`
- **Metrics**: Admin-authenticated Prometheus endpoint at `/metrics`, JSON metrics via admin API
- **Tracing**: OpenTelemetry with configurable exporters
- **Logging**: Structured JSON logging via Serilog

## Security

- **Authentication**: API key, OIDC (Azure AD, Google, Okta, Auth0), Basic auth compatibility
- **Authorization**: Per-service and per-layer access policies with role-based control
- **Transport**: HTTPS enforcement, HSTS, CSP, security headers
- **Data**: Encrypted connection credentials, SSRF protection on imports/webhooks

## Related Repositories

| Repository | Purpose |
|---|---|
| [honua-server](https://github.com/honua-io/honua-server) | Server runtime (this repo) |
| [honua-server-admin](https://github.com/honua-io/honua-server-admin) | Blazor WASM admin UI |
| [honua-sdk-js](https://github.com/honua-io/honua-sdk-js) | JavaScript/TypeScript SDK + MCP server |
| [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) | .NET SDK |
| [honua-sdk-python](https://github.com/honua-io/honua-sdk-python) | Python SDK |
| [honua-mobile](https://github.com/honua-io/honua-mobile) | .NET MAUI mobile apps |
| [honua-helm](https://github.com/honua-io/honua-helm) | Kubernetes Helm charts |
| [honua-terraform](https://github.com/honua-io/honua-terraform) | Terraform IaC modules |
| [honua-devops](https://github.com/honua-io/honua-devops) | Operator tooling and release orchestration |
