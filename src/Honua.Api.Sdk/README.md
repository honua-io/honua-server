# Honua.Core.Sdk

[![NuGet Version](https://img.shields.io/nuget/v/Honua.Core.Sdk.svg)](https://www.nuget.org/packages/Honua.Core.Sdk)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

**Runtime .NET library for building production applications with Honua geospatial services.**

Part of the Honua functionality-first architecture:
- **Runtime SDK** (this package): Feature queries, spatial operations, mobile apps
- **[Admin Tools](https://github.com/honua-io/honua-server-admin)**: Service management, bulk operations, user administration

## Features

- **Runtime-Focused**: Lightweight library optimized for production applications
- **gRPC-First**: Native gRPC protocol support with modern performance
- **Feature Queries**: Rich spatial filtering and geospatial operations
- **Mobile-Ready**: Optimized for .NET MAUI mobile applications
- **Cross-Platform**: Supports .NET 10.0, Android, iOS, and Windows

## Quick Start

### Server Applications

```csharp
// Register with DI container
services.AddHonuaFeatureClient(options =>
{
    options.BaseUrl = "https://api.honua.com";
    options.ApiKey = "your-api-key";
});

// Use in your service
public class MyGeoService(IHonuaFeatureClient client)
{
    public async Task<QueryResult<Feature>> GetFeaturesAsync(string serviceId, int layerId)
    {
        var query = new FeatureQuery
        {
            Where = "population > 50000",
            OutFields = "*",
            ReturnGeometry = true
        };

        return await client.QueryFeaturesAsync(serviceId, layerId, query);
    }
}
```

### Mobile Applications

```csharp
// In MauiProgram.cs
builder.Services.AddHonuaFeatureClient(options =>
{
    options.BaseUrl = "https://api.honua.com";
    options.ApiKey = "your-api-key";
    options.EnableOfflineSync = true;
});

// In your pages
public partial class FieldDataPage : ContentPage
{
    private readonly IHonuaFeatureClient _client;

    public FieldDataPage(IHonuaFeatureClient client)
    {
        _client = client;
        InitializeComponent();
    }

    private async Task LoadNearbyFeatures()
    {
        var location = await Geolocation.GetLocationAsync();
        var buffer = GeometryHelper.CreateBuffer(location, 1000); // 1km radius

        var query = new FeatureQuery
        {
            SpatialFilter = new SpatialFilter
            {
                FilterGeometry = buffer,
                Relationship = SpatialRelationship.Intersects
            }
        };

        var features = await _client.QueryFeaturesAsync("field-assets", 0, query);
        DisplayFeatures(features);
    }
}
```

## Architecture

This SDK follows the **functionality-first architecture**:

| Use Case | Package |
|----------|---------|
| **Feature Queries** | Honua.Core.Sdk |
| **Mobile Apps** | Honua.Core.Sdk |
| **Service Management** | [honua-admin-tools](https://github.com/honua-io/honua-server-admin) |
| **Bulk Operations** | [honua-admin-tools](https://github.com/honua-io/honua-server-admin) |

## Documentation

See the main [Honua Core SDK repository](https://github.com/honua-io/honua-server) for complete documentation, examples, and API reference.

## License

Licensed under the Apache License, Version 2.0.