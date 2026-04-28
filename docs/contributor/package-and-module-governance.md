# Package and Module Governance

Honua keeps package versions centralized and optional capabilities explicit so the base server stays lean as providers, cloud integrations, and protocol adapters grow.

## Package Version Rule

All NuGet package versions are declared in `Directory.Packages.props` at the repository root. Project files use `PackageReference` only to identify the package and any project-local metadata:

```xml
<PackageReference Include="Grpc.Tools" PrivateAssets="All" />
```

Do not add `Version`, `VersionOverride`, or child `<Version>` metadata to a `.csproj` file. When adding or updating a dependency:

- add or update the `PackageVersion` entry in `Directory.Packages.props`
- keep `PrivateAssets`, `IncludeAssets`, analyzer metadata, generated-code metadata, and similar consumption rules in the consuming `.csproj`
- use the same central version for every project unless there is an approved compatibility reason to change the package for the whole repo
- run the architecture tests before opening a PR

The architecture suite enforces this rule with `PackageGovernanceTests`.

## Base Runtime Boundary

The base runtime is the smallest package graph needed to host shared canonical behavior and the default deployment shape:

- `Honua.Core`: domain models, canonical requests/responses, protocol-neutral abstractions, validators, shared format/query/edit/raster/process contracts, and cross-cutting policy contracts
- `Honua.Postgres`: default PostgreSQL/PostGIS provider implementation behind Core abstractions
- `Honua.DuckDB`: embedded read-only provider implementation behind Core abstractions
- `Honua.Server`: ASP.NET Core host, protocol adapters, composition root, shared endpoint registration, and base runtime services

Protocol adapters in `Honua.Server` may parse wire formats and map to canonical pipelines, but optional provider or cloud-specific SDKs should not become new ambient dependencies of the base server unless the capability is part of the default runtime contract.

## Optional Module Boundary

Create or use a dedicated assembly when a capability introduces a dependency cluster that is not required by the base runtime. Good candidates include:

- cloud storage providers and object-store-specific clients
- cloud control-plane or batch execution backends
- optional identity/auth provider integrations
- future non-Postgres data providers
- optional import/export format adapters when the dependency is large, native, or narrowly used
- provider-specific operational tooling

Optional modules should expose a small public registration surface, usually one `IServiceCollection` or endpoint-registration extension type. Implementation details, SDK wrappers, repositories, and provider clients should remain internal. Module registration should fail clearly at startup when configuration requests a missing module or unsupported capability.

## Registration Pattern

Use extension-method registration from the owning module:

```csharp
services.AddHonuaAwsBatch(options);
app.MapHonuaAwsBatchDiagnostics();
```

Registration should:

- bind and validate module-specific options
- register implementations behind Core or server-owned abstractions
- publish capability metadata so protocols and admin APIs can report what is available
- avoid direct dependencies from one protocol adapter to another protocol adapter
- keep startup behavior deterministic when a feature is configured but its module is absent

## Build and Test Expectations

For package or module-boundary changes, run:

```bash
dotnet restore Honua.sln
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj --filter Category=Architecture
```

For module-specific implementation changes, also run the owning project tests and any affected protocol integration tests. Prefer targeted builds/tests for the module slice first, then broader solution validation before merging.
