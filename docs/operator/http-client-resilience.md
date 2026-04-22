# HTTP Client Resilience

This document describes the HTTP client resilience patterns implemented in Honua Server to provide standardized retry policies, circuit breakers, and operational visibility for external service dependencies.

## Overview

The HTTP resilience system provides:

- **Retry Policies**: Automatic retry with exponential backoff for transient failures
- **Circuit Breakers**: Fail-fast protection to prevent cascading failures
- **Health Monitoring**: Circuit breaker state tracking and external service health checks
- **Operational Visibility**: Structured logs for retry attempts and circuit breaker state changes
- **Service Isolation**: Per-service circuit breakers to prevent cross-contamination

## Configuration

### Basic Configuration

HTTP resilience is configured via the `HttpResilience` section in `appsettings.json`:

```json
{
  "HttpResilience": {
    "FastApi": {
      "MaxRetryAttempts": 2,
      "BaseDelayMs": 200,
      "BackoffExponent": 1.5,
      "JitterPercentage": 0.15,
      "CircuitBreakerFailures": 3,
      "CircuitBreakDurationSeconds": 15,
      "TimeoutSeconds": 10
    },
    "Standard": {
      "MaxRetryAttempts": 3,
      "BaseDelayMs": 500,
      "BackoffExponent": 2.0,
      "JitterPercentage": 0.2,
      "CircuitBreakerFailures": 5,
      "CircuitBreakDurationSeconds": 30,
      "TimeoutSeconds": 30
    },
    "SlowService": {
      "MaxRetryAttempts": 5,
      "BaseDelayMs": 1000,
      "BackoffExponent": 2.0,
      "JitterPercentage": 0.25,
      "CircuitBreakerFailures": 8,
      "CircuitBreakDurationSeconds": 120,
      "TimeoutSeconds": 300
    },
    "ServiceOverrides": {
      "arcgis-rest": {
        "MaxRetryAttempts": 3,
        "BaseDelayMs": 1000,
        "CircuitBreakDurationSeconds": 60
      },
      "webhook-critical": {
        "MaxRetryAttempts": 1,
        "BaseDelayMs": 100,
        "CircuitBreakerFailures": 2
      }
    }
  }
}
```

### Environment Variables

Configuration can be overridden via environment variables:

```bash
# Fast API settings
HttpResilience__FastApi__MaxRetryAttempts=3
HttpResilience__FastApi__BaseDelayMs=300

# Service-specific overrides
HttpResilience__ServiceOverrides__arcgis-rest__MaxRetryAttempts=5
HttpResilience__ServiceOverrides__geoserver-rest__TimeoutSeconds=120
```

## Service Types and Profiles

### Predefined Profiles

#### FastApi Profile
- **Use for**: Geocoding APIs, webhooks, identity providers
- **Characteristics**: Low latency, quick failures
- **Default Settings**:
  - 2 retry attempts
  - 200ms base delay
  - 15-second circuit break duration
  - 10-second timeout

#### Standard Profile
- **Use for**: General HTTP services, REST APIs
- **Characteristics**: Balanced resilience and performance
- **Default Settings**:
  - 3 retry attempts
  - 500ms base delay
  - 30-second circuit break duration
  - 30-second timeout

#### SlowService Profile
- **Use for**: Import services, discovery operations, large data transfers
- **Characteristics**: High tolerance for delays, robust retry behavior
- **Default Settings**:
  - 5 retry attempts
  - 1-second base delay
  - 2-minute circuit break duration
  - 5-minute timeout

### Service Type Mapping

| Service Type | Profile | Purpose |
|-------------|---------|---------|
| `arcgis-rest` | SlowService | ArcGIS REST API for service discovery and feature import |
| `geoserver-rest` | SlowService | GeoServer REST API for migration and import |
| `aws-secrets-manager` | FastApi | AWS Secrets Manager for secure configuration |
| `azure-key-vault` | FastApi | Azure Key Vault for secure configuration |
| `nominatim-geocoding` | FastApi | OpenStreetMap geocoding service |
| `azure-maps-geocoding` | FastApi | Azure Maps geocoding service |
| `nl-query` | FastApi | Natural language query processing |
| `*-webhook` | FastApi | All webhook delivery endpoints |
| `alerts-*` | FastApi | Alert delivery services |

## Transient Failure Detection

The system automatically retries the following failures:

### HTTP Exceptions
- `HttpRequestException`: Network connectivity issues
- `TaskCanceledException` with `TimeoutException`: Request timeouts
- `SocketException`: Low-level socket errors

### HTTP Response Codes
- **500 Internal Server Error**: Server-side errors
- **502 Bad Gateway**: Gateway/proxy errors
- **503 Service Unavailable**: Temporary service issues
- **504 Gateway Timeout**: Upstream timeouts
- **408 Request Timeout**: Client timeout
- **429 Too Many Requests**: Rate limiting

### Non-Retryable Failures
- **4xx Client Errors** (except 408, 429): Bad request, authentication, authorization
- **2xx Success**: Successful responses
- **3xx Redirects**: Handled by HttpClient redirect policy

## Circuit Breaker Behavior

### States

1. **Closed**: Normal operation, requests flow through
2. **Open**: Circuit is open, requests fail immediately
3. **Half-Open**: Testing state, allows one request to test service recovery

### Failure Counting

Circuit breakers count consecutive failures per service type:
- Each service type has an isolated circuit breaker
- Failures on one service don't affect other services
- Counter resets on successful response

### Recovery

When a circuit breaker opens:
1. All requests to that service fail immediately
2. After the break duration expires, circuit moves to half-open
3. Next request is allowed through as a test
4. If test succeeds, circuit closes; if it fails, circuit reopens

## Health Checks

This branch does not register dedicated HTTP-client or circuit-breaker health checks by default.
The runtime health/readiness endpoints cover the checks that are actually wired from the composition root.
External HTTP-client resilience is currently observable through logs and the generic health-state endpoints, not through preconfigured per-service probes.

### Health Check Results

- **Healthy**: Service is responding normally
- **Degraded**: Reserved for checks the runtime explicitly wires as degraded rather than unhealthy
- **Unhealthy**: Reserved for critical internal failures

## Logging and Observability

### Current Behavior

The current implementation emits structured retry and circuit-breaker logs and exposes health-state checks. Dedicated retry/circuit-breaker metrics are not wired yet, so do not assume the following counters or histograms exist until the runtime telemetry implementation lands.

### Metric Dimensions

- `service_type`: The service type identifier
- `attempt`: Retry attempt number
- `state`: Circuit breaker state (open, closed, half-open)

## Usage Examples

### Registering a Resilient HTTP Client

```csharp
// Typed client with resilience
services.AddResilientHttpClient<MyServiceClient>(
    "my-service",
    HttpResiliencePolicies.FastApiDefaults,
    configureClient: client =>
    {
        client.BaseAddress = new Uri("https://api.example.com");
        client.DefaultRequestHeaders.Add("User-Agent", "MyApp/1.0");
    });

// Named client with resilience
services.AddResilientHttpClient(
    "external-api",
    "external-service",
    HttpResiliencePolicies.SlowServiceDefaults,
    configureClient: client =>
    {
        client.Timeout = TimeSpan.FromMinutes(2);
    });
```

### Custom Resilience Configuration

```csharp
var customOptions = new ResiliencePolicyOptions
{
    MaxRetryAttempts = 4,
    BaseDelay = TimeSpan.FromMilliseconds(750),
    CircuitBreakerFailures = 6,
    CircuitBreakDuration = TimeSpan.FromSeconds(45)
};

services.AddResilientHttpClient<CustomClient>(
    "custom-service",
    customOptions);
```

### Manual Policy Usage

```csharp
public class MyService
{
    private readonly HttpClient _httpClient;
    
    public MyService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    public async Task<string> CallExternalApi(CancellationToken cancellationToken)
    {
        var policy = HttpResiliencePolicies.GetHttpPolicy("my-service");
        
        var context = HttpResiliencePolicies.CreateHttpContext(
            onRetry: (result, delay, attempt) =>
            {
                // Custom retry logging
                Console.WriteLine($"Retrying request (attempt {attempt})");
            });
        
        using var response = await policy.ExecuteAsync(
            async ct => await _httpClient.GetAsync("/api/data", ct),
            context);
            
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
```

## Best Practices

### Service Type Naming

Use descriptive, kebab-case names for service types:
- ✅ `arcgis-rest`, `azure-key-vault`, `webhook-notifications`
- ❌ `Service1`, `ExternalAPI`, `HTTP_CLIENT`

### Timeout Configuration

Set appropriate timeouts based on service characteristics:
- **Fast APIs**: 5-15 seconds
- **Standard APIs**: 30-60 seconds
- **Slow operations**: 2-10 minutes

### Circuit Breaker Tuning

Adjust circuit breaker settings based on service reliability:
- **Reliable services**: Higher failure threshold (8-10 failures)
- **Unreliable services**: Lower failure threshold (3-5 failures)
- **Critical services**: Longer break duration for stability
- **Non-critical services**: Shorter break duration for quick recovery

### Health Check Configuration

Configure appropriate health check endpoints:
- Use lightweight health endpoints when available
- Avoid endpoints that trigger expensive operations
- Set reasonable timeouts (5-15 seconds for external services)

### Error Handling

Always handle circuit breaker exceptions in your application code:

```csharp
try
{
    var result = await CallExternalService();
    return result;
}
catch (CircuitBreakerOpenException)
{
    // Circuit breaker is open - provide fallback or return cached data
    return GetCachedResult();
}
catch (HttpRequestException)
{
    // Request failed after all retries - handle gracefully
    return GetDefaultResult();
}
```

## Troubleshooting

### Common Issues

#### Circuit Breaker Opening Frequently
- **Cause**: External service is unreliable or timeout is too low
- **Solution**: Increase failure threshold or timeout, or implement better error handling in the external service

#### Excessive Retry Attempts
- **Cause**: Non-transient errors being retried
- **Solution**: Review error classification, ensure only transient errors are retried

#### High Latency
- **Cause**: Too many retries or long retry delays
- **Solution**: Reduce retry attempts or base delay for time-sensitive operations

### Debugging

Enable detailed logging for resilience operations:

```csharp
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});
```

Look for log messages indicating retry attempts and circuit breaker state changes.

### Monitoring

Monitor these key metrics:
- Circuit breaker state changes per service
- Retry attempt rates and success rates
- Average response times per service
- Health check failure rates

Set up alerts for:
- Circuit breakers staying open for extended periods
- High retry rates indicating service degradation
- Health check failures for critical services
