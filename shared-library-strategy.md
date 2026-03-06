# Honua Shared Library Strategy

## Problem: C# Code Duplication

Currently duplicated between honua-server and MAUI client:
- gRPC conversion helpers (GrpcConversionHelpers.cs, FormGrpcConverters.cs)
- Domain models (FeatureQuery, FormDefinition, etc.)
- Spatial utilities and validation logic

## Solution: Multi-Target Shared Library

### 1. Create Honua.Shared Project Structure

```
src/Honua.Shared/
├── Honua.Shared.csproj                  # Multi-target: net8.0, net8.0-android, net8.0-ios
├── Models/
│   ├── FeatureQuery.cs                 # Shared domain models
│   ├── SpatialFilter.cs
│   ├── FormDefinition.cs
│   └── ValidationRules.cs
├── Converters/
│   ├── GrpcConversionHelpers.cs        # Moved from server
│   ├── FormGrpcConverters.cs           # Moved from server
│   └── GeometryConverters.cs           # Spatial conversions
├── Services/
│   ├── IHonuaAuthenticationProvider.cs # Shared auth interface
│   ├── ISpatialCalculator.cs           # Spatial utilities interface
│   └── IValidationService.cs           # Form/data validation
├── Extensions/
│   ├── ProtoExtensions.cs              # Extension methods for proto types
│   ├── GeometryExtensions.cs           # Spatial extension methods
│   └── CollectionExtensions.cs         # LINQ helpers
└── Constants/
    ├── SpatialConstants.cs             # EPSG codes, tolerances
    └── ValidationConstants.cs          # Validation rules, patterns
```

### 2. Multi-Target Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net8.0;net8.0-android;net8.0-ios;net8.0-maccatalyst</TargetFrameworks>
    <UseMaui>true</UseMaui>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <PackageId>Honua.Shared</PackageId>
    <Version>1.0.0</Version>
    <Description>Shared code for Honua server and mobile clients</Description>
  </PropertyGroup>

  <ItemGroup>
    <!-- Core dependencies available on all platforms -->
    <PackageReference Include="Google.Protobuf" Version="3.25.1" />
    <PackageReference Include="Grpc.Core.Api" Version="2.59.0" />
    <PackageReference Include="Grpc.Net.Client" Version="2.59.0" />
    <PackageReference Include="NetTopologySuite" Version="2.5.0" />
    <PackageReference Include="System.Collections.Immutable" Version="8.0.0" />
  </ItemGroup>

  <!-- Platform-specific dependencies -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
    <!-- Server-specific packages -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
  </ItemGroup>

  <ItemGroup Condition="$(TargetFramework.Contains('android')) OR $(TargetFramework.Contains('ios'))">
    <!-- Mobile-specific packages -->
    <PackageReference Include="Microsoft.Maui.Essentials" Version="8.0.3" />
  </ItemGroup>

</Project>
```

### 3. Refactored Conversion Helpers

```csharp
// src/Honua.Shared/Converters/GrpcConversionHelpers.cs
namespace Honua.Shared.Converters;

/// <summary>
/// Shared conversion helpers between domain types and gRPC proto messages.
/// Works on both server and mobile clients.
/// </summary>
public static class GrpcConversionHelpers
{
    private static readonly GeometryFactory _geoFactory = new();

    [ThreadStatic]
    private static WKBReader? _wkbReader;
    [ThreadStatic]
    private static WKBWriter? _wkbWriter;

    private static WKBReader WkbReader => _wkbReader ??= new WKBReader();
    private static WKBWriter WkbWriter => _wkbWriter ??= new WKBWriter();

    /// <summary>
    /// Converts proto QueryFeaturesRequest to domain FeatureQuery.
    /// Shared between server query processing and client query building.
    /// </summary>
    public static FeatureQuery ToFeatureQuery(Geospatial.V1.QueryFeaturesRequest request)
    {
        return new FeatureQuery
        {
            Where = string.IsNullOrEmpty(request.Where) ? null : request.Where,
            ObjectIds = request.ObjectIds.Count > 0
                ? request.ObjectIds.ToImmutableArray()
                : null,
            OutFields = request.OutFields.Count > 0
                ? request.OutFields.ToImmutableArray()
                : null,
            SpatialFilter = request.SpatialFilter != null
                ? ToSpatialFilter(request.SpatialFilter)
                : null,
            Offset = request.ResultOffset > 0 ? request.ResultOffset : null,
            Count = request.ResultRecordCount > 0 ? request.ResultRecordCount : null
        };
    }

    /// <summary>
    /// Converts domain FeatureQuery to proto QueryFeaturesRequest.
    /// Used by mobile clients to build query requests.
    /// </summary>
    public static Geospatial.V1.QueryFeaturesRequest ToProtoRequest(FeatureQuery query, string serviceId, int layerId)
    {
        var request = new Geospatial.V1.QueryFeaturesRequest
        {
            ServiceId = serviceId,
            LayerId = layerId,
            Where = query.Where ?? string.Empty,
            ReturnGeometry = query.ReturnGeometry,
            ResultOffset = query.Offset ?? 0,
            ResultRecordCount = query.Count ?? 1000
        };

        if (query.ObjectIds != null)
        {
            request.ObjectIds.AddRange(query.ObjectIds);
        }

        if (query.OutFields != null)
        {
            request.OutFields.AddRange(query.OutFields);
        }

        if (query.SpatialFilter != null)
        {
            request.SpatialFilter = ToProtoSpatialFilter(query.SpatialFilter);
        }

        return request;
    }

    /// <summary>
    /// Converts NetTopologySuite geometry to proto geometry.
    /// Shared spatial conversion logic.
    /// </summary>
    public static Geospatial.V1.Geometry ToProtoGeometry(Geometry? geometry)
    {
        if (geometry == null) return new Geospatial.V1.Geometry();

        return geometry switch
        {
            Point point => new Geospatial.V1.Geometry
            {
                Point = new Geospatial.V1.PointGeometry
                {
                    X = point.X,
                    Y = point.Y,
                    Z = double.IsNaN(point.Z) ? null : point.Z,
                    M = double.IsNaN(point.M) ? null : point.M
                }
            },
            LineString lineString => ToProtoLineString(lineString),
            Polygon polygon => ToProtoPolygon(polygon),
            MultiPoint multiPoint => ToProtoMultiPoint(multiPoint),
            MultiLineString multiLineString => ToProtoMultiLineString(multiLineString),
            MultiPolygon multiPolygon => ToProtoMultiPolygon(multiPolygon),
            _ => throw new NotSupportedException($"Geometry type {geometry.GetType().Name} is not supported")
        };
    }

    // Additional conversion methods...
}
```

### 4. Shared Domain Models

```csharp
// src/Honua.Shared/Models/FeatureQuery.cs
namespace Honua.Shared.Models;

/// <summary>
/// Domain model for feature queries.
/// Shared between server query processing and mobile client building.
/// </summary>
public class FeatureQuery
{
    public string? Where { get; set; }
    public ImmutableArray<long>? ObjectIds { get; set; }
    public ImmutableArray<string>? OutFields { get; set; }
    public bool ReturnGeometry { get; set; } = true;
    public SpatialFilter? SpatialFilter { get; set; }
    public int? Offset { get; set; }
    public int? Count { get; set; }
    public string? OrderBy { get; set; }
    public bool ReturnDistinct { get; set; }
}

/// <summary>
/// Spatial filtering criteria for feature queries.
/// </summary>
public class SpatialFilter
{
    public required Geometry FilterGeometry { get; set; }
    public SpatialRelationship Relationship { get; set; } = SpatialRelationship.Intersects;
    public double? BufferDistance { get; set; }
    public DistanceUnit? BufferUnit { get; set; }
}

/// <summary>
/// Supported spatial relationships for filtering.
/// </summary>
public enum SpatialRelationship
{
    Intersects,
    Contains,
    Within,
    Crosses,
    Touches,
    Overlaps,
    Disjoint,
    Equals
}
```

### 5. Mobile-Specific Implementations

```csharp
// src/Honua.Mobile.Core/Services/MobileGeocodingService.cs
using Honua.Shared.Converters;
using Honua.Shared.Models;

namespace Honua.Mobile.Core.Services;

/// <summary>
/// Mobile-specific geocoding service using shared conversion logic.
/// </summary>
public class MobileGeocodingService : IHonuaGeocodingService
{
    private readonly Geospatial.V1.FeatureService.FeatureServiceClient _featureClient;

    public async Task<IEnumerable<GeocodingResult>> GeocodeAsync(string address)
    {
        // Build query using shared FeatureQuery model
        var query = new FeatureQuery
        {
            Where = $"address LIKE '%{address}%'",
            OutFields = ImmutableArray.Create("address", "score", "location"),
            Count = 10
        };

        // Convert to proto using shared converter
        var request = GrpcConversionHelpers.ToProtoRequest(query, "geocoding-service", 1);

        // Execute query
        var response = await _featureClient.QueryFeaturesAsync(request);

        // Convert back using shared converter
        return response.Features.Select(f => new GeocodingResult
        {
            Address = f.Attributes["address"].StringValue,
            Score = f.Attributes["score"].DoubleValue,
            Location = GrpcConversionHelpers.ToNtsGeometry(f.Geometry)
        });
    }
}
```

### 6. Project References Update

**honua-server projects:**
```xml
<ProjectReference Include="../Honua.Shared/Honua.Shared.csproj" />
```

**MAUI client projects:**
```xml
<ProjectReference Include="../Honua.Shared/Honua.Shared.csproj" />
```

## Benefits

### ✅ Eliminated Code Duplication
- Single source of truth for conversion logic
- Shared domain models prevent drift
- Common validation and utility functions

### ✅ Consistent Behavior
- Identical spatial calculations on client and server
- Same validation rules applied everywhere
- Unified error handling patterns

### ✅ Easier Maintenance
- Bug fixes applied to both client and server
- New features added once, available everywhere
- Simplified testing with shared test utilities

### ✅ Platform Optimization
- Conditional compilation for platform-specific features
- Mobile-optimized implementations when needed
- Server performance optimizations preserved

## Migration Steps

1. **Create Honua.Shared project**
2. **Move GrpcConversionHelpers from server**
3. **Extract domain models to shared**
4. **Update server to use shared library**
5. **Update mobile SDK to use shared library**
6. **Add shared unit tests**
7. **Package as NuGet for distribution**

This approach eliminates duplication while maintaining platform-specific optimizations where needed.