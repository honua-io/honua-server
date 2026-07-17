# ADR-0066: Plugin Data-Store and Output-Format Extension Points

## Status
Accepted

## Context

The plugin SDK (`src/Honua.Plugins.Abstractions/`, `src/Honua.Plugins/`) established in
issue #347 (phase 1) and #1562 (phase 2) has compile-time, DI-aggregated extension points
for edit hooks, computed fields, feature/field validators, custom endpoints, and background
services. It deliberately does **no runtime assembly loading**: plugins are referenced at
build time, discovered through explicit `builder.Add<T>()` calls, and registered in DI at
startup. That constraint exists because `Honua.Server` publishes as Native AOT (ADR-0018),
which forbids dynamic assembly loading, reflection-heavy discovery, and hot-deploy.

The SDK has **no data-store extension point and no output-format extension point** (issue
#2856). Every data source and every output format we support lives in core, maintained by us,
with no exit. The pressure is real and already visible: the read/query-only cloud backends
(Snowflake, Databricks, Redshift, Oracle) are gated behind compile-time build profiles
(`eng/Honua.BuildProfiles.props`) precisely because we reached for a compile-time lever when
the runtime one did not exist. GeoServer's fifteen-year modernization was made tractable in
part because its `datastore` and output-format SPIs let modules be *evicted* from core to
extensions rather than deleted — a move only possible because the seams existed.

The open question this ADR must resolve (per the issue's follow-on split guidance): **do
these extension points belong in the plugin SDK at all, given our compile-time/AOT-static
posture?** A runtime data-store SPI is in genuine tension with static registration, and "no,
and here is why" was an acceptable outcome. This ADR decides the tension **before** either
point is split into its own implementation issue.

## Decision

**Qualified yes.** Both a read-only data-store point and a feature output-format point belong
in the SDK, realized the same way every other extension point already is: **compile-time,
DI-aggregated, statically-rooted contributions** discovered through `builder.Add<T>()`. The
runtime-SPI framing was the wrong framing; the read-path precedent (`IComputedFieldProvider`,
also a "contribute behavior the core doesn't own" seam) shows the static model already carries
this class of extension without violating AOT. Neither point introduces assembly scanning,
dynamic loading, or reflection in a hot path.

### Data-store extension point — reuse the existing provider seam

A plugin contributes a read-only vector source by implementing the **existing** Core provider
contract from ADR-0035:
`Honua.Core.Features.FeatureStore.Abstractions.IFeatureDataProvider` (with its
`IFeatureReader`, capability declaration, and null `Writer` for read-only). We add **no new
data-store interface**. The plugin SDK's only new surface is:

- `PluginCapability.DataStore`, the declarative capability a data-store plugin must request; and
- builder wiring that registers a `[Plugin]` type implementing `IFeatureDataProvider` as an
  **additional** `IFeatureDataProvider` in DI.

The server's existing composition (`InfrastructureCompositionRoot`) already builds
`IFeatureDataProviderRegistry` from `serviceProvider.GetServices<IFeatureDataProvider>()`,
resolved lazily and scoped, so it enumerates every registration — including plugin-contributed
ones — with **no router changes and independent of registration order**. Layers bind to the
plugin provider by name through a secure connection, exactly as first-party secondary providers
do; `DataProviderNames.Normalize` already passes unknown provider names through, so a novel
provider name works without a core edit. A third party therefore adds a read-only vector source
out-of-tree by shipping an assembly that references `Honua.Core` + `Honua.Plugins.Abstractions`,
implements `IFeatureDataProvider`, and is registered via
`AddHonuaPlugins(cfg, p => p.Add<TSource>())` — no patching of core.

Because the provider contract lives in `Honua.Core` (not the minimal
`Honua.Plugins.Abstractions`), a data-store plugin references `Honua.Core`. That is heavier
than a validator plugin, and it is inherent: contributing a data source *is* a Core-level
concern. We accept and document this asymmetry rather than duplicate the mature provider
contract into the SDK package.

### Output-format extension point — a new, ASP.NET-free serializer seam

There is no unified output-format registry in the codebase today; format selection is
per-protocol, hard-coded `switch` statements. We add a new, deliberately ASP.NET-free contract
to `Honua.Plugins.Abstractions`:

- `IFeatureOutputFormat` — `FormatId` / `MediaType` / `FileExtension` +
  `WriteAsync(IAsyncEnumerable<Feature>, FeatureOutputFormatContext, Stream, CancellationToken)`;
- `PluginCapability.OutputFormats`, the capability a format plugin must request;
- `IFeatureOutputFormatRegistry` (+ `NoOpFeatureOutputFormatRegistry`), the host-facing
  aggregate that keys formats by wire token and gates resolution behind the Enterprise
  `plugin.sdk` entitlement and the operator kill-switch.

The registry is wired into the admin bulk-export path (`Honua.Io/ExportEndpoints`), the
cleanest single dispatch seam in the codebase: it already streams `IAsyncEnumerable<Feature>`
to a writer over the response body. A requested format that is not built-in resolves to a
licensed plugin format. Query-protocol content negotiation (GeoServices `f=`, OGC API `f=`),
which lives behind protocol-local `switch` statements and advertised-format sets, is a
documented follow-up, not this ADR's scope.

### Proof it is load-bearing

The built-in CSV export writer is **ported to flow through `IFeatureOutputFormat`**
(`CsvOutputFormat`), so real, in-production CSV export exercises the same contract a plugin
format implements — the seam is load-bearing, not decorative. Byte production still delegates
to the unchanged `CsvExportWriter`, so behavior is identical.

### Interaction with build profiles

The two levers are **orthogonal and complementary**:

- **Build profiles** (`eng/Honua.BuildProfiles.props` → `DefineConstants` → `#if` carve-outs)
  are the **first-party** lever: they decide which backends *we* ship in *our* image, and are
  the correct tool for drivers that are AOT-incompatible (Oracle/Snowflake use reflection and
  are excluded from AOT-verification publishes). A build profile is a ship-or-not switch over
  code we own.
- **The plugin data-store seam** is the **out-of-tree** lever: it lets a *third party* add a
  read-only source without a core `DefineConstants` gate and without us owning the code. A
  plugin data-store is the alternative to a build-profile gate *for sources we do not want to
  own*; for first-party backends the build profile remains the right lever.

They compose: a single build may include first-party providers (profile-gated) and third-party
plugin providers (SDK-registered) at once. A plugin data-store is **not** a replacement for the
build-profile mechanism, and porting the read-only cloud backends out of core is explicitly not
a consequence this ADR mandates (it is a possible future move the seam now makes *possible*).

## Consequences

### Positive

- Data-store reuses the mature ADR-0035 provider contract with zero router/registry changes;
  the SDK only learns to *register* a provider-implementing plugin.
- Output formats gain their first shared, pluggable seam, and the export path now proves it.
- Both points stay compile-time and AOT-safe; no assembly scanning or dynamic loading is
  introduced, honoring the non-goals in #2856.
- A clean split follows: the data-store point and output-format point can now be extended
  independently (e.g. wiring plugin formats into query-protocol negotiation) on top of this
  decision.

### Negative / trade-offs

- Data-store plugins reference `Honua.Core`, a heavier dependency than the minimal
  `Honua.Plugins.Abstractions` a validator needs. Inherent to contributing a data source.
- Plugin extension instances are singletons (consistent with every other extension point), so a
  data-store or output-format plugin **must be thread-safe**.
- Output formats are wired into the admin export path only; query-protocol `f=` negotiation and
  the durable async export-job pipeline remain built-in-only for now (documented follow-ups).
- `Honua.Io` now takes a ProjectReference on `Honua.Plugins.Abstractions` (the lean contract
  package). The module-dependency policy and Io-isolation architecture guards are updated to
  permit exactly this edge, mirroring the existing allowance for protocol assemblies consuming
  `IPluginEditPipeline`.

### Cross-repo / SDK impact

The new public surface (`IFeatureOutputFormat`, `IFeatureOutputFormatRegistry`,
`FeatureOutputFormatContext`, `FeatureOutputField`, `PluginOutputFormatDescriptor`, and the two
new `PluginCapability` flags) ships in the published `Honua.Plugins.Abstractions` package that
third-party plugin authors consume. `honua-sdk-dotnet` should surface these contracts when it
next documents the plugin SDK; there is no breaking change to existing exported contracts (the
additions are purely additive — new enum members and new types).

## Alternatives considered

- **"No" — these do not belong in the SDK.** Rejected: the compile-time model already carries
  read-path "contribute behavior" seams (`IComputedFieldProvider`), so a static data-store and
  output-format seam is feasible without an AOT-violating runtime SPI. The tension dissolves
  once the requirement is read as *static contribution* rather than *runtime discovery*.
- **A parallel runtime data-store SPI with assembly scanning** (GeoServer-style dynamic SPI).
  Rejected outright: breaks Native AOT and is an explicit non-goal (#2856).
- **A brand-new data-store interface in the SDK package.** Rejected: it would duplicate the
  mature `IFeatureDataProvider`/ADR-0035 contract and fork the provider ecosystem. Reusing the
  existing seam is why the data-store point needs *no* new interface and *no* router change.
