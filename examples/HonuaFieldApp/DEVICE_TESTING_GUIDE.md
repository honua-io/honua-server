# Honua Field App - Device Testing Guide

## Overview

This document provides comprehensive instructions for testing the Honua Field Data Collection app on real iOS and Android devices. The app demonstrates production-ready mobile SDK capabilities including gRPC connectivity, GPS tracking, offline sync, and performance monitoring.

## Prerequisites

### Development Environment Setup

#### For iOS Testing
1. **macOS with Xcode 15+**
2. **iOS Developer Account** (free or paid)
3. **Physical iOS device** (iPhone 12+ recommended, iOS 15+)
4. **USB cable** for device connection

#### For Android Testing
1. **Android Studio 2024.1+**
2. **Physical Android device** (API 26+, Android 8.0+)
3. **USB cable** with data transfer capability
4. **Developer options enabled** on Android device

### Server Configuration
1. **Running Honua server instance** (local or remote)
2. **gRPC services enabled** in server configuration
3. **Test data** available in target service/layers
4. **Network connectivity** between device and server

## iOS Device Testing

### 1. Project Configuration

```bash
# Navigate to project directory
cd /path/to/honua-server/examples/HonuaFieldApp

# Clean and rebuild for iOS
dotnet clean
dotnet build -f net10.0-ios --configuration Release
```

### 2. iOS Deployment Setup

#### Xcode Configuration
1. Open project in Xcode: `open Platforms/iOS/HonuaFieldApp.xcodeproj`
2. Configure **Team ID** in project settings
3. Set up **App ID** with location and camera capabilities
4. Configure **Provisioning Profile** for device testing

#### Signing & Provisioning
```bash
# Check available provisioning profiles
security find-identity -v -p codesigning

# Update Info.plist with correct Bundle Identifier
# Ensure location and camera permissions are properly declared
```

### 3. Device Installation

#### Option A: Xcode Deployment
1. Connect iOS device via USB
2. Select device in Xcode device list
3. Build and run project (`Cmd+R`)
4. Trust developer certificate on device (Settings > General > VPN & Device Management)

#### Option B: CLI Deployment
```bash
# Deploy to connected iOS device
dotnet build -f net10.0-ios --configuration Release /p:RuntimeIdentifier=ios-arm64
dotnet run --project HonuaFieldApp.csproj -f net10.0-ios --configuration Release
```

### 4. iOS Testing Scenarios

#### GPS Accuracy Testing
- **Location**: Test outdoors with clear sky view
- **Accuracy Check**: Verify GPS accuracy ≤10 meters
- **Background Tracking**: Test location updates while app backgrounded
- **Battery Impact**: Monitor battery drain during GPS tracking

#### Camera Integration
- **Photo Capture**: Test camera functionality and image quality
- **Geotagging**: Verify GPS coordinates embedded in EXIF data
- **Storage**: Confirm photos saved correctly with metadata

#### Performance Validation
- **Map Rendering**: Measure time to render 1000+ features
- **Memory Usage**: Monitor memory consumption (<500MB target)
- **Network Efficiency**: Test gRPC vs REST performance
- **UI Responsiveness**: Ensure 60fps during map operations

## Android Device Testing

### 1. Android Configuration

```bash
# Build for Android
dotnet build -f net10.0-android --configuration Release

# Install Android SDK tools
dotnet workload install android
```

### 2. Device Setup

#### Enable Developer Options
1. Go to **Settings > About Phone**
2. Tap **Build Number** 7 times
3. Enable **USB Debugging** in Developer Options
4. Enable **Stay Awake** for testing

#### Install via ADB
```bash
# Check device connection
adb devices

# Install app
adb install bin/Release/net10.0-android/com.honua.fieldapp-Signed.apk

# View logs
adb logcat -s HonuaFieldApp
```

### 3. Android Testing Scenarios

#### Location Services
- **Permission Handling**: Test runtime permission requests
- **Accuracy Modes**: Test High/Medium/Low accuracy settings
- **Battery Optimization**: Test with/without battery optimization disabled
- **Network Location**: Test GPS + network-based location

#### Network Connectivity
- **WiFi/Cellular**: Test gRPC connectivity on different networks
- **Network Transitions**: Test WiFi ↔ Cellular handoff
- **Offline Mode**: Test offline data storage and sync
- **Poor Connectivity**: Test with weak signal conditions

#### Performance Monitoring
- **CPU Usage**: Monitor CPU consumption during heavy operations
- **Memory Leaks**: Test for memory leaks during extended use
- **Thermal Throttling**: Test performance under thermal stress
- **Frame Rate**: Measure UI frame rate during map interactions

## End-to-End Testing Scenarios

### 1. Field Data Collection Workflow

```csharp
// Complete field workflow test sequence
1. Launch app and verify GPS acquisition
2. Query features from server via gRPC
3. Navigate to collection point using map
4. Capture GPS location (verify ≤10m accuracy)
5. Take photo with geotagging
6. Fill data collection form
7. Submit feature to server
8. Verify feature appears on map
9. Test offline sync when network unavailable
```

### 2. Performance Benchmarking

#### Map Rendering Performance
- **Target**: Render 1000 features in <5 seconds
- **Memory**: Keep memory usage <500MB
- **Frame Rate**: Maintain 60fps during map operations

#### Network Performance
- **gRPC Latency**: Measure query response times
- **Data Transfer**: Monitor network efficiency
- **Retry Logic**: Test network failure recovery

#### Battery Performance
- **GPS Tracking**: <20% battery drain per hour
- **Background Sync**: Minimal battery impact
- **Screen On Time**: Optimize for field work scenarios

### 3. Stress Testing

#### Large Dataset Testing
```csharp
// Test large feature queries
var query = new FeatureQuery
{
    Where = "1=1",
    ResultRecordCount = 10000,
    ReturnGeometry = true
};

// Measure performance
var stopwatch = Stopwatch.StartNew();
var result = await client.QueryFeaturesAsync(serviceId, layerId, query, context);
stopwatch.Stop();

// Validate results
Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(10000)); // 10s max
Assert.That(result.Features.Count, Is.EqualTo(10000));
```

#### Extended Operation Testing
- **Continuous GPS**: 8+ hours of GPS tracking
- **Memory Stability**: No memory leaks during extended use
- **Thermal Management**: Performance under thermal stress
- **Battery Life**: Extended field work scenarios

## Deployment Configurations

### iOS Production Build

```bash
# Archive for App Store
dotnet publish -f net10.0-ios --configuration Release /p:ArchiveOnBuild=true

# Create IPA for distribution
dotnet build -f net10.0-ios --configuration Release /p:BuildIpa=true
```

### Android Production Build

```bash
# Create signed APK
dotnet publish -f net10.0-android --configuration Release /p:AndroidSigningKeyStore=release.keystore

# Create AAB for Play Store
dotnet build -f net10.0-android --configuration Release /p:AndroidPackageFormat=aab
```

## Performance Validation Criteria

### Success Criteria
- **Map Rendering**: <5 seconds for 1000+ features
- **GPS Accuracy**: ≤10 meters in open areas
- **Memory Usage**: <500MB during normal operation
- **Battery Life**: <20% drain per hour during active use
- **Network Efficiency**: gRPC performs ≥20% better than REST
- **UI Responsiveness**: 60fps maintained during map operations
- **Offline Sync**: Successfully handles disconnected operation

### Performance Monitoring

```csharp
// Real-time performance tracking
public class PerformanceValidator
{
    public async Task ValidateMapPerformance()
    {
        var stopwatch = Stopwatch.StartNew();
        await mapView.LoadFeaturesAsync(testFeatures);
        stopwatch.Stop();

        Assert.That(stopwatch.ElapsedSeconds, Is.LessThan(5));
        Assert.That(GetMemoryUsage(), Is.LessThan(500_000_000)); // 500MB
        Assert.That(GetFrameRate(), Is.GreaterThan(55)); // Near 60fps
    }
}
```

## Troubleshooting

### Common iOS Issues
- **Code Signing**: Verify provisioning profile matches bundle ID
- **Permissions**: Check Info.plist permission descriptions
- **Background Location**: Ensure proper capability configuration

### Common Android Issues
- **Permissions**: Runtime permission handling for Android 6+
- **Battery Optimization**: May affect background GPS tracking
- **Network Security**: HTTP traffic requires security config

### Server Connection Issues
- **gRPC Configuration**: Verify server gRPC endpoints are enabled
- **Network Accessibility**: Check firewall and network policies
- **Authentication**: Validate API keys and authentication flow

## Documentation and Reporting

### Test Report Template
1. **Device Information**: Model, OS version, hardware specs
2. **Performance Metrics**: Rendering time, memory usage, battery drain
3. **Feature Validation**: GPS accuracy, camera quality, network performance
4. **Issue Log**: Any bugs or performance concerns found
5. **Recommendations**: Optimizations or configuration changes needed

### Production Readiness Checklist
- [ ] GPS accuracy meets field requirements (≤10m)
- [ ] Camera captures high-quality geotagged photos
- [ ] Map renders 1000+ features in <5 seconds
- [ ] Memory usage stays below 500MB
- [ ] Battery drain <20% per hour during active use
- [ ] Offline sync works reliably
- [ ] gRPC connectivity stable across network conditions
- [ ] App passes all platform-specific validation requirements

This comprehensive testing approach ensures the Honua Mobile SDK is production-ready for field data collection scenarios and meets the performance criteria needed for real-world deployment.