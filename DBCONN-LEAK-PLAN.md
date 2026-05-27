# Honua.Core ADO.NET Leak De-Leak Plan (Modularization Phase-0)

Goal: stop `Honua.Core` public abstractions from leaking `System.Data.Common`
(`DbConnection`/`DbTransaction`) into interface signatures, which would block a
clean `Honua.Core.Abstractions` extraction.

Baseline build: `dotnet build src/Honua.Core/Honua.Core.csproj -c Debug` →
**succeeds, 0 warnings, 0 errors** on this branch (the trunk metadata-cutover
break noted elsewhere does not affect this worktree).

## Confirmed leak surface (exhaustive)

A repo-wide scan for `System.Data.Common` / `DbConnection` / `DbTransaction` in
`src/Honua.Core/**` public interfaces found exactly three leaking abstractions.
(The `Features/Infrastructure/Monitoring/*` matches are false positives: they
only contain the identifier `IActiveDbConnectionTracker`, no ADO.NET types.)

| Interface | File | Leaking member(s) |
|---|---|---|
| `IDatabaseConnectionProvider` | `src/Honua.Core/Features/Infrastructure/Abstractions/IDatabaseConnectionProvider.cs` | `:25` `Task<DbConnection> OpenConnectionAsync(...)`; `:33` `Task<(DbConnection, DbTransaction)> OpenTransactionAsync(...)` |
| `IFeatureChangeOutboxRepository` | `src/Honua.Core/Features/Infrastructure/Events/Outbox/IFeatureChangeOutboxRepository.cs` | `:20-24` `WriteOutboxRowAsync(DbConnection, DbTransaction, ...)` |
| `ITableDiscoveryService` | `src/Honua.Core/Features/Admin/Abstractions/ITableDiscoveryService.cs` | `:30-32` `DiscoverPostGisTablesAsync(DbConnection, ...)` (second overload) |

## Blast radius (distinct files referencing each interface)

| Interface | src files | test files | total | files calling the *leaking* member |
|---|---|---|---|---|
| `IDatabaseConnectionProvider` | 70 | 30 | **100** | 68 call `OpenConnectionAsync` / `OpenTransactionAsync` |
| `IFeatureChangeOutboxRepository` | 6 | 3 | **9** | 3 (1 caller chain in Postgres + impl + 1 test fake) |
| `ITableDiscoveryService` | 5 | 2 | **7** | 3 production + 1 test for the `DbConnection` overload |

### `IDatabaseConnectionProvider` references (src, 70 files — abbreviated)
Pervasive across `Honua.Postgres` (FeatureStore, Raster, Alerts, Import,
Catalog, Metadata, Security, Admin, Geometry, AnomalyDetection, Styling,
Attachments, Mobile, Infrastructure), `Honua.DuckDB`, `Honua.MySql`,
`Honua.Server` (Program.cs, CloudDemo, Admin LayerValidation, Rendering),
plus the Core interface + `ServiceValidationHelpers`. 30 test files reference it
including `TestKit` fixtures.

### `IFeatureChangeOutboxRepository` references (all 9)
- `src/Honua.Core/Features/Infrastructure/Events/Outbox/IFeatureChangeOutboxRepository.cs` (decl)
- `src/Honua.Core/Features/Infrastructure/Events/Outbox/IOutboxCapabilityProvider.cs` (doc xref only)
- `src/Honua.Postgres/Features/Infrastructure/Events/Outbox/PostgresFeatureChangeOutboxRepository.cs` (impl)
- `src/Honua.Postgres/Features/FeatureStore/Services/FeatureDataAccess.Core.cs` (field/dep type)
- `src/Honua.Postgres/Features/FeatureStore/Services/FeatureDataAccess.Edits.cs` (caller of `WriteOutboxRowAsync`)
- `src/Honua.Postgres/Features/FeatureStore/ServiceCollectionExtensions.cs` (DI)
- `src/Honua.Server/Features/Infrastructure/Events/Outbox/OutboxDispatcherBackgroundService.cs` (dispatcher — uses **only** the DbConnection-free methods)
- `tests/.../Outbox/OutboxDispatcherBackgroundServiceTests.cs` (test fake implements `WriteOutboxRowAsync`)
- `tests/.../FeatureStore/FeatureDataAccessOutboxScopeWiringTests.cs` (wiring test)

### `ITableDiscoveryService` references (all 7)
- `src/Honua.Core/Features/Admin/Abstractions/ITableDiscoveryService.cs` (decl)
- `src/Honua.Postgres/Features/Admin/PostgreSqlTableDiscoveryService.cs` (impl — both overloads)
- `src/Honua.Postgres/Features/Admin/PostgreSqlLayerPublishingService.cs` (uses string overload)
- `src/Honua.Postgres/ServiceCollectionExtensions.cs` (DI)
- `src/Honua.Server/Features/Admin/AdminEndpoints.cs` (uses string overload)
- `src/Honua.Server/Features/Admin/Services/LayerValidationService.cs` (uses **DbConnection** overload)
- `tests/.../Admin/PostgreSqlTableDiscoveryServiceTests.cs` (regression test for the DbConnection overload)

## Per-interface decision

### 1. `IDatabaseConnectionProvider` — DEFER (do not touch)
- 100 files; 68 call the leaking members. The returned `DbConnection` is the
  unit of work threaded through nearly every Postgres read/write/raster/import
  path. De-leaking requires a real connection-handle abstraction
  (`IConnectionLease` / `ITransactionScope` exposing a provider-neutral handle)
  plus migrating ~68 call sites. That is a dedicated, separately-reviewed effort,
  not a Phase-0 mechanical change. **Documented; left for a follow-up.**

### 2. `IFeatureChangeOutboxRepository` — IMPLEMENT (bounded, behavior-preserving)
- `WriteOutboxRowAsync(DbConnection, DbTransaction, ...)` is a **provider-internal
  atomic-write contract**: the outbox row must commit in the *same* transaction
  as the feature mutation. Its only caller is `FeatureDataAccess` (Honua.Postgres);
  its only implementer is `PostgresFeatureChangeOutboxRepository` (Honua.Postgres).
  The `OutboxDispatcherBackgroundService` in `Honua.Server` uses **only** the
  DbConnection-free methods (`ClaimPendingAsync`, `MarkDispatchedAsync`,
  `MarkFailedAsync`, `RecoverExpiredClaimsAsync`, `GetBacklogMetricsAsync`).
- **Change**: split the write path off the Core public interface into a new
  provider-internal interface `IFeatureChangeOutboxWriter` in `Honua.Postgres`.
  `PostgresFeatureChangeOutboxRepository` implements both. `FeatureDataAccess`
  depends on the writer; DI wires it from the same scoped instance. The Core
  interface keeps only the DbConnection-free dispatcher contract and drops
  `using System.Data.Common`.
- **Files touched (~7)**: Core interface, new Postgres writer interface,
  Postgres impl (no body change — just `: IFeatureChangeOutboxWriter`),
  `FeatureDataAccess.Core.cs` (dep/field type), DI extensions, dispatcher test
  fake, (Edits.cs call site unchanged — same method name on writer).
- **Risk**: low. Pure relocation of a member already only used provider-side; no
  behavior change, no SQL change, no public-contract change for the dispatcher.

### 3. `ITableDiscoveryService` — DEFER (not cleanly bounded; load-bearing)
- Naive fix (drop the `DbConnection` overload, make `LayerValidationService` use
  the connection-string overload via `GetConnectionString()`) is a **behavioral
  regression**, not a mechanical refactor:
  - The `DbConnection` overload exists so `LayerValidationService` reuses a
    connection opened through `IDatabaseConnectionProvider.OpenConnectionAsync`,
    which under `SecureConnectionAwareDatabaseProvider` applies (a) the
    `QueryConcurrencyGate` admission control, (b) **secure named-connection
    resolution**, and (c) schema search-path setup.
  - `GetConnectionString()` returns the **default** connection string, not the
    secure-resolved one, and a raw `NpgsqlConnection` bypasses the gate and
    search-path. For secure-mode deployments this would discover tables against
    the wrong database.
  - The overload's regression test
    (`PostgreSqlTableDiscoveryServiceTests`) explicitly asserts the overload
    unwraps the provider's `SemaphoreReleasingConnection` wrapper.
- Properly de-leaking this needs the same connection-handle abstraction as
  `IDatabaseConnectionProvider` (so the gated/secure connection can be passed
  without exposing `DbConnection`). It is therefore **coupled to effort #1 and
  deferred with it.**

## Outcome
Implement the outbox split only. Defer `IDatabaseConnectionProvider` and
`ITableDiscoveryService` to a dedicated connection-handle-abstraction effort,
since both are either pervasive or behaviorally load-bearing on the leaked type.
