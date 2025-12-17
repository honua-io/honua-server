# Honua Server Project Overview

## Project Purpose
Honua Server is a **greenfield MVP geospatial feature server** that serves and edits PostGIS data over multiple protocols with a small, fast footprint. It provides ArcGIS-compatible endpoints alongside modern standards for GIS applications.

## Current Status
- **Phase**: Phase 0 (Foundation/Planning)
- **Implementation**: Minimal - only health endpoints (`/healthz/live`, `/healthz/ready`)
- **Repository**: Clean slate greenfield implementation (legacy `../Honua.Server/` used as reference only)

## Core Protocols Supported
1. **GeoServices REST FeatureServer** - ArcGIS-compatible queries + full editing (applyEdits, attachments, related records)
2. **OGC API Features** - Modern REST/JSON for GIS apps with transaction support
3. **OData v4** - Full CRUD access for Excel/Power BI with spatial queries (`geo.distance`, `geo.intersects`)
4. **Vector Tiles (MVT)** - PostGIS-native tile generation with TileJSON metadata

## Key MVP Features
- **PostGIS-only** data source (single database focus)
- **File Import**: GeoJSON, Shapefile, GeoPackage, CSV (lat/lon or WKT), KML/KMZ (no GDAL required)
- **CRS Support**: PostGIS-based reprojection, any EPSG code, auto-detect from source files
- **Esri Service Import Wizard**: paste ArcGIS Server URL, import layers, publish to Honua
- **Visual Style Editor**: embedded Maputnik for MapLibre-based styling
- **OIDC Authentication**: Azure AD, Google, generic OIDC provider support
- **Redis cache (optional)**: metadata cache for multi-instance; in-memory fallback for single instance
- **Deployment templates**: Helm chart for Kubernetes, Terraform modules for AWS/Azure/GCP
- **.NET Aspire**: Local dev orchestration with dashboard (traces, logs, metrics, health)

## Architecture Principles
1. **Greenfield Clean Slate** - No legacy code porting, reference only
2. **Vertical Slices** - Organize by feature (FeatureServer, OgcFeatures) not by layer
3. **Quality First** - Comprehensive guardrails, warnings as errors, 80%+ coverage
4. **Performance Focused** - Native AOT, zero-allocation patterns, object pooling
5. **Integration-First Testing** - Real PostgreSQL via Testcontainers, minimal mocking
6. **Phase-Based Development** - Clear exit criteria and measurable milestones

## Technology Stack
- **Runtime**: .NET 10 with Native AOT compilation
- **Web Framework**: Minimal APIs (lean, fast, AOT-compatible)
- **Database**: PostgreSQL + PostGIS (single database)
- **Data Access**: Raw Npgsql (no ORM/EF for maximum control and AOT compatibility)
- **Admin UI**: Blazor WebAssembly (C# end-to-end)
- **Testing**: xUnit + Testcontainers (integration-first with real PostgreSQL)
- **Orchestration**: .NET Aspire (local dev dashboard, service discovery, OpenTelemetry)
- **Container**: Docker with Alpine base (~30-40MB AOT image)

## Project Structure
```
src/
├── Honua.Server/              # Main host (Minimal APIs)
├── Honua.Core/                # Domain models, abstractions  
├── Honua.Postgres/            # PostgreSQL implementation
└── Honua.Admin/               # Blazor WASM admin UI (planned)

tests/
├── Honua.TestKit/             # Shared test infrastructure
├── Honua.Core.Tests/          # Unit tests
├── Honua.Server.Tests/        # Integration tests
└── Honua.Architecture.Tests/  # Architecture enforcement (planned)
```

## Development Phases
- **Phase 0**: Foundation (Current) - Repository setup, empty but deployable
- **Phase 1**: FeatureServer Query - Read-only query endpoint
- **Phase 2**: FeatureServer Editing - Full CRUD via applyEdits  
- **Phase 3**: OGC API Features - Core + Transactions conformance
- **Phase 3.25**: Vector Tiles (MVT) - TileJSON + MVT endpoint
- **Phase 3.5**: OData v4 - Excel/Power BI integration with spatial queries
- **Phase 4**: Admin UI + File Import - Blazor WASM interface
- **Phase 4.5**: Deployment Templates - Helm chart + Terraform modules
- **Phase 5**: Authentication + Polish - OIDC + production hardening

## Deferred (Post-MVP)
- **Beta**: Query caching, GeometryServer, MapServer export, OData `$expand`/`$apply`
- **GA**: OData `/$batch`, legacy OGC (WFS/WMS), layer-level RBAC, audit logging
- **Later**: Additional databases, file formats, outputs, object storage, AI features