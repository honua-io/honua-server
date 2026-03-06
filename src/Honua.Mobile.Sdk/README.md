# Honua Mobile SDK

Cross-platform .NET MAUI SDK for mobile geospatial applications with offline-first architecture.

## Features

- **Offline-First**: Built for field data collection with GeoPackage local storage
- **Cross-Platform**: Works on iOS, Android, and Windows with native integrations
- **Battery Aware**: Intelligent sync policies and background processing
- **Field Collection**: Optimized for data collection scenarios like utility inspections
- **Progress Reporting**: Real-time sync progress with UI-friendly observable patterns
- **Native Integrations**: Camera, GPS, maps, and AR capabilities

## Quick Start

```csharp
// Configure in MauiProgram.cs
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();
    builder
        .UseMauiApp<App>()
        .AddHonuaMobileClient(options =>
        {
            options.ServerAddress = "https://api.example.com";
            options.ApiKey = "your-api-key";
            options.OfflineDatabase = "fielddata.gpkg";
            options.SyncPolicy = SyncPolicy.WifiOnly;
        });

    return builder.Build();
}

// Use in your ViewModels
public class FieldDataViewModel : ObservableObject
{
    private readonly IHonuaMobileClient _client;

    public FieldDataViewModel(IHonuaMobileClient client)
    {
        _client = client;
    }

    public async Task CollectFeatureAsync()
    {
        var location = await Geolocation.GetLocationAsync();

        var feature = new Feature
        {
            Geometry = new Point(location.Longitude, location.Latitude),
            Attributes = new Dictionary<string, object>
            {
                ["inspection_date"] = DateTime.Now,
                ["inspector"] = Preferences.Get("username", "unknown"),
                ["status"] = "pending"
            }
        };

        // Save offline-first
        await _client.SaveFeatureOfflineAsync(feature);

        // Sync when network is available
        await _client.SyncWhenAvailableAsync();
    }
}
```

## Offline Storage

The SDK uses GeoPackage for standards-compliant offline storage:

```csharp
// Configure offline capabilities
services.AddHonuaMobileClient(options =>
{
    options.OfflineDatabase = "project_data.gpkg";
    options.OfflineMaxFeatures = 50000;
    options.OfflineRetentionDays = 30;
    options.AutoCleanup = true;
});

// Work offline
await client.DownloadAreaAsync(boundingBox, layerIds);
var features = await client.QueryOfflineAsync(query);
await client.SaveOfflineAsync(newFeatures);
```

## Synchronization

Battery-aware sync with configurable policies:

```csharp
// Sync policies
options.SyncPolicy = SyncPolicy.WifiOnly;        // Default for battery life
options.SyncPolicy = SyncPolicy.WifiOrCellular;  // For critical updates
options.SyncPolicy = SyncPolicy.Manual;          // User-controlled

// Monitor sync progress
client.SyncProgress.Subscribe(progress =>
{
    ProgressBar.Progress = progress.Percentage / 100.0;
    StatusLabel.Text = progress.Message;
});
```

## Platform Features

### Camera Integration
```csharp
var photo = await MediaPicker.CapturePhotoAsync();
await client.AttachPhotoToFeatureAsync(feature.Id, photo);
```

### GPS and Location
```csharp
var location = await client.GetHighAccuracyLocationAsync();
var heading = await client.GetCompassHeadingAsync();
```

### Background Sync
```csharp
// Automatically sync in background when conditions are met
await client.EnableBackgroundSyncAsync(TimeSpan.FromHours(4));
```

## License

Licensed under the Apache License, Version 2.0.