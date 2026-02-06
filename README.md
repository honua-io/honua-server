# Honua Server

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.6-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-ready-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

Cloud Native GIS Server. Modern GIS Infrastructure for the Cloud Era.

Honua Server is a cloud-native, open GIS server designed for interoperability, performance, and long-term flexibility.

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

See `docs/contributor/adr/0008-env-var-configuration.md` for complete environment variable reference.

## Documentation

User documentation:
- **[Control Plane API](docs/user/CONTROL_PLANE_API.md)** - Admin + automation API (headless use)
- **[Standards APIs](docs/user/STANDARDS_APIS.md)** - FeatureServer, OGC, OData, MVT
- **[API Examples](docs/user/API_EXAMPLES.md)** - Comprehensive examples for standards APIs
- **[Protocol Coverage Index](docs/user/specifications/protocol-coverage.md)** - Coverage status across supported standards

Contributor documentation:
- **[Agent Instructions](AGENTS.md)** - Canonical agent and project rules
- **[Getting Started](docs/contributor/development/getting-started.md)** - Development environment setup
- **[Architecture Documentation](docs/contributor/ARCHITECTURE.md)** - System design and architectural decisions
- **[ADR Index](docs/contributor/adr/README.md)** - Architecture Decision Records with complete rationale

DevOps documentation:
- **[Infrastructure Deployments](infrastructure/README.md)** - Docker, Helm, and Terraform options
- **[Operational Excellence](docs/devops/OPERATIONAL_EXCELLENCE.md)** - Production best practices
- **[Troubleshooting Guide](docs/devops/TROUBLESHOOTING.md)** - Solutions to common issues and debugging tips
