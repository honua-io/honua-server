# Rate Limiting Documentation

## Overview

Honua Server implements application-level rate limiting middleware to protect against abuse, DoS attacks, and runaway queries. The implementation uses a sliding window algorithm with per-IP address tracking.

## How It Works

The rate limiting middleware (`RateLimitingMiddleware`) operates at the ASP.NET Core pipeline level and:

1. **Tracks requests per IP address** using a sliding window algorithm
2. **Enforces configurable limits** for requests per time window
3. **Provides standard HTTP headers** for client awareness
4. **Exempts specific endpoints** like health checks from rate limiting
5. **Handles proxy scenarios** by respecting X-Forwarded-For headers

### Architecture

```
┌─────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Client    │───▶│ Rate Limiting    │───▶│ Application     │
│   Request   │    │ Middleware       │    │ Endpoints       │
└─────────────┘    └──────────────────┘    └─────────────────┘
                            │
                            ▼
                   ┌──────────────────┐
                   │ In-Memory        │
                   │ Client Store     │
                   │ (Per IP)         │
                   └──────────────────┘
```

### Sliding Window Algorithm

The rate limiter uses a sliding window approach:

- Each IP address has a queue of request timestamps
- When a new request arrives, expired timestamps are removed
- If the current request count exceeds the limit, the request is rejected
- The window continuously "slides" with each request

## Configuration

### Default Settings

```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 1000,
    "WindowSize": "00:10:00"
  }
}
```

### Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxRequestsPerWindow` | `int` | 1000 | Maximum requests per IP per window |
| `WindowSize` | `TimeSpan` | 10 minutes | Size of the sliding window |

### Configuration Examples

#### Production Environment
```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 1000,
    "WindowSize": "00:10:00"
  }
}
```

#### Development Environment
```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 10000,
    "WindowSize": "00:01:00"
  }
}
```

#### High-Traffic Environment
```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 5000,
    "WindowSize": "00:05:00"
  }
}
```

#### Strict Security Environment
```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 100,
    "WindowSize": "00:15:00"
  }
}
```

### Environment Variable Configuration

You can also configure rate limiting via environment variables:

```bash
RateLimit__MaxRequestsPerWindow=500
RateLimit__WindowSize=00:05:00
```

## Response Headers

### Successful Requests

All successful requests include rate limit information headers:

```http
HTTP/1.1 200 OK
X-RateLimit-Limit: 1000
X-RateLimit-Remaining: 999
X-RateLimit-Reset: 1640995200
Content-Type: application/json
```

| Header | Description |
|--------|-------------|
| `X-RateLimit-Limit` | Maximum requests allowed in the window |
| `X-RateLimit-Remaining` | Requests remaining in current window |
| `X-RateLimit-Reset` | Unix timestamp when the window resets |

### Rate Limited Requests

When rate limits are exceeded:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 600
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.29",
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Rate limit exceeded. Maximum 1000 requests per 10 minutes.",
  "instance": "/rest/services/1/FeatureServer/0/query"
}
```

| Header | Description |
|--------|-------------|
| `Retry-After` | Seconds to wait before making another request |

## Exempted Endpoints

The following endpoints are exempt from rate limiting:

- `/healthz/*` - Health check endpoints
- `/_framework/*` - Blazor framework assets
- `/favicon.ico` - Favicon requests

Additionally, requests from localhost (loopback addresses) are exempted in development environments.

## IP Address Detection

The middleware detects client IP addresses in the following priority order:

1. **X-Forwarded-For header** - First IP in the comma-separated list
2. **X-Real-IP header** - Single IP address
3. **Connection RemoteIpAddress** - Direct connection IP

This ensures proper rate limiting when behind reverse proxies or load balancers.

### Proxy Configuration Examples

#### Nginx
```nginx
location / {
    proxy_pass http://backend;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Real-IP $remote_addr;
}
```

#### Traefik
```yaml
http:
  routers:
    api:
      rule: "Host(`api.example.com`)"
      service: honua-server
  services:
    honua-server:
      loadBalancer:
        servers:
        - url: "http://honua-server:8080"
```

## Monitoring and Logging

### Log Events

Rate limiting events are logged at the following levels:

#### Warning Level (EventId: 4101)
```json
{
  "timestamp": "2024-01-15T10:30:00Z",
  "level": "Warning",
  "messageTemplate": "Rate limit exceeded for client {ClientIp}. Limit: {MaxRequests} requests per {WindowMinutes} minutes",
  "properties": {
    "ClientIp": "203.0.113.10",
    "MaxRequests": 1000,
    "WindowMinutes": 10,
    "EventId": 4101
  }
}
```

#### Debug Level (EventId: 4102)
```json
{
  "timestamp": "2024-01-15T10:35:00Z",
  "level": "Debug",
  "messageTemplate": "Rate limit cache cleanup removed {ExpiredCount} expired client entries",
  "properties": {
    "ExpiredCount": 25,
    "EventId": 4102
  }
}
```

### Metrics and Alerting

Monitor the following metrics for operational excellence:

- **Rate limit violations per minute**
- **Top rate-limited IP addresses**
- **Rate limit cache size**
- **429 response rate**

Example query for rate limit violations:
```kusto
logs
| where Level == "Warning" and EventId == 4101
| summarize count() by bin(TimeGenerated, 1m), ClientIp
| top 10 by count_
```

## Troubleshooting

### Common Issues

#### 1. Legitimate Users Being Rate Limited

**Symptoms:**
- High volume of 429 responses
- Users reporting access issues
- Legitimate traffic patterns triggering limits

**Resolution:**
```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 2000,  // Increase limit
    "WindowSize": "00:10:00"       // Keep window same
  }
}
```

**Prevention:**
- Monitor request patterns
- Set alerts for 429 response rates > 1%
- Implement user-based rate limiting for authenticated APIs

#### 2. Rate Limiting Not Working

**Symptoms:**
- No rate limit headers in responses
- Abuse requests not being blocked
- No rate limiting logs

**Diagnostics:**
1. Verify middleware is registered:
   ```csharp
   app.UseRateLimiting(); // Should be before endpoints
   ```

2. Check configuration binding:
   ```json
   {
     "RateLimit": {
       "MaxRequestsPerWindow": 1000,
       "WindowSize": "00:10:00"
     }
   }
   ```

3. Verify environment is not development:
   ```bash
   ASPNETCORE_ENVIRONMENT=Production
   ```

#### 3. Inconsistent Rate Limiting Behind Proxy

**Symptoms:**
- Rate limiting varies by request
- Multiple IPs for same client
- Proxy headers not working

**Resolution:**
1. Ensure proxy sets proper headers:
   ```nginx
   proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
   proxy_set_header X-Real-IP $remote_addr;
   ```

2. Verify IP detection in logs:
   ```json
   {
     "ClientIp": "203.0.113.10",  // Should be actual client IP, not proxy IP
     "EventId": 4101
   }
   ```

#### 4. Memory Usage from Rate Limit Cache

**Symptoms:**
- Increasing memory usage over time
- Large number of IP addresses in cache
- Performance degradation

**Monitoring:**
```csharp
// Add memory monitoring for rate limit cache
var cacheSize = _clients.Count;
var memoryUsage = GC.GetTotalMemory(false);
```

**Mitigation:**
- Implement cache cleanup (not yet implemented)
- Consider Redis for distributed scenarios
- Monitor cache size in production

### Debug Mode

Enable detailed rate limiting logs:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "Honua.Server.Features.Infrastructure.Middleware.RateLimitingMiddleware": "Debug"
      }
    }
  }
}
```

### Testing Rate Limiting

#### Integration Test Example

```bash
# Test rate limiting with curl
for i in {1..20}; do
  curl -H "X-Forwarded-For: 203.0.113.100" \
       -w "%{http_code} " \
       http://localhost:8080/ogc/features
done
```

Expected output:
```
200 200 200 200 200 429 429 429 429 429 ...
```

#### Load Testing

Use tools like Apache Bench or Artillery for load testing:

```bash
# Apache Bench
ab -n 100 -c 10 -H "X-Forwarded-For: 203.0.113.200" http://localhost:8080/ogc/features

# Artillery
artillery run --target http://localhost:8080 rate-limit-test.yml
```

## Performance Considerations

### Memory Usage

- Each IP address creates a `ClientRateLimit` object
- Request timestamps are stored in memory queues
- Memory usage scales with unique IP count and request rate
- Expired entries are cleaned up per request

### CPU Usage

- Minimal CPU overhead per request
- O(n) complexity for expired entry cleanup (where n = requests in window)
- Thread-safe operations use locking

### Scalability

#### Single Instance
- Suitable for most deployments
- Handles thousands of concurrent IPs
- Memory-bounded by unique client count

#### Multi-Instance (Load Balanced)
- Each instance maintains separate rate limit state
- Effective rate limit = configured limit × instance count
- Consider distributed rate limiting (Redis) for strict enforcement

## Security Considerations

### Attack Vectors

1. **IP Spoofing**
   - Mitigated by proxy configuration
   - X-Forwarded-For validation at proxy level

2. **Distributed Attacks**
   - Rate limiting is per-IP, not global
   - Large botnets can still overwhelm with unique IPs

3. **Memory Exhaustion**
   - Unique IPs create cache entries
   - Monitor cache size in production

### Best Practices

1. **Use behind a reverse proxy** with proper IP forwarding
2. **Monitor rate limit metrics** and adjust limits based on usage patterns
3. **Implement graduated responses** (warnings before blocking)
4. **Use with other security layers** (WAF, DDoS protection)

## Future Enhancements

### Planned Improvements

1. **User-Based Rate Limiting**
   ```csharp
   // For authenticated requests
   services.AddRateLimiter(options => {
       options.AddPolicy("PerUser", context =>
           RateLimitPartition.CreateFixedWindowLimiter(
               partitionKey: context.User.Identity?.Name,
               factory: _ => new FixedWindowRateLimiterOptions {
                   Window = TimeSpan.FromMinutes(10),
                   PermitLimit = 1000
               }));
   });
   ```

2. **Redis-Based Distributed Rate Limiting**
   - Consistent limits across multiple instances
   - Shared state for horizontal scaling

3. **Dynamic Rate Limit Adjustment**
   - Based on server load and response times
   - Circuit breaker integration

4. **Advanced Cache Management**
   - Automatic cleanup of expired entries
   - LRU eviction for memory management

### Integration with Microsoft.AspNetCore.RateLimiting

For Beta/v2, consider migrating to ASP.NET Core's built-in rate limiting:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(10),
                PermitLimit = 1000
            }));
});
```

Benefits:
- Native ASP.NET Core integration
- More sophisticated algorithms
- Better telemetry and diagnostics
- Standardized configuration patterns