# Architecture Overview

This document describes the current Honua Server architecture and the constraints that shape the codebase.

## Goals

- **Provider-backed**: PostgreSQL/PostGIS is the primary read/write provider; DuckDB is an embedded read-only provider for analytics and reference workloads; SQL Server and MySQL/MariaDB are read/query-only providers for enterprise/user-managed spatial tables.
- **Open standards**: serve multiple GIS and data protocols from one dataset.
- **Clean dependencies**: `Honua.Core` <- `Honua.Postgres` / `Honua.DuckDB` / `Honua.SqlServer` / `Honua.MySql` <- `Honua.Server`.
- **Minimal API surface**: endpoints are defined with Minimal APIs, not MVC controllers.
- **AOT-friendly**: avoid reflection in hot paths and use source-generated JSON/logging.

## Solution Layout

```
src/
├── Honua.Server/     # ASP.NET Core host + Minimal API endpoints
├── Honua.Core/       # Domain models + abstractions
├── Honua.Postgres/   # PostgreSQL/PostGIS implementation (read/write)
├── Honua.DuckDB/     # DuckDB implementation (read-only)
├── Honua.SqlServer/  # SQL Server geometry/geography implementation (read-only)
└── Honua.MySql/      # MySQL/MariaDB implementation (read/query-only)
```

Key points:
- **Honua.Core** defines domain models, protocol DTOs, and abstractions.
- **Honua.Postgres** implements Core interfaces using raw Npgsql and PostGIS.
- **Honua.DuckDB** implements Core read interfaces (`IFeatureReader`, `IStreamingFeatureStore`, etc.) for embedded DuckDB databases. Write operations are rejected at startup via capability stripping.
- **Honua.SqlServer** implements `IFeatureReader` against SQL Server `geometry`/`geography` tables. Registered as an additional `IFeatureDataProvider` and selected per-layer when the layer's `DataConnection` resolves to provider `sqlserver`/`mssql`. Edits, native MVT/FlatGeobuf/Geobuf/GML, and statistics aggregates are deliberately disabled in this slice.
- **Honua.MySql** implements Core read interfaces against user-managed MySQL 8.0.11+ / MariaDB 10.6+ tables. The provider declares `FeatureProviderCapabilities.ReadOnlyMySql` and throws `NotSupportedException` for unsupported paths (edits, statistics, native MVT/FlatGeobuf/Geobuf/GML, streaming GeoJSON, KNN, cross-SRID filters). The provider name resolves under `mysql` and the `mariadb` alias.
- **Honua.Server** composes endpoints and handlers, selecting the active primary provider via `DataSource:Provider` configuration; additional read-only providers (DuckDB, SQL Server, MySQL/MariaDB) plug in alongside or are selected through provider binding.
- The Blazor admin UI lives in the separate `honua-server-admin` repo and talks to this server's Admin API.

## Feature Slices (Server)

The server is organized by vertical slices under `src/Honua.Server/Features/`.

- **FeatureServer**: GeoServices REST query/edit/attachments/related records.
- **MapServer**: GeoServices REST map rendering (export/identify/legend) + layer query.
- **OGC Features**: collections/items with transactions.
- **OGC Tiles**: tilesets metadata and vector tiles.
- **OData**: CRUD + query options ($filter, $select, $orderby, $top, $skip, $count, $search, $apply, $batch).
- **Tiles**: MVT + TileJSON.
- **Geoprocessing**: gRPC `ProcessService` — plan validation, dry-run estimation, async job lifecycle. Workspace lifecycle management — artifact storage, retention policies, quota evaluation, promotion from temporary to durable workspaces, and background cleanup. Protocol adapters: GeoServices REST GPServer — catalog-backed service info, task info, async `submitJob`, job status polling, cancellation, and per-parameter result routes over the canonical process runtime; generic built-in tasks are currently async-only and do not publish a generic `execute` route, while execution-engine result delivery remains follow-on. OGC API Processes — REST process discovery, async execution, and job/result endpoints over the same canonical runtime.
- **Control Plane**: Durable job orchestration substrate — queue, claim/heartbeat, retry, reconciliation, structured execution logs, and artifact references ([ADR-0031](adr/0031-durable-job-orchestration-substrate.md)). `AddJobOrchestration()` is safe for lean API images; `AddJobWorker()` adds queue-based claim/execute for dedicated worker hosts (not yet wired). The pluggable batch-compute backend contract and execution-job reconciler are registered directly in the combined host. The execution-job reconciler (`ExecutionJobReconcilerBackgroundService`) runs on every Redis-enabled host, polling active jobs and dispatching to pluggable `IBatchComputeBackend` adapters resolved by `(BackendName, TargetKind)`. The `LocalBatchComputeBackend` bridges in-process worker progress; additional backends (AWS Batch, Kubernetes Job) plug in through the same contract.
- **Orchestration**: Declarative workflow layer that composes canonical `AnalysisPlan` jobs into chained, scheduled, and DAG-style runs ([ADR-0032](adr/0032-workflow-orchestration-layer.md)). Steps submit through `IWorkflowJobExecutor` (geoprocessing-backed) so every workflow step reuses canonical job, retry, and cancellation semantics. Background services (`WorkflowOrchestrationBackgroundService`, `WorkflowSchedulerBackgroundService`) only start when Redis-backed stores are available.
- **Admin**: connections, publishing, metadata, styles, imports, operations, observability.
- **Import**: file import pipeline, public ArcGIS GeoServices REST layer import, and GeoServer/ArcGIS migration inventory.

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

### SQL Server Provider

- **Microsoft.Data.SqlClient**: standard .NET SQL Server driver with built-in pooling.
- Same **QueryBuilder + DataAccess** split as Postgres/DuckDB, translating to T-SQL with `geometry::*` / `geography::*` spatial functions and `OFFSET`/`FETCH` paging.
- **Read-only by design**: `IFeatureDataProvider.Writer` is `null`; statistics, top-features, date/value/H3 bins, and temporal extents throw `NotSupportedException`.
- **Plug-in registration**: `AddSqlServerFeatureProvider` wires the provider as an additional `IFeatureDataProvider` selected per-layer through `FeatureProviderBindingResolver`. The primary backend (Postgres or DuckDB) remains in place.
- **Identifier safety**: all configured identifiers (table, schema, primary key, geometry column, attribute fields) are validated against `[A-Za-z_][A-Za-z0-9_]*` and bracket-quoted.
- See the [SQL Server Provider Guide](../operator/sqlserver-provider.md) for operator configuration.

### MySQL / MariaDB Provider

- **MySqlConnector**: pooled `MySqlDataSource` against MySQL 8.0.11+ / MariaDB 10.6+; no ORM.
- Same **QueryBuilder + DataAccess** split as Postgres, with MySQL-canonical spatial SQL (`MBRIntersects` + `ST_Intersects`, `ST_Within`, `ST_Distance_Sphere`, etc.). All identifiers are backtick-quoted; parameters use named `@p0..@pN` form.
- **Read/query-only by design**: edits, statistics, native MVT/FlatGeobuf/Geobuf/GML output, streaming GeoJSON, KNN/nearest-neighbor, and temporal (`datetime`) filters are reported as unsupported in `FeatureProviderCapabilities.ReadOnlyMySql` and throw `NotSupportedException` at the query builder / filter translator boundary. Cross-SRID filters and output transforms are likewise rejected — there is no portable `ST_Transform`; callers must pre-project. The Point/MultiPoint distance guard is enforced both for `FeatureQuery.SpatialFilter` and for CQL2 distance predicates translated through `MySqlSqlFilterTranslator`.
- **Read-only DI surface**: `MySqlFeatureStore` implements `IFeatureDataProvider`, `IFeatureReader`, `IPagedFeatureReader`, and `IStreamingFeatureStore`. The provider registers no-op stubs (`ReadOnlyMySqlFeatureWriter`, `ReadOnlyMySqlReplicaRepository`, `ReadOnlyMySqlChangeTracker`, `ReadOnlyMySqlTileProvider`, `ReadOnlyMySqlGmlFeatureStore`) so DI activation succeeds for protocol handlers (FeatureServer query executor, gRPC service, OGC handlers, WFS, OData) under `DataSource:Provider=mysql`; calls to write-shaped APIs raise `NotSupportedException`. `Program.cs` skips the Postgres replica registration for `mysql`/`mariadb` so the stubs are not overwritten. Streaming iterates the existing select path in fixed-size pages (default 1000) — there is no native MySQL cursor — and treats any present `Limit` (including `Limit=0`) as a budget rather than falling through to the page size.
- **Extent**: `GetExtentAsync` is supported only for Point and Polygon/MultiPolygon layers (Point uses `MIN/MAX(ST_X/ST_Y)`; Polygon/MultiPolygon uses `ST_PointN(ST_ExteriorRing(ST_Envelope(geom)), 1|3)`). Other geometry types raise `NotSupportedException` because MariaDB does not support the 2-arg `ST_SRID` retag and `ST_X`/`ST_Y` are point-only on MySQL. Invalid `GeometryType` configuration values are rejected at startup (no silent fallback to `Point`); mappings use `Enum.Parse` to surface any future validator bypass.
- **User-managed tables**: unlike PostGIS (internal `features` table discriminated by `layer_id`), MySQL targets user tables. `MySql:Layers` is validated at startup and builds the singleton `MySqlLayerMappingRegistry` plus `MySqlLayerCatalog`; the store and query builder read that registry by layer ID to bind the qualified table, primary key column, geometry column, and SRID.
- **Configuration-driven catalog**: layers and services are defined in `appsettings.json`. Attribute columns must be declared explicitly (no schema introspection in this slice).
- **Telemetry**: each query opens a `mysql.<operation>` activity span on the `Honua.MySql.FeatureDataAccess` `ActivitySource`, registered with the central `Honua.ServiceDefaults` `AddSource` list so spans flow through the OTLP exporter when tracing is enabled. Failures emit a structured `MySqlLog.QueryFailed` event (EventId 8902).
- See the [MySQL/MariaDB Provider Guide](../operator/mysql-provider.md) for operator configuration, version floors, and limitations.

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

## Architectural Constraints (Enforced)

- **No controllers**: Minimal APIs only.
- **Dependency flow**: Core <- Postgres / DuckDB / SqlServer / MySql <- Server.
- **Public API docs**: all public types require XML documentation.
- **AOT compatibility**: reflection avoided in hot paths; source-gen JSON.

For deployment architecture and infrastructure details, see:
- [Deployment Scenarios](../operator/DEPLOYMENT_SCENARIOS.md)
- [Architecture Diagrams](ARCHITECTURE_DIAGRAMS.md)
- [Platform Overview](../PLATFORM.md)

Historical AI-operator design notes are archived and are not part of the current contributor entrypoints.
