# Honua Server MVP

[![CI](https://github.com/honua-io/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/honua-io/honua-server/branch/trunk/graph/badge.svg)](https://codecov.io/gh/honua-io/honua-server)
[![CodeQL](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml/badge.svg)](https://github.com/honua-io/honua-server/actions/workflows/codeql.yml)
[![License](https://img.shields.io/badge/License-Elastic_License_2.0-blue.svg)](https://github.com/honua-io/honua-server/blob/trunk/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.6-brightgreen.svg)](https://postgis.net/)
[![Docker](https://img.shields.io/badge/Docker-ready-blue.svg)](https://hub.docker.com/r/honuaio/honua-server)

Honua MVP serves and edits PostGIS data over multiple protocols with a small, fast footprint:
- **GeoServices REST FeatureServer** — GeoServices REST compatible queries + full editing (applyEdits, attachments, related records).
- **OGC API Features** — Modern REST/JSON for GIS apps with transaction support.
- **OData v4** — Full CRUD access for Excel/Power BI with spatial queries.
- **Vector Tiles (MVT)** — PostGIS-native tile generation.

Includes **file import** (GeoJSON, Shapefile, GeoPackage, CSV, KML) and a **GeoServices Import Wizard** for easy migration. Everything else (images, multi-DB, AI, advanced admin) is deferred to keep the surface area tight. See `docs/ROADMAP.md` for what comes next.

## Status

This repo is in planning/Phase 0. The MVP scope below is planned and not yet implemented.

Current endpoints:
- `/healthz/live`
- `/healthz/ready`

## Quick Start (Phase 0)

```bash
dotnet run --project src/Honua.Server
```

## Planned Quick Start (post-Phase 0)

**Local Development (with Aspire, planned):**
```bash
cd src/Honua.AppHost
dotnet run
# Opens Aspire dashboard with Honua + PostgreSQL + Redis
```

**Docker (planned):**
```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  ghcr.io/honuaio/honua-server:latest
```

Planned endpoints:
- Admin UI: `/admin`
- FeatureServer: `/rest/services/{service}/FeatureServer/{layer}/query`
- OGC API Features landing: `/ogc/features`
- OData v4: `/odata/v4/Layers('{layer}')/Features`

## Planned MVP Scope
- PostGIS-only data source.
- Full FeatureServer: query, applyEdits, add/update/delete, attachments, related records.
- OGC API Features: collections/items, filters, bbox/geometry, POST/PUT/DELETE transactions.
- **OData v4**: Excel/Power BI integration with spatial functions (`geo.distance`, `geo.intersects`) + POST/PATCH/DELETE.
- **Vector Tiles (MVT)**: PostGIS `ST_AsMVT`, TileJSON metadata.
- **File Import**: GeoJSON, Shapefile, GeoPackage, CSV (lat/lon or WKT), KML/KMZ — no GDAL required.
- **CRS Support**: PostGIS-based reprojection, any EPSG code, auto-detect from source files.
- Outputs: GeoJSON, GeoServices JSON, MVT.
- **GeoServices Import Wizard**: paste GeoServices REST server URL, import layers, publish to Honua.
- **Visual Style Editor**: embedded Maputnik for MapLibre-based styling (Simple, UniqueValue, ClassBreaks).
- Minimal admin: connect PostGIS, publish a layer/service, enable/disable, view health, map preview.
- **OIDC Authentication**: Azure AD, Google, generic OIDC provider support.
- **Redis cache (optional)**: metadata cache for multi-instance; in-memory fallback for single instance.
- **Deployment templates**: Helm chart for Kubernetes, Terraform modules for AWS/Azure/GCP.
- **.NET Aspire**: Local dev orchestration with dashboard (traces, logs, metrics, health).

## Deferred (post-MVP)
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
Limits__Query__MaxOffset=100000           # Max paging offset
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

## Roadmap

See `docs/ROADMAP.md` for the staged plan (Beta, GA, Later).
