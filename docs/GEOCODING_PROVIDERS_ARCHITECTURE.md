# Geocoding Providers Architecture

This document describes the core geocoding provider architecture for GitHub issue #365, implementing a clean abstraction layer that supports multiple geocoding providers with failover, caching, and GeoServices compatibility.

## Architecture Overview

The architecture follows the established Honua patterns with clear separation between domain models, abstractions, and implementations:

```
Honua.Core/Features/Geocoding/
├── Domain/
│   ├── GeocodeModels.cs                    # Core request/response models
│   ├── ProviderCapabilities.cs            # Provider capability definitions
│   ├── ProviderConfigurations.cs          # Provider-specific configurations
│   ├── GeocodingConfiguration.cs          # Main geocoding configuration
│   ├── GeocodingConfigurationValidator.cs # Configuration validation
│   └── GeocodeErrors.cs                   # Error handling models
├── Abstractions/
│   ├── IGeocodeProvider.cs                # Core provider interface
│   ├── BaseGeocodeProvider.cs             # Base implementation
│   └── IGeocodeProviderRegistry.cs        # Provider registry interfaces
├── Services/
│   └── GeocodeCoordinatorService.cs       # Provider coordination logic
├── Providers/
│   └── MockGeocodeProvider.cs             # Mock provider for testing
├── Integration/
│   └── ProviderIntegrationExtensions.cs   # Provider registration helpers
└── ServiceCollectionExtensions.cs         # DI registration
```

## Key Components

### 1. Core Abstractions

**IGeocodeProvider**: The main provider interface supporting:
- Forward geocoding (address → coordinates)
- Reverse geocoding (coordinates → address)
- Suggestions/autocomplete
- Batch geocoding
- Health checks

**BaseGeocodeProvider**: Base implementation providing:
- Common functionality for all providers
- Capability validation
- Error handling patterns
- Score normalization utilities

### 2. Domain Models

**GeocodeModels.cs** contains standardized request/response models:
- `ForwardGeocodeRequest` - Address to coordinates
- `ReverseGeocodeRequest` - Coordinates to address
- `SuggestGeocodeRequest` - Autocomplete requests
- `BatchGeocodeRequest` - Multiple address requests
- `GeocodeCandidate` - Geocoding result
- `ReverseGeocodeMatch` - Reverse geocoding result
- `GeocodeSuggestion` - Autocomplete suggestion

**ProviderCapabilities.cs** defines provider capabilities:
- Supported operations (forward, reverse, suggest, batch)
- Spatial reference systems
- Rate limits and constraints
- Authentication requirements

### 3. Configuration System

The configuration supports multiple providers with individual settings:

```json
{
  "Geocoding": {
    "Enabled": true,
    "DefaultProvider": "nominatim",
    "EnableFailover": true,
    "MaxFailoverAttempts": 3,
    "Providers": {
      "Nominatim": { ... },
      "AmazonLocation": { ... },
      "AzureMaps": { ... },
      "Esri": { ... },
      "GoogleMaps": { ... },
      "Mapbox": { ... }
    }
  }
}
```

### 4. Provider Registry & Coordination

**IGeocodeProviderRegistry**: Manages provider instances and factory functions

**IGeocodeCoordinatorService**: Orchestrates geocoding operations with:
- Automatic provider selection
- Failover to backup providers
- Response time tracking
- Error aggregation

## Supported Providers

The architecture is designed to support these providers:

1. **Nominatim** (OpenStreetMap) - Free, open source
2. **Amazon Location Services** - AWS-based geocoding
3. **Azure Maps** - Microsoft's mapping platform
4. **Esri ArcGIS** - Enterprise GIS geocoding
5. **Google Maps** - Google's geocoding API
6. **MapBox** - Open source friendly commercial service

Each provider has its own configuration class with provider-specific options.

## GeoServices Compatibility

The architecture maintains compatibility with ArcGIS GeoServices REST API by:

- Supporting the same request/response models in the server layer
- Mapping core models to GeoServices formats
- Preserving spatial reference system handling
- Maintaining attribute structure expectations

## Usage Examples

### Basic Setup

```csharp
// Program.cs
services.AddGeocodingCore(configuration);
services.AddAllGeocodeProviders(configuration);

// Or register specific providers
services.AddNominatimGeocodeProvider(configuration);
services.AddMockGeocodeProvider(); // For testing
```

### Using the Coordinator

```csharp
public class GeocodingService
{
    private readonly IGeocodeCoordinatorService _coordinator;

    public GeocodingService(IGeocodeCoordinatorService coordinator)
    {
        _coordinator = coordinator;
    }

    public async Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> GeocodeAsync(
        string address,
        string? preferredProvider = null)
    {
        var request = new ForwardGeocodeRequest(address, MaxResults: 10);
        return await _coordinator.ForwardGeocodeAsync(request, preferredProvider);
    }
}
```

### Direct Provider Access

```csharp
public class CustomGeocodingService
{
    private readonly IGeocodeProviderRegistry _registry;

    public async Task<IReadOnlyList<GeocodeCandidate>> GeocodeWithNominatim(string address)
    {
        var provider = _registry.GetProvider("nominatim");
        if (provider == null) throw new InvalidOperationException("Nominatim not available");

        var request = new ForwardGeocodeRequest(address, MaxResults: 5);
        return await provider.ForwardGeocodeAsync(request);
    }
}
```

## Error Handling

The architecture provides comprehensive error handling:

- **GeocodeProviderException** - Provider configuration or availability issues
- **GeocodeRequestException** - Invalid request parameters
- **GeocodeRateLimitException** - Rate limiting errors
- **GeocodeAuthenticationException** - Authentication failures

Error codes are standardized across providers for consistent handling.

## Testing

The **MockGeocodeProvider** supports comprehensive testing scenarios:

```csharp
// Configure mock provider for testing
var mockConfig = new MockGeocodeProviderConfiguration
{
    SimulateFailure = false,
    DelayMs = 100,
    ResultCount = 3
};

services.AddMockGeocodeProvider(mockConfig);
```

## Future Enhancements

The architecture is designed to support future enhancements:

1. **Caching Layer** - Redis-based result caching
2. **Analytics** - Provider performance tracking
3. **Rate Limiting** - Request throttling per provider
4. **Circuit Breaker** - Automatic provider failover
5. **Load Balancing** - Distribute requests across providers
6. **Custom Providers** - Plugin architecture for custom implementations

## Migration from Existing Code

The current Honua.Server geocoding implementation can be gradually migrated:

1. Keep existing endpoints unchanged
2. Replace internal provider calls with coordinator service
3. Move provider implementations to use core abstractions
4. Add new providers incrementally
5. Enable failover and coordination features

This maintains backward compatibility while enabling the new architecture.