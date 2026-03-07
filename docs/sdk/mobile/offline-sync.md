# Offline Synchronization Guide

This guide covers implementing offline-first data synchronization in mobile applications using the Honua Mobile SDK.

## Overview

The Honua Mobile SDK provides comprehensive offline synchronization capabilities designed for field data collection scenarios where network connectivity may be intermittent or unavailable.

## Core Concepts

### Offline-First Architecture

The SDK follows an offline-first approach:
1. **Local Storage**: All data is stored locally first using SQLite
2. **Background Sync**: Automatic synchronization when connectivity is available
3. **Conflict Resolution**: Intelligent conflict resolution for concurrent edits
4. **Progress Tracking**: Real-time sync progress with UI-friendly observables

### Storage Technologies

- **SQLite**: Local database for structured data
- **File System**: Local storage for photos, attachments, and cached maps
- **GeoPackage**: Standards-compliant offline geodatabase format

## Configuration

### Basic Setup

Configure offline capabilities in `MauiProgram.cs`:

```csharp
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();
    builder
        .UseMauiApp<App>()
        .AddHonuaMobile(options =>
        {
            options.ServerAddress = "https://api.example.com";
            options.ApiKey = "your-api-key";

            // Offline configuration
            options.EnableOfflineMode = true;
            options.OfflineDatabase = "fielddata.gpkg";
            options.OfflineMaxFeatures = 50000;
            options.OfflineRetentionDays = 30;
            options.AutoCleanup = true;

            // Sync policies
            options.SyncPolicy = SyncPolicy.WifiPreferred;
            options.BatteryPolicy = BatteryPolicy.Conservative;
        });

    return builder.Build();
}
```

### Sync Policies

Control when synchronization occurs:

```csharp
public enum SyncPolicy
{
    Manual,           // User-initiated only
    WifiOnly,         // Only on WiFi connections
    WifiPreferred,    // WiFi preferred, cellular as fallback
    Any              // Any available connection
}

public enum BatteryPolicy
{
    Conservative,     // Minimal background activity
    Normal,          // Balanced performance and battery
    Performance      // Maximum performance, higher battery usage
}
```

## Data Management

### Local Data Storage

Store data offline-first:

```csharp
public class OfflineDataService
{
    private readonly IHonuaMobileClient _client;

    public OfflineDataService(IHonuaMobileClient client)
    {
        _client = client;
    }

    public async Task SaveFeatureOfflineAsync(Feature feature)
    {
        // Always save locally first
        await _client.SaveFeatureOfflineAsync(feature);

        // Queue for sync when network becomes available
        await _client.QueueForSyncAsync(feature.Id);
    }

    public async Task<IEnumerable<Feature>> QueryOfflineAsync(FeatureQuery query)
    {
        // Query from local database
        return await _client.QueryFeaturesOfflineAsync(query);
    }
}
```

### Data Download

Download data for offline use:

```csharp
[RelayCommand]
public async Task DownloadAreaForOfflineAsync()
{
    try
    {
        // Define area of interest
        var boundingBox = new Envelope
        {
            MinX = -122.5,
            MinY = 37.7,
            MaxX = -122.3,
            MaxY = 37.9
        };

        var layerIds = new[] { 0, 1, 2 }; // Layers to download

        // Download with progress tracking
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadProgressPercent = p.PercentComplete;
            DownloadStatusText = p.StatusMessage;
        });

        await _client.DownloadAreaAsync(
            serviceId: "inspection-service",
            boundingBox: boundingBox,
            layerIds: layerIds,
            progress: progress
        );

        await Shell.Current.DisplayAlert("Success", "Area downloaded for offline use", "OK");
    }
    catch (Exception ex)
    {
        await Shell.Current.DisplayAlert("Error", $"Download failed: {ex.Message}", "OK");
    }
}
```

## Synchronization

### Automatic Sync

Configure automatic synchronization:

```csharp
public class SyncService : ObservableObject
{
    private readonly IHonuaMobileClient _client;
    private readonly IConnectivity _connectivity;

    public SyncService(IHonuaMobileClient client, IConnectivity connectivity)
    {
        _client = client;
        _connectivity = connectivity;

        // Subscribe to connectivity changes
        _connectivity.ConnectivityChanged += OnConnectivityChanged;

        // Enable background sync
        _ = EnableBackgroundSyncAsync();
    }

    private async void OnConnectivityChanged(object sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            await StartSyncAsync();
        }
    }

    private async Task EnableBackgroundSyncAsync()
    {
        await _client.EnableBackgroundSyncAsync(interval: TimeSpan.FromHours(4));
    }
}
```

### Manual Sync

Implement user-initiated sync:

```csharp
[RelayCommand]
public async Task SyncNowAsync()
{
    if (_connectivity.NetworkAccess != NetworkAccess.Internet)
    {
        await Shell.Current.DisplayAlert("No Connection",
            "Internet connection required for sync", "OK");
        return;
    }

    try
    {
        Issyncing = true;

        // Monitor sync progress
        _client.SyncProgress.Subscribe(progress =>
        {
            SyncProgressPercent = progress.PercentComplete;
            SyncStatusText = progress.StatusMessage;
        });

        var result = await _client.SyncAsync();

        await Shell.Current.DisplayAlert("Sync Complete",
            $"Uploaded: {result.UploadedFeatures}, Downloaded: {result.DownloadedFeatures}",
            "OK");
    }
    catch (Exception ex)
    {
        await Shell.Current.DisplayAlert("Sync Error", ex.Message, "OK");
    }
    finally
    {
        IsSyncing = false;
    }
}
```

### Selective Sync

Sync only specific data:

```csharp
public async Task SyncLayerAsync(int layerId)
{
    var context = new SyncContext
    {
        LayerIds = new[] { layerId },
        SyncDirection = SyncDirection.Both,
        ConflictResolution = ConflictResolution.ServerWins
    };

    await _client.SyncAsync(context);
}

public async Task UploadOnlyAsync()
{
    var context = new SyncContext
    {
        SyncDirection = SyncDirection.Upload,
        ConflictResolution = ConflictResolution.ClientWins
    };

    await _client.SyncAsync(context);
}
```

## Conflict Resolution

### Conflict Strategies

Handle data conflicts when multiple users edit the same features:

```csharp
public enum ConflictResolution
{
    ServerWins,       // Server data takes precedence
    ClientWins,       // Local data takes precedence
    MergeAttributes,  // Merge non-conflicting attributes
    UserDecides      // Prompt user to resolve
}
```

### Custom Conflict Resolution

Implement custom conflict resolution logic:

```csharp
public class CustomConflictResolver : IConflictResolver
{
    public async Task<Feature> ResolveConflictAsync(
        Feature localFeature,
        Feature serverFeature,
        ConflictContext context)
    {
        // Custom resolution logic
        if (context.ConflictType == ConflictType.AttributeChange)
        {
            return await MergeAttributesAsync(localFeature, serverFeature);
        }
        else if (context.ConflictType == ConflictType.GeometryChange)
        {
            // Use most recent geometry
            var newerFeature = localFeature.LastModified > serverFeature.LastModified
                ? localFeature
                : serverFeature;
            return newerFeature;
        }

        return serverFeature; // Default to server
    }

    private async Task<Feature> MergeAttributesAsync(Feature local, Feature server)
    {
        var merged = new Feature
        {
            Id = local.Id,
            Geometry = local.Geometry, // Keep local geometry
            Attributes = new Dictionary<string, object>()
        };

        // Merge attributes with local taking precedence
        foreach (var attr in server.Attributes)
        {
            merged.Attributes[attr.Key] = attr.Value;
        }

        foreach (var attr in local.Attributes)
        {
            merged.Attributes[attr.Key] = attr.Value; // Local overwrites server
        }

        return merged;
    }
}
```

## Storage Management

### Cache Management

Manage local storage efficiently:

```csharp
public class CacheManager
{
    private readonly IHonuaMobileClient _client;

    public async Task<StorageInfo> GetStorageInfoAsync()
    {
        return await _client.GetOfflineStorageInfoAsync();
    }

    [RelayCommand]
    public async Task CleanupOldDataAsync()
    {
        var options = new CleanupOptions
        {
            RetentionDays = 30,
            MaxSizeMB = 500,
            PreserveUserData = true
        };

        var cleaned = await _client.CleanupOfflineDataAsync(options);

        await Shell.Current.DisplayAlert("Cleanup Complete",
            $"Freed {cleaned.FreedSpaceMB:F1} MB", "OK");
    }

    [RelayCommand]
    public async Task ClearAllOfflineDataAsync()
    {
        var result = await Shell.Current.DisplayAlert("Confirm",
            "This will delete all offline data. Continue?", "Yes", "No");

        if (result)
        {
            await _client.ClearOfflineDataAsync();
        }
    }
}
```

### Attachment Handling

Manage photos and files offline:

```csharp
public async Task SaveAttachmentOfflineAsync(string featureId, FileResult file)
{
    // Save file locally with reference
    var localPath = await _client.SaveAttachmentOfflineAsync(featureId, file);

    // Update feature with local attachment reference
    var feature = await _client.GetFeatureOfflineAsync(featureId);
    feature.Attributes["photo_path"] = localPath;
    feature.Attributes["photo_synced"] = false;

    await _client.SaveFeatureOfflineAsync(feature);
    await _client.QueueForSyncAsync(featureId);
}
```

## Progress Monitoring

### Sync Progress UI

Display sync progress to users:

```csharp
public class SyncProgressViewModel : ObservableObject
{
    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string statusText = "Ready";

    [ObservableProperty]
    private bool isSyncing;

    public void SubscribeToSyncProgress(IHonuaMobileClient client)
    {
        client.SyncProgress.Subscribe(progress =>
        {
            ProgressPercent = progress.PercentComplete;
            StatusText = progress.StatusMessage;
            IsSyncing = progress.IsActive;
        });
    }
}
```

XAML for progress display:

```xml
<Grid IsVisible="{Binding IsSyncing}">
    <ProgressBar Progress="{Binding ProgressPercent}" />
    <Label Text="{Binding StatusText}" HorizontalOptions="Center" />
</Grid>
```

## Testing

### Offline Testing

Test offline scenarios:

```csharp
[Test]
public async Task OfflineMode_SaveAndQuery_WorksWithoutNetwork()
{
    // Arrange
    var client = CreateOfflineClient();
    var feature = CreateTestFeature();

    // Act - simulate no network
    await client.SetNetworkModeAsync(NetworkMode.Offline);
    await client.SaveFeatureOfflineAsync(feature);

    var results = await client.QueryFeaturesOfflineAsync(new FeatureQuery
    {
        Where = $"OBJECTID = {feature.Id}"
    });

    // Assert
    Assert.AreEqual(1, results.Count());
}
```

### Sync Testing

Test synchronization scenarios:

```csharp
[Test]
public async Task Sync_ConflictResolution_HandlesCorrectly()
{
    // Arrange
    var localFeature = CreateFeatureWithAttributes("local", "value1");
    var serverFeature = CreateFeatureWithAttributes("server", "value2");

    // Act
    var resolver = new CustomConflictResolver();
    var resolved = await resolver.ResolveConflictAsync(
        localFeature, serverFeature, new ConflictContext());

    // Assert
    Assert.AreEqual("value1", resolved.Attributes["local"]); // Local wins
}
```

## Performance Tips

1. **Batch Operations**: Group multiple saves/updates together
2. **Efficient Queries**: Use spatial and attribute indexing
3. **Background Processing**: Perform sync in background threads
4. **Smart Caching**: Cache frequently accessed data
5. **Connection Awareness**: Respect user's data plan and battery

## Troubleshooting

### Common Issues

**Sync failures**
- Check network connectivity
- Verify API credentials
- Review conflict resolution settings

**Storage issues**
- Monitor available storage space
- Implement proper cleanup routines
- Check file permissions

**Performance problems**
- Optimize query filters
- Reduce data transfer size
- Use background processing

## Best Practices

1. **Design for offline-first** from the start
2. **Provide clear sync status** to users
3. **Handle conflicts gracefully** with user input when needed
4. **Implement proper error handling** and retry logic
5. **Test extensively** in offline scenarios
6. **Monitor storage usage** and provide cleanup options

## Related Documentation

- [Mobile SDK Overview](../README.md)
- [Camera Integration](camera-integration.md)
- [Performance Guide](performance.md)
- [Troubleshooting](troubleshooting.md)