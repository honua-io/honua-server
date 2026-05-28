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

This PR scaffolds the assembly and moves the full Phase 0 contract surface:

- **Outbox cluster** — `IFeatureChangeOutboxRepository` (already de-leaked in
  PR #1232), `IOutboxHealth`, `IOutboxCapabilityProvider`,
  `FeatureChangeOutboxEntry`, `OutboxBacklogMetrics`, `OutboxStatuses`.
  `FeatureMutationOutboxScope` stays in Core because it transitively depends
  on `FeatureStore.Domain.Feature` and a feature-mutation API surface that
  itself remains in Core.
- **Metadata v2 graph types** — every file in `Features/Metadata/Domain/V2/`
  plus `IMetadataV2GraphProvider` and `IMetadataV2GraphStore`.
- **Console / Scene / Security domain records** — the transitive closure
  pulled in by Metadata v2 (`Features/Console/Domain/`,
  `Features/Scene/Domain/`, `Features/Security/Domain/`).
- **FeatureStore.Domain records** — the entire `Features/FeatureStore/Domain/`
  directory; verified clean of heavy package refs (the lone identifier match
  for `FlatGeobuf` was an enum/property name, not a `using`).
- **Shared/Models support types** — `SpatialReference`, `BoundingBox`,
  `FieldNames`, `CrsDefinition` (which exports `AxisOrder`).
- **Catalog.Domain.GeometryType** — the enum used by filter translation.
- **Filter abstractions** — `FilterExpression`, `SqlFragment`,
  `FilterOperators` (`BinaryOperator`, `UnaryOperator`, `LiteralType`,
  `SpatialOperator`, `TemporalOperator`, `ArrayOperator`),
  `FilterTranslationContext`, `ISqlFilterTranslator`,
  `IFilterExpressionTranslator`, `IFilterExpressionService` (with its result
  records `FilterParseResult` / `FilterTranslationResult` and the
  `FilterLanguage` enum). The impls `FilterExpressionService` and
  `FilterExpressionTranslator` stay in Core because they depend on
  parser implementations (Cql2 / GeoServicesSql / OData) and on
  `FilterExpressionNormalizer` — the impl-file/interface-file split is
  done in this PR so the abstractions can ship cleanly.
- **`IDatabaseConnectionProvider`** — moved to Abstractions as-is. Its
  `DbConnection` / `DbTransaction` surface uses `System.Data.Common`, a
  BCL assembly which the no-heavy-deps invariant does not ban. The
  audit-C3 `IDatabaseSession` refactor that fully drops `DbConnection`
  from the public surface is left as a follow-up because it touches
  ~125 call sites and is orthogonal to the assembly extraction. See
  the deferred items below.

All moves preserve their original `Honua.Core.*` namespaces (executed as
`git mv`) so no consumer code needs a `using` update — only the assembly
that contains the type changes.

`CA1716` (namespace identifier matches reserved keyword "Shared") is added
to `<NoWarn>` on `Honua.Core` and `Honua.Core.Abstractions` because the
`Honua.Core.Features.Shared.Models` namespace now spans two assemblies and
the analyzer fires on the namespace declaration in both. Renaming the
namespace would touch every consumer; suppressing the rule is the
proportionate fix.

### Scope deferred to follow-up PRs

1. **C3 IDatabaseConnectionProvider `IDatabaseSession` de-leak.** The
   interface is now in Abstractions but still surfaces
   `DbConnection` / `DbTransaction`. Introducing an
   `IDatabaseSession` abstraction that replaces the leaky surface (per
   the structural audit, memory: `structural-audit-2026-05` C3) requires
   migrating ~125 call sites across `Honua.Postgres`, `Honua.MySql`, and
   `Honua.Server`, and retaining compatibility for secure-mode callers
   that pass an already-opened `DbConnection` through to provider code.
   This refactor is orthogonal to the assembly extraction and is tracked
   as its own follow-up.
2. **`ITableDiscoveryService` stays in Core indefinitely.** Its
   `DbConnection`-typed overload is load-bearing for secure-mode (caller
   passes the already-opened secure connection through), and there is no
   abstraction that would not regress that path. Documenting the
   exception so the architecture test can be tightened later without
   churning this interface.
3. **`FeatureMutationOutboxScope` stays in Core.** It depends on
   `FeatureStore.Domain.Feature` (which moved) but also on the
   feature-mutation API surface (`IFeatureWriter` etc.) which remains
   in Core. Moving the scope class requires either pulling more API
   surface into Abstractions or splitting the scope's dependencies —
   neither is required by Phase 1 protocol extractions.

Sequencing rule: each follow-up PR rebases on the previous merge and uses
the commit prefix `refactor(core-abstractions): ...` for traceability.

## Phase 1 plug-in host contract

The plug-in host contract that the modularization plan attributes to Phase 1
landed in this same change set so a subsequent assembly-extraction PR is a
mechanical move rather than an API redesign:

- `Honua.Server.Features.Infrastructure.Hosting.IHonuaProtocolModule` — the
  per-protocol interface with `Name`, `ConfigureServices`, and `MapEndpoints`.
  Lives in `Honua.Server` (not `Honua.Core.Abstractions`) because it depends
  on ASP.NET Core's `IEndpointRouteBuilder`. Adding the AspNetCore framework
  reference to the contract-only assembly was rejected as overreach; the
  trade-off is that extracted protocol assemblies will reference Server's
  hosting surface alongside `Honua.Core.Abstractions`.
- Four implementations under
  `Honua.Server.Features.Infrastructure.Hosting.Modules` — `ODataProtocolModule`,
  `OgcApiProtocolModule`, `OgcClassicProtocolModule`, `GeoServicesProtocolModule`.
  Each wraps the existing `AddXxx` / `MapXxxEndpoints` static helpers.
- `FeatureRegistrationExtensions.AddDiscoveredProtocolModules` /
  `MapDiscoveredProtocolModules` — composition entry points that iterate the
  registry, optionally filtered by an `enabledNames` list (modeled on the
  data-provider pattern's `Protocols:Enabled` shape). The module registry is
  an explicit `IReadOnlyList` rather than reflection over the assembly so
  `PublishAot=true` does not flag IL2070 / IL2072 trim warnings.

The new entry points are **additive**. `AddServerFeatures` and
`MapServerFeatureEndpoints` still own the canonical direct registration calls.
A follow-up PR will migrate the per-protocol registrations into modules and
remove the duplicated direct calls so the module path is the single source of
truth. Today calling both paths would double-register the four wrapped
protocols.

`Honua.Architecture.Tests.ProtocolModuleCoverageTests` enforces:

1. Every Phase 1 protocol (`OData`, `OgcApi`, `OgcClassic`, `GeoServices`)
   has an `IHonuaProtocolModule` implementation in the loaded
   `Honua.Server` assembly.
2. Every module is `sealed` (one impl per protocol).
3. Every module has a parameterless constructor (host activates with
   `new()` from the registry).

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
