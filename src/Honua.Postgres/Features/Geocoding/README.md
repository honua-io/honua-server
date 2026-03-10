# Esri ArcGIS REST Geocoding Provider

This implementation provides a complete geocoding provider for Esri ArcGIS REST services, following the Honua geocoding architecture patterns.

## Features

### Core Capabilities
- ✅ Forward geocoding (address to coordinates) via `findAddressCandidates` endpoint
- ✅ Reverse geocoding (coordinates to address) via `reverseGeocode` endpoint
- ✅ Autocomplete/suggestions via `suggest` endpoint
- ✅ Batch geocoding via `geocodeAddresses` endpoint
- ✅ Structured address input support
- ✅ Search bounds and biasing
- ✅ Multiple spatial reference systems (4326, 3857, 102100)

### Authentication
- ✅ API key authentication (recommended)
- ✅ OAuth2 client credentials flow
- ✅ Automatic token management and refresh
- ✅ Token caching with configurable duration

### Advanced Features
- ✅ Rate limiting with token bucket algorithm
- ✅ HTTP compression support
- ✅ Comprehensive error handling
- ✅ Health checks
- ✅ Configurable output fields
- ✅ Custom locators support
- ✅ Country and category filtering

## Configuration

### Basic Setup with API Key

```json
{
  "Geocoding": {
    "Esri": {
      "Enabled": true,
      "BaseUrl": "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
      "ApiKey": "your-esri-api-key-here",
      "MaxResults": 10,
      "TimeoutSeconds": 30
    }
  }
}
```

### OAuth2 Setup

```json
{
  "Geocoding": {
    "Esri": {
      "Enabled": true,
      "BaseUrl": "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "TokenEndpoint": "https://www.arcgis.com/sharing/rest/oauth2/token",
      "TokenCacheDurationMinutes": 55
    }
  }
}
```

### Service Registration

```csharp
// In your Startup.cs or Program.cs
services.AddEsriGeocoding(configuration);

// Or with custom configuration
services.AddEsriGeocoding(options =>
{
    options.BaseUrl = "https://your-custom-geocoding-service.com";
    options.ApiKey = "your-api-key";
    options.MaxResults = 5;
    options.EnableSuggestions = true;
    options.EnableBatchGeocoding = true;
});
```

## Usage Examples

### Forward Geocoding

```csharp
var provider = serviceProvider.GetRequiredService<IGeocodeProvider>();

// Simple address search
var request = new ForwardGeocodeRequest(
    "1600 Amphitheatre Parkway, Mountain View, CA",
    MaxResults: 5,
    CountryCodes: "US");

var results = await provider.ForwardGeocodeAsync(request);

// Structured address search
var structuredRequest = new ForwardGeocodeRequest("", InputType: GeocodeInputType.Structured)
{
    StructuredAddress = new StructuredAddress
    {
        AddressNumber = "1600",
        StreetName = "Amphitheatre Parkway",
        City = "Mountain View",
        Region = "CA",
        Country = "US"
    }
};

var structuredResults = await provider.ForwardGeocodeAsync(structuredRequest);
```

### Reverse Geocoding

```csharp
var reverseRequest = new ReverseGeocodeRequest(
    X: -122.0856,
    Y: 37.4220,
    DistanceMeters: 100);

var reverseResult = await provider.ReverseGeocodeAsync(reverseRequest);
```

### Autocomplete/Suggestions

```csharp
var suggestRequest = new SuggestGeocodeRequest(
    "1600 Amphi",
    MaxResults: 5,
    CountryCodes: "US")
{
    BiasLocation = new GeocodePoint(-122.0856, 37.4220)
};

var suggestions = await provider.SuggestAsync(suggestRequest);
```

### Batch Geocoding

```csharp
var batchRequest = new BatchGeocodeRequest(new[]
{
    "1600 Amphitheatre Parkway, Mountain View, CA",
    "1 Infinite Loop, Cupertino, CA",
    "410 Terry Ave N, Seattle, WA"
});

var batchResults = await provider.BatchGeocodeAsync(batchRequest);
```

## Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `BaseUrl` | string | Esri World GeocodeServer | Base URL for the geocoding service |
| `ApiKey` | string | null | API key for authentication |
| `ClientId` | string | null | OAuth2 client ID |
| `ClientSecret` | string | null | OAuth2 client secret |
| `TokenEndpoint` | string | Esri OAuth endpoint | OAuth2 token endpoint |
| `TimeoutSeconds` | int | 30 | HTTP request timeout |
| `MaxResults` | int | 10 | Maximum results per request (max 50) |
| `MaxBatchSize` | int | 1000 | Maximum batch size (max 1000) |
| `DefaultSpatialReference` | int | 4326 | Default WKID for coordinates |
| `DefaultOutFields` | string[] | Standard fields | Fields to return in responses |
| `DefaultCountries` | string[] | null | Default country codes for biasing |
| `EnableSuggestions` | bool | true | Enable autocomplete functionality |
| `EnableBatchGeocoding` | bool | true | Enable batch operations |
| `UseCompression` | bool | true | Enable HTTP compression |
| `RateLimitRequestsPerSecond` | double? | null | Rate limiting (optional) |
| `UserAgent` | string | Honua default | HTTP User-Agent header |
| `TokenCacheDurationMinutes` | int | 55 | OAuth token cache duration |

## Error Handling

The provider handles various error conditions:

- **HTTP errors** (network issues, timeouts) → `HttpRequestException`
- **Esri API errors** (invalid parameters, quota exceeded) → `InvalidOperationException`
- **Authentication errors** (invalid credentials) → `InvalidOperationException`
- **Rate limiting** (too many requests) → `InvalidOperationException`
- **Validation errors** (unsupported spatial reference) → `ArgumentException`

## Spatial Reference Systems

Supported coordinate systems:
- **4326** - WGS84 Geographic (latitude/longitude)
- **3857** - Web Mercator Auxiliary Sphere
- **102100** - Web Mercator (legacy)

## Performance Considerations

- **Token Caching**: OAuth tokens are cached and automatically refreshed
- **Rate Limiting**: Configurable rate limiting prevents API quota exhaustion
- **Compression**: HTTP compression reduces bandwidth usage
- **Connection Pooling**: Uses HttpClient best practices
- **Concurrent Requests**: Thread-safe implementation supports concurrent operations

## Monitoring and Health Checks

The provider implements `CheckHealthAsync()` for monitoring:

```csharp
var health = await provider.CheckHealthAsync();
if (health.IsHealthy)
{
    Console.WriteLine($"Provider healthy, response time: {health.ResponseTimeMs}ms");
}
else
{
    Console.WriteLine($"Provider unhealthy: {health.ErrorMessage}");
}
```

## API Documentation References

- [ArcGIS REST API - Geocoding](https://developers.arcgis.com/rest/geocode/)
- [findAddressCandidates](https://developers.arcgis.com/rest/geocode/api-reference/geocoding-find-address-candidates.htm)
- [reverseGeocode](https://developers.arcgis.com/rest/geocode/api-reference/geocoding-reverse-geocode.htm)
- [suggest](https://developers.arcgis.com/rest/geocode/api-reference/geocoding-suggest.htm)
- [geocodeAddresses](https://developers.arcgis.com/rest/geocode/api-reference/geocoding-geocode-addresses.htm)

## Troubleshooting

### Common Issues

1. **Authentication Errors**
   - Verify API key or OAuth credentials
   - Check token endpoint URL
   - Ensure sufficient API credits

2. **No Results Returned**
   - Check address format and country codes
   - Verify spatial reference system
   - Review search bounds if specified

3. **Rate Limiting**
   - Configure `RateLimitRequestsPerSecond`
   - Check your Esri service limits
   - Consider using batch operations

4. **Timeout Issues**
   - Increase `TimeoutSeconds`
   - Check network connectivity
   - Verify service endpoint availability

### Debugging

Enable detailed logging to diagnose issues:

```csharp
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});
```

The provider logs key operations including:
- Authentication token requests
- API calls and responses
- Error conditions
- Performance metrics