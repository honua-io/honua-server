# Honua Server MVP

Honua MVP serves and edits PostGIS data over multiple protocols with a small, fast footprint:
- **GeoServices REST FeatureServer** — ArcGIS-compatible queries + full editing (applyEdits, attachments, related records).
- **OGC API Features** — Modern REST/JSON for GIS apps with transaction support.
- **OData v4** — Full CRUD access for Excel/Power BI with spatial queries.
- **Vector Tiles (MVT)** — PostGIS-native tile generation.

Includes **file import** (GeoJSON, Shapefile, GeoPackage, CSV, KML) and an **Esri Service Import Wizard** for easy migration. Everything else (images, multi-DB, AI, advanced admin) is deferred to keep the surface area tight. See `docs/ROADMAP.md` for what comes next.

## Quick Start

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
  ghcr.io/honuaio/honua-server:latest
```

- Admin UI: `http://localhost:8080/admin`
- FeatureServer: `http://localhost:8080/rest/services/{service}/FeatureServer/{layer}/query`
- OGC API Features landing: `http://localhost:8080/ogc/features`
- OData v4: `http://localhost:8080/odata/v4/Layers('{layer}')/Features`
- Health: `http://localhost:8080/healthz/live`

## What's In (MVP)
- PostGIS-only data source.
- Full FeatureServer: query, applyEdits, add/update/delete, attachments, related records.
- OGC API Features: collections/items, filters, bbox/geometry, POST/PUT/DELETE transactions.
- **OData v4**: Excel/Power BI integration with spatial functions (`geo.distance`, `geo.intersects`) + POST/PATCH/DELETE.
- **Vector Tiles (MVT)**: PostGIS `ST_AsMVT`, TileJSON metadata.
- **File Import**: GeoJSON, Shapefile, GeoPackage, CSV (lat/lon or WKT), KML/KMZ — no GDAL required.
- **CRS Support**: PostGIS-based reprojection, any EPSG code, auto-detect from source files.
- Outputs: GeoJSON, Esri JSON, MVT.
- **Esri Service Import Wizard**: paste ArcGIS Server URL, import layers, publish to Honua.
- **Visual Style Editor**: embedded Maputnik for MapLibre-based styling (Simple, UniqueValue, ClassBreaks).
- Minimal admin: connect PostGIS, publish a layer/service, enable/disable, view health, map preview.
- **OIDC Authentication**: Azure AD, Google, generic OIDC provider support.
- **Redis cache (optional)**: metadata cache for multi-instance; in-memory fallback for single instance.
- **Deployment templates**: Helm chart for Kubernetes, Terraform modules for AWS/Azure/GCP.
- **.NET Aspire**: Local dev orchestration with dashboard (traces, logs, metrics, health).

## What's Deferred
- **Beta:** Query caching, GeometryServer basics, MapServer export, OData `$expand`/`$apply`, OGC API Styles.
- **GA:** OData `/$batch`, legacy OGC (WFS/WMS), layer-level RBAC, audit logging.
- **Later:** Additional databases (SQL Server, MySQL, SQLite, DuckDB, warehouses, NoSQL, Oracle), additional file formats (FileGDB, MapInfo TAB — requires GDAL), additional outputs (KML export, Shapefile export, PNG/JPEG), object storage, AI features, CLI/agent tooling.

## Minimal Config (env)

```bash
ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres"
HONUA_ADMIN_PASSWORD="change-me"
```

## Roadmap

See `docs/ROADMAP.md` for the staged plan (Beta, GA, Later).
