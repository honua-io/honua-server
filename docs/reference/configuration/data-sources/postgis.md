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

## Connection poolers and proxies (RDS Proxy, PgBouncer)

Honua applies its PostgreSQL session settings (`lock_timeout`, `statement_timeout`, `idle_in_transaction_session_timeout`, and the default `search_path`) with plain `SET` statements that run after each physical connection opens — not via the libpq `options` startup parameter, which AWS RDS Proxy rejects (`0A000: Feature not supported: RDS Proxy currently doesn't support command-line options`) and transaction-mode poolers such as PgBouncer break on.

- **AWS RDS Proxy** is supported and is the recommended way to protect connection slots when running Honua on Lambda or other rapidly scaling serverless platforms. Note that the per-connection `SET` statements cause [session pinning](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/rds-proxy-pinning.html) on the proxy: each Honua-held connection stays pinned to one database connection. This reduces the proxy's ability to multiplex but preserves the thing the proxy is deployed for — capping total database connections; size `Limits__Connections__MaxConnectionPoolSize` accordingly.
- **PgBouncer (transaction mode)**: connections open without errors, but transaction-mode pooling does not guarantee that session-level `SET` values follow Honua's connection across transactions. Prefer session mode, or use direct connections with Honua's own pool limits.
- **Npgsql multiplexing** (`Limits__Connections__Multiplexing=true`, default `false`) is mutually exclusive with RDS Proxy and transaction-mode poolers: in multiplexing mode Honua intentionally keeps the session settings on the `options` startup parameter, because startup parameters are the only delivery that survives interleaved logical sessions on shared physical connections. Leave multiplexing off (the default) when connecting through any pooler or proxy.

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
| `Limits__Connections__Multiplexing` | `false` | Npgsql multiplexing (`false`, `true`, or `auto`). Incompatible with RDS Proxy and transaction-mode poolers — see [Connection poolers and proxies](#connection-poolers-and-proxies-rds-proxy-pgbouncer). |

The full admission set (adaptive bounds, target lease duration, update interval) is in the [environment variable reference](../environment-variables.md#admission-and-pooling). Pool and admission behavior can be observed at `GET /monitoring/metrics/connection-pool`.

## Dedicated raster worker pool

The `raster-postgis` worker has a separate, opt-in data source and governance policy. It is not
registered by the ordinary managed/web composition and never falls back to
`ConnectionStrings__DefaultConnection`. The dedicated connection must authenticate as the exact
role configured by `RequiredRole`; a role mismatch fails before raster operation SQL is allowed.
Provision that role outside the web container with only the table/schema privileges required by
the enabled raster operations.

| Variable | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__RasterPostgis` | none | **Required by the `raster-postgis` worker.** Dedicated Npgsql connection string and pool. |
| `Geoprocessing__Raster__Postgis__RequiredRole` | `honua_raster_gp` | Exact database role required on every governed connection. |
| `Geoprocessing__Raster__Postgis__SearchPathSchema` | `honua` | Fixed safe schema when tenant-schema routing is disabled. |
| `Geoprocessing__Raster__Postgis__RequireTenantSchema` | `false` | Require the configured tenant schema resolver and fail closed when it cannot resolve a tenant. |
| `Geoprocessing__Raster__Postgis__MaxConcurrency` | `4` | Process-wide PostGIS raster attempts and dedicated pool maximum. |
| `Geoprocessing__Raster__Postgis__MaxConcurrencyPerTenant` | `2` | Default concurrent attempt ceiling for one tenant. |
| `Geoprocessing__Raster__Postgis__QueueTimeout` | `00:00:05` | Combined wait budget for per-tenant and global admission. |
| `Geoprocessing__Raster__Postgis__StatementTimeout` | `00:10:00` | Server-side statement timeout applied before operation SQL. |
| `Geoprocessing__Raster__Postgis__LockTimeout` | `00:00:10` | Server-side lock timeout applied before operation SQL. |
| `Geoprocessing__Raster__Postgis__IdleInTransactionTimeout` | `00:01:00` | Server-side idle transaction timeout applied before operation SQL. |

`Geoprocessing__Raster__Postgis__WorkLimits` contains positive ceilings for
`MaxSourceCount`, `MaxBandCount`, `MaxZoneCount`, `MaxInputPixels`, `MaxOutputPixels`,
`MaxDecodedBytes`, `MaxScratchBytes`, and `MaxDatabaseWork`. Unknown cost inputs are rejected.
Per-tenant limits use
`Geoprocessing__Raster__Postgis__Tenants__<tenant-id>__MaxConcurrency` and sparse
`...__WorkLimits__<dimension>` overrides. Tenant overrides may only tighten the global values;
omitted dimensions inherit them.

Every acquired connection is non-multiplexed and reset when returned to its dedicated pool. Honua
checks `current_user`, then applies the tenant id, durable operation id, attempt number,
`search_path`, and all three server-side timeouts before handing the connection to provider code.
The durable job cancellation token is passed to Npgsql commands so cancellation actively
interrupts database work.

## Related pages

- [Data sources overview](README.md)
- [Environment variables](../environment-variables.md)
