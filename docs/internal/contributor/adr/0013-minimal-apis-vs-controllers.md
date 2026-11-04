# ADR-0013: Minimal APIs vs Controllers Decision

## Status
Accepted

## Context

ASP.NET Core provides two primary approaches for implementing HTTP endpoints:

1. **Traditional Controllers**: Class-based approach with attributes for routing
2. **Minimal APIs**: Function-based approach with builder pattern registration

For Honua Server's multi-protocol geospatial API implementation, we need an approach that:
- Supports AOT (Ahead-of-Time) compilation for fast cold starts
- Minimizes dependency footprint per endpoint
- Enables clean separation of protocol concerns
- Avoids the "god controller" anti-pattern from legacy systems
- Maintains high performance for geospatial data serving

The legacy Honua system suffered from controllers with 20+ dependencies injected via constructor, creating maintenance nightmares and testing difficulties.

## Decision

**Use Minimal APIs exclusively** for all HTTP endpoint implementation.

**Explicitly reject Controllers** to avoid recreating the dependency injection anti-patterns that plagued the legacy system.

### Implementation Pattern

**Endpoint Registration**
```csharp
// Honua.Server/Features/Protocols/GeoServices/FeatureServer/FeatureServerEndpoints.cs
public static class FeatureServerEndpoints
{
    public static void MapFeatureServerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/rest/services/{serviceId}/FeatureServer")
            .WithTags("FeatureServer")
            .WithOpenApi();

        // Query endpoint with explicit dependencies
        group.MapGet("/{layerId}/query", QueryFeaturesGet)
            .WithName("QueryFeaturesGet")
            .WithSummary("Query features using GET parameters");

        group.MapPost("/{layerId}/query", QueryFeaturesPost)
            .WithName("QueryFeaturesPost")
            .WithSummary("Query features using POST body");
    }

    // Explicit dependency injection per endpoint (max 5 dependencies)
    private static async Task<IResult> QueryFeaturesGet(
        int serviceId,
        int layerId,
        IFeatureStore featureStore,
        ILayerCatalog layerCatalog,
        IQueryFormatter queryFormatter,
        [FromQuery] FeatureServerQueryParameters parameters)
    {
        // Implementation focused on single responsibility
    }
}
```

**Protocol Separation**
```csharp
// Each protocol gets its own endpoint class
// Honua.Server/Features/Protocols/Ogc/Api/Features/OgcFeaturesEndpoints.cs
public static class OgcFeaturesEndpoints
{
    public static void MapOgcFeaturesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/ogc")
            .WithTags("OGC API Features")
            .WithOpenApi();

        group.MapGet("/collections/{collectionId}/items", GetFeatures);
    }

    private static async Task<IResult> GetFeatures(
        string collectionId,
        IFeatureStore featureStore,
        IQueryFormatter queryFormatter,
        [FromQuery] OgcQueryParameters parameters)
    {
        // OGC-specific parameter mapping to shared domain models
    }
}
```

## Architecture Constraints

### 1. Dependency Injection Limits
- **Endpoint limit**: Maximum 5 dependencies per endpoint function
- **Handler limit**: Maximum 4 dependencies per handler class (if used)
- Enforced by architecture tests to prevent god controller anti-pattern

### 2. Single Responsibility
- Each endpoint function handles exactly one HTTP operation
- No shared state between endpoints
- Protocol-specific concerns isolated to respective endpoint classes

### 3. Explicit Dependencies
- All dependencies explicitly declared in function signature
- No hidden dependencies through base classes or attributes
- Clear dependency chain visible in endpoint signature

## Benefits Over Controllers

### 1. Dependency Control
**Traditional Controller Problem**
```csharp
// ANTI-PATTERN: Legacy controller with excessive dependencies
public class FeatureController : ControllerBase
{
    // 22 dependencies injected - maintenance nightmare
    public FeatureController(
        IFeatureStore featureStore,
        ILayerCatalog layerCatalog,
        IQueryFormatter queryFormatter,
        IValidator validator,
        ILogger<FeatureController> logger,
        IMetrics metrics,
        IEventBus eventBus,
        IGeometryConverter converter,
        ICacheService cache,
        ISecurityService security,
        IRateLimitService rateLimiter,
        IConfigurationService config,
        IHealthService health,
        IAuditService audit,
        INotificationService notifications,
        IFileService fileService,
        IEmailService emailService,
        IBackgroundTaskService backgroundTasks,
        IFeatureValidationService featureValidator,
        IPermissionService permissions,
        ITokenService tokenService,
        IEncryptionService encryption
    )
    {
        // Constructor bloat
    }
}
```

**Minimal API Solution**
```csharp
// CLEAN: Explicit, focused dependencies per operation
private static async Task<IResult> QueryFeatures(
    int layerId,
    IFeatureStore featureStore,      // Required for data access
    ILayerCatalog layerCatalog,      // Required for layer validation
    IQueryFormatter queryFormatter,  // Required for response formatting
    [FromQuery] QueryParameters parameters)
{
    // Single responsibility: query features
    // Only the dependencies actually needed
}
```

### 2. Protocol Isolation
```csharp
// Each protocol completely isolated
app.MapFeatureServerEndpoints();  // GeoServices REST API
app.MapOgcFeaturesEndpoints();   // OGC API Features
app.MapODataEndpoints();         // OData v4 API
app.MapTileEndpoints();          // MVT tiles API
```

### 3. AOT Compatibility
- Minimal APIs have first-class AOT support in .NET 8+
- Controllers require additional reflection trimming
- Source-generated route discovery
- No reflection in endpoint registration

### 4. Performance Benefits
- Reduced allocation overhead (no controller instantiation)
- Direct function calls without controller pipeline
- Smaller memory footprint per request
- Faster route matching

## Implementation Guidelines

### 1. Endpoint Organization
```csharp
// Feature-based organization within vertical slices
src/Honua.Server/Features/
├── FeatureServer/
│   ├── FeatureServerEndpoints.cs    // All FeatureServer HTTP endpoints
│   ├── FeatureServerHandler.cs      // Complex business logic (if needed)
│   └── Models/                      // FeatureServer-specific DTOs
├── OgcFeatures/
│   ├── OgcFeaturesEndpoints.cs      // All OGC HTTP endpoints
│   └── Models/                      // OGC-specific DTOs
└── Admin/
    └── AdminEndpoints.cs            // Admin interface endpoints
```

### 2. Parameter Binding
```csharp
// Explicit parameter binding with validation
private static async Task<IResult> QueryFeatures(
    [FromRoute] int layerId,              // Path parameter
    [FromQuery] QueryParameters query,    // Query string
    [FromServices] IFeatureStore store,   // Dependency injection
    [FromBody] FilterRequest? filter,     // Request body (POST)
    HttpContext context)                  // Framework services
{
    // Parameter validation at endpoint level
    if (layerId <= 0)
        return Results.BadRequest("Invalid layer ID");

    if (query.Limit is > 1000)
        return Results.BadRequest("Limit exceeds maximum of 1000");
}
```

### 3. Error Handling
```csharp
// Protocol-specific error handling
private static async Task<IResult> HandleFeatureServerOperation(/* params */)
{
    try
    {
        // Business logic
        var result = await businessOperation();
        return Results.Ok(result);
    }
    catch (ValidationException ex)
    {
        // FeatureServer error format
        var error = new EsriErrorResponse
        {
            Error = new EsriError
            {
                Code = 400,
                Message = ex.Message,
                Details = ex.Errors
            }
        };
        return Results.BadRequest(error);
    }
}

private static async Task<IResult> HandleOgcOperation(/* params */)
{
    try
    {
        // Same business logic, different error format
        var result = await businessOperation();
        return Results.Ok(result);
    }
    catch (ValidationException ex)
    {
        // OGC error format (RFC 7807)
        var error = new ProblemDetails
        {
            Type = "https://honua.io/problems/validation-error",
            Title = "Validation Failed",
            Status = 400,
            Detail = ex.Message
        };
        return Results.Problem(error);
    }
}
```

### 4. Testing Strategy
```csharp
// Integration tests against actual endpoints
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class FeatureServerQueryEndpointTests
{
    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeaturesGet_ValidParameters_ReturnsFeatures()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/rest/services/1/FeatureServer/1/query?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<FeatureQueryResult>(content);
        result.Should().NotBeNull();
    }
}
```

## Consequences

### Positive
- **Dependency Clarity**: Exact dependencies visible in endpoint signature
- **Protocol Isolation**: No cross-contamination between API protocols
- **AOT Compatibility**: First-class support for native compilation
- **Performance**: Reduced overhead vs controller pipeline
- **Testability**: Easy to test individual endpoint functions
- **Anti-Pattern Prevention**: Cannot accidentally create god controllers

### Negative
- **Learning Curve**: Developers familiar with controllers need to adapt
- **Code Generation**: Some tooling still controller-focused (Swagger, etc.)
- **Endpoint Proliferation**: More files vs consolidated controllers

### Mitigation
- **Training**: Team training on Minimal API patterns
- **Tooling**: Use OpenAPI generation that supports Minimal APIs
- **Organization**: Feature-based organization keeps related endpoints together
- **Architecture Tests**: Automated verification of dependency limits

## Enforcement

### Architecture Tests
```csharp
[ArchitectureTest]
public void Controllers_ShouldNotExist()
{
    var controllerTypes = Types
        .InAssembly(typeof(Program).Assembly)
        .That()
        .Inherit(typeof(ControllerBase));

    controllerTypes.Should().BeEmpty(
        "Controllers are forbidden - use Minimal APIs instead");
}

[ArchitectureTest]
public void EndpointFunctions_ShouldHaveLimitedDependencies()
{
    var endpointMethods = GetEndpointMethods();

    foreach (var method in endpointMethods)
    {
        var dependencies = method.GetParameters()
            .Where(p => p.GetCustomAttribute<FromServicesAttribute>() != null ||
                       IsServiceType(p.ParameterType))
            .Count();

        dependencies.Should().BeLessOrEqualTo(5,
            $"Endpoint {method.Name} has {dependencies} dependencies, max allowed is 5");
    }
}
```

### Code Review Checklist
- [ ] No `ControllerBase` inheritance
- [ ] Endpoint dependencies ≤ 5
- [ ] Protocol-specific endpoints in appropriate feature folder
- [ ] `[Endpoint]` attribute present for architecture test discovery
- [ ] Error handling appropriate for protocol

## Related ADRs
- [ADR-0012](0012-clean-architecture-implementation.md): Clean Architecture enables protocol isolation
- [ADR-0014](0014-dependency-injection-limits.md): Dependency limits prevent god controller anti-pattern
- [ADR-0015](0015-vertical-slice-architecture.md): Feature organization supports protocol separation

## Migration Notes

**Legacy to Minimal API Conversion**
```csharp
// Legacy Controller (REMOVE)
[ApiController]
[Route("api/[controller]")]
public class FeaturesController : ControllerBase
{
    [HttpGet("{id}/query")]
    public async Task<IActionResult> Query(int id, [FromQuery] QueryParams query)
    {
        // Implementation
    }
}

// Minimal API Replacement (ADD)
public static void MapFeatureEndpoints(this WebApplication app)
{
    app.MapGet("/api/features/{id}/query",
        async (int id, [FromQuery] QueryParams query, IFeatureStore store) =>
        {
            // Same implementation, explicit dependencies
        });
}
```