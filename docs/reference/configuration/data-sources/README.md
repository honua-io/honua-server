---
type: index
title: "Data sources"
description: "Honua serves data from one primary provider (PostgreSQL/PostGIS by default) plus optional read-only providers that plug in alongside it through the shared feature-provider router."
---
# Data sources

Honua serves data from one primary provider (PostgreSQL/PostGIS by default) plus optional read-only providers that plug in alongside it through the shared feature-provider router. PostGIS is the only full read/write backend; the others are read/query slices for serving data in place.

## Provider capability matrix

| Provider | Access | Selected by | Notes |
| --- | --- | --- | --- |
| [PostGIS](postgis.md) | Full read/write | `DataSource__Provider=postgres` (default) | Edits, statistics, native MVT/FlatGeobuf/Geobuf/GeoParquet/GeoArrow output, analytics, raster. |
| [DuckDB](duckdb.md) | Read-only (primary) | `DataSource__Provider=duckdb` | Embedded single-file database for analytical and reference layers; no external infrastructure. |
| [SQL Server](sql-server.md) | Read/query only (additional) | Layer connection resolves to `sqlserver`/`mssql` | `geometry`/`geography` tables; no edits, native MVT, encoded exports, or statistics. |
| [Oracle](oracle.md) | Read/query only (additional) | Layer connection resolves to `oracle`/`oracledb` | Standard `SDO_GEOMETRY` only; ArcSDE `ST_Geometry` and versioned tables are refused. Not Native AOT compatible. |
| [MySQL/MariaDB](mysql-mariadb.md) | Read/query only (primary) | `DataSource__Provider=mysql` | MySQL 8.0.12+ / MariaDB 10.6+; no edits, statistics, encoded exports, streaming GeoJSON, KNN, temporal filters, or cross-SRID transforms. |
| [Amazon Redshift](redshift.md) | Read/query only (additional) | Layer connection resolves to `redshift` | Native `GEOMETRY`/`GEOGRAPHY` (not PostGIS) over the PostgreSQL wire protocol via Npgsql; no edits, native MVT, encoded exports, statistics, distance/KNN, or temporal filters. |
| [Snowflake](snowflake.md) | Read/query only (additional) | Layer connection resolves to `snowflake`/`snowflakedb` | Native `GEOGRAPHY`/`GEOMETRY` tables; no edits, statistics, native MVT/encoded exports, distance/KNN, temporal filters, or cross-SRID transforms. Not Native AOT compatible. |
| [Databricks](databricks.md) | Read/query only (additional, best-effort) | Layer connection resolves to `databricks`/`databrickssql`/`dbsql` | HTTP read-through over the SQL Statement Execution REST API against a SQL Warehouse; no native driver. Statistics are supported; edits and native encoded exports are not. Spatial functions depend on the DBSQL/runtime. |

Provider selection variables are listed in the [environment variable reference](../environment-variables.md#database-and-providers). Warehouse registration flags are at the end of this page.

## Nightly HTTP-stack coverage

DuckDB, MySQL/MariaDB, and SQL Server are exercised nightly through the full HTTP
stack against real seeded rows — GeoServices FeatureServer, OGC API Features, OData,
and tiles where applicable; each provider's page lists its exact coverage and gaps.
Oracle, Redshift, Snowflake, and Databricks remain experimental and are not part of
that suite.

## Tested PostgreSQL configurations

| Provider | Engine version | PostGIS version | CI status |
| --- | --- | --- | --- |
| Self-hosted | PostgreSQL 16.x | PostGIS 3.4 | Tested |
| Self-hosted | PostgreSQL 17.x | PostGIS 3.5 | Tested |
| Self-hosted | PostgreSQL 18.x | PostGIS 3.6 | Tested |
| AWS Aurora PostgreSQL | 16.x | PostGIS 3.4 | Tested (CI proxy) |
| Azure Database for PostgreSQL Flexible Server | 16.x, 17.x | PostGIS 3.5 | Tested (CI proxy) |

CI uses `postgis/postgis` Docker images as version-level proxies for managed-service behavior. True managed-service validation requires deployment to actual Aurora/Azure instances.

## Other supported engine versions

| Provider | Versions |
| --- | --- |
| SQL Server | See [SQL Server provider](sql-server.md#supported-versions). |
| Oracle | See [Oracle provider](oracle.md). |
| MySQL/MariaDB | MySQL 8.0.12+, MariaDB 10.6+ — see [MySQL/MariaDB provider](mysql-mariadb.md). |
| Amazon Redshift | PostgreSQL-wire-compatible; native Redshift spatial layer — see [Amazon Redshift provider](redshift.md). |
| Snowflake | See [Snowflake provider](snowflake.md). |
| DuckDB | Embedded; bundled with the server — see [DuckDB provider](duckdb.md). |
| Databricks | Any SQL Warehouse reachable over the Statement Execution REST API — see [Databricks provider](databricks.md). |

## PostGIS requirements and managed-Postgres setup

Required extensions, connection-string format, Aurora and Azure Flexible Server setup, pooling, and startup validation moved to the dedicated [PostGIS page](postgis.md).

## Warehouse provider environment variables

Amazon Redshift, Snowflake, and Databricks register in `InfrastructureCompositionRoot` only when `WarehouseProviderDecisions` reports the provider enabled. Each experimental gate defaults to false, and `{Provider}:Enabled` defaults to true, so the provider stays unregistered until the gate is set. Setting `{Provider}__Enabled=true` while its gate is false throws at startup. These flags do not change capability or statistics rows.

They are secondary providers. `DataSource__Provider` accepts only `postgres`/`postgis`, `duckdb`, or `mysql`/`mariadb`. Setting it to `redshift`, `snowflake`, or `databricks` throws at startup. A layer reaches a warehouse through its data connection's provider name.

Environment names below are the `.env.example` form. They bind to the colon keys the composition root reads (`Experimental:Features:RedshiftProvider`, `Redshift:Enabled`, and the same pattern for Snowflake and Databricks).

| Variable | Default | Role |
| --- | --- | --- |
| `Experimental__Features__RedshiftProvider` | `false` | Experimental gate. Required before Redshift registers. |
| `Redshift__Enabled` | `true` | Set `false` to skip registration. Explicit `true` without the gate throws. |
| `Redshift__ConnectionString` | unset | Default Npgsql connection string, including the password. Leave unset when layers use a secure data connection. |
| `Redshift__CommandTimeoutSeconds` | `60` | Read command timeout. |
| `Experimental__Features__SnowflakeProvider` | `false` | Experimental gate. Required before Snowflake registers. |
| `Snowflake__Enabled` | `true` | Set `false` to skip registration. Explicit `true` without the gate throws. |
| `Snowflake__ConnectionString` | unset | ADO.NET connection string (`account`, `user`, `password`, `db`, `schema`, `warehouse`, `role`). This string is the connection secret. |
| `Snowflake__CommandTimeoutSeconds` | `60` | Read command timeout. |
| `Experimental__Features__DatabricksProvider` | `false` | Experimental gate. Required before Databricks registers. |
| `Databricks__Enabled` | `true` | Set `false` to skip registration. Explicit `true` without the gate throws. |
| `Databricks__Host` | unset | Absolute HTTPS workspace URL. Blank leaves a registered provider dormant. |
| `Databricks__WarehouseId` | unset | SQL warehouse id. Required when `Host` is set. |
| `Databricks__Token` | unset | Personal access token or OAuth bearer token. Required when `Host` is set. |
| `Databricks__Catalog` | unset | Optional default Unity Catalog name. |
| `Databricks__Schema` | unset | Optional default schema. |
| `Databricks__CommandTimeoutSeconds` | `120` | Statement wait budget. |

`Snowflake.Data` is not Native AOT compatible. AOT verification sets MSBuild `HonuaSkipSnowflakeForAotVerification=true`, which drops the `Honua.Snowflake` project reference and defines `HONUA_SKIP_SNOWFLAKE`. Startup then compiles out Snowflake registration, and the Snowflake decision is unsupported even if its gate is on. Redshift (Npgsql) and Databricks (HTTP) have no equivalent skip switch.

Store connection strings and `Databricks__Token` in the deployment secret mechanism. Do not commit them. Provider pages describe layer bindings and failure behavior. This section does not restate statistics or predicate support.
