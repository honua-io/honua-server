# ADR-0014: Dependency Injection Limits Rationale

## Status
Accepted

## Context

The legacy Honua system suffered from severe dependency injection anti-patterns, with controllers requiring 20+ dependencies. This created:
- **Maintenance nightmares**: Changes to any service affected dozens of constructors
- **Testing complexity**: Mocking 22+ dependencies for unit tests
- **Cognitive overload**: Developers couldn't understand what a class actually needed
- **Performance issues**: Excessive object graph construction
- **Circular dependency risks**: Complex webs of interconnected services

Modern dependency injection frameworks make it easy to inject many services, but this convenience often leads to the "god class" anti-pattern where classes know too much about the system.

For Honua Server's multi-protocol geospatial API, we need clear boundaries that:
- Enforce single responsibility principle
- Keep cognitive load manageable
- Enable efficient testing
- Maintain clear separation of concerns
- Prevent architectural decay over time

## Decision

**Enforce strict dependency injection limits** at different architectural levels:

### Limit Thresholds
- **Endpoint Functions**: Maximum **5 dependencies**
- **Handler Classes**: Maximum **4 dependencies**
- **Service Classes**: Maximum **3 dependencies**
- **Infrastructure Classes**: Maximum **2 dependencies**

### Enforcement Mechanism
Automated architecture tests that fail CI builds when limits are exceeded.

```csharp
[ArchitectureTest]
public void EndpointFunctions_ShouldHaveLimitedDependencies()
{
    var endpointMethods = GetAllEndpointMethods();

    foreach (var method in endpointMethods)
    {
        var dependencyCount = CountInjectedDependencies(method);
        dependencyCount.Should().BeLessOrEqualTo(5,
            $"Endpoint {method.DeclaringType?.Name}.{method.Name} " +
            $"has {dependencyCount} dependencies, maximum allowed is 5");
    }
}

[ArchitectureTest]
public void HandlerClasses_ShouldHaveLimitedDependencies()
{
    var handlerTypes = Types
        .InCurrentDomain()
        .That()
        .HaveNameEndingWith("Handler")
        .And()
        .AreClasses();

    foreach (var handlerType in handlerTypes)
    {
        var constructor = handlerType.GetConstructors().FirstOrDefault();
        if (constructor == null) continue;

        var dependencyCount = constructor.GetParameters().Length;
        dependencyCount.Should().BeLessOrEqualTo(4,
            $"Handler {handlerType.Name} has {dependencyCount} dependencies, " +
            $"maximum allowed is 4");
    }
}
```

## Rationale Behind Limits

### Endpoint Functions: 5 Dependencies Maximum

**Typical necessary dependencies for geospatial endpoints:**
1. **Data Access** (IFeatureStore) - Required for all CRUD operations
2. **Validation** (ILayerCatalog) - Required for layer existence checks
3. **Formatting** (IQueryFormatter) - Required for protocol-specific responses
4. **Logging** (ILogger) - Required for observability
5. **Authentication/Authorization** (ISecurityContext) - Required for access control

**Example compliant endpoint:**
```csharp
private static async Task<IResult> QueryFeatures(
    [FromRoute] int layerId,
    [FromQuery] FeatureQueryParameters parameters,
    IFeatureStore featureStore,        // 1. Data access
    ILayerCatalog layerCatalog,        // 2. Layer validation
    IQueryFormatter queryFormatter,    // 3. Response formatting
    ILogger<FeatureServerEndpoints> logger,  // 4. Observability
    ISecurityContext securityContext)  // 5. Security
{
    // Implementation
}
```

**What exceeds the limit indicates design problems:**
- Caching, metrics, events → Should be handled via decorators
- Multiple validators → Should be composed into single validator
- Configuration → Should be injected into handlers, not endpoints
- Business logic services → Should be handled by domain handlers

### Handler Classes: 4 Dependencies Maximum

**Handlers encapsulate complex business logic:**
```csharp
// Compliant handler - focused responsibility
internal class FeatureQueryHandler
{
    private readonly IFeatureStore _featureStore;     // 1. Data access
    private readonly IQueryValidator _validator;      // 2. Input validation
    private readonly IGeometryConverter _converter;   // 3. Spatial operations
    private readonly ILogger<FeatureQueryHandler> _logger; // 4. Observability

    public FeatureQueryHandler(
        IFeatureStore featureStore,
        IQueryValidator validator,
        IGeometryConverter converter,
        ILogger<FeatureQueryHandler> logger)
    {
        _featureStore = featureStore;
        _validator = validator;
        _converter = converter;
        _logger = logger;
    }

    public async Task<QueryResult> HandleAsync(FeatureQuery query)
    {
        // Focused business logic
    }
}
```

### Service Classes: 3 Dependencies Maximum

**Core business services should be highly focused:**
```csharp
// Compliant service - single responsibility
internal class SpatialQueryBuilder
{
    private readonly IGeometryConverter _converter;   // 1. Spatial operations
    private readonly ISpatialReferenceSystem _srs;   // 2. Coordinate systems
    private readonly ILogger<SpatialQueryBuilder> _logger; // 3. Observability

    public string BuildQuery(SpatialFilter filter, int srid)
    {
        // Build PostGIS spatial queries
    }
}
```

### Infrastructure Classes: 2 Dependencies Maximum

**Infrastructure should be minimal and focused:**
```csharp
// Compliant repository - data access only
internal class PostgresFeatureRepository
{
    private readonly NpgsqlDataSource _dataSource;   // 1. Database connection
    private readonly ILogger<PostgresFeatureRepository> _logger; // 2. Observability

    public async Task<Feature[]> QueryAsync(string sql, NpgsqlParameter[] parameters)
    {
        // Raw database operations only
    }
}
```

## Violation Patterns and Refactoring

### Common Violation: God Service Anti-Pattern

**Problematic (8 dependencies):**
```csharp
// VIOLATION: Too many concerns mixed together
public class FeatureService
{
    public FeatureService(
        IFeatureStore store,           // 1
        ILayerCatalog catalog,         // 2
        IValidator validator,          // 3
        ILogger logger,               // 4
        IEventBus eventBus,           // 5
        ICacheService cache,          // 6
        IMetricsCollector metrics,    // 7
        ISecurityService security)    // 8 - EXCEEDS LIMIT
    {
    }
}
```

**Refactored Solution: Decorator Pattern**
```csharp
// Core business logic - 3 dependencies
internal class CoreFeatureService : IFeatureService
{
    private readonly IFeatureStore _store;      // 1. Data access
    private readonly ILayerCatalog _catalog;   // 2. Layer validation
    private readonly IValidator _validator;    // 3. Input validation

    // Clean business logic only
}

// Cross-cutting concerns as decorators
internal class CachedFeatureService : IFeatureService
{
    private readonly IFeatureService _inner;   // 1. Core service
    private readonly ICacheService _cache;     // 2. Caching

    // Caching behavior only
}

internal class MetricsFeatureService : IFeatureService
{
    private readonly IFeatureService _inner;       // 1. Core service
    private readonly IMetricsCollector _metrics;   // 2. Metrics

    // Metrics behavior only
}

// Registration with decoration chain
services.AddScoped<IFeatureService, CoreFeatureService>();
services.Decorate<IFeatureService, CachedFeatureService>();
services.Decorate<IFeatureService, MetricsFeatureService>();
```

### Common Violation: Configuration Injection

**Problematic:**
```csharp
public class QueryHandler
{
    public QueryHandler(
        IFeatureStore store,
        IOptions<DatabaseConfig> dbConfig,     // Configuration violation
        IOptions<CacheConfig> cacheConfig,     // Configuration violation
        IOptions<SecurityConfig> securityConfig) // Configuration violation
    {
    }
}
```

**Refactored Solution:**
```csharp
// Configuration injected into services that need it
internal class ConfiguredFeatureStore : IFeatureStore
{
    private readonly IFeatureStore _inner;
    private readonly DatabaseConfig _config;   // Configuration where it belongs

    public ConfiguredFeatureStore(
        IFeatureStore inner,
        IOptions<DatabaseConfig> config)
    {
        _inner = inner;
        _config = config.Value;
    }
}

// Handler only gets abstraction
public class QueryHandler
{
    private readonly IFeatureStore _store;     // Pre-configured service

    public QueryHandler(IFeatureStore store)  // 1 dependency - clean!
    {
        _store = store;
    }
}
```

## Benefits

### 1. Maintainability
- **Clear responsibility**: Each class has focused, understandable purpose
- **Easy changes**: Modifications affect minimal number of classes
- **Reduced coupling**: Dependencies explicit and limited

### 2. Testability
- **Simple mocking**: Maximum 5 mocks per test
- **Fast tests**: Minimal object graph construction
- **Clear test setup**: Easy to understand what's being tested

### 3. Performance
- **Faster construction**: Fewer dependencies to resolve
- **Memory efficiency**: Smaller object graphs
- **AOT compatibility**: Simpler dependency trees optimize better

### 4. Cognitive Load
- **Developer productivity**: Easy to understand class responsibilities
- **Onboarding**: New developers can quickly grasp system structure
- **Code review**: Dependencies visible at constructor level

## Implementation Guidelines

### 1. Dependency Categorization
**Always Required (count toward limit):**
- Domain services (IFeatureStore, ILayerCatalog)
- Business logic validators
- Data formatters
- Security services

**Framework Services (don't count toward limit):**
- ILogger<T> - Observability should not count against business logic
- HttpContext - Framework-provided context
- CancellationToken - Cancellation support

**Route/Query Parameters (don't count):**
- [FromRoute] parameters
- [FromQuery] parameters
- [FromBody] request bodies

### 2. Refactoring Strategies

**Strategy 1: Decorator Pattern**
```csharp
// Separate cross-cutting concerns
services.AddScoped<IFeatureService, CoreFeatureService>();
services.Decorate<IFeatureService, CachedFeatureService>();
services.Decorate<IFeatureService, LoggingFeatureService>();
```

**Strategy 2: Facade Services**
```csharp
// Combine related operations
public interface IGeospatialOperations
{
    Task<Feature[]> QueryAsync(SpatialFilter filter);
    Task<Geometry> TransformAsync(Geometry geometry, int targetSrid);
    Task<bool> ValidateSpatialReferenceAsync(int srid);
}

internal class GeospatialOperations : IGeospatialOperations
{
    private readonly IFeatureStore _store;
    private readonly IGeometryConverter _converter;
    private readonly ISpatialReferenceValidator _validator;

    // Combines three related operations under one interface
}
```

**Strategy 3: Configuration Objects**
```csharp
// Bundle related configuration
public record QueryConfiguration(
    int MaxRecordCount,
    TimeSpan QueryTimeout,
    bool EnableSpatialIndex);

// Inject configuration bundle instead of multiple IOptions<T>
public class QueryHandler
{
    private readonly IFeatureStore _store;
    private readonly QueryConfiguration _config;

    public QueryHandler(IFeatureStore store, QueryConfiguration config)
    {
        _store = store;
        _config = config;
    }
}
```

## Consequences

### Positive
- **Architectural Integrity**: Forces thoughtful design decisions
- **Maintainability**: Classes remain focused and understandable
- **Testability**: Simple test setup with minimal mocking
- **Performance**: Efficient dependency resolution and object construction
- **Code Quality**: Prevents gradual degradation toward god classes

### Negative
- **Initial Design Overhead**: Requires upfront thinking about dependency organization
- **Potential Over-Engineering**: May lead to excessive abstraction in simple cases
- **Learning Curve**: Developers must understand dependency organization patterns

### Mitigation
- **Clear Guidelines**: Documented patterns for common refactoring scenarios
- **Architecture Reviews**: Regular reviews to ensure limits serve the design well
- **Tooling Support**: IDE templates for common decorator and facade patterns
- **Training**: Team education on dependency management patterns

## Enforcement

### CI Pipeline Integration
```yaml
# .github/workflows/ci.yml
- name: Enforce Architecture Rules
  run: |
    dotnet test tests/dotnet/Honua.Architecture.Tests/ \
      --filter "Category=Architecture" \
      --logger "trx" \
      --results-directory TestResults/

    # Fail build if architecture tests fail
    if [ $? -ne 0 ]; then
      echo "::error::Architecture constraints violated"
      exit 1
    fi
```

### Pre-commit Hooks
```bash
#!/bin/sh
# Run architecture tests before commit
echo "Checking dependency injection limits..."
dotnet test tests/dotnet/Honua.Architecture.Tests/ --filter "Category=DependencyLimits" -q

if [ $? -ne 0 ]; then
    echo "❌ Dependency injection limits exceeded. Please refactor before committing."
    exit 1
fi

echo "✅ Dependency limits OK"
```

## Related ADRs
- [ADR-0012](0012-clean-architecture-implementation.md): Clean Architecture enables focused dependencies
- [ADR-0013](0013-minimal-apis-vs-controllers.md): Minimal APIs make dependencies explicit
- [ADR-0015](0015-vertical-slice-architecture.md): Vertical slices organize dependencies by feature

## Future Considerations

### Potential Adjustments
- **Domain Growth**: Limits may need adjustment as domain complexity increases
- **Framework Changes**: New ASP.NET Core features might affect dependency patterns
- **Performance Analysis**: Monitor impact of decorator chains on performance

### Success Metrics
- Average dependencies per endpoint: Target ≤ 3.5 (well below 5 limit)
- Test setup complexity: Maximum 5 mocks per integration test
- Build performance: Dependency resolution should remain under 100ms
- Developer feedback: Quarterly surveys on maintainability and cognitive load