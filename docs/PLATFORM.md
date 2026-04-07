# Honua Platform Overview

Honua is a cloud-native geospatial feature server. It publishes, queries, edits, and renders spatial data through industry-standard protocols — enabling ArcGIS Pro, QGIS, MapLibre, Power BI, Excel, and custom applications to connect to the same data source simultaneously. The primary provider is PostgreSQL/PostGIS (full read/write). An embedded DuckDB provider supports read-only analytical and reference workloads without external database infrastructure.

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
                    |  MapServer/WMS/WMTS     |
                    |  ImageServer            |
                    |  OGC Features/Maps/Tiles|
                    |  OData v4              |
                    |  Vector Tiles (MVT)     |
                    |  Geometry Service       |
                    |  Admin API              |
                    |  gRPC (internal)        |
                    +------+-----+------------+
                           |     |
              +------------+     +------------+
              |                               |
 +------------+------------+   +--------------+-----------+
 |   PostgreSQL + PostGIS   |   |  DuckDB (read-only,     |
 |   (full read/write)      |   |  analytics & reference)  |
 +-------------------------+   +--------------------------+
```

## Protocols at a Glance

Honua serves multiple protocols from a single dataset. No ETL, no data duplication. Both the PostGIS and DuckDB providers expose the same protocol surface for read operations.

| Protocol | Primary Clients | Use Case |
|---|---|---|
| **GeoServices REST FeatureServer** | ArcGIS Pro, Esri SDKs, ArcGIS Online | Feature query, editing, attachments, related records |
| **GeoServices REST MapServer** | ArcGIS Pro, Esri map clients | Server-rendered map images, identify, legends |
| **GeoServices REST ImageServer** | ArcGIS raster workflows | Raster/image export and identify |
| **GeoServices Geometry Service** | Esri geometry operations | Buffer, project, intersect, union, clip, difference |
| **OGC API Features** | QGIS, MapLibre, any OGC client | Feature CRUD with CQL2 filtering |
| **OGC API Maps** | OGC map clients | Standards-based rendered map images |
| **OGC API Tiles** | QGIS, MapLibre | Vector and raster tile access |
| **WMS 1.3 / WMTS 1.0** | Legacy OGC clients | Map image and tile services |
| **WFS 2.0** | Legacy OGC clients | Feature query with GML output |
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
```

The DuckDB provider serves pre-built `.duckdb` files containing data prepared offline (e.g. from GeoParquet, Shapefile, or CSV imports). It supports feature queries, spatial filters, statistics, and GeoJSON/streaming export, but not editing, MVT, H3, native WFS GML output, or replica/extract workflows. See the [DuckDB Provider Guide](operator/duckdb-provider.md) for configuration and limitations.

## Key Capabilities

### Multi-Protocol Query
Every published layer is automatically available through all protocols. A single dataset published from PostGIS is simultaneously queryable via FeatureServer REST, OGC API Features, OData, WFS, and vector tiles.

### Feature Editing
Full CRUD support across protocols:
- **FeatureServer**: `addFeatures`, `updateFeatures`, `deleteFeatures`, `applyEdits`
- **OGC API Features**: POST, PUT, PATCH, DELETE on `/items`
- **OData v4**: POST, PATCH, DELETE with `$batch` support

### Map Rendering
Server-side rendering via SkiaSharp:
- **MapServer**: export, identify, legend, find
- **OGC API Maps**: collection and dataset maps with CRS/scaling support
- **WMS 1.3 / WMTS 1.0**: GetMap, GetFeatureInfo, GetTile

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
- MapLibre style editing
- Metadata manifest export/apply with approval workflows

## Deployment

Honua runs as a single container. The default PostgreSQL provider requires an external PostGIS database; the DuckDB provider is fully embedded and needs no external infrastructure.

```
  PostgreSQL (default):         DuckDB (embedded):
  ---------------------         ------------------
  1x Honua container            1x Honua container
  1x PostgreSQL/PostGIS         .duckdb file (bundled or mounted)
  Redis (optional)
  Reverse proxy (TLS)           Reverse proxy (TLS)
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
