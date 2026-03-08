# Troubleshooting Guide

This guide helps resolve common issues when developing with the Honua Mobile SDK.

## Build and Deployment Issues

### MAUI Workload Installation

**Problem**: Build errors related to missing MAUI workloads

**Solution**:
```bash
# Install MAUI workloads
dotnet workload install maui

# Update existing workloads
dotnet workload update

# Verify installation
dotnet workload list
```

### Package Installation Errors

**Problem**: "No packages exist with this id" when adding Honua packages

**Solutions**:
1. Check package version compatibility:
   ```bash
   dotnet list package --vulnerable
   dotnet list package --deprecated
   ```

2. Clear NuGet cache:
   ```bash
   dotnet nuget locals all --clear
   ```

3. Verify package source configuration in `NuGet.config`:
   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
     </packageSources>
   </configuration>
   ```

### Platform-Specific Build Errors

**Android Build Failures**:
```bash
# Clean and rebuild
dotnet clean
dotnet build -f net10.0-android

# Check Android SDK path
echo $ANDROID_SDK_ROOT
```

**iOS Build Failures**:
```bash
# Clean and rebuild
dotnet clean
dotnet build -f net10.0-ios

# Verify Xcode installation
xcode-select --print-path
```

## Runtime Issues

### gRPC Connection Failures

**Problem**: Cannot connect to Honua server

**Diagnostic Steps**:
```csharp
public async Task DiagnoseConnectionAsync()
{
    try
    {
        // Test basic connectivity
        var client = new HttpClient();
        var response = await client.GetAsync("https://your-server.com/health");

        Debug.WriteLine($"HTTP Status: {response.StatusCode}");

        // Test gRPC connection
        var channel = GrpcChannel.ForAddress("https://your-server.com");
        var grpcClient = new FeatureService.FeatureServiceClient(channel);

        var request = new GetServiceInfoRequest { ServiceId = "test" };
        var grpcResponse = await grpcClient.GetServiceInfoAsync(request);

        Debug.WriteLine("gRPC connection successful");
    }
    catch (HttpRequestException ex)
    {
        Debug.WriteLine($"HTTP Error: {ex.Message}");
    }
    catch (RpcException ex)
    {
        Debug.WriteLine($"gRPC Error: {ex.Status.Detail}");
    }
}
```

**Common Solutions**:
1. Verify server address and port
2. Check SSL/TLS certificate issues
3. Ensure API key is correctly configured
4. Test network connectivity

### Permission Issues

**Problem**: Camera or location permissions denied

**Android Solution**:
```csharp
public async Task<bool> RequestCameraPermissionAsync()
{
    var status = await Permissions.CheckStatusAsync<Permissions.Camera>();

    if (status == PermissionStatus.Denied)
    {
        status = await Permissions.RequestAsync<Permissions.Camera>();
    }

    if (status != PermissionStatus.Granted)
    {
        await Shell.Current.DisplayAlert("Permission Required",
            "Camera permission is required for this feature", "OK");
        return false;
    }

    return true;
}
```

**iOS Solution**:
Check `Info.plist` usage descriptions and request permissions at runtime:
```csharp
public async Task<bool> CheckLocationPermissionAsync()
{
    var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

    if (status == PermissionStatus.Denied)
    {
        // Guide user to settings
        await Shell.Current.DisplayAlert("Permission Required",
            "Please enable location access in Settings", "OK");
        AppInfo.ShowSettingsUI();
        return false;
    }

    return status == PermissionStatus.Granted;
}
```

### Memory Issues

**Problem**: OutOfMemoryException when handling large datasets or images

**Solutions**:

1. **Implement proper disposal**:
```csharp
public async Task ProcessLargeImageAsync(FileResult imageFile)
{
    using var stream = await imageFile.OpenReadAsync();
    using var image = Image.Load(stream);

    // Process image
    image.Mutate(x => x.Resize(1920, 1080));

    using var outputStream = new MemoryStream();
    image.SaveAsJpeg(outputStream, new JpegEncoder { Quality = 85 });

    // Memory is automatically freed when using blocks exit
}
```

2. **Implement pagination for large datasets**:
```csharp
public async Task<List<Feature>> LoadFeaturesPagedAsync(int page, int pageSize = 50)
{
    var query = new FeatureQuery
    {
        Where = "1=1",
        ResultOffset = page * pageSize,
        ResultRecordCount = pageSize
    };

    var result = await _client.QueryFeaturesAsync("service-id", 0, query);
    return result.Features.ToList();
}
```

3. **Use background processing**:
```csharp
public async Task ProcessLargeDatasetAsync()
{
    await Task.Run(async () =>
    {
        // Process data in background thread
        var features = await LoadAllFeaturesAsync();

        // Update UI on main thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateUI(features);
        });
    });
}
```

## Data and Synchronization Issues

### Sync Failures

**Problem**: Data not synchronizing between device and server

**Diagnostic Code**:
```csharp
public async Task DiagnoseSyncIssuesAsync()
{
    var storageInfo = await _client.GetOfflineStorageInfoAsync();

    Debug.WriteLine($"Pending uploads: {storageInfo.PendingUploads}");
    Debug.WriteLine($"Last sync: {storageInfo.LastSyncTime}");
    Debug.WriteLine($"Sync status: {storageInfo.SyncStatus}");

    // Check specific feature sync status
    var feature = await _client.GetFeatureOfflineAsync("feature-id");
    Debug.WriteLine($"Feature sync status: {feature.SyncStatus}");
    Debug.WriteLine($"Last modified: {feature.LastModified}");
}
```

**Common Solutions**:
1. Check network connectivity
2. Verify API credentials
3. Check for data conflicts
4. Ensure sufficient storage space

### Database Corruption

**Problem**: SQLite database corruption errors

**Solution**:
```csharp
public async Task HandleDatabaseCorruptionAsync()
{
    try
    {
        // Attempt to repair database
        await _client.RepairOfflineDatabaseAsync();
    }
    catch (DatabaseCorruptedException)
    {
        // Backup user data if possible
        var userData = await _client.ExportUserDataAsync();

        // Clear corrupted database
        await _client.ClearOfflineDataAsync();

        // Reinitialize and restore user data
        await _client.InitializeOfflineStorageAsync();
        await _client.ImportUserDataAsync(userData);

        // Re-download server data
        await _client.SyncAsync();
    }
}
```

## UI and Performance Issues

### Slow List Performance

**Problem**: Large lists causing UI lag

**Solution**: Implement virtualization and lazy loading:

```csharp
public class VirtualizedFeatureList : ObservableObject
{
    private const int PageSize = 50;
    private readonly ObservableCollection<Feature> _features = new();

    public ObservableCollection<Feature> Features => _features;

    [RelayCommand]
    public async Task LoadMoreFeaturesAsync()
    {
        if (IsLoading) return;

        IsLoading = true;

        try
        {
            var features = await LoadFeaturesPagedAsync(_features.Count / PageSize, PageSize);

            foreach (var feature in features)
            {
                _features.Add(feature);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
```

### Map Rendering Issues

**Problem**: Maps not displaying or poor performance

**Solutions**:

1. **Check map configuration**:
```csharp
// Ensure map is properly configured
<maps:Map x:Name="FieldMap"
          MapType="Street"
          IsShowingUser="True"
          HasZoomEnabled="True"
          HasScrollEnabled="True" />
```

2. **Optimize pin display**:
```csharp
public void UpdateMapPins(IEnumerable<Feature> features)
{
    // Clear existing pins efficiently
    FieldMap.Pins.Clear();

    // Add new pins in batches
    var pins = features.Take(100).Select(CreatePin);

    foreach (var pin in pins)
    {
        FieldMap.Pins.Add(pin);
    }
}
```

3. **Use clustering for many points**:
```csharp
public void SetupMapClustering()
{
    // Configure clustering for performance
    FieldMap.EnableClustering = true;
    FieldMap.ClusterThreshold = 20;
}
```

## Debugging Techniques

### Enable Detailed Logging

```csharp
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();

    builder.Logging.AddDebug();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);

    builder.Services.AddHonuaMobile(options =>
    {
        options.EnableDetailedLogging = true;
        options.LogLevel = LogLevel.Debug;
    });

    return builder.Build();
}
```

### Network Traffic Monitoring

```csharp
public class NetworkMonitor
{
    public void LogNetworkActivity()
    {
        // Monitor network requests
        var handler = new LoggingHandler();
        var httpClient = new HttpClient(handler);

        // Use with gRPC channel
        var channel = GrpcChannel.ForAddress("https://api.example.com",
            new GrpcChannelOptions { HttpHandler = handler });
    }
}

public class LoggingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Debug.WriteLine($"Request: {request.Method} {request.RequestUri}");

        var response = await base.SendAsync(request, cancellationToken);

        Debug.WriteLine($"Response: {response.StatusCode}");

        return response;
    }
}
```

### Feature State Debugging

```csharp
public void DebugFeatureState(Feature feature)
{
    Debug.WriteLine($"Feature ID: {feature.Id}");
    Debug.WriteLine($"Geometry Type: {feature.Geometry?.GeometryType}");
    Debug.WriteLine($"Attributes Count: {feature.Attributes?.Count}");
    Debug.WriteLine($"Last Modified: {feature.LastModified}");
    Debug.WriteLine($"Sync Status: {feature.SyncStatus}");

    if (feature.Attributes != null)
    {
        foreach (var attr in feature.Attributes)
        {
            Debug.WriteLine($"  {attr.Key}: {attr.Value}");
        }
    }
}
```

## Common Error Messages

### "Unable to resolve service for type"

**Cause**: Dependency injection not properly configured

**Solution**:
```csharp
// Ensure all services are registered
builder.Services.AddHonuaMobile(options => { ... });

// For custom services
builder.Services.AddSingleton<IMyService, MyService>();
```

### "The request was aborted: The request was canceled"

**Cause**: Request timeout or cancellation

**Solution**:
```csharp
// Configure longer timeout
var channel = GrpcChannel.ForAddress("https://api.example.com", new GrpcChannelOptions
{
    HttpHandler = new HttpClientHandler(),
    MaxReceiveMessageSize = 4 * 1024 * 1024, // 4MB
    MaxSendMessageSize = 4 * 1024 * 1024,
});

// Use cancellation tokens properly
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await _client.QueryFeaturesAsync("service", 0, query, cts.Token);
```

### "SSL connection could not be established"

**Cause**: SSL/TLS certificate issues

**Solution**:
```csharp
// For development only - bypass SSL validation
var handler = new HttpClientHandler();
#if DEBUG
handler.ServerCertificateCustomValidationCallback =
    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#endif

var channel = GrpcChannel.ForAddress("https://localhost:5001",
    new GrpcChannelOptions { HttpHandler = handler });
```

## Getting Help

### Information to Collect

When reporting issues, include:

1. **Environment Information**:
```csharp
public string GetEnvironmentInfo()
{
    return $"""
        Platform: {DeviceInfo.Platform}
        Version: {DeviceInfo.VersionString}
        Model: {DeviceInfo.Model}
        SDK Version: {typeof(IHonuaMobileClient).Assembly.GetName().Version}
        .NET Version: {Environment.Version}
        """;
}
```

2. **Error Details**:
   - Full exception message and stack trace
   - Steps to reproduce
   - Expected vs. actual behavior

3. **Log Files**:
   - Enable detailed logging
   - Include relevant log entries

### Support Channels

- **GitHub Issues**: For bugs and feature requests
- **Documentation**: Latest documentation and examples
- **Community Forums**: Community-driven support and discussions

## Prevention Strategies

1. **Implement comprehensive error handling**
2. **Use proper disposal patterns**
3. **Test on multiple devices and platforms**
4. **Monitor performance metrics**
5. **Keep SDK and dependencies updated**
6. **Follow platform-specific guidelines**

## Related Documentation

- [Mobile SDK Overview](../README.md)
- [Camera Integration](camera-integration.md)
- [Offline Sync](offline-sync.md)
- [Performance Guide](performance.md)