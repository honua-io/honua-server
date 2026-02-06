# ADR-0015: Vertical Slice Architecture Pattern

## Status
Accepted

## Context

Traditional layered architecture organizes code by technical concerns (Controllers, Services, Models, Repositories), which often leads to:

**Problems with Horizontal Layers:**
- **High coupling across layers**: Changes to a feature require modifications in multiple layer folders
- **Scattered feature logic**: A single user story touches files in 4+ different directories
- **Merge conflicts**: Multiple developers editing the same "layer" files
- **Cognitive overhead**: Developers must navigate multiple folders to understand one feature
- **Difficult feature removal**: Deleting a feature requires hunting through all layers

For Honua Server's multi-protocol geospatial API, we have distinct feature concerns:
- **Protocol isolation**: FeatureServer vs OGC Features vs OData have different requirements
- **Feature cohesion**: Query operations differ significantly from Admin operations
- **Team organization**: Different developers can work on different protocols simultaneously
- **Deployment flexibility**: Potentially deploy subsets of features independently

## Decision

**Implement Vertical Slice Architecture** where code is organized by feature/business capability rather than technical layers.

### Organizational Structure

```
src/Honua.Server/Features/
├── FeatureServer/              # GeoServices REST API
│   ├── FeatureServerEndpoints.cs      # HTTP endpoints
│   ├── FeatureServerHandler.cs        # Business logic
│   ├── FeatureServerLog.cs            # Feature-specific logging
│   ├── AttachmentEndpoints.cs         # Sub-feature: attachments
│   ├── AttachmentHandler.cs           # Attachment business logic
│   ├── Models/                        # FeatureServer-specific DTOs
│   │   └── FeatureServerModels.cs
│   └── Services/                      # FeatureServer-specific services
│       ├── QueryFormatters.cs
│       ├── FeatureQueryValidator.cs
│       └── GeoServicesGeometryConverter.cs
├── OgcFeatures/                # OGC API Features
│   ├── OgcFeaturesEndpoints.cs        # HTTP endpoints
│   ├── OgcJsonContext.cs              # JSON serialization
│   └── Models/                        # OGC-specific DTOs
│       └── OgcModels.cs
├── OData/                      # OData v4 API
│   ├── ODataEndpoints.cs              # HTTP endpoints
│   ├── Models/                        # OData-specific DTOs
│   └── Services/                      # OData-specific services
│       ├── ODataBatchHandler.cs
│       └── ODataAggregationHandler.cs
├── Admin/                      # Administrative interface
│   ├── AdminEndpoints.cs              # Admin HTTP endpoints
│   └── Models/                        # Admin-specific DTOs
├── Import/                     # File import feature
│   ├── ImportEndpoints.cs
│   └── ImportJsonContext.cs
├── HealthCheck/                # System health monitoring
│   ├── HealthEndpoints.cs
│   ├── ReadinessCheckService.cs
│   └── IReadinessCheckService.cs
└── Infrastructure/             # Shared technical concerns
    ├── Middleware/             # Cross-cutting middleware
    ├── Authentication/         # Auth infrastructure
    ├── Security/              # Security utilities
    ├── Caching/               # Caching infrastructure
    └── Services/              # Shared technical services
```

### Key Principles

#### 1. Feature Cohesion
**All code for a feature lives together:**
```csharp
// Everything for FeatureServer query operations in one place
Features/FeatureServer/
├── FeatureServerEndpoints.cs     # HTTP layer: MapGet/MapPost
├── FeatureServerHandler.cs       # Business logic: validation, transformation
├── FeatureServerLog.cs           # Observability: structured logging
├── Models/FeatureServerModels.cs # DTOs: request/response objects
└── Services/QueryFormatters.cs   # Services: response formatting
```

#### 2. Protocol Independence
**Each protocol slice is self-contained:**
```csharp
// OGC Features completely independent of FeatureServer
Features/OgcFeatures/
├── OgcFeaturesEndpoints.cs       # OGC-specific HTTP endpoints
├── OgcJsonContext.cs             # OGC-specific JSON serialization
└── Models/OgcModels.cs           # OGC-specific DTOs

// No dependencies between protocol slices
// Both protocols use shared Core abstractions
```

#### 3. Shared Infrastructure
**Common technical concerns remain shared:**
```csharp
Features/Infrastructure/
├── Middleware/                   # Used by all features
├── Authentication/               # Used by secured features
├── Security/                     # Used by all features
└── Services/GeometryConverter.cs # Used by spatial features
```

## Implementation Patterns

### 1. Feature Registration Pattern
**Each feature slice registers its own services:**

```csharp
// Features/FeatureServer/FeatureServerServices.cs
public static class FeatureServerServices
{
    public static IServiceCollection AddFeatureServerServices(
        this IServiceCollection services)
    {
        // Feature-specific services
        services.AddScoped<IFeatureQueryValidator, FeatureQueryValidator>();
        services.AddScoped<QueryFormatter>();
        services.AddScoped<GeoServicesGeometryConverter>();

        return services;
    }
}

// Features/OgcFeatures/OgcFeaturesServices.cs
public static class OgcFeaturesServices
{
    public static IServiceCollection AddOgcFeaturesServices(
        this IServiceCollection services)
    {
        // OGC-specific services
        services.AddScoped<OgcGeometryConverter>();
        services.AddScoped<CqlFilterParser>();

        return services;
    }
}

// Program.cs - Composition root
builder.Services.AddFeatureServerServices();
builder.Services.AddOgcFeaturesServices();
builder.Services.AddODataServices();
```

### 2. Feature Endpoint Registration
**Each feature exposes its endpoints independently:**

```csharp
// Features/FeatureServer/FeatureServerEndpoints.cs
public static class FeatureServerEndpoints
{
    public static void MapFeatureServerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/rest/services/{serviceId}/FeatureServer")
            .WithTags("FeatureServer")
            .RequireAuthorization("FeatureServerPolicy");

        group.MapGet("/{layerId}/query", QueryFeaturesGet);
        group.MapPost("/{layerId}/query", QueryFeaturesPost);
        group.MapPost("/{layerId}/applyEdits", ApplyEdits);
    }

    private static async Task<IResult> QueryFeaturesGet(/* ... */) { }
    private static async Task<IResult> QueryFeaturesPost(/* ... */) { }
    private static async Task<IResult> ApplyEdits(/* ... */) { }
}

// Program.cs - Feature composition
app.MapFeatureServerEndpoints();  // GeoServices REST
app.MapOgcFeaturesEndpoints();    // OGC API Features
app.MapODataEndpoints();          // OData v4
app.MapAdminEndpoints();          // Admin interface
```

### 3. Domain Sharing Pattern
**Shared business logic via Core abstractions:**

```csharp
// Both FeatureServer and OGC Features use same domain services
// but with different parameter mapping and response formatting

// FeatureServer endpoint
private static async Task<IResult> QueryFeaturesGet(
    int layerId,
    [FromQuery] FeatureServerQueryParameters parameters,
    IFeatureStore featureStore,     // Shared Core abstraction
    QueryFormatter formatter)       // FeatureServer-specific formatter
{
    // Map FeatureServer parameters to domain model
    var query = new FeatureQuery
    {
        Where = parameters.Where,
        Limit = parameters.ResultRecordCount,
        Offset = parameters.ResultOffset
    };

    // Use shared domain service
    var result = await featureStore.QueryAsync(layerId, query);

    // Format with FeatureServer-specific formatter
    return formatter.FormatFeatureServerResponse(result);
}

// OGC Features endpoint
private static async Task<IResult> GetFeatures(
    string collectionId,
    [FromQuery] OgcQueryParameters parameters,
    IFeatureStore featureStore,     // Same shared Core abstraction
    OgcFormatter formatter)         // OGC-specific formatter
{
    // Map OGC parameters to same domain model
    var query = new FeatureQuery
    {
        Where = parameters.Filter,    // CQL2 instead of SQL
        Limit = parameters.Limit,
        Offset = parameters.Offset
    };

    // Use same shared domain service
    var result = await featureStore.QueryAsync(layerId, query);

    // Format with OGC-specific formatter
    return formatter.FormatOgcResponse(result);
}
```

### 4. Feature-Specific Configuration
**Configuration organized by feature:**

```csharp
// Features/FeatureServer/Models/FeatureServerConfiguration.cs
public record FeatureServerConfiguration
{
    public int MaxRecordCount { get; init; } = 1000;
    public bool AllowGeometryUpdates { get; init; } = true;
    public string[] SupportedFormats { get; init; } = ["json", "geojson"];
}

// Features/OgcFeatures/Models/OgcConfiguration.cs
public record OgcConfiguration
{
    public int DefaultLimit { get; init; } = 10;
    public int MaxLimit { get; init; } = 10000;
    public string[] SupportedCrs { get; init; } = ["4326", "3857"];
}

// Configuration registration with feature services
services.Configure<FeatureServerConfiguration>(
    builder.Configuration.GetSection("FeatureServer"));
services.Configure<OgcConfiguration>(
    builder.Configuration.GetSection("OgcFeatures"));
```

## Benefits

### 1. Feature Development Velocity
**Complete feature development in single location:**

```bash
# Working on FeatureServer query enhancement
git status
    modified: Features/FeatureServer/FeatureServerEndpoints.cs
    modified: Features/FeatureServer/FeatureServerHandler.cs
    added:    Features/FeatureServer/Services/AdvancedQueryValidator.cs
    modified: tests/Honua.Server.Tests/Features/FeatureServer/QueryTests.cs
```

**All changes localized to FeatureServer slice - no cross-cutting modifications**

### 2. Team Organization
**Teams can own entire vertical slices:**

- **GeoServices Team**: Owns `Features/FeatureServer/` completely
- **Standards Team**: Owns `Features/OgcFeatures/` and `Features/OData/`
- **Admin Team**: Owns `Features/Admin/` and `Features/Import/`
- **Platform Team**: Owns `Features/Infrastructure/` and shared Core

**Minimal coordination needed between teams for feature development**

### 3. Protocol Independence
**Add/remove/modify protocols independently:**

```csharp
// Adding new WFS protocol doesn't affect existing protocols
Features/Wfs/
├── WfsEndpoints.cs
├── WfsServices.cs
└── Models/WfsModels.cs

// Register new protocol
app.MapWfsEndpoints();  // No impact on existing endpoints
```

### 4. Feature Toggling
**Enable/disable features at deployment time:**

```csharp
// Program.cs - Conditional feature registration
if (builder.Configuration.GetValue<bool>("Features:FeatureServerEnabled"))
{
    app.MapFeatureServerEndpoints();
}

if (builder.Configuration.GetValue<bool>("Features:OgcFeaturesEnabled"))
{
    app.MapOgcFeaturesEndpoints();
}

if (builder.Configuration.GetValue<bool>("Features:AdminEnabled"))
{
    app.MapAdminEndpoints();
}
```

### 5. Testing Organization
**Tests organized by feature:**

```
tests/Honua.Server.Tests/Features/
├── FeatureServer/
│   ├── QueryEndpointTests.cs
│   ├── ApplyEditsEndpointTests.cs
│   └── AttachmentEndpointTests.cs
├── OgcFeatures/
│   ├── CollectionsEndpointTests.cs
│   └── ItemsEndpointTests.cs
└── Admin/
    ├── LayerManagementTests.cs
    └── UserManagementTests.cs
```

**Test failures clearly indicate which feature/protocol needs attention**

## Anti-Patterns to Avoid

### ❌ Cross-Slice Dependencies
```csharp
// WRONG: FeatureServer depending on OGC-specific code
Features/FeatureServer/FeatureServerHandler.cs:
using Honua.Server.Features.OgcFeatures.Models;  // ❌ Cross-slice dependency

// CORRECT: Both slices depend on shared Core abstractions
using Honua.Core.Features.Models;  // ✅ Shared domain model
```

### ❌ Shared Mutable State
```csharp
// WRONG: Shared static state between features
public static class GlobalFeatureState
{
    public static Dictionary<string, object> SharedCache { get; } = new();
}

// CORRECT: Feature-specific state or proper DI
public class FeatureServerCache
{
    private readonly IMemoryCache _cache;
    // Feature-specific caching logic
}
```

### ❌ Feature Leakage in Infrastructure
```csharp
// WRONG: Infrastructure knowing about specific features
public class GlobalExceptionMiddleware
{
    private IResult HandleFeatureServerException(Exception ex) { }
    private IResult HandleOgcException(Exception ex) { }  // Feature leakage
}

// CORRECT: Generic error handling with feature-specific customization
public class GlobalExceptionMiddleware
{
    private readonly IEnumerable<IErrorHandler> _errorHandlers;
    // Generic handling with strategy pattern
}
```

## Implementation Guidelines

### 1. Feature Discovery
**Use conventions for automatic feature discovery:**

```csharp
// Automatically register all feature services
public static IServiceCollection AddFeatureServices(this IServiceCollection services)
{
    var assembly = typeof(Program).Assembly;
    var serviceTypes = assembly.GetTypes()
        .Where(t => t.Name.EndsWith("Services") && t.IsClass && t.IsSealed)
        .Where(t => t.GetMethod("AddServices") != null);

    foreach (var serviceType in serviceTypes)
    {
        var method = serviceType.GetMethod("AddServices");
        method?.Invoke(null, new object[] { services });
    }

    return services;
}
```

### 2. Feature Health Checks
**Each feature can expose health status:**

```csharp
// Features/FeatureServer/FeatureServerHealthCheck.cs
public class FeatureServerHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Feature-specific health logic
        return HealthCheckResult.Healthy("FeatureServer operational");
    }
}

// Registration
services.AddHealthChecks()
    .AddCheck<FeatureServerHealthCheck>("featureserver")
    .AddCheck<OgcFeaturesHealthCheck>("ogc-features");
```

### 3. Feature Metrics
**Feature-specific observability:**

```csharp
// Features/FeatureServer/FeatureServerMetrics.cs
public class FeatureServerMetrics
{
    private readonly Counter<long> _queryCounter;
    private readonly Histogram<double> _queryDuration;

    public void RecordQuery(string operation, double durationMs)
    {
        _queryCounter.Add(1, new("operation", operation), new("protocol", "featureserver"));
        _queryDuration.Record(durationMs, new("operation", operation));
    }
}
```

## Consequences

### Positive
- **Feature Isolation**: Changes to one protocol don't affect others
- **Team Autonomy**: Teams can work independently on their feature slices
- **Faster Development**: All related code co-located for easy modification
- **Clear Ownership**: Obvious who owns which code
- **Easier Testing**: Test failures clearly indicate problem location
- **Deployment Flexibility**: Can deploy subsets of features

### Negative
- **Code Duplication Risk**: Similar logic might be duplicated across slices
- **Learning Curve**: Developers must understand slice boundaries
- **Cross-Feature Changes**: Infrastructure changes might require updates across slices

### Mitigation
- **Shared Abstractions**: Use Core layer for common domain logic
- **Architecture Tests**: Automated validation of slice boundaries
- **Code Reviews**: Ensure proper abstraction vs duplication decisions
- **Team Training**: Education on vertical slice principles

## Enforcement

### Architecture Tests
```csharp
[ArchitectureTest]
public void Features_ShouldNotDependOnOtherFeatures()
{
    var featureAssemblies = GetFeatureAssemblies();

    foreach (var feature in featureAssemblies)
    {
        var dependencies = feature.GetReferencedAssemblies();
        var featureDependencies = dependencies
            .Where(d => d.Name.Contains("Features") && d.Name != feature.GetName().Name);

        featureDependencies.Should().BeEmpty(
            $"Feature {feature.GetName().Name} should not depend on other features. " +
            $"Found dependencies: {string.Join(", ", featureDependencies.Select(d => d.Name))}");
    }
}

[ArchitectureTest]
public void Features_ShouldOnlyDependOnCoreAndInfrastructure()
{
    var featureTypes = Types
        .InNamespace("Honua.Server.Features")
        .Except(Types.InNamespace("Honua.Server.Features.Infrastructure"));

    foreach (var featureType in featureTypes)
    {
        var dependencies = featureType.Dependencies
            .Where(d => d.Namespace.StartsWith("Honua"))
            .Where(d => !d.Namespace.StartsWith("Honua.Core"))
            .Where(d => !d.Namespace.StartsWith("Honua.Server.Features.Infrastructure"))
            .Where(d => d.Namespace != featureType.Namespace);

        dependencies.Should().BeEmpty(
            $"Feature {featureType.FullName} has invalid dependencies: " +
            $"{string.Join(", ", dependencies.Select(d => d.FullName))}");
    }
}
```

## Related ADRs
- [ADR-0012](0012-clean-architecture-implementation.md): Vertical slices operate within Clean Architecture layers
- [ADR-0013](0013-minimal-apis-vs-controllers.md): Minimal APIs support feature-based endpoint organization
- [ADR-0014](0014-dependency-injection-limits.md): Dependency limits prevent feature bloat

## Migration Strategy

**Gradual Migration from Layered to Vertical Slices:**

1. **Phase 1**: Move endpoints to feature folders
2. **Phase 2**: Move feature-specific services to feature folders
3. **Phase 3**: Move feature-specific models to feature folders
4. **Phase 4**: Consolidate shared infrastructure
5. **Phase 5**: Add architecture tests to prevent regression

**Success Metrics:**
- Feature development velocity (time from requirement to deployment)
- Cross-team coordination overhead
- Merge conflict frequency
- Developer satisfaction with code organization