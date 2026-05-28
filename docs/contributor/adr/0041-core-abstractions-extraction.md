# ADR-0041: Honua.Core.Abstractions Extraction

## Status

Accepted

## Context

`Honua.Core` has grown to ~889 source files and depends on heavy geospatial and
data-layer libraries (`NetTopologySuite`, `Parquet.Net`, `FlatGeobuf`,
`AWSSDK.LocationService`) through its `.csproj` package references. The
modularization plan (memory: `modularization-plan`) calls out two consequences:

1. **No clean seam for protocol assemblies.** Protocol surfaces (Phase 1 of the
   modularization plan: `Honua.Protocols.OData`, `Honua.Protocols.OgcApi`,
   `Honua.Protocols.OgcClassic`, `Honua.Protocols.GeoServices`) need a
   contract-only project to reference; today they would transitively pull
   `Honua.Core`'s heavy package graph regardless of what they actually use.
2. **No place for abstractions that explicitly cannot leak ADO.NET / NTS.**
   The structural audit (memory: `structural-audit-2026-05`, C3) flagged
   `IFeatureChangeOutboxRepository`, `IDatabaseConnectionProvider`, and
   `ITableDiscoveryService` for leaking ADO.NET types from Core. The outbox
   contract was de-leaked in PR #1232 (commit `565dd50`) but it still lives in
   `Honua.Core` alongside the heavy libs.

The natural fix is to create `Honua.Core.Abstractions` — a contract-only
assembly that `Honua.Core` ProjectReferences and that protocol assemblies can
reference without inheriting the heavy package graph. This ADR records that
decision and the incremental sequencing used to land it safely.

## Decision

Introduce `src/Honua.Core.Abstractions/Honua.Core.Abstractions.csproj` with a
deliberately small package surface:

- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.Options`

The assembly's invariant — **no transitive dependency on
`Npgsql` / `NetTopologySuite` / `AWSSDK*` / `Parquet*` / `FlatGeobuf`** — is
enforced in CI by `Honua.Architecture.Tests.CoreAbstractionsIsolationTests`,
which combines a NetArchTest assembly-level dependency check with a
`.csproj`-level package-reference check. The same suite asserts the dependency
direction (`Honua.Core.Abstractions` MUST NOT reference `Honua.Core`).

`Honua.Core` ProjectReferences `Honua.Core.Abstractions`. The dependency
direction is one-way: contracts live in Abstractions, implementations live in
Core.

This ADR is the second link in the
[ADR-0037](0037-unified-ci-test-tier-strategy.md) lineage; future
modularization-plan ADRs (Phase 1 protocol extraction, Phase 2 per-protocol
test projects, Phase 3 CI rework) continue the chain.

### Scope landed in this PR

This PR scaffolds the assembly and moves the cleanly-isolated outbox
contract surface:

- `IFeatureChangeOutboxRepository` (already de-leaked in PR #1232)
- `IOutboxHealth`
- `IOutboxCapabilityProvider`
- `FeatureChangeOutboxEntry`
- `OutboxBacklogMetrics`
- `OutboxStatuses`

All six files retain their `Honua.Core.Features.Infrastructure.Events.Outbox`
namespace so no caller needs a `using` update. `FeatureMutationOutboxScope`
stays in `Honua.Core` because it transitively depends on
`Honua.Core.Features.FeatureStore.Domain.Feature`, which has not yet been
sequenced for the move (see follow-ups below).

### Scope deferred to follow-up PRs

The full Phase 0 spec also calls for moving:

1. **Metadata v2 graph types + `IMetadataV2GraphProvider` / `IMetadataV2GraphStore`.**
   Survey confirmed these are clean of heavy package refs at the file level,
   but they pull a transitive closure through `Honua.Core.Features.Console.Domain`,
   `Honua.Core.Features.Scene.Domain`, `Honua.Core.Features.Security.Domain`,
   and `Honua.Core.Features.FeatureStore.Domain` — roughly 110 files. None of
   those targets carry heavy refs (a survey ruled this out), so the cluster
   is moveable as a single subsequent PR.
2. **Filter abstractions** (`FilterExpression`, `SqlFragment`,
   `IFilterExpressionService`, `IFilterExpressionTranslator`,
   `ISqlFilterTranslator`, `FilterTranslationContext`). These reference
   `MetadataV2Resource`, so the metadata-v2 move sequences ahead of them.
   Several interface/impl pairs live in a single file (e.g.
   `FilterExpressionService.cs` holds both `IFilterExpressionService` and
   `FilterExpressionService`) and need to be split during the move so the
   impl can stay in Core (it depends on `FilterParserGuard`, which itself
   depends on `NetTopologySuite`).
3. **C3 IDatabaseConnectionProvider de-leak** — the structural audit's
   `IDatabaseSession` (or equivalent) refactor. The interface surface has
   ~125 call sites across `Honua.Postgres`, `Honua.MySql`, and `Honua.Server`,
   and the refactor needs to retain compatibility for secure-mode callers
   that pass the underlying `DbConnection` directly. This is its own multi-day
   refactor PR; landing it after the metadata-v2 / filter moves keeps the
   per-PR review scope bounded.
4. **`ITableDiscoveryService` stays in Core indefinitely.** Its
   `DbConnection`-typed overload is load-bearing for secure-mode (caller
   passes the already-opened secure connection through), and there is no
   abstraction that would not regress that path.

Sequencing rule: each follow-up PR rebases on the previous merge and uses the
commit prefix `refactor(core-abstractions): ...` for traceability.

## Consequences

### Positive

- Phase 1 of the modularization plan is unblocked. Protocol assemblies can
  start referencing `Honua.Core.Abstractions` as content lands in subsequent
  PRs without taking a heavy-package transitive dep.
- The no-heavy-deps invariant is now mechanically enforced; future drift is
  caught by the architecture tests at PR time, not by a downstream consumer
  reporting an unexpected NuGet bloat.
- The dependency direction (Core depends on Abstractions, never the reverse)
  is enforced at the project-reference level.

### Negative

- A second `.csproj` adds a small build-graph step. Build wall-clock impact is
  negligible (~1s incremental on a clean restore) and is absorbed by the
  Phase 3 CI work that's already in the modularization plan.
- The follow-up sequencing means Phase 0 ships across multiple PRs instead of
  one. This is a deliberate trade: the metadata-v2 closure is a ~110-file
  move with cascading interface/impl splits, and the IDatabaseConnectionProvider
  refactor is a separate ~125-site touch. Bundling these into one PR would
  produce a review unit that is impractical to land safely.

### Neutral

- File namespaces are preserved across the move (`Honua.Core.*`). This keeps
  every existing `using` working unchanged and lets the move land as a
  history-preserving `git mv` rather than a rename.
- The Abstractions assembly's `RootNamespace` is also `Honua.Core`, so new
  files added there stay consistent with the rest of the tree.

## Implementation Notes

- `Honua.Core.Abstractions.csproj` uses `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
  matching `Honua.Core`.
- `InternalsVisibleTo` is granted to the same set of test and provider
  assemblies that `Honua.Core` grants, so internal contracts can move freely
  between Core and Abstractions in follow-up PRs without breaking tests.
- `CoreAbstractionsIsolationTests.Abstractions_ShouldNotDependOn_HeavyPackages`
  uses `NetArchTest` against the loaded assembly. The companion
  `AbstractionsCsproj_ShouldNotReference_HeavyPackages` parses the `.csproj`
  XML directly. Both run in the Architecture tier so they execute on every PR.
- The verification commands documented in the modularization plan
  (`dotnet build -c Debug -nologo` and the filtered Architecture test run)
  remain the canonical local gate; see ADR-0037 for the CI tier model.
