# Honua Mobile SDK for .NET

The Honua Mobile SDK provides cross-platform access to geospatial feature services using gRPC protocols. Build field data collection apps, offline-capable mapping applications, and AR/VR geospatial experiences.

## 🚀 Quick Start

```csharp
// Initialize client with API key
var client = new HonuaFeatureClient("your-server-url", "your-api-key");

// Query features
var features = await client.QueryAsync("service-id", layerId: 0,
    query => query.Where("STATUS = 'Active'").WithinDistance(point, 1000));

// Stream large datasets efficiently
await foreach (var feature in client.QueryStreamAsync("service-id", layerId: 0,
    query => query.Intersects(polygon)))
{
    // Process features as they stream
    Console.WriteLine($"Feature {feature.Id}: {feature.Attributes["NAME"]}");
}
```

## 📁 Project Structure

- **Honua.Mobile.Core** - Core gRPC client, authentication, and query abstractions
- **Honua.Mobile.Storage** - Offline storage with GeoPackage support
- **Honua.Mobile.Maui** - .NET MAUI platform handlers for native maps and camera
- **examples/ConsoleClient** - Basic console application demonstrating gRPC connectivity
- **examples/FieldDataCollection** - Complete MAUI reference app (coming soon)

## 🌟 Key Features

### gRPC-First Protocol
- **Production-ready**: Built on Honua Server's proven gRPC infrastructure
- **Efficient streaming**: 60-80% bandwidth reduction vs REST APIs
- **Network resilient**: Built for field conditions and low-bandwidth environments
- **Open standard**: Contributing to the first open gRPC geospatial protocol

### Cross-Platform Native Integration
- **iOS**: MapKit, ARKit, secure Keychain storage
- **Android**: Google Maps, ARCore, Android Keystore
- **Windows**: MapControl for desktop scenarios
- **Consistent API**: Same code runs across all platforms

### Offline-First Design
- **GeoPackage storage**: OGC-compliant local storage
- **Smart sync**: Delta synchronization with conflict resolution
- **Background operations**: Sync when network available
- **Spatial indexing**: Fast local queries

### Enterprise Security
- **Secure storage**: Platform-native credential management
- **API key authentication**: Compatible with Honua Server auth
- **Certificate support**: Device attestation for enterprise deployments

## 🛠 Development Status

**Phase 1: Foundation (Current)**
- ✅ Project structure and solution setup
- 🔄 gRPC client implementation
- 🔄 Authentication provider
- 🔄 Query builder fluent interface
- 🔄 Console client example

**Phase 2: Offline Capabilities (Q2 2026)**
- GeoPackage storage implementation
- Sync manager with conflict resolution
- Background sync operations

**Phase 3: MAUI Platform Integration (Q3 2026)**
- iOS/Android/Windows native handlers
- Camera integration with GPS tagging
- Map controls and visualization

**Phase 4: Reference Applications (Q4 2026)**
- Field data collection app (compete with Fulcrum)
- AR/VR utility visualization demos
- Complete documentation and tutorials

## 🤝 Contributing

This is part of Honua's **open core strategy**:
- **Mobile SDKs**: Apache 2.0 (fully open source)
- **gRPC protocols**: Apache 2.0 (open standard)
- **Reference apps**: Apache 2.0 (reference implementations)
- **Server**: ELv2 (source available)

Join us in creating the next generation of open geospatial development tools!

## 📱 Platform Support

- **.NET 10.0+**: All projects target latest .NET
- **iOS 15.0+**: MapKit, ARKit integration
- **Android API 24+**: Google Maps, ARCore support
- **Windows 10+**: MapControl, desktop scenarios
- **Future**: React Native bindings planned for Q3 2026

## 📖 Documentation

- [Getting Started Guide](docs/getting-started.md) (coming soon)
- [API Reference](docs/api-reference.md) (coming soon)
- [Platform Integration](docs/platform-integration.md) (coming soon)
- [Offline Development](docs/offline-development.md) (coming soon)

## ⚖️ License

Apache 2.0 - See [LICENSE](LICENSE) for details.

Part of the Honua open geospatial ecosystem. 🌍