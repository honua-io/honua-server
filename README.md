# Honua Server MVP

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/honua-io/honua-server/branch/trunk/graph/badge.svg)](https://codecov.io/gh/honua-io/honua-server)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.6-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-ready-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

Honua Server is a lightweight, PostGIS-native API layer that publishes one database through multiple GIS and data protocols — without ETL or proprietary stacks.

Why it matters:
- **One PostGIS source, many clients** — ArcGIS (GeoServices REST), OGC API Features, OData/BI tools, and vector tiles.
- **Full editing support** — applyEdits, attachments, related records, and transactions (not just read-only tiles).
- **Container-first** — quick local/dev setup and straightforward cloud deployment.

Protocols:
- **GeoServices REST FeatureServer** — GeoServices REST compatible queries + full editing (applyEdits, attachments, related records).
- **OGC API Features** — Modern REST/JSON for GIS apps with transaction support.
- **OData v4** — Full CRUD access for Excel/Power BI with spatial queries.
- **Vector Tiles (MVT)** — PostGIS-native tile generation.

Includes **file import** APIs (GeoJSON, Shapefile, GeoPackage, CSV, KML) and **Esri service import endpoints** for migration. Deployment templates (Helm + AWS/Azure Terraform) are available under `infrastructure/`, including serverless options (Lambda + Functions). Admin UI is in progress; see `docs/ROADMAP.md` for what comes next.

## Status

MVP endpoints are implemented and functional, but APIs are still stabilizing.
Current entrypoints:
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

```bash
dotnet run --project src/Honua.Server
```

## Docker (optional)

**Local Development (with Aspire):**
```bash
cd src/Honua.AppHost
dotnet run
# Opens Aspire dashboard with Honua + PostgreSQL + Redis
```

**Docker:**
```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  honuaio/honua-server:latest
```

**Image tags:**
- `latest` on trunk builds
- `vX.Y.Z`, `vX.Y`, `vX` on release tags
- `nightly` for nightly JIT images
- `nightly-aot` for nightly AOT images

## Current capabilities
Implemented (server + admin API):
- PostGIS-only data source.
- FeatureServer: query, applyEdits, attachments, related records.
- OGC API Features: collections/items, filters, bbox/geometry, POST/PUT/DELETE transactions.
- OGC API Tiles: tilesets metadata + vector tiles.
- OData v4: CRUD with spatial functions (`geo.distance`, `geo.intersects`); $batch/$apply/$search endpoints exist with limited coverage.
- Vector tiles (MVT): PostGIS `ST_AsMVT` via `/tiles/{layerId}/{z}/{x}/{y}.mvt`.
- TileJSON metadata: `/tiles/{layerId}/tile.json` with MapLibre style discovery.
- Public MapLibre styles: `/api/styles/{layerId}.json`.
- File import: GeoJSON, Shapefile, GeoPackage, CSV (lat/lon or WKT), KML/KMZ — no GDAL required.
- CRS support: PostGIS-based reprojection, EPSG via `spatial_ref_sys`, auto-detect from source files.
- Admin APIs: connections, services/layers/relationships/styles, import jobs, operations progress.
- OIDC authentication (server-side plumbing) and optional Redis metadata cache.
- .NET Aspire local dev orchestration with dashboard (traces, logs, metrics, health).

Pending MVP items (open issues):
- Service enable/disable controls (#58).
- Admin UI (project setup, connections, publishing, health dashboard, map preview) (#25, #26, #27, #42, #43).
- Embedded Maputnik style editor (#30).
- Canonical cross-protocol style pipeline (#244).
- Esri Service Import Wizard UI (#187).
- GCP Terraform module (#34).
- Docs and security hardening (#38, #39).

## Deferred (post-MVP)
- **Operational/enterprise**: audit logging/storage + compliance dashboards, secure-connection allowlist/audit, edge rate limiting templates (nginx/ALB/WAF).
- **Beta:** Query caching, GeometryServer basics, MapServer export, OData `$expand`/`$apply`, OGC API Styles.
- **GA:** OData `/$batch`, legacy OGC (WFS/WMS), layer-level RBAC, audit logging.
- **Later:** Additional databases (SQL Server, MySQL, SQLite, DuckDB, warehouses, NoSQL, Oracle), additional file formats (FileGDB, MapInfo TAB — requires GDAL), additional outputs (KML export, Shapefile export, PNG/JPEG), object storage, AI features, CLI/agent tooling.

## Configuration

### Minimal Config (env)

```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
HONUA_ADMIN_PASSWORD="change-me"
```

### Advanced Configuration

**Resource Limits** (Issue #63 - shared across all protocols):
```bash
# Query limits (affects all protocols: FeatureServer, OGC API, OData, MVT)
Limits__Query__MaxRecordCount=2000        # Max features per query
Limits__Query__DefaultRecordCount=1000    # Default when not specified
Limits__Query__MaxOffset=1000000          # Max paging offset
Limits__Query__QueryTimeout=00:00:30      # Query execution timeout

# Geometry limits
Limits__Geometry__MaxVertices=10000       # Max vertices per geometry
Limits__Geometry__MaxPolygons=100         # Max polygons per geometry
Limits__Geometry__MaxCoordinateValue=180  # Max coordinate value

# Edit limits (FeatureServer applyEdits, OGC API transactions, OData CRUD)
Limits__Edits__MaxPayloadSize=10485760    # 10MB max request payload
Limits__Edits__MaxFeaturesPerRequest=1000 # Max features per edit operation
Limits__Edits__MaxAttachmentSize=52428800 # 50MB max attachment size

# Connection limits
Limits__Connections__MaxConcurrent=100    # Max concurrent requests
Limits__Connections__RequestTimeout=00:01:00  # Request timeout

# Optional: CORS, basemap provider, attachment types
Cors__AllowedOrigins__0="http://localhost:3000"
Basemap__Provider="openfreemap"
Limits__Attachments__AllowedMimeTypes="image/*,application/pdf"
```

**Validation**: Invalid configuration will cause startup failure with detailed error messages. All limits are validated for logical consistency (e.g., DefaultRecordCount ≤ MaxRecordCount).

See `docs/adr/0008-env-var-configuration.md` for complete environment variable reference.

## Documentation

User documentation:
- **[API Examples](docs/API_EXAMPLES.md)** - Comprehensive examples for all supported protocols (GeoServices REST, OGC API Features, OData v4, MVT)
- **[Protocol Coverage Index](docs/specifications/protocol-coverage.md)** - Coverage status across supported standards

Developer documentation:
- **[Getting Started](docs/development/getting-started.md)** - Development environment setup
- **[Architecture Documentation](docs/ARCHITECTURE.md)** - System design and architectural decisions
- **[ADR Index](docs/adr/README.md)** - Architecture Decision Records with complete rationale

DevOps documentation:
- **[Infrastructure Deployments](infrastructure/README.md)** - Docker, Helm, and Terraform options
- **[Operational Excellence](docs/OPERATIONAL_EXCELLENCE.md)** - Production best practices
- **[Troubleshooting Guide](docs/TROUBLESHOOTING.md)** - Solutions to common issues and debugging tips

## Roadmap

See `docs/ROADMAP.md` for the staged plan (Beta, GA, Later).
