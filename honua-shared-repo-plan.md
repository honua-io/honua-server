# honua-shared Repository Plan

## Repository Structure

```
https://github.com/honua-io/honua-server/
├── .github/workflows/
│   ├── ci.yml                          # Build, test, lint
│   ├── release.yml                     # Publish to NuGet
│   └── dependency-update.yml           # Update buf.build dependencies
├── src/
│   └── Honua.Shared/
│       ├── Honua.Shared.csproj         # Multi-target .NET 10
│       ├── Models/                     # Shared domain models
│       │   ├── FeatureQuery.cs
│       │   ├── SpatialFilter.cs
│       │   ├── FormDefinition.cs
│       │   └── ValidationRules.cs
│       ├── Converters/                 # gRPC conversion helpers
│       │   ├── GrpcConversionHelpers.cs
│       │   ├── FormGrpcConverters.cs
│       │   └── GeometryConverters.cs
│       ├── Services/                   # Shared service interfaces
│       │   ├── IHonuaAuthenticationProvider.cs
│       │   ├── ISpatialCalculator.cs
│       │   └── IValidationService.cs
│       ├── Extensions/                 # Extension methods
│       │   ├── ProtoExtensions.cs
│       │   ├── GeometryExtensions.cs
│       │   └── CollectionExtensions.cs
│       └── Constants/                  # Shared constants
│           ├── SpatialConstants.cs
│           └── ValidationConstants.cs
├── tests/
│   └── Honua.Shared.Tests/
│       ├── Converters/
│       │   ├── GrpcConversionHelpersTests.cs
│       │   └── FormGrpcConvertersTests.cs
│       ├── Models/
│       │   └── FeatureQueryTests.cs
│       └── TestData/
│           └── SampleGeometries.cs
├── docs/
│   ├── getting-started.md
│   ├── api-reference.md
│   └── migration-guide.md
├── LICENSE                             # ELv2 license
├── README.md                           # Usage examples
├── CHANGELOG.md                        # Version history
├── buf.yaml                            # Dependency on geospatial standard
└── Honua.Shared.sln                    # Solution file

## NuGet Package Configuration

Package ID: `Honua.Shared`
Targets: `net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`
License: ELv2
Repository: https://github.com/honua-io/honua-server

## Dependencies

### Core Dependencies
- buf.build/geospatial/standard:v0.2.0 (gRPC proto definitions)
- Google.Protobuf:3.28.2
- Grpc.Net.Client:2.67.0
- NetTopologySuite:2.5.0
- System.Collections.Immutable:9.0.0

### Platform-Specific
- Server (net10.0): Microsoft.Extensions.DependencyInjection, Logging.Abstractions
- Mobile: Microsoft.Maui.Essentials

## Consumer Repository Integration

### honua-server
```xml
<PackageReference Include="Honua.Shared" Version="1.0.0" />
```

### honua-mobile-sdk
```xml
<PackageReference Include="Honua.Shared" Version="1.0.0" />
```

### honua-js-sdk (future TypeScript bindings)
```json
{
  "dependencies": {
    "@honua/shared": "^1.0.0"
  }
}
```

## Migration Plan

### Phase 1: Create Repository (Week 1)
1. Create `honua-shared` GitHub repository
2. Set up CI/CD pipeline with GitHub Actions
3. Configure NuGet package publishing
4. Add comprehensive README and documentation

### Phase 2: Move Shared Code (Week 2)
1. Extract GrpcConversionHelpers from honua-server
2. Extract FormGrpcConverters from honua-server
3. Create shared domain models (FeatureQuery, SpatialFilter, etc.)
4. Add comprehensive unit tests

### Phase 3: Update Consumers (Week 3)
1. Update honua-server to reference Honua.Shared package
2. Remove duplicated conversion code from honua-server
3. Update honua-mobile-sdk to use Honua.Shared
4. Verify consistent behavior across platforms

### Phase 4: Continuous Integration (Week 4)
1. Set up automated dependency updates (buf.build/geospatial/standard)
2. Configure breaking change detection
3. Implement semantic versioning workflow
4. Add cross-platform compatibility testing

## Benefits

✅ **Zero Code Duplication**: Single implementation shared across all platforms
✅ **Consistent Behavior**: Same spatial calculations and conversions everywhere
✅ **Independent Releases**: Shared library versioned separately from consumers
✅ **Easy Maintenance**: Bug fixes and features applied once, available everywhere
✅ **Platform Optimization**: Conditional compilation for mobile vs server
✅ **Future-Proof**: New SDKs automatically get shared functionality

## Example Usage After Migration

### Server (honua-server)
```csharp
using Honua.Shared.Converters;
using Honua.Shared.Models;

// Convert incoming gRPC request to domain model
var query = GrpcConversionHelpers.ToFeatureQuery(request);

// Process query with existing server logic
var results = await _featureService.ExecuteQueryAsync(query);

// Convert results back to gRPC response
var response = GrpcConversionHelpers.ToQueryFeaturesResponse(results);
```

### Mobile Client (MAUI)
```csharp
using Honua.Shared.Converters;
using Honua.Shared.Models;

// Build query using shared domain model
var query = new FeatureQuery
{
    Where = "population > 100000",
    SpatialFilter = new SpatialFilter
    {
        FilterGeometry = userDrawnPolygon,
        Relationship = SpatialRelationship.Intersects
    }
};

// Convert to gRPC request using shared converter
var request = GrpcConversionHelpers.ToProtoRequest(query, serviceId, layerId);

// Execute query
var response = await _featureClient.QueryFeaturesAsync(request);
```

This approach eliminates all C# code duplication while maintaining platform-specific optimizations and enabling consistent behavior across all Honua applications.