# Source-Generator Inventory

_Audit date: 2026-05-28 (.NET SDK 10.0.103, branch `refactor/core-abstractions-phase0b-metadata-v2`)._

This repo ships **no custom Roslyn source generators**. All compile-time code
generation is performed by SDK-shipped generators, every one of which
implements `IIncrementalGenerator` (input-hash cached, re-runs only when the
relevant subset of the syntax tree / additional files changes).

## Generators in use

Emitted-files inspection on `src/Honua.Core` (built with
`/p:EmitCompilerGeneratedFiles=true`) shows four generators contributing
output:

| Generator                                                                                | Interface              | Trigger                                                                                       |
|------------------------------------------------------------------------------------------|------------------------|-----------------------------------------------------------------------------------------------|
| `System.Text.Json.SourceGeneration.JsonSourceGenerator`                                  | `IIncrementalGenerator`| `[JsonSerializable]` partials (157 contexts repo-wide)                                        |
| `Microsoft.Extensions.Logging.Generators.LoggerMessageGenerator`                         | `IIncrementalGenerator`| `[LoggerMessage]` partials                                                                    |
| `Microsoft.Extensions.Configuration.Binder.SourceGeneration.ConfigurationBindingGenerator`| `IIncrementalGenerator`| `ConfigurationBinder.Bind` / `Configure<T>` calls, opted in via `<EnableConfigurationBindingGenerator>` in `Directory.Build.props` |
| `System.Text.RegularExpressions.Generator.RegexGenerator`                                | `IIncrementalGenerator`| `[GeneratedRegex]` partials                                                                   |

In addition, `src/Honua.Server` (`Sdk="Microsoft.NET.Sdk.Web"`, `PublishAot=true`)
implicitly enables the ASP.NET Core
`Microsoft.AspNetCore.Http.RequestDelegateGenerator` for Minimal-API endpoint
wiring; it has been incremental since its introduction in .NET 7.

All five are part of the runtime / SDK and are known-good
`IIncrementalGenerator` implementations &mdash; there is nothing in this
repository to port.

## Cache behaviour (measured)

`src/Honua.Core/Honua.Core.csproj`, Debug, `--no-restore`:

| Scenario                                                                  | Wall time |
|---------------------------------------------------------------------------|-----------|
| Cold (`--no-incremental`, `EmitCompilerGeneratedFiles=true`)              | ~65 s     |
| Full first build of `Honua.Core` after fresh restore                      | ~55 s     |
| Second build, no source changes                                           | ~2.7 s    |

The ~52 s delta on the no-change rebuild is the headline evidence that the
incremental cache is doing its job: generators are not re-executed when their
inputs have not changed.

## `[JsonSerializable]` context-size audit

161 files mention `JsonSerializerContext`; 157 declare one (the rest are
re-exports). Sizes (lines containing `[JsonSerializable]`):

| Lines | File                                                                                              |
|-------|---------------------------------------------------------------------------------------------------|
| 157   | `src/Honua.Server/Features/Import/ImportJsonContext.cs`                                           |
| 148   | `src/Honua.Server/Features/Protocols/GeoServices/FeatureServer/Models/FeatureServerJsonContext.cs`|
|  79   | `src/Honua.Server/Features/Protocols/Mcp/Models/McpJsonContext.cs`                                |
|  70   | `src/Honua.Server/Features/Infrastructure/Monitoring/MetricsJsonContext.cs`                       |
|  66   | `src/Honua.Server/Features/Import/GeoServerImportApiJsonContext.cs`                               |

The remaining 152 contexts each declare <= 50 serializable types &mdash; the
threshold called out in the audit brief.

The `JsonSourceGenerator` _is_ incremental on its inputs, but its cache key
includes every `[JsonSerializable(typeof(T))]` reachable from the partial
class plus the full transitive graph of `T`'s reachable members. The five
contexts above therefore have an outsized "blast radius": any edit to a type
in their reachable graph invalidates regeneration of the whole context.

That said:

- These five contexts are domain-cohesive (`ImportJsonContext` covers the
  Import feature's request/response surface; `FeatureServerJsonContext`
  covers the GeoServices FeatureServer API). The types they enumerate
  largely co-change. Splitting them mechanically would scatter related
  serializers across multiple partials and add maintenance overhead without
  changing the practical cache-miss rate (a single rename in `Import` already
  touches dozens of the 157 types).
- The Server is `PublishAot=true`, which means every `[JsonSerializable]`
  must be resolvable at compile time. AOT typically wants _fewer, broader_
  contexts so the linker has one well-known graph to keep, not many.

**Recommendation:** No split today. Re-evaluate only if profiling on a
representative inner-loop edit shows JsonSourceGenerator dominating the
per-file rebuild. The `--no-source-change` 2.7 s warm rebuild measured above
is the signal that the cache is healthy across the whole project.

## What is _not_ here

- No `ISourceGenerator` (legacy, non-cacheable) implementations.
- No custom Roslyn analyzer / generator projects (`OutputItemType="Analyzer"`
  is absent from every `.csproj`).
- No mass-produced `JsonContext` files emitted by repo tooling that would
  need bulk de-duplication.

If a custom generator is added in the future, it MUST implement
`IIncrementalGenerator` (use `context.SyntaxProvider.CreateSyntaxProvider`,
not `context.RegisterForSyntaxNotifications`) and its pipeline values must
be `record`s / `IEquatable<T>` so the incremental cache can compare them by
value. Anything that captures a `Compilation`, `ISymbol`, or `SyntaxNode`
in a pipeline value will break the cache.
