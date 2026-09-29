---
type: reference
title: "PostGIS provider"
description: "PostgreSQL with PostGIS is Honua's default and only full read/write provider."
---
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

Honua never creates `postgis_raster` from application startup. When the
extension is absent, the canonical migration runner leaves the optional raster
migration root unjournaled and verifies the non-raster core floor. Provisioning
the extension later makes that root eligible on the next migration run.

## Startup validation

Honua performs a PostGIS preflight check at startup:

1. Queries `SELECT version()` for the engine version.
2. Queries `pg_extension` for installed extensions.
3. Logs engine and PostGIS versions for operator visibility.
4. Non-Development environments fail fast (CrashLoopBackOff) if PostGIS is missing.
5. Development mode logs a warning and continues. Missing `postgis_raster` warns but never blocks startup.

For deployments using a non-default `Database:Schema`, migration 109 moves a
complete legacy Metadata v2, SensorThings, or raster family out of `honua` into
the configured schema. It aborts transactionally on partial or coexisting
families so operators can reconcile ambiguous state without an automatic merge.
Because older nodes still query the legacy schema, this move is a contract-phase
migration: drain older nodes, review the deploy preflight, and provide the
one-shot `HONUA_APPROVE_CONTRACT_MIGRATIONS` nonce reported by the migration gate
before applying it.

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

Pool maxima and minima apply **per data source, per Honua process**. The primary
database, named secure connections, and registered source-bound feature/tile
connections have independently owned pools. Source-bound reads use the configured
pool settings and the same admission gate as primary database operations. A
source's credentials, database, and search path remain independent of the primary
connection. Primary `Database:Schema` and request-scoped schema overrides do not
apply to bound source connections.

The admission ceiling is shared within each process; it bounds active leases,
not the sum of idle physical connections across pools or replicas. Account for
all source pools and application replicas when budgeting PostgreSQL connection
slots. Credential changes for a registered connection retire its previous pool;
pending opens and active connection leases retain their pool until the connection
is returned. This also protects multiplexed logical connections between commands.
Shutdown retires all cached pools with the same lifetime guarantees. Legacy bindings without a stable connection
ID use a separate pool for each distinct connection string.

Named secure and source-bound pools are retired when their registered connection
is deleted locally. Pools with no active or opening leases also expire after
`Limits:Connections:ConnectionIdleLifetimeSeconds`, checked every
`ConnectionPruningIntervalSeconds`. This bounds idle pools on other server
instances and requests that finish opening after a deletion. The idle period
starts when the last lease returns. Minimum pool size applies while the source
pool remains active; a later request recreates an expired pool. Active leases
finish normally, and the primary default database pool is unaffected.

The full admission set (adaptive bounds, target lease duration, update interval) is in the [environment variable reference](../environment-variables.md#admission-and-pooling). Pool and admission behavior can be observed at `GET /monitoring/metrics/connection-pool`.

## Indexing numeric source columns

For source-backed layers with physical columns, Honua retains the published
numeric type in filter expressions. Newly published PostgreSQL `smallint` columns
include a provider hint that lets simple `Integer` comparisons (`=`, `!=`, `<>`,
`<`, `<=`, `>`, `>=` with an integer literal) use an ordinary index on the source
column. Parameters retain their original integer width, including out-of-range
literals.

Publication hints can become stale. Before an eligible buffered or streaming read,
Honua checks the actual column type in a row-free statement batched with the query
in one database round trip. The batch holds the relation lock until execution
finishes, preventing external DDL between verification and use. A mismatched type,
domain, or custom operator search path uses the original declared-type query.
The discarded statement is guarded against evaluating source rows or row-security
policies. Existing transactions, bindings without hints, compound predicates,
arithmetic, explicit casts, JSONB attributes, counts and aggregates retain the
canonical casts.
Republish an existing source layer to record its current smallint hints; existing
publications do not discover or persist these hints during queries.

Those canonical queries can still use an expression index matching the declared
type. For example, `priority::integer` can prevent an ordinary `smallint` index
from serving a selective filter; an expression index preserves its semantics:

```sql
CREATE INDEX CONCURRENTLY features_priority_integer_idx
    ON public.features ((priority::integer));
ANALYZE public.features;
EXPLAIN SELECT * FROM public.features WHERE priority::integer = 32767;
```

Replace the example table and column with the source binding's names. Run
`CREATE INDEX CONCURRENTLY` outside a transaction block. Check the actual query
plan and workload before retaining the additional index: it consumes storage and
adds maintenance work on writes. See PostgreSQL's
[indexes on expressions](https://www.postgresql.org/docs/current/indexes-expressional.html).

This example applies to physical `smallint` columns published as `Integer`, not
fields stored inside a JSONB attributes document. Honua keeps numeric casts
because removing them can change arithmetic overflow and decimal-to-Double
comparison results. An expression index supports the canonical declared-type
predicate without changing those query semantics. Check the plan before adding
one to a newly published source whose simple comparisons already use its ordinary
index.

## Bounded source-backed spatial reads

`Database__PreferSerialBoundedSpatialReads=true` opts into a narrow serial-planner
profile for source-backed PostGIS point layers. The default is `false`. Eligible
reads have a simple intersects/envelope bbox, an effective first-page limit of
1–100 features before the extra pagination probe row, default ordering (including
the normalized ascending primary-ID sort), and no distinct, branch-version or null-geometry request.
Unknown geometry types, ambient transactions and borrowed mutation transactions
retain ordinary planning.
Counts, statistics, streaming, tiles, larger pages, later pages and custom sorts
also retain ordinary planning.

For an eligible read, Honua batches a transaction-local
`max_parallel_workers_per_gather=0` setting with the original parameterized
feature SELECT. The setting ends with the batch, including errors, cancellation
and early reader disposal. The scoped SELECT uses the equivalent `SELECT ALL`
modifier so auto-preparation cannot reuse a parallel plan from an ordinary read.
Connection pool limits, query admission, predicates, authorization, CRS and
pagination remain in effect.

This profile targets parallel-worker startup overhead observed in bounded point
bbox reads. It does not set a PostgreSQL global or session default. Benchmark
representative selectivities and concurrent workloads on your deployment before
enabling it; limiting returned rows does not limit the work required to find them.

## Related pages

- [Data sources overview](README.md)
- [Environment variables](../environment-variables.md)
