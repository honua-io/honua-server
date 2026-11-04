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

## Beta Capability Status

The following documents the current state of geocoding capabilities as of the beta release:

### Implemented and Validated

- **Forward geocoding** (`findAddressCandidates`): Fully implemented. Nominatim, Azure Maps, and Amazon Location providers supported. GET and POST methods. GET-only alias route without locator name.
- **Reverse geocoding** (`reverseGeocode`): Fully implemented. All active providers supported. GET and POST methods. GET-only alias route without locator name.
- **Suggest/autocomplete** (`suggest`): Fully implemented. Azure Maps and Amazon Location support native suggest. Nominatim supports suggest when `EnableSuggestFromSearch` is enabled (reuses the search endpoint). GET and POST methods. GET-only alias route without locator name.
- **Batch geocoding** (`geocodeAddresses`): Implemented at handler level with full request parsing, validation, and response mapping. Pipeline-validated via MockGeocodeProvider. No external provider supports native batch yet; when they do, they participate automatically via the coordinator. GET and POST methods. GET-only alias route without locator name.
- **Failover**: Fully implemented for all operations (forward, reverse, suggest, batch) via `GeocodeCoordinatorService`. Configurable via `EnableFailover` and `MaxFailoverAttempts`. For batch and suggest, the coordinator skips providers that lack the required capability and continues to the next provider in the failover chain. When a client explicitly requests a specific provider via the `provider` parameter and that provider lacks the required capability, a 400 is returned without attempting failover.
- **Provider health checks**: Each provider exposes `CheckHealthAsync` for monitoring.

### Known Limits

- **Spatial reference**: Only `outSR=4326` (WGS 84) is currently supported. Requests for other SRIDs return 400.
- **Batch OBJECTID**: No OBJECTID tracking in batch responses. Results are returned in input order.
- **No fan-out**: Batch requests require native provider batch support. Sequential forward-geocode fan-out (dispatching individual calls when no provider supports native batch) is deferred.
- **Batch size**: When an explicit `provider` parameter is supplied, capped by that provider's `MaxBatchSize`. Otherwise, a default cap of 100 is applied.

### Future Enhancements

1. **Fan-out batch strategy** - Sequential forward calls for providers without native batch support
2. **Caching layer** - Redis-based result caching
3. **Additional SRIDs** - Coordinate reprojection for non-4326 output
4. **OBJECTID tracking** - Per-record OBJECTID in batch responses per GeoServices spec
5. **Custom providers** - Plugin architecture for custom implementations
