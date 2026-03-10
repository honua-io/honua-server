# Esri Geocoding Provider Integration Guide

This guide shows how to integrate the Esri geocoding provider into the Honua server application.

## 1. Add Package References

Add the following package reference to your project file if using the provider from a separate assembly:

```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="8.0.0" />
<PackageReference Include="System.Threading.RateLimiting" Version="8.0.0" />
```

## 2. Configuration Setup

### Complete Configuration Example

```json
{
  "Geocoding": {
    "Enabled": true,
    "DefaultProvider": "esri",
    "LocatorName": "World",
    "DefaultSpatialReferenceWkid": 4326,

    "Nominatim": {
      "BaseUrl": "https://nominatim.openstreetmap.org",
      "UserAgent": "Honua/1.0 (+https://honua.io)",
      "Email": "admin@honua.io",
      "TimeoutSeconds": 10,
      "DefaultMaxResults": 10,
      "DefaultMaxSuggestions": 5,
      "EnableSuggestFromSearch": true,
      "CountryCodes": "us,ca"
    },

    "Esri": {
      "Enabled": true,
      "Priority": 10,
      "BaseUrl": "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",

      // Use API Key authentication
      "ApiKey": "your-esri-api-key-here",

      // OR use OAuth2 authentication (comment out ApiKey above)
      // "ClientId": "your-client-id",
      // "ClientSecret": "your-client-secret",
      // "TokenEndpoint": "https://www.arcgis.com/sharing/rest/oauth2/token",
      // "TokenCacheDurationMinutes": 55,

      "TimeoutSeconds": 30,
      "MaxResults": 10,
      "DefaultSpatialReference": 4326,
      "DefaultCountries": ["US", "CA"],

      "DefaultOutFields": [
        "Addr_type",
        "Country",
        "PlaceName",
        "Region",
        "Subregion",
        "City",
        "Postal",
        "AddNum",
        "StName",
        "StType",
        "District",
        "MetroArea",
        "Neighborhood",
        "LongLabel",
        "ShortLabel",
        "Match_addr"
      ],

      "EnableSuggestions": true,
      "EnableBatchGeocoding": true,
      "MaxBatchSize": 1000,
      "UseCompression": true,
      "UserAgent": "Honua/1.0 (+https://honua.io) Esri-Provider",
      "RateLimitRequestsPerSecond": 10.0
    }
  }
}
```

### Environment Variables (Alternative)

You can also use environment variables for sensitive configuration:

```bash
export ESRI_API_KEY="your-api-key-here"
export ESRI_CLIENT_ID="your-client-id"
export ESRI_CLIENT_SECRET="your-client-secret"
```

Then in your configuration:

```json
{
  "Geocoding": {
    "Esri": {
      "ApiKey": "${ESRI_API_KEY}",
      "ClientId": "${ESRI_CLIENT_ID}",
      "ClientSecret": "${ESRI_CLIENT_SECRET}"
    }
  }
}
```

## 3. Service Registration

The Esri provider is automatically registered when the configuration section exists. The main geocoding service registration handles this:

```csharp
// In Program.cs or Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // This automatically registers Esri provider if configured
    services.AddGeocoding(Configuration);

    // Or register explicitly if needed
    services.AddEsriGeocoding(Configuration.GetSection("Geocoding:Esri"));
}
```

## 4. Usage in Controllers/Services

### Dependency Injection

```csharp
[ApiController]
[Route("api/[controller]")]
public class GeocodingController : ControllerBase
{
    private readonly IGeocodeProvider _geocodeProvider;
    private readonly IGeocodeProviderResolver _providerResolver;

    public GeocodingController(
        IGeocodeProvider defaultProvider,
        IGeocodeProviderResolver providerResolver)
    {
        _geocodeProvider = defaultProvider;
        _providerResolver = providerResolver;
    }

    [HttpGet("geocode")]
    public async Task<ActionResult<List<GeocodeCandidate>>> Geocode(
        [FromQuery] string address,
        [FromQuery] string? provider = null)
    {
        // Use specific provider or default
        var geocoder = provider != null
            ? _providerResolver.GetProvider(provider) ?? _geocodeProvider
            : _geocodeProvider;

        var request = new ForwardGeocodeRequest(address, MaxResults: 5);
        var results = await geocoder.ForwardGeocodeAsync(request);

        return Ok(results);
    }
}
```

### Advanced Usage with Provider Selection

```csharp
public class LocationService
{
    private readonly IGeocodeProviderResolver _providerResolver;
    private readonly ILogger<LocationService> _logger;

    public LocationService(
        IGeocodeProviderResolver providerResolver,
        ILogger<LocationService> logger)
    {
        _providerResolver = providerResolver;
        _logger = logger;
    }

    public async Task<GeocodeCandidate?> FindBestLocationAsync(string address)
    {
        // Try Esri first (high accuracy), fallback to Nominatim
        var providers = new[] { "esri", "nominatim" };

        foreach (var providerName in providers)
        {
            try
            {
                var provider = _providerResolver.GetProvider(providerName);
                if (provider == null) continue;

                var request = new ForwardGeocodeRequest(address, MaxResults: 1);
                var results = await provider.ForwardGeocodeAsync(request);

                if (results.Count > 0 && results[0].Score > 80)
                {
                    _logger.LogInformation("Found location using {Provider}: {Address}",
                        providerName, results[0].Address);
                    return results[0];
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} failed for address: {Address}",
                    providerName, address);
            }
        }

        return null;
    }

    public async Task<List<GeocodeSuggestion>> GetSuggestionsAsync(
        string partialAddress,
        int maxResults = 5)
    {
        // Use Esri for suggestions if available
        var esriProvider = _providerResolver.GetProvider("esri");
        if (esriProvider?.Capabilities.SupportsSuggest == true)
        {
            try
            {
                var request = new SuggestGeocodeRequest(partialAddress, maxResults);
                return (await esriProvider.SuggestAsync(request)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Esri suggestions failed, falling back to search");
            }
        }

        // Fallback to Nominatim search-based suggestions
        var nominatimProvider = _providerResolver.GetProvider("nominatim");
        if (nominatimProvider?.Capabilities.SupportsSuggest == true)
        {
            var request = new SuggestGeocodeRequest(partialAddress, maxResults);
            return (await nominatimProvider.SuggestAsync(request)).ToList();
        }

        return new List<GeocodeSuggestion>();
    }
}
```

## 5. Health Monitoring

### Configure Health Checks

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddGeocoding(Configuration);

    services.AddHealthChecks()
        .AddCheck<GeocodingHealthCheck>("geocoding");
}

public class GeocodingHealthCheck : IHealthCheck
{
    private readonly IGeocodeProviderResolver _resolver;

    public GeocodingHealthCheck(IGeocodeProviderResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var providers = _resolver.GetAllProviders();
        var results = new List<(string Name, bool Healthy, string? Error, double? ResponseTime)>();

        foreach (var provider in providers)
        {
            try
            {
                var health = await provider.CheckHealthAsync(cancellationToken);
                results.Add((health.ProviderName, health.IsHealthy, health.ErrorMessage, health.ResponseTimeMs));
            }
            catch (Exception ex)
            {
                results.Add((provider.Name, false, ex.Message, null));
            }
        }

        var unhealthy = results.Where(r => !r.Healthy).ToList();
        if (unhealthy.Count == results.Count)
        {
            return HealthCheckResult.Unhealthy(
                "All geocoding providers are unhealthy",
                data: results.ToDictionary(r => r.Name, r => (object)r));
        }

        if (unhealthy.Any())
        {
            return HealthCheckResult.Degraded(
                $"{unhealthy.Count} of {results.Count} providers are unhealthy",
                data: results.ToDictionary(r => r.Name, r => (object)r));
        }

        return HealthCheckResult.Healthy(
            "All geocoding providers are healthy",
            data: results.ToDictionary(r => r.Name, r => (object)r));
    }
}
```

### Monitor Performance

```csharp
public class GeocodingMetrics
{
    private readonly ILogger<GeocodingMetrics> _logger;
    private readonly IMetrics _metrics; // Using your metrics library

    public async Task<TResult> TrackGeocoding<TResult>(
        string operation,
        string provider,
        Func<Task<TResult>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            stopwatch.Stop();

            _metrics.Counter("geocoding.requests")
                .WithTag("operation", operation)
                .WithTag("provider", provider)
                .WithTag("status", "success")
                .Increment();

            _metrics.Timer("geocoding.duration")
                .WithTag("operation", operation)
                .WithTag("provider", provider)
                .Record(stopwatch.Elapsed);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _metrics.Counter("geocoding.requests")
                .WithTag("operation", operation)
                .WithTag("provider", provider)
                .WithTag("status", "error")
                .WithTag("error_type", ex.GetType().Name)
                .Increment();

            _logger.LogError(ex, "Geocoding operation failed: {Operation} with {Provider}",
                operation, provider);
            throw;
        }
    }
}
```

## 6. Testing

### Unit Tests with Mocked Provider

```csharp
[Fact]
public async Task LocationService_ShouldUseEsriFirst()
{
    // Arrange
    var esriMock = new Mock<IGeocodeProvider>();
    var nominatimMock = new Mock<IGeocodeProvider>();
    var resolverMock = new Mock<IGeocodeProviderResolver>();

    esriMock.Setup(p => p.Name).Returns("esri");
    esriMock.Setup(p => p.ForwardGeocodeAsync(It.IsAny<ForwardGeocodeRequest>(), default))
        .ReturnsAsync(new List<GeocodeCandidate>
        {
            new("123 Test St", -122.0, 37.0, 95.0, new Dictionary<string, string?>())
        });

    resolverMock.Setup(r => r.GetProvider("esri")).Returns(esriMock.Object);
    resolverMock.Setup(r => r.GetProvider("nominatim")).Returns(nominatimMock.Object);

    var service = new LocationService(resolverMock.Object, Mock.Of<ILogger<LocationService>>());

    // Act
    var result = await service.FindBestLocationAsync("123 Test St");

    // Assert
    Assert.NotNull(result);
    Assert.Equal(95.0, result.Score);
    esriMock.Verify(p => p.ForwardGeocodeAsync(It.IsAny<ForwardGeocodeRequest>(), default), Times.Once);
    nominatimMock.Verify(p => p.ForwardGeocodeAsync(It.IsAny<ForwardGeocodeRequest>(), default), Times.Never);
}
```

### Integration Tests

```csharp
[Fact]
public async Task EsriProvider_Integration_ShouldWork()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddEsriGeocoding(options =>
    {
        options.ApiKey = Environment.GetEnvironmentVariable("ESRI_API_KEY");
        options.BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer";
    });

    await using var provider = services.BuildServiceProvider();
    var geocoder = provider.GetRequiredService<IGeocodeProvider>();

    Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ESRI_API_KEY")),
        "ESRI_API_KEY not configured");

    var request = new ForwardGeocodeRequest("1600 Amphitheatre Parkway, Mountain View, CA");
    var results = await geocoder.ForwardGeocodeAsync(request);

    Assert.NotEmpty(results);
    Assert.Contains("Amphitheatre", results[0].Address);
}
```

## 7. Performance Optimization

### Connection Pooling

```csharp
services.AddEsriGeocoding(configuration)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 20
    });
```

### Caching Results

```csharp
public class CachedGeocodingService : IGeocodeProvider
{
    private readonly IGeocodeProvider _inner;
    private readonly IMemoryCache _cache;

    public async Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"geocode:{request.Query}:{request.SpatialReferenceWkid}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<GeocodeCandidate>? cached))
        {
            return cached;
        }

        var results = await _inner.ForwardGeocodeAsync(request, cancellationToken);

        _cache.Set(cacheKey, results, TimeSpan.FromHours(24));

        return results;
    }
}
```

## 8. Troubleshooting

### Common Issues and Solutions

1. **Authentication Errors**
   ```
   Error: "Failed to obtain OAuth token from Esri"
   Solution: Verify ClientId/ClientSecret or ApiKey in configuration
   ```

2. **Rate Limiting**
   ```
   Error: "Rate limit exceeded for Esri geocoding provider"
   Solution: Reduce RateLimitRequestsPerSecond or upgrade your Esri plan
   ```

3. **No Results**
   ```
   Check: Address format, country codes, search bounds
   Debug: Enable detailed logging to see API requests/responses
   ```

### Debugging Configuration

```json
{
  "Logging": {
    "LogLevel": {
      "Honua.Postgres.Features.Geocoding": "Debug",
      "System.Net.Http.HttpClient": "Information"
    }
  }
}
```

This comprehensive integration guide should help you successfully implement the Esri geocoding provider in your Honua application.