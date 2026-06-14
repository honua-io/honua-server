# ADR-0046: Audit C3 — Progressive `IDatabaseSession` Migration With Coexistence

## Status

Accepted

## Context

The May 2026 structural audit (`structural-audit-2026-05`) flagged Group C
item **C3 (Med)**: ADO.NET `DbConnection` / `DbTransaction` leak into three
interfaces in the `Honua.Core` abstractions surface:

- `IDatabaseConnectionProvider.OpenConnectionAsync(...)` returns `DbConnection`
- `IDatabaseConnectionProvider.OpenTransactionAsync(...)` returns `(DbConnection, DbTransaction)`
- `IFeatureChangeOutboxRepository` and `ITableDiscoveryService` similarly leak

The leak forces every consumer of `Honua.Core.Abstractions` (including the
modularization Phase 1 protocol assemblies that should remain lightweight)
into a transitive runtime contract with `System.Data.Common`. More
importantly it pulls the connection-lifetime decision out into ~125 call
sites — every one of which is responsible for `await using` and correct
transaction commit/rollback discipline.

The audit prescribed extracting an `IDatabaseSession` abstraction that owns
the underlying connection and exposes a Dapper-style command surface
(`ExecuteAsync`, `QuerySingleOrDefaultAsync<T>`, `QueryAsync<T>`,
`BeginTransactionAsync`).

## Decision

### Coexistence, not replacement

`IDatabaseSession` and `IDatabaseSessionFactory` are introduced as an
**additive** abstraction in `Honua.Core.Abstractions`. The leaky
`OpenConnectionAsync` / `OpenTransactionAsync` members on
`IDatabaseConnectionProvider` are **NOT** removed in this ADR. Both
interfaces are registered side-by-side in DI; consumers migrate
progressively, file-by-file, over multiple PRs.

A shared base implementation, `AdoNetDatabaseSession` (in
`Honua.Core/Features/Infrastructure/Session/`), centralises the
provider-neutral command/reader/transaction plumbing. Each relational
provider (Postgres, MySql, DuckDB) declares a thin subclass plus a
factory that wraps its existing `IDatabaseConnectionProvider`
implementation so the resilience pipeline (deadlock retry, schema
search-path, connection tracking, concurrency gate) is reused.

SqlServer is excluded — it has no `IDatabaseConnectionProvider`
implementation; it uses a separate `ISqlServerConnectionFactory` pattern
that is already de-leaked at the call-site boundary.

### Why not migrate all 125 call sites

The audit's original sketch ("Dapper or thin custom session") presumes a
call-site pattern that this codebase does not have. The PostgreSQL data
access in particular is built directly on `NpgsqlCommand` with:

- Typed `NpgsqlDbType.{Bigint,Text,TimestampTz,Smallint,Jsonb}` parameter
  binding (the session's reflection-based parameter binder cannot
  preserve the `NpgsqlDbType` precision/discriminator on every shape —
  the database accepts the call but loses the strict type matching that
  exists today).
- Multi-column `DbDataReader.GetXxx(int)` row materialisation (the
  session API exposes scalar streaming only; row materialisation would
  require either a row-mapper facility — out of scope — or leaking
  `DbDataReader` back into the contract surface, which defeats the
  purpose).
- Prepared-statement caching via `PreparedStatementCache.GetOrCreatePreparedCommandAsync`,
  which is keyed off `NpgsqlConnection` and `NpgsqlCommand` directly.
- Streaming binary `COPY` operations (`PostgresBulkLoader`, raster
  COG ingestion) which use `NpgsqlBinaryImporter` — fundamentally
  outside the `string sql` model.

Rewriting these to a Dapper-style session would either (a) discard the
NpgsqlDbType discipline and the prepared-statement cache (silent
correctness/perf regression), or (b) bolt a row-mapper / streaming
escape hatch onto `IDatabaseSession` that re-leaks the very ADO.NET
types this ADR was supposed to remove.

The session abstraction therefore lives alongside the legacy provider.
Greenfield code (and any new data-access path simple enough to fit
`ExecuteAsync` + parameter object) should prefer
`IDatabaseSessionFactory`. Existing entangled call sites are deferred
to future work that introduces either:

- A row-mapping facility on `IDatabaseSession` (Dapper-style materialiser), AND
- A typed-parameter facade so providers can preserve their dialect's
  type discriminators without exposing them, AND
- A streaming-bulk-load facility orthogonal to row-by-row session calls.

That broader effort is the natural follow-on to the C4 ISqlDialect
abstraction (also in the May 2026 audit) and is sequenced after the
metadata-v2 cutover lands.

### What was migrated

The Tranche A target (Postgres Alerts, 9 files) was inspected; every
file uses multi-column `DbDataReader` row materialisation in at least
one method, so an honest end-to-end migration of any single file
requires the row-mapper facility above. To avoid checking in
half-migrated files where one method uses the session and another uses
the legacy provider, this ADR explicitly defers all call-site
migration to follow-on work. The session abstraction ships ready for
consumption.

### What was kept

- `IDatabaseConnectionProvider.OpenConnectionAsync(CancellationToken)`
- `IDatabaseConnectionProvider.OpenTransactionAsync(IsolationLevel, CancellationToken)`
- `IDatabaseConnectionProvider.ExecuteWithDeadlockRetryAsync(...)`
- `IDatabaseConnectionProvider.GetConnectionString()`
- The `DatabaseConnectionProviderExtensions.OpenNpgsqlConnectionAsync`
  shim that returns an `NpgsqlConnectionLease` (Postgres-internal only,
  not part of the `Honua.Core.Abstractions` surface).

### Arch test

A new architecture-test member,
`CoreAbstractionsIsolationTests.DatabaseConnectionProvider_ShouldNotLeak_AdoNetTypes`,
encodes the eventual goal: the public signature of
`IDatabaseConnectionProvider` must not mention `System.Data.Common.DbConnection`
or `System.Data.Common.DbTransaction`. The test is currently marked
`Skip = "Audit C3 deferred — see ADR-0046"`, so it documents the
target without blocking CI. Removing the `Skip` will be the final step
of the follow-on work that deletes the leaky members.

## Consequences

- `Honua.Core.Abstractions` gains a clean, ADO.NET-free session
  abstraction usable by future protocol modules and any new feature
  data-access code.
- The `DbConnection`/`DbTransaction` leak remains in
  `IDatabaseConnectionProvider` until the row-mapping and
  typed-parameter follow-ups land. The arch test documents the goal
  state with a skipped assertion.
- No call-site behaviour changes; risk of regression for this ADR is
  limited to the new files (session implementation), which are not yet
  consumed by any caller.

## Follow-ons

- Row-mapping / multi-column materialisation on `IDatabaseSession`.
- Typed-parameter facade so providers preserve dialect-specific
  parameter type metadata without exposing it.
- Bulk-load facility for `COPY`-style streaming ingestion.
- After all of the above, delete the leaky
  `OpenConnectionAsync` / `OpenTransactionAsync` members and remove
  the `Skip` on `DatabaseConnectionProvider_ShouldNotLeak_AdoNetTypes`.
