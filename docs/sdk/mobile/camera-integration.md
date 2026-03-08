# Camera Integration Guide

This guide covers integrating camera functionality into mobile applications using the Honua Mobile SDK.

## Overview

The Honua Mobile SDK provides cross-platform camera integration for field data collection scenarios. This includes photo capture, gallery selection, and automatic attachment to geospatial features.

## Prerequisites

- .NET MAUI workload installed
- Platform-specific permissions configured
- Honua Mobile SDK integrated into your project

## Platform-Specific Setup

### Android

Add required permissions to `AndroidManifest.xml`:

```xml
<uses-permission android:name="android.permission.CAMERA" />
<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
<uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
```

### iOS

Add usage descriptions to `Info.plist`:

```xml
<key>NSCameraUsageDescription</key>
<string>This app needs access to the camera to capture field data photos</string>
<key>NSPhotoLibraryUsageDescription</key>
<string>This app needs access to the photo library to select images for field data</string>
```

## Implementation

### Basic Camera Service

The Mobile SDK provides the `ICameraService` interface for camera operations:

```csharp
public interface ICameraService
{
    Task<FileResult?> CapturePhotoAsync(CameraOptions? options = null);
    Task<FileResult?> PickPhotoAsync();
    Task<IEnumerable<FileResult>> PickMultiplePhotosAsync(int maxCount = 10);
    Task<bool> IsCameraAvailableAsync();
}
```

### Service Registration

Register the camera service in `MauiProgram.cs`:

```csharp
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();
    builder
        .UseMauiApp<App>()
        .AddHonuaMobile(options =>
        {
            // Configure your options
        })
        .ConfigureFonts(fonts =>
        {
            fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
        });

    // Camera service is automatically registered
    return builder.Build();
}
```

### Capturing Photos

Basic photo capture in a view model:

```csharp
public class DataCollectionViewModel : ObservableObject
{
    private readonly ICameraService _cameraService;

    public DataCollectionViewModel(ICameraService cameraService)
    {
        _cameraService = cameraService;
    }

    [RelayCommand]
    public async Task CapturePhotoAsync()
    {
        try
        {
            if (!await _cameraService.IsCameraAvailableAsync())
            {
                await Shell.Current.DisplayAlert("Error", "Camera not available", "OK");
                return;
            }

            var photo = await _cameraService.CapturePhotoAsync(new CameraOptions
            {
                AllowCropping = true,
                Quality = 0.8f,
                CompressionQuality = 85
            });

            if (photo != null)
            {
                await ProcessPhotoAsync(photo);
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to capture photo: {ex.Message}", "OK");
        }
    }

    private async Task ProcessPhotoAsync(FileResult photo)
    {
        // Process the captured photo
        // Attach to current feature, save locally, etc.
    }
}
```

### Advanced Configuration

Configure camera options for different use cases:

```csharp
public class CameraOptions
{
    public bool AllowCropping { get; set; } = false;
    public float Quality { get; set; } = 1.0f; // 0.0 to 1.0
    public int CompressionQuality { get; set; } = 95; // 1-100
    public PhotoSize PhotoSize { get; set; } = PhotoSize.Medium;
    public CameraDevice RequestedCamera { get; set; } = CameraDevice.Rear;
}

// For field data collection - optimized for size and quality
var fieldDataOptions = new CameraOptions
{
    Quality = 0.8f,
    CompressionQuality = 85,
    PhotoSize = PhotoSize.Medium,
    AllowCropping = true
};

// For documentation - highest quality
var documentationOptions = new CameraOptions
{
    Quality = 1.0f,
    CompressionQuality = 95,
    PhotoSize = PhotoSize.Large,
    AllowCropping = false
};
```

### Integration with Honua Features

Attach photos to geospatial features:

```csharp
[RelayCommand]
public async Task AttachPhotoToFeatureAsync()
{
    var photo = await _cameraService.CapturePhotoAsync();
    if (photo == null) return;

    // Get current location
    var location = await Geolocation.GetLocationAsync();

    // Create feature with photo
    var feature = new Feature
    {
        Geometry = new Point(location.Longitude, location.Latitude),
        Attributes = new Dictionary<string, object>
        {
            ["photo_path"] = photo.FullPath,
            ["photo_name"] = photo.FileName,
            ["capture_time"] = DateTime.UtcNow,
            ["inspector"] = "Current User"
        }
    };

    // Save to Honua service
    await _honuaClient.SaveFeatureAsync("your-service-id", 0, feature);
}
```

## Error Handling

Handle common camera-related errors:

```csharp
public async Task<FileResult?> SafeCapturePhotoAsync()
{
    try
    {
        return await _cameraService.CapturePhotoAsync();
    }
    catch (PermissionException)
    {
        await Shell.Current.DisplayAlert("Permission Required",
            "Camera permission is required to take photos.", "OK");
        return null;
    }
    catch (FeatureNotSupportedException)
    {
        await Shell.Current.DisplayAlert("Not Supported",
            "Camera is not supported on this device.", "OK");
        return null;
    }
    catch (Exception ex)
    {
        await Shell.Current.DisplayAlert("Error",
            $"An error occurred: {ex.Message}", "OK");
        return null;
    }
}
```

## Performance Considerations

### Memory Management

Large photos can consume significant memory. Consider:

1. **Compression**: Use appropriate quality settings
2. **Resizing**: Resize images for your use case
3. **Disposal**: Properly dispose of streams

```csharp
public async Task<byte[]> GetCompressedPhotoAsync(FileResult photo)
{
    using var stream = await photo.OpenReadAsync();
    using var image = await Image.LoadAsync(stream);

    // Resize if too large
    if (image.Width > 1920 || image.Height > 1920)
    {
        var aspectRatio = (float)image.Width / image.Height;
        var newWidth = image.Width > image.Height ? 1920 : (int)(1920 * aspectRatio);
        var newHeight = image.Height > image.Width ? 1920 : (int)(1920 / aspectRatio);

        image.Mutate(x => x.Resize(newWidth, newHeight));
    }

    using var outputStream = new MemoryStream();
    await image.SaveAsJpegAsync(outputStream, new JpegEncoder { Quality = 85 });
    return outputStream.ToArray();
}
```

### Background Processing

For multiple photos or large uploads:

```csharp
[RelayCommand]
public async Task ProcessMultiplePhotosAsync()
{
    var photos = await _cameraService.PickMultiplePhotosAsync(maxCount: 5);

    // Process in background
    _ = Task.Run(async () =>
    {
        foreach (var photo in photos)
        {
            try
            {
                await ProcessPhotoInBackgroundAsync(photo);
            }
            catch (Exception ex)
            {
                // Log error, continue with next photo
                Debug.WriteLine($"Failed to process {photo.FileName}: {ex.Message}");
            }
        }
    });
}
```

## Testing

### Unit Tests

Test camera service integration:

```csharp
[Test]
public async Task CameraService_CapturePhoto_ReturnsFileResult()
{
    // Arrange
    var mockCameraService = new Mock<ICameraService>();
    mockCameraService.Setup(x => x.CapturePhotoAsync(It.IsAny<CameraOptions>()))
                    .ReturnsAsync(new MockFileResult("test.jpg"));

    // Act
    var result = await mockCameraService.Object.CapturePhotoAsync();

    // Assert
    Assert.IsNotNull(result);
    Assert.AreEqual("test.jpg", result.FileName);
}
```

### Device Testing

Test on real devices with different camera configurations:

- Different camera resolutions
- Front vs. rear camera
- Various lighting conditions
- Different storage availability

## Troubleshooting

### Common Issues

**Camera permission denied**
- Ensure permissions are declared in platform manifests
- Request permissions at runtime before accessing camera

**Out of memory exceptions**
- Reduce photo quality/size
- Process photos in background
- Dispose of streams properly

**Photos not saving**
- Check storage permissions
- Verify storage space availability
- Ensure proper file paths

### Debugging

Enable detailed logging:

```csharp
services.AddLogging(builder =>
{
    builder.AddDebug();
    builder.SetMinimumLevel(LogLevel.Debug);
});
```

## Best Practices

1. **Always check permissions** before camera operations
2. **Handle device limitations** gracefully
3. **Optimize photo size** for your use case
4. **Provide visual feedback** during capture/processing
5. **Test on multiple devices** and orientations
6. **Implement proper error handling** for all scenarios

## Related Documentation

- [Mobile SDK Overview](../README.md)
- [Offline Sync Guide](offline-sync.md)
- [Performance Optimization](performance.md)
- [Troubleshooting Guide](troubleshooting.md)