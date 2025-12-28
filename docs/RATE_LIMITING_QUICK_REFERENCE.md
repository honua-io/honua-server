# Rate Limiting Quick Reference

## TL;DR

Honua Server has built-in rate limiting middleware that protects against abuse with a sliding window algorithm. Default: **1000 requests per 10 minutes per IP**.

## Quick Configuration

### Environment Variables
```bash
export RateLimit__MaxRequestsPerWindow=1000
export RateLimit__WindowSize=00:10:00
```

### appsettings.json
```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 1000,
    "WindowSize": "00:10:00"
  }
}
```

## Common Scenarios

| Scenario | MaxRequests | Window | Use Case |
|----------|-------------|---------|----------|
| **Production Default** | 1000 | 10 min | Standard production deployment |
| **Development** | 10000 | 1 min | Local development, high limits |
| **High Traffic** | 5000 | 5 min | High-volume production |
| **Strict Security** | 100 | 15 min | Security-sensitive environments |
| **Behind API Gateway** | 10000 | 1 min | Gateway handles primary limiting |

## Response Headers

### Successful Request
```http
X-RateLimit-Limit: 1000
X-RateLimit-Remaining: 999
X-RateLimit-Reset: 1640995200
```

### Rate Limited (429)
```http
HTTP/1.1 429 Too Many Requests
Retry-After: 600
```

## Exempted Endpoints

- `/healthz/*` (health checks)
- `/_framework/*` (Blazor assets)
- `/favicon.ico`
- Localhost requests (development)

## Quick Testing

```bash
# Test rate limiting with curl
for i in {1..20}; do
  curl -H "X-Forwarded-For: 203.0.113.100" \
       -w "%{http_code} " \
       http://localhost:8080/ogc/features
done
```

Expected: First 5-10 requests succeed (200), then 429 responses.

## Monitoring

### Key Metrics
- 429 response rate (should be < 1% normally)
- Rate limit violations per minute
- Top rate-limited IP addresses

### Log Query (Structured Logging)
```json
{
  "level": "Warning",
  "eventId": 4101,
  "message": "Rate limit exceeded for client {ClientIp}"
}
```

## Quick Troubleshooting

### Issue: Legitimate users getting 429
**Fix:** Increase `MaxRequestsPerWindow` or decrease `WindowSize`

### Issue: Rate limiting not working
**Check:**
1. Middleware registered: `app.UseRateLimiting()`
2. Not in development mode
3. Proxy headers configured

### Issue: Different limits per instance
**Cause:** Load balancer distributing to multiple instances
**Fix:** Add proxy-level rate limiting or use Redis (future)

## Architecture

```
Client Request → Rate Limit Check → Application
                      ↓
               In-Memory IP Tracker
```

- **Algorithm**: Sliding window
- **Storage**: In-memory per instance
- **Scope**: Per IP address
- **Thread Safety**: Yes (concurrent dictionary + locks)

## Integration

### With Reverse Proxy
```nginx
# nginx.conf
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Real-IP $remote_addr;
```

### With Docker
```yaml
# docker-compose.yml
services:
  honua:
    environment:
      - RateLimit__MaxRequestsPerWindow=1000
      - RateLimit__WindowSize=00:10:00
```

## Full Documentation

See [RATE_LIMITING.md](RATE_LIMITING.md) for comprehensive documentation including:
- Detailed troubleshooting
- Security considerations
- Performance implications
- Future enhancements