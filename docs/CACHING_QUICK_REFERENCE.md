# Honua Server Caching Quick Reference

## Cache Types Overview

| Cache Type | Purpose | Implementation | Scope |
|------------|---------|----------------|-------|
| Application Cache | Domain objects, metadata | `MemoryResponseCache` | Per-instance |
| Output Cache | HTTP responses | ASP.NET Core | Per-instance |
| Internal Cache | SRID lookups, geometry | Domain-specific | Per-instance |

## Common Cache Operations

### Application Cache (IResponseCache)

```csharp
// Inject dependency
private readonly IResponseCache _cache;

// Get or create pattern (recommended)
var metadata = await _cache.GetOrCreateAsync(
    $"layer:definition:{layerId}",
    async () => await GetFromDatabase(layerId),
    TimeSpan.FromMinutes(30));

// Explicit set
await _cache.SetAsync("key", value, TimeSpan.FromMinutes(5));

// Get cached value
var cached = await _cache.GetAsync<MyType>("key");

// Pattern-based invalidation
await _cache.RemoveByPatternAsync("layer:*");
```

### Output Cache (Endpoints)

```csharp
// Apply cache policy to endpoint
app.MapGet("/api/metadata", GetMetadata)
   .CacheOutput("ServiceMetadata");

// Conditional caching
app.MapGet("/api/data", GetData)
   .CacheOutput(policy => policy
       .Expire(TimeSpan.FromMinutes(10))
       .VaryByQuery("format", "version")
       .Tag("data"));
```

## Cache Key Patterns

### Naming Convention
```
{domain}:{type}:{identifier}[:{subtype}]
```

### Examples
```csharp
// Layer definitions
$"layer:definition:{layerId}"

// Service metadata
$"service:metadata:{serviceId}"

// Spatial reference
$"spatial:reference:{srid}"

// User-specific data
$"user:{userId}:preferences"

// Existence checks
$"layer:exists:{layerId}"
$"service:exists:{serviceName}"
```

## Cache Expiration Guidelines

| Data Type | Recommended TTL | Rationale |
|-----------|----------------|-----------|
| Layer Schema | 30 minutes | Infrequent changes |
| Service Config | 5 minutes | May change during admin |
| SRID Definitions | 24 hours | Very stable |
| User Sessions | 20 minutes | Security requirement |
| MVT Tiles | 1 hour | Balance freshness/performance |
| API Documentation | 1 hour | Rarely changes |

## Output Cache Policies

### Standard Policies
```csharp
// Metadata endpoints (5 min)
.CacheOutput("ServiceMetadata")
.CacheOutput("LayerMetadata")

// OGC endpoints (10-60 min)
.CacheOutput("OgcCollections")      // 10 min
.CacheOutput("OgcConformance")      // 60 min

// Tile endpoints (60 min)
.CacheOutput("MvtTile")
```

### Custom Policy Example
```csharp
options.AddPolicy("CustomData", policy =>
{
    policy.Expire(TimeSpan.FromMinutes(15));
    policy.SetVaryByRouteValue("id");
    policy.SetVaryByQuery("format");
    policy.Tag("custom");
});
```

## Cache Invalidation Patterns

### Event-Based Invalidation
```csharp
// Layer schema changed
await _cache.RemoveByPatternAsync($"layer:*:{layerId}");

// Service configuration changed
await _cache.RemoveByPatternAsync($"service:*:{serviceId}");

// User permissions changed
await _cache.RemoveByPatternAsync($"user:{userId}:*");
```

### Tag-Based Invalidation (Output Cache)
```csharp
// Invalidate all metadata
await _outputCacheStore.EvictByTagAsync("metadata", CancellationToken.None);

// Invalidate specific service
await _outputCacheStore.EvictByTagAsync($"service-{serviceId}", CancellationToken.None);
```

## Performance Monitoring

### Hit Rate Queries
```csharp
// SRID cache metrics
var metrics = PostgresFeatureStore.PerformanceMetrics.GetMetrics();
var hitRate = metrics["cache_hit_rate"];

// Memory cache statistics (if enabled)
var stats = _memoryCache.GetCurrentStatistics();
var hitRatio = stats.TotalHits / (double)(stats.TotalHits + stats.TotalMisses);
```

### Health Check
```csharp
// Test cache functionality
var testKey = $"health:check:{Guid.NewGuid()}";
await _cache.SetAsync(testKey, "test", TimeSpan.FromSeconds(1));
var result = await _cache.GetAsync<string>(testKey);
await _cache.RemoveAsync(testKey);
```

## Configuration Examples

### Memory Cache Limits
```csharp
services.Configure<MemoryCacheOptions>(options =>
{
    options.SizeLimit = 10000;                                    // Max entries
    options.CompactionPercentage = 0.25;                         // Remove 25% when full
    options.ExpirationScanFrequency = TimeSpan.FromMinutes(1);   // Cleanup interval
});
```

### Environment-Specific Settings
```json
{
  "Development": {
    "Cache": {
      "DefaultTTL": "00:00:30",
      "MaxSize": 1000
    }
  },
  "Production": {
    "Cache": {
      "DefaultTTL": "00:05:00",
      "MaxSize": 50000
    }
  }
}
```

### Metadata Cache Options
```json
{
  "Cache": {
    "NegativeTtlSeconds": 30,
    "JitterPercentage": 0.2
  }
}
```

## Troubleshooting

### Common Issues

#### Low Hit Rates
```csharp
// Check key consistency
logger.LogInformation("Cache key: {Key}", cacheKey);

// Verify TTL isn't too short
var entry = _cache.GetAsync<object>(key);
if (entry == null)
{
    logger.LogWarning("Cache miss for {Key}", key);
}
```

#### Memory Pressure
```csharp
// Monitor cache size
var stats = _memoryCache.GetCurrentStatistics();
logger.LogInformation("Cache entries: {Count}, Size: {Size}",
    stats.CurrentEntryCount, stats.CurrentEstimatedSize);

// Enable compaction
services.Configure<MemoryCacheOptions>(options =>
{
    options.SizeLimit = 5000;  // Reduce limit
    options.CompactionPercentage = 0.50;  // More aggressive cleanup
});
```

#### Stale Data
```csharp
// Implement cache warming
public async Task WarmCacheAsync()
{
    var layers = await _dataStore.GetAllLayersAsync();
    foreach (var layer in layers)
    {
        await _cache.GetOrCreateAsync(
            $"layer:definition:{layer.Id}",
            () => Task.FromResult(layer),
            TimeSpan.FromMinutes(30));
    }
}
```

### Debug Cache Behavior
```csharp
// Add cache logging
services.AddSingleton<IResponseCache>(provider =>
{
    var cache = new MemoryResponseCache(
        provider.GetRequiredService<IMemoryCache>(),
        provider.GetRequiredService<ILogger<MemoryResponseCache>>());

    return new LoggingCacheDecorator(cache, logger);  // Wrap with logging
});

public class LoggingCacheDecorator : IResponseCache
{
    private readonly IResponseCache _inner;
    private readonly ILogger _logger;

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var result = await _inner.GetAsync<T>(key, cancellationToken);
        _logger.LogDebug("Cache {Operation} for key {Key}: {Status}",
            "GET", key, result != null ? "HIT" : "MISS");
        return result;
    }
    // ... other methods with logging
}
```

## Multi-Instance Considerations

### Current Limitations
- In-memory cache is per-instance
- No automatic synchronization
- Manual invalidation required

### Solutions
1. **Redis Distributed Cache** - Shared cache across instances
2. **Hybrid Strategy** - Local + distributed cache
3. **Cache Bus** - Invalidation message coordination

### Quick Multi-Instance Setup
```csharp
// Add Redis for distributed caching
services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = connectionString;
    options.InstanceName = "Honua";
});

// Replace local cache for critical data
services.AddSingleton<IResponseCache, DistributedResponseCache>();
```

## Best Practices Checklist

### Development
- [ ] Use consistent cache key naming
- [ ] Set appropriate TTL for data volatility
- [ ] Implement cache warming for critical data
- [ ] Add cache metrics to monitoring
- [ ] Test cache invalidation scenarios

### Operations
- [ ] Monitor hit rates (target >80%)
- [ ] Set memory limits appropriate to environment
- [ ] Configure alerts for cache health
- [ ] Plan for multi-instance deployments
- [ ] Document cache dependencies

### Security
- [ ] Don't cache sensitive data without encryption
- [ ] Respect user permissions in cache keys
- [ ] Implement tenant isolation if required
- [ ] Log cache operations for auditing
- [ ] Clear sensitive data from cache on logout

## Resource Links

- **Comprehensive Documentation**: `docs/CACHING_STRATEGY.md`
- **Implementation**: `src/Honua.Server/Features/Infrastructure/Caching/`
- **Tests**: `tests/Honua.Server.Tests/Infrastructure/Caching/`
- **Performance Report**: `docs/MEMORY_OPTIMIZATIONS_REPORT.md`
