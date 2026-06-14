# ADR-0017: Redis Caching with Fallback Strategy

## Status
Accepted

## Context

Honua Server requires a caching strategy that can:
- Serve high-frequency geospatial data requests efficiently
- Maintain availability even when cache infrastructure fails
- Scale across multiple server instances
- Support complex geospatial data structures (layer metadata, feature catalogs)

The system handles geospatial protocols that frequently request the same metadata:
- Layer definitions and schemas
- Service catalogs and capabilities
- Spatial reference system information
- Feature counts and bounding boxes

Without caching, these requests result in expensive database queries for data that changes infrequently. However, pure in-memory caching doesn't scale across multiple instances, and pure distributed caching creates a single point of failure.

## Decision

Implement a **hybrid Redis caching strategy with graceful in-memory fallback**.

### Architecture

1. **Primary Cache**: Redis (via IDistributedCache with Aspire Redis integration)
   - Shared across all server instances
   - Persistent storage for complex geospatial metadata
   - Supports expiration and cache invalidation

2. **Fallback Cache**: Concurrent in-memory cache
   - Activates automatically when Redis is unavailable
   - Uses ConcurrentDictionary with TTL-based cleanup
   - Maintains service availability during Redis outages

3. **Health Monitoring**: Continuous Redis availability checking
   - Automatic switchover between Redis and fallback
   - Performance metrics tracking (cache hits/misses)
   - Proactive health reporting for monitoring systems

### Implementation Pattern

```csharp
// Primary cache with fallback
internal sealed class RedisCacheService : ICacheService, ICacheHealthChecker
{
    private readonly IDistributedCache? _distributedCache;
    private readonly ConcurrentDictionary<string, CacheEntry> _fallbackCache;
    private volatile bool _isUsingFallback;

    public async Task<T?> GetAsync<T>(string key)
    {
        // Try Redis first, fall back to in-memory on failure
        if (!_isUsingFallback && _distributedCache != null)
        {
            try
            {
                return await _distributedCache.GetAsync<T>(key);
            }
            catch (RedisException)
            {
                _isUsingFallback = true;
                // Fall through to in-memory cache
            }
        }

        return _fallbackCache.TryGetValue(key, out var entry) ? entry.Value : default;
    }
}
```

### Cache Strategy by Data Type

**Layer Metadata (High Priority)**
- Cache TTL: 1 hour
- Fallback TTL: 30 minutes
- Used by: All protocol endpoints

**Feature Counts (Medium Priority)**
- Cache TTL: 15 minutes
- Fallback TTL: 5 minutes
- Used by: Service capabilities, pagination

**Service Catalogs (Low Priority)**
- Cache TTL: 4 hours
- Fallback TTL: 1 hour
- Used by: Root endpoints, service discovery

## Consequences

### Positive
- **High Availability**: Service remains functional during Redis outages
- **Performance**: Redis provides optimal performance when available
- **Scalability**: Shared cache across multiple instances
- **Observability**: Built-in metrics for cache performance monitoring
- **Graceful Degradation**: Automatic fallback without service interruption

### Negative
- **Complexity**: Dual caching logic increases implementation complexity
- **Memory Usage**: Fallback cache consumes server memory
- **Cache Coherence**: Potential inconsistency between Redis and fallback caches
- **Testing Overhead**: Must test both cache paths and failure scenarios

### Operational Impact
- **Monitoring**: Requires alerts on cache hit ratio degradation
- **Deployment**: Redis must be provisioned but service tolerates its absence
- **Memory Planning**: Instance memory must accommodate fallback cache
- **Performance Tuning**: Cache TTL values may need adjustment based on usage patterns

### Migration Path
- Existing deployments automatically gain fallback capability
- Redis can be added to existing deployments without code changes
- Cache warming strategies can be implemented incrementally