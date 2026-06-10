# ADR-0012: Clean Architecture Implementation

## Status
Accepted

## Context

Honua Server requires a robust architectural foundation that enforces separation of concerns, maintains testability, and supports long-term maintainability across multiple geospatial protocols (GeoServices REST, OGC API Features, OData v4, MVT).

Traditional layered architectures often lead to:
- Tight coupling between layers
- Difficulty testing business logic in isolation
- Dependency violations where lower layers depend on higher layers
- Infrastructure concerns bleeding into domain logic

The project needs an architectural approach that:
- Enforces proper dependency direction
- Isolates domain logic from external concerns
- Enables independent testing of business rules
- Supports multiple API protocols without duplication
- Maintains AOT compatibility for cloud-native deployment

## Decision

Implement **Clean Architecture** with three primary layers and strict dependency direction enforcement:

```
Honua.Server (Presentation/Host)
    ↓ (depends on)
Honua.Postgres (Infrastructure)
    ↓ (depends on)
Honua.Core (Domain/Application)
```

### Core Principles

#### 1. Dependency Inversion
- **Core** defines abstractions (interfaces) and domain models
- **Infrastructure** implements Core interfaces
- **Server** uses both Core abstractions and Infrastructure implementations
- Dependencies flow inward: outer layers depend on inner layers, never the reverse

#### 2. Layer Responsibilities

**Honua.Core (Application/Domain Layer)**
- Domain models and business logic
- Application interfaces (IFeatureStore, ILayerCatalog)
- Query models and validation rules
- Protocol-agnostic business operations

**Honua.Postgres (Infrastructure Layer)**
- Database access implementations
- External service adapters
- Infrastructure concerns (caching, logging)
- PostGIS spatial operations

**Honua.Server (Presentation Layer)**
- API endpoints and routing
- HTTP concerns and middleware
- Dependency injection composition
- Protocol-specific models and serialization

#### 3. Enforcement Mechanisms

**Automated Architecture Tests**
```csharp
[ArchitectureTest]
public void Core_ShouldNotDependOnInfrastructure()
{
    var coreAssembly = typeof(IFeatureStore).Assembly;
    var dependencies = coreAssembly.GetReferencedAssemblies();

    dependencies.Should().NotContain(a =>
        a.Name.Contains("Postgres") ||
        a.Name.Contains("Server"),
        "Core layer must not depend on Infrastructure or Presentation layers");
}

[ArchitectureTest]
public void Infrastructure_CanDependOnCore()
{
    var postgresAssembly = typeof(PostgresFeatureStore).Assembly;
    var dependencies = postgresAssembly.GetReferencedAssemblies();

    dependencies.Should().Contain(a => a.Name == "Honua.Core",
        "Infrastructure layer should depend on Core abstractions");
}

[ArchitectureTest]
public void InfrastructureTypes_ShouldBeInternal()
{
    var postgresAssembly = typeof(PostgresFeatureStore).Assembly;
    var publicTypes = postgresAssembly.GetExportedTypes()
        .Where(t => !t.IsInterface);

    publicTypes.Should().BeEmpty(
        "Infrastructure implementations should be internal - only interfaces should be public");
}
```

## Implementation Patterns

### 1. Abstraction-First Design

**Domain Interface (Core)**
```csharp
// Honua.Core/Features/Abstractions/IFeatureStore.cs
public interface IFeatureStore
{
    Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query);
    Task<Feature?> GetByIdAsync(int layerId, object featureId);
    Task<ApplyEditsResult> ApplyEditsAsync(int layerId, ApplyEditsRequest request);
}
```

**Infrastructure Implementation (Internal)**
```csharp
// Honua.Postgres/Features/PostgresFeatureStore.cs
internal class PostgresFeatureStore : IFeatureStore
{
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query)
    {
        // Raw Npgsql implementation with PostGIS queries
    }
}
```

**Composition Root (Server)**
```csharp
// Honua.Server/Program.cs
builder.Services.AddScoped<IFeatureStore, PostgresFeatureStore>();
```

### 2. Protocol-Agnostic Domain Models

**Shared Domain Model**
```csharp
// Honua.Core/Features/Models/FeatureQuery.cs
public record FeatureQuery
{
    public string? Where { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public SpatialFilter? SpatialFilter { get; init; }
    public string[]? OutFields { get; init; }
    public bool ReturnGeometry { get; init; } = true;
}
```

**Protocol-Specific Mapping**
```csharp
// Honua.Server/Features/Protocols/GeoServices/FeatureServer/FeatureServerEndpoints.cs
private static FeatureQuery MapToFeatureQuery(FeatureServerQueryParameters parameters)
{
    return new FeatureQuery
    {
        Where = parameters.Where,
        Limit = parameters.ResultRecordCount,
        Offset = parameters.ResultOffset,
        SpatialFilter = CreateSpatialFilter(parameters.Geometry, parameters.SpatialRel),
        OutFields = parameters.OutFields?.Split(','),
        ReturnGeometry = parameters.ReturnGeometry ?? true
    };
}

// Honua.Server/Features/Protocols/Ogc/Api/Features/OgcFeaturesEndpoints.cs
private static FeatureQuery MapToFeatureQuery(OgcQueryParameters parameters)
{
    return new FeatureQuery
    {
        Where = parameters.Filter, // CQL2 filter
        Limit = parameters.Limit,
        Offset = parameters.Offset,
        SpatialFilter = CreateSpatialFilter(parameters.Bbox),
        OutFields = parameters.Properties?.Split(','),
        ReturnGeometry = true // GeoJSON always includes geometry
    };
}
```

### 3. Cross-Cutting Concern Isolation

**Logging (Infrastructure Concern)**
```csharp
// Honua.Server/Features/Infrastructure/Logging/Log.cs
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Executing query for layer {LayerId} with filter: {Filter}")]
    internal static partial void QueryExecuting(ILogger logger, int layerId, string? filter);
}
```

**Caching (Infrastructure Implementation)**
```csharp
// Honua.Server/Features/Infrastructure/Caching/CachedFeatureStore.cs
internal class CachedFeatureStore : IFeatureStore
{
    private readonly IFeatureStore _inner;
    private readonly IMemoryCache _cache;

    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query)
    {
        var cacheKey = $"query:{layerId}:{query.GetHashCode()}";

        if (_cache.TryGetValue(cacheKey, out QueryResult<Feature>? cached))
            return cached!;

        var result = await _inner.QueryAsync(layerId, query);
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
        return result;
    }
}
```

### 4. Dependency Injection Composition

**Service Registration Pattern**
```csharp
// Honua.Server/Program.cs - Composition Root
var builder = WebApplication.CreateBuilder(args);

// Core abstractions
builder.Services.AddScoped<IFeatureStore, PostgresFeatureStore>();
builder.Services.AddScoped<ILayerCatalog, PostgresLayerCatalog>();
builder.Services.AddScoped<IQueryFormatter, QueryFormatter>();

// Infrastructure decorators (optional)
builder.Services.Decorate<IFeatureStore, CachedFeatureStore>();
builder.Services.Decorate<IFeatureStore, LoggingFeatureStore>();

// Protocol-specific services
builder.Services.AddScoped<IFeatureQueryValidator, FeatureQueryValidator>();
```

## Benefits Achieved

### 1. Testability
- Core business logic testable in isolation
- Infrastructure implementations easily mocked
- Protocol endpoints testable without database

### 2. Maintainability
- Clear separation of concerns
- Dependencies flow in single direction
- Changes isolated to appropriate layers

### 3. Protocol Independence
- Same business logic serves multiple API protocols
- Zero duplication between FeatureServer and OGC endpoints
- New protocols added without affecting existing code

### 4. Performance
- AOT-compatible throughout all layers
- No reflection in Core business logic
- Infrastructure optimizations don't affect domain models

## Consequences

### Positive
- **Enforced Separation**: Architecture tests prevent dependency violations
- **Protocol Sharing**: Same business logic serves all API protocols
- **Testing Independence**: Core logic testable without external dependencies
- **Flexibility**: Easy to swap infrastructure implementations
- **Maintainability**: Clear boundaries reduce cognitive load

### Negative
- **Initial Complexity**: More interfaces and abstractions to design
- **Mapping Overhead**: Protocol-specific models require mapping to domain models
- **Learning Curve**: Developers must understand dependency inversion principles

### Mitigation
- **Architecture Tests**: Automated enforcement prevents violations
- **Code Generation**: Source generators reduce mapping boilerplate
- **Documentation**: Clear examples in ADRs and code comments
- **Pair Programming**: Knowledge sharing during implementation

## Related ADRs
- [ADR-0013](0013-minimal-apis-vs-controllers.md): Minimal APIs implementation aligns with Clean Architecture
- [ADR-0015](0015-vertical-slice-architecture.md): Vertical slices organize features within Clean Architecture layers
- [ADR-0009](0009-shared-filter-ast.md): Shared filtering demonstrates protocol-agnostic domain logic

## Implementation Status

**Phase 1 (Current)**: ✅ Complete
- Core abstractions defined
- Infrastructure implementations created
- Architecture tests enforcing boundaries

**Phase 2-5**: Maintained
- All new features must follow Clean Architecture principles
- Architecture tests run on every PR
- Dependency direction violations block merges
