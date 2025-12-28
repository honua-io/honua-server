# ADR-0004: Application-Level Rate Limiting

## Status
Superseded - Implementation changed during development

## Context
MVP needs protection against abuse and runaway queries. Options:
- ASP.NET Core Rate Limiting middleware
- Reverse proxy rate limiting (nginx, Traefik)
- API Gateway (Kong, YARP)

## Decision
**UPDATED**: Implemented application-level rate limiting middleware instead of proxy-based approach.

**Rationale for Change:**
- Provides immediate protection without proxy dependency
- Enables fine-grained control over rate limiting behavior
- Supports testing and development scenarios without proxy setup
- Allows for future enhancements like user-based rate limiting
- Standard HTTP headers for client awareness

## Implementation Details

### Current Implementation
- Custom `RateLimitingMiddleware` using sliding window algorithm
- Per-IP address tracking with configurable limits
- Default: 1000 requests per 10-minute window
- Exempts health checks and development endpoints
- Proper proxy support via X-Forwarded-For headers

### Configuration
```json
{
  "RateLimit": {
    "MaxRequestsPerWindow": 1000,
    "WindowSize": "00:10:00"
  }
}
```

## Consequences

### Positive
- Works out-of-the-box without proxy configuration
- Consistent behavior across all deployment scenarios
- Comprehensive testing coverage with integration tests
- Detailed logging and monitoring capabilities
- Standard HTTP headers (X-RateLimit-*, Retry-After)

### Negative
- Additional application complexity and memory usage
- Per-instance rate limiting (not global across load-balanced instances)
- Requires careful tuning for different environments

### Migration Path
For production deployments requiring strict global limits:
1. Keep current implementation for baseline protection
2. Add proxy-level rate limiting for additional protection
3. Future: Consider Redis-based distributed rate limiting

## Documentation
See [RATE_LIMITING.md](../RATE_LIMITING.md) for comprehensive configuration and troubleshooting guide.

## Future Enhancements
- Migration to `Microsoft.AspNetCore.RateLimiting` in Beta
- Redis-based distributed rate limiting for multi-instance deployments
- User-based rate limiting for authenticated endpoints
