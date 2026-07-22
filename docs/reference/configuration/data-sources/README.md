# Data sources

Honua serves data from one primary provider (PostgreSQL/PostGIS by default) plus optional read-only providers that plug in alongside it through the shared feature-provider router. PostGIS is the only full read/write backend; the others are read/query slices for serving data in place.

## Provider capability matrix

| Provider | Access | Selected by | Notes |
| --- | --- | --- | --- |
| [PostGIS](postgis.md) | Full read/write | `DataSource__Provider=postgres` (default) | Edits, statistics, native MVT/FlatGeobuf/Geobuf/GeoParquet/GeoArrow output, analytics, raster. |
| [DuckDB](duckdb.md) | Read-only (primary) | `DataSource__Provider=duckdb` | Embedded single-file database for analytical and reference layers; no external infrastructure. |
| [SQL Server](sql-server.md) | Read/query only (additional) | Layer connection resolves to `sqlserver`/`mssql` | `geometry`/`geography` tables; no edits, native MVT, encoded exports, or statistics. |
| [Oracle](oracle.md) | Read/query only (additional) | Layer connection resolves to `oracle`/`oracledb` | Standard `SDO_GEOMETRY` only; ArcSDE `ST_Geometry` and versioned tables are refused. Not Native AOT compatible. |
| [MySQL/MariaDB](mysql-mariadb.md) | Read/query only (primary) | `DataSource__Provider=mysql` | MySQL 8.0.11+ / MariaDB 10.6+; no edits, statistics, encoded exports, streaming GeoJSON, KNN, temporal filters, or cross-SRID transforms. |
| [Amazon Redshift](redshift.md) | Read/query only (additional) | Layer connection resolves to `redshift` | Native `GEOMETRY`/`GEOGRAPHY` (not PostGIS) over the PostgreSQL wire protocol via Npgsql; no edits, native MVT, encoded exports, statistics, distance/KNN, or temporal filters. |
| [Snowflake](snowflake.md) | Read/query only (additional) | Layer connection resolves to `snowflake`/`snowflakedb` | Native `GEOGRAPHY`/`GEOMETRY` tables; no edits, statistics, native MVT/encoded exports, distance/KNN, temporal filters, or cross-SRID transforms. Not Native AOT compatible. |
| [Databricks](databricks.md) | Read/query only (additional, best-effort) | Layer connection resolves to `databricks`/`databrickssql`/`dbsql` | HTTP read-through over the SQL Statement Execution REST API against a SQL Warehouse; no native driver. No edits/statistics/encoded exports; spatial functions depend on the DBSQL/runtime. |

Provider selection variables are listed in the [environment variable reference](../environment-variables.md#database-and-providers).

### HTTP-Stack GA Proof

DuckDB, MySQL/MariaDB, and SQL Server each have real HTTP-stack interface-level test
coverage (`Honua.ProviderSmoke.Tests`, honua-server#2947): a provider-parameterized
web-app fixture boots the actual ASP.NET Core host against the provider (DuckDB
in-process, MySQL and SQL Server via Testcontainers) and asserts real seeded-row
correctness — not just 200s — through the read/query GA surface each documents support
for (GeoServices FeatureServer, OGC API Features, OData, and tiles where applicable; see
each provider's page for exact coverage and known gaps). Runs nightly and on demand via
[`provider-http-smoke.yml`](../../../../.github/workflows/provider-http-smoke.yml).
Oracle, Redshift, Snowflake, and Databricks remain experimental and are not part of this
suite; Oracle additionally gained a real-database provider-layer lane (see the
[Oracle provider](oracle.md#integration) page) as promotion groundwork.

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
| MySQL/MariaDB | MySQL 8.0.11+, MariaDB 10.6+ — see [MySQL/MariaDB provider](mysql-mariadb.md). |
| Amazon Redshift | PostgreSQL-wire-compatible; native Redshift spatial layer — see [Amazon Redshift provider](redshift.md). |
| Snowflake | See [Snowflake provider](snowflake.md). |
| DuckDB | Embedded; bundled with the server — see [DuckDB provider](duckdb.md). |
| Databricks | Any SQL Warehouse reachable over the Statement Execution REST API — see [Databricks provider](databricks.md). |

## PostGIS requirements and managed-Postgres setup

Required extensions, connection-string format, Aurora and Azure Flexible Server setup, pooling, and startup validation moved to the dedicated [PostGIS page](postgis.md).
