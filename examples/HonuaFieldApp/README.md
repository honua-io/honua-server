# Honua Field Data Collection App Template

This is a production-ready template for creating mobile field data collection applications using the Honua Mobile SDK.

## Features

- **Real-time gRPC Communication**: Connect to Honua server for live data synchronization
- **GPS Location Services**: Capture precise location data for field observations
- **Offline-First Architecture**: Work seamlessly without internet connectivity
- **Camera Integration**: Capture photos and attach them to field data
- **Dynamic Form Generation**: Configurable forms for different data collection needs
- **Performance Monitoring**: Built-in performance tracking and optimization
- **Cross-Platform**: Native iOS and Android support using .NET MAUI

## Prerequisites

- .NET 10 SDK
- Visual Studio 2022 (17.13+) with MAUI workloads
- Android SDK for Android development
- Xcode for iOS development (macOS only)

## Quick Start

1. **Install MAUI Workloads** (if not already installed):
   ```bash
   dotnet workload install maui
   ```

2. **Configure Server Connection**:
   Edit `MauiProgram.cs` and update the server configuration:
   ```csharp
   builder.Services.AddHonuaMobile(options =>
   {
       options.ServerAddress = "https://your-honua-server.com"; // Update this
       options.ApiKey = "your-api-key"; // Configure authentication

       // Mobile-optimized settings
       options.RequestTimeout = TimeSpan.FromSeconds(30);
       options.EnableOfflineMode = true;
       options.OfflineDatabase = "honua_offline.db";
   });
   ```

3. **Build and Run**:
   ```bash
   # For Android
   dotnet build -f net10.0-android
   dotnet run -f net10.0-android

   # For iOS (macOS only)
   dotnet build -f net10.0-ios
   dotnet run -f net10.0-ios
   ```

## Project Structure

```
HonuaFieldApp/
├── Services/                          # Application services
│   ├── ICameraService.cs              # Camera functionality interface
│   ├── IFormDataService.cs            # Form data management interface
│   ├── IGpsLocationService.cs         # GPS location services interface
│   ├── IMapRenderingService.cs        # Map rendering interface
│   └── IPerformanceMonitorService.cs  # Performance monitoring interface
├── ViewModels/                        # MVVM view models
│   ├── DataCollectionViewModel.cs     # Data collection page logic
│   └── MapPageViewModel.cs            # Map page logic
├── Views/                             # XAML views (implement as needed)
├── App.xaml.cs                        # Application entry point
├── AppShell.xaml.cs                   # Navigation shell
├── MauiProgram.cs                     # Dependency injection setup
└── HonuaFieldApp.csproj              # Project configuration
```

## Key Components

### 1. Mobile SDK Integration

The template uses the `Honua.Mobile.Sdk` package which provides:
- Offline-first data synchronization
- gRPC client for Honua server communication
- Mobile-optimized performance policies
- Cross-platform storage with Entity Framework Core SQLite

### 2. Service Layer

Modular service interfaces allow for:
- **Camera Services**: Photo capture and gallery selection
- **GPS Services**: Location tracking and accuracy management
- **Map Services**: Feature rendering and spatial queries
- **Form Services**: Dynamic form generation and validation
- **Performance Services**: Memory and network monitoring

### 3. MVVM Architecture

Clean separation of concerns with:
- **Models**: Domain entities from Honua.Core
- **ViewModels**: UI logic and data binding using CommunityToolkit.Mvvm
- **Views**: XAML interfaces (implement based on your UI framework)

## Configuration Options

### Mobile Client Options

```csharp
public class HonuaMobileClientOptions
{
    public string ServerAddress { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool EnableOfflineMode { get; set; } = true;
    public string OfflineDatabase { get; set; } = "honua_offline.db";
}
```

### Network and Battery Policies

```csharp
// Network policy options
NetworkPolicy.WifiOnly          // Only sync on WiFi
NetworkPolicy.WifiPreferred     // Prefer WiFi, fallback to cellular
NetworkPolicy.Any              // Use any available connection

// Battery policy options
BatteryPolicy.Conservative      // Minimal background activity
BatteryPolicy.Normal           // Balanced performance and battery
BatteryPolicy.Performance      // Maximum performance, higher battery usage
```

## Implementation Examples

### GPS Location Collection

```csharp
var location = await _gpsService.GetCurrentLocationAsync(
    new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(10)));

if (location != null)
{
    // Use location for feature geometry
    var point = geometryFactory.CreatePoint(
        new Coordinate(location.Longitude, location.Latitude));
}
```

### Photo Capture

```csharp
var photo = await _cameraService.CapturePhotoAsync();
if (photo != null)
{
    // Attach photo to feature attributes
    formData["Photo"] = photo.FullPath;
}
```

### Offline Data Sync

```csharp
var context = new MobileContext
{
    AllowOffline = true,
    NetworkPolicy = NetworkPolicy.WifiPreferred,
    BatteryPolicy = BatteryPolicy.Normal
};

var result = await _honuaClient.QueryFeaturesAsync(
    serviceId, layerId, query, context);
```

## Platform-Specific Considerations

### Android
- Requires location and camera permissions in `AndroidManifest.xml`
- Support for Android 5.0+ (API level 21)
- Uses Google Play Services for location accuracy

### iOS
- Requires privacy usage descriptions in `Info.plist`
- Support for iOS 11.0+
- Uses Core Location for GPS services

## Customization

### Adding Custom Services

1. Define your service interface:
   ```csharp
   public interface IMyCustomService
   {
       Task<MyResult> DoSomethingAsync();
   }
   ```

2. Implement the service:
   ```csharp
   public class MyCustomService : IMyCustomService
   {
       public async Task<MyResult> DoSomethingAsync()
       {
           // Implementation
       }
   }
   ```

3. Register in `MauiProgram.cs`:
   ```csharp
   builder.Services.AddTransient<IMyCustomService, MyCustomService>();
   ```

### Extending Form Definitions

Create custom form field types by extending the `FormField` class and implementing custom validation logic in `IFormDataService`.

## Deployment

### Android Deployment

1. Create a signed APK:
   ```bash
   dotnet publish -f net10.0-android -c Release
   ```

2. The APK will be generated in `bin/Release/net10.0-android/publish/`

### iOS Deployment

1. Archive for App Store:
   ```bash
   dotnet publish -f net10.0-ios -c Release
   ```

2. Use Xcode to upload to App Store Connect

## Troubleshooting

### Common Issues

1. **"No packages exist with this id"**
   - Ensure all package versions are compatible with .NET 10
   - Check that MAUI workloads are properly installed

2. **gRPC Connection Failures**
   - Verify server address and API key configuration
   - Check network connectivity and firewall settings

3. **Permission Denied Errors**
   - Add required permissions to platform-specific manifests
   - Request permissions at runtime before using services

4. **Offline Database Issues**
   - Ensure Entity Framework migrations are applied
   - Check database file permissions and storage availability

## Support

For additional help and documentation:
- [Honua Documentation](https://honua.io/docs)
- [.NET MAUI Documentation](https://docs.microsoft.com/dotnet/maui/)
- [GitHub Issues](https://github.com/honua-io/honua-server/issues)

## License

This template is released under the Apache 2.0 License. See the LICENSE file for details.