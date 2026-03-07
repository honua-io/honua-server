# Honua.Admin.Tools

[![NuGet Version](https://img.shields.io/nuget/v/Honua.Admin.Tools.svg)](https://www.nuget.org/packages/Honua.Admin.Tools)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

**Administrative tooling for Honua geospatial platform in .NET applications.**

Part of the Honua functionality-first architecture:
- **[Runtime SDK](https://github.com/honua-io/honua-server)**: Feature queries, spatial operations, mobile apps
- **Admin Tools** (this package): Service management, bulk operations, user administration

For multi-language admin tooling (JavaScript, Python, CLI), see [honua-admin-tools](https://github.com/honua-io/honua-server-admin).

## Features

- **Service Management**: Deploy, configure, and monitor geospatial services
- **User Administration**: User management with role-based access control
- **Bulk Operations**: Import/export large datasets with progress tracking
- **Blazor Integration**: Pre-built components for administrative UIs
- **Monitoring**: Performance metrics and health monitoring
- **Diagnostics**: Enhanced error handling and troubleshooting tools

## Quick Start

```csharp
// Register in Blazor Server application
services.AddHonuaAdminClient(options =>
{
    options.BaseUrl = "https://api.honua.com";
    options.ApiKey = "admin-api-key";
    options.EnableRealTimeUpdates = true;
});

// Use in Blazor components
@inject IHonuaAdminClient AdminClient

<AdminServiceList Services="@services" OnServiceSelected="@OnServiceSelected" />

@code {
    private IEnumerable<ServiceInfo> services = Array.Empty<ServiceInfo>();

    protected override async Task OnInitializedAsync()
    {
        services = await AdminClient.GetServicesAsync();
    }

    private async Task OnServiceSelected(string serviceId)
    {
        var details = await AdminClient.GetServiceDetailsAsync(serviceId);
        // Handle service selection
    }
}
```

## Service Management

Deploy and manage geospatial services:

```csharp
// Deploy a new service
var serviceConfig = new ServiceConfiguration
{
    Name = "ParcelData",
    DataSource = "postgresql://server/gisdb",
    Layers = new[]
    {
        new LayerConfiguration
        {
            Name = "parcels",
            TableName = "public.parcels",
            GeometryColumn = "geom",
            SpatialReference = 4326
        }
    }
};

var result = await client.DeployServiceAsync(serviceConfig);

// Monitor service health
var health = await client.GetServiceHealthAsync(serviceId);
var metrics = await client.GetServiceMetricsAsync(serviceId, TimeSpan.FromHours(24));
```

## Bulk Operations

Efficient handling of large datasets:

```csharp
// Bulk import with progress tracking
var importOptions = new BulkImportOptions
{
    ServiceId = "my-service",
    LayerId = 0,
    DataFormat = DataFormat.GeoJSON,
    BatchSize = 1000,
    ValidationMode = ValidationMode.Strict
};

await foreach (var progress in client.ImportDataAsync(fileStream, importOptions))
{
    Console.WriteLine($"{progress.Percentage:F1}% - {progress.Message}");
}

// Bulk export
var exportOptions = new BulkExportOptions
{
    Format = DataFormat.Shapefile,
    IncludeGeometry = true,
    SpatialReference = 4326
};

using var exportStream = await client.ExportServiceDataAsync(serviceId, exportOptions);
```

## Architecture

This .NET admin package is part of the larger [honua-admin-tools](https://github.com/honua-io/honua-server-admin) ecosystem:

| Technology | Package | Purpose |
|------------|---------|---------|
| **.NET** | Honua.Admin.Tools | Blazor components, admin clients |
| **JavaScript** | @honua/admin-tools | Web admin interfaces |
| **Python** | honua-admin | Scripting and automation |
| **CLI** | @honua/cli | Command-line operations |

## Multi-Language Support

For admin operations in other languages:

```bash
# JavaScript/TypeScript
npm install @honua/admin-tools

# Python
pip install honua-admin

# CLI tool
npm install -g @honua/cli
```

## Documentation

See the [honua-admin-tools repository](https://github.com/honua-io/honua-server-admin) for complete multi-language documentation and examples.

## License

Licensed under the Apache License, Version 2.0.