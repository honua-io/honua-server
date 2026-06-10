# PostGIS provider

PostgreSQL with PostGIS is Honua's default and only full read/write provider. This page covers the connection string, required extensions, managed-Postgres (Aurora / Azure Flexible Server) setup, and the pooling/admission variables that govern database load.

## Connection

| Variable | Purpose |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | **Required.** Standard Npgsql connection string. |

```
Host=db.example.com;Port=5432;Database=honua;Username=honua_app;Password=...;SSL Mode=Require;Trust Server Certificate=false
```

- Honua expects a single primary (read-write) endpoint.
- There is no replica connection setting — read replicas are not load-balanced at the application layer. Point Honua at the writer endpoint and rely on DNS-level failover.
- For TLS configuration, see the [TLS guide](../../../guides/secure/tls-and-mtls.md).

## Supported versions

PostgreSQL 16–18 with PostGIS 3.4–3.6 are tested in CI; see the [tested configurations matrix](README.md#tested-postgresql-configurations).

## Required extensions

| Extension | Required | Purpose |
| --- | --- | --- |
| `postgis` | Yes | Spatial operations, geometry types, spatial indexing. |
| `postgis_raster` | For raster features | Raster data storage and analysis. |
| `pgcrypto` | Yes | Cryptographic functions for secure identifiers. |
| `unaccent` | Yes | Accent-insensitive text search normalization. |

```sql
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_raster;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS unaccent;
```

## Startup validation

Honua performs a PostGIS preflight check at startup:

1. Queries `SELECT version()` for the engine version.
2. Queries `pg_extension` for installed extensions.
3. Logs engine and PostGIS versions for operator visibility.
4. Non-Development environments fail fast (CrashLoopBackOff) if PostGIS is missing.
5. Development mode logs a warning and continues. Missing `postgis_raster` warns but never blocks startup.

The preflight result is available via `GET /api/v1/admin/deploy/preflight?includeDiagnostics=true` in the `databaseCompatibility` field.

## AWS Aurora PostgreSQL

1. Create a custom DB cluster parameter group (or modify the default); Aurora bundles PostGIS — enable it via the parameter group. Optionally include `pg_stat_statements` in `shared_preload_libraries` for monitoring.
2. Create the four extensions listed above on your database.
3. Connection pooling: Aurora provides a built-in PgBouncer-compatible endpoint. Use the cluster writer endpoint for Honua's connection.
4. Failover: Aurora automatic failover updates the cluster DNS endpoint; no application-level changes are needed.

## Azure Database for PostgreSQL Flexible Server

1. In **Server parameters**, set `azure.extensions` to include `POSTGIS`, `POSTGIS_RASTER`, `PGCRYPTO`, `UNACCENT` (may require a restart).
2. Create the four extensions on your database.
3. Connection pooling: Flexible Server supports built-in PgBouncer; Honua works with both direct and pooled connections.
4. High availability: configure zone-redundant HA. Honua connects to the primary endpoint; failover is transparent at the DNS level.

## Pooling and admission variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `Limits__Connections__MaxConnectionPoolSize` | `200` | Npgsql pool maximum. |
| `Limits__Connections__MinConnectionPoolSize` | `20` | Npgsql pool minimum. |
| `Limits__Connections__MaxConcurrentQueries` | `200` | Ceiling on concurrently executing queries. |
| `Limits__Connections__ConnectionIdleLifetimeSeconds` | `600` | Idle connection lifetime. |
| `Limits__Connections__ConnectionAcquisitionTimeoutSeconds` | `5` | Max wait to acquire a pooled connection. |
| `Limits__Connections__CommandTimeoutSeconds` | `30` | Command timeout. |
| `Limits__Connections__StatementTimeout` / `LockTimeout` | `00:00:30` | Server-side statement and lock timeouts. |
| `Limits__Connections__IdleInTransactionTimeout` | `00:01:00` | Idle-in-transaction timeout. |
| `Limits__Connections__AdaptiveConcurrencyEnabled` | `false` | Adaptive query admission below the concurrency ceiling. |

The full admission set (adaptive bounds, target lease duration, update interval) is in the [environment variable reference](../environment-variables.md#admission-and-pooling). Pool and admission behavior can be observed at `GET /monitoring/metrics/connection-pool`.

## Related pages

- [Data sources overview](README.md)
- [Environment variables](../environment-variables.md)
