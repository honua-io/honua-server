# Architecture Overview

This document describes the current Honua Server architecture and the constraints that shape the codebase.

## Goals

- **Provider-backed**: PostgreSQL/PostGIS is the primary read/write provider; DuckDB is an embedded read-only provider for analytics and reference workloads.
- **Open standards**: serve multiple GIS and data protocols from one dataset.
- **Clean dependencies**: `Honua.Core` <- `Honua.Postgres` / `Honua.DuckDB` <- `Honua.Server`.
- **Minimal API surface**: endpoints are defined with Minimal APIs, not MVC controllers.
- **AOT-friendly**: avoid reflection in hot paths and use source-generated JSON/logging.

## Solution Layout

```
src/
├── Honua.Server/     # ASP.NET Core host + Minimal API endpoints
├── Honua.Core/       # Domain models + abstractions
├── Honua.Postgres/   # PostgreSQL/PostGIS implementation (read/write)
├── Honua.DuckDB/     # DuckDB implementation (read-only)
└── Honua.Admin/      # Blazor WASM admin UI
```

Key points:
- **Honua.Core** defines domain models, protocol DTOs, and abstractions.
- **Honua.Postgres** implements Core interfaces using raw Npgsql and PostGIS.
- **Honua.DuckDB** implements Core read interfaces (`IFeatureReader`, `IStreamingFeatureStore`, etc.) for embedded DuckDB databases. Write operations are rejected at startup via capability stripping.
- **Honua.Server** composes endpoints and handlers, selecting the active provider via `DataSource:Provider` configuration.
- **Honua.Admin** is a standalone UI that talks to the Admin API.

## Feature Slices (Server)

The server is organized by vertical slices under `src/Honua.Server/Features/`.

- **FeatureServer**: GeoServices REST query/edit/attachments/related records.
- **MapServer**: GeoServices REST map rendering (export/identify/legend) + layer query.
- **OGC Features**: collections/items with transactions.
- **OGC Tiles**: tilesets metadata and vector tiles.
- **OData**: CRUD + query options ($filter, $select, $orderby, $top, $skip, $count, $search, $apply, $batch).
- **Tiles**: MVT + TileJSON.
- **Geoprocessing**: gRPC `ProcessService` — plan validation, dry-run estimation, async job lifecycle.
- **Admin**: connections, publishing, metadata, styles, imports, operations, observability.
- **Import**: file import pipeline + Esri service import.

## Data Access

### Postgres Provider

- **Raw Npgsql**: no ORM.
- **QueryBuilder + DataAccess** split:
  - `FeatureQueryBuilder` constructs parameterized SQL.
  - `FeatureDataAccess` executes queries and maps results.
- **Prepared statement cache** is optional and uses safe parameter binding.
- **JSONB attributes** are accessed via validated field names and parameterized values.

### DuckDB Provider

- **DuckDB.NET.Data**: embedded database, no external server.
- Same **QueryBuilder + DataAccess** split as Postgres, with DuckDB-compatible spatial SQL (PostGIS-compatible function names).
- **Read-only by design**: write interfaces are wired to `ReadOnlyFeatureWriter` which rejects all mutations.
- **Configuration-driven catalog**: layers and services are defined in `appsettings.json`, not in an admin database.
- See the [DuckDB Provider Guide](../operator/duckdb-provider.md) for operator configuration.

## Configuration and Limits

- Configuration is environment-variable friendly with source-generated validation.
- Shared limits are enforced across protocols (`Limits__*`).
- Secret references are supported for connection strings and admin credentials.

## Security

- Admin APIs are protected with API keys and OIDC (when enabled).
- Public protocol endpoints are read/write based on server configuration and limits.
- No in-app audit/compliance storage is implemented; use external tooling if needed.

## Observability

- OpenTelemetry-based instrumentation is wired into the host.
- Built-in endpoints provide health and metrics snapshots:
  - `/healthz/live`, `/healthz/ready`
  - `/api/v1/admin/performance/*`
  - `/api/v1/admin/observability/*`

## Testing

- Integration tests use Testcontainers + PostGIS.
- Architecture tests enforce dependency direction and endpoint coverage.
- Performance benchmarks live under `benchmarks/`.

## Architectural Constraints (Enforced)

- **No controllers**: Minimal APIs only.
- **Dependency flow**: Core <- Postgres / DuckDB <- Server.
- **Public API docs**: all public types require XML documentation.
- **AOT compatibility**: reflection avoided in hot paths; source-gen JSON.

For deployment architecture and infrastructure details, see:
- [Deployment Scenarios](../operator/DEPLOYMENT_SCENARIOS.md)
- [Architecture Diagrams](ARCHITECTURE_DIAGRAMS.md)
- [Platform Overview](../PLATFORM.md)

For the forward-looking AI-first analyst and builder direction, see:
- [AI-First Operator Architecture](AI_OPERATOR_ARCHITECTURE.md)
