# ADR-0016: Performance Optimization Strategies

## Status
Accepted

## Context

Geospatial feature servers face unique performance challenges:

**Geospatial-Specific Concerns:**
- **Large dataset queries**: PostGIS tables with millions of geographic features
- **Complex spatial operations**: Geometry intersections, transformations, buffering
- **Variable response sizes**: Single feature vs 10,000 features with complex polygons
- **Multiple coordinate systems**: On-the-fly transformations between SRIDs
- **Protocol diversity**: Same data served via multiple APIs (GeoServices, OGC, OData)

**Cloud-Native Requirements:**
- **Fast cold starts**: Sub-100ms initialization for serverless deployment
- **AOT compatibility**: Native compilation for optimal performance
- **Memory efficiency**: Handle large geometric datasets without memory explosion
- **Horizontal scaling**: Stateless operation across multiple instances

**Multi-Protocol Efficiency:**
- **Zero duplication**: Same business logic serving all protocols
- **Format optimization**: Efficient serialization for JSON, GeoJSON, PBF
- **Caching strategies**: Protocol-agnostic response caching

## Decision

Implement **comprehensive performance optimization strategy** across multiple dimensions:

### 1. AOT-First Architecture

**Native Compilation Strategy:**
```csharp
// PublishAot=true for production builds
// Source generators for all reflection-heavy operations
// Zero runtime reflection in hot paths

// JSON Serialization - Source Generated
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FeatureQueryResult))]
[JsonSerializable(typeof(Feature))]
[JsonSerializable(typeof(Geometry))]
internal partial class FeatureServerJsonContext : JsonSerializerContext
{
}

// Logging - Source Generated
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Executing spatial query for layer {LayerId} with filter {Filter}")]
    internal static partial void SpatialQueryExecuting(ILogger logger, int layerId, string? filter);
}

// Configuration - Compile-time binding
builder.Services.Configure<DatabaseConfig>(builder.Configuration.GetSection("Database"));
```

### 2. Database Performance Strategy

**PostGIS Optimization Patterns:**
```csharp
// Raw Npgsql for optimal performance - no ORM overhead
internal class PostgresFeatureStore : IFeatureStore
{
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query)
    {
        var sql = BuildOptimizedQuery(layerId, query);
        var parameters = BuildParameters(query);

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);

        // Streaming reader for large result sets
        await using var reader = await command.ExecuteReaderAsync();

        var features = new List<Feature>();
        while (await reader.ReadAsync())
        {
            // Direct field access by ordinal (fastest)
            var feature = new Feature
            {
                Id = reader.GetInt64(0),
                Geometry = reader.IsDBNull(1) ? null :
                    await GeometryConverter.FromPostGisAsync(reader, 1),
                Attributes = ReadAttributes(reader, layerDefinition.Fields)
            };
            features.Add(feature);
        }

        return new QueryResult<Feature>(features, hasMore: features.Count == query.Limit);
    }

    private static string BuildOptimizedQuery(int layerId, FeatureQuery query)
    {
        var sql = new StringBuilder();

        // Use prepared statement patterns for optimal query plan caching
        sql.AppendLine($"SELECT id, ST_AsBinary(geometry) as geom, attributes");
        sql.AppendLine($"FROM features WHERE layer_id = $1");

        // Spatial index utilization
        if (query.SpatialFilter != null)
        {
            sql.AppendLine("AND geometry && $2::geometry");  // Use spatial index
            sql.AppendLine("AND ST_Intersects(geometry, $2::geometry)");  // Exact check
        }

        // Attribute filtering with proper index hints
        if (!string.IsNullOrEmpty(query.Where))
        {
            sql.AppendLine($"AND ({query.Where})");
        }

        // Efficient pagination using cursor-based approach for large offsets
        if (query.Offset > 10000)
        {
            sql.AppendLine("ORDER BY id");
            sql.AppendLine($"LIMIT {query.Limit + 1}");  // +1 for hasMore detection
        }
        else
        {
            sql.AppendLine($"ORDER BY id OFFSET {query.Offset} LIMIT {query.Limit + 1}");
        }

        return sql.ToString();
    }
}
```

### 3. Memory Management Strategy

**Efficient Geometry Handling:**
```csharp
// Stream-based geometry processing for large datasets
internal class StreamingGeometryConverter : IGeometryConverter
{
    // Use span-based operations for geometry coordinate arrays
    public async ValueTask<Geometry> FromPostGisAsync(NpgsqlDataReader reader, int ordinal)
    {
        var binaryData = await reader.GetFieldValueAsync<byte[]>(ordinal);
        var span = binaryData.AsSpan();

        return ParseWkb(span);  // Zero-copy parsing where possible
    }

    private static Geometry ParseWkb(ReadOnlySpan<byte> wkb)
    {
        // Use stackalloc for small coordinate arrays
        if (wkb.Length < 1024)
        {
            Span<double> coordinates = stackalloc double[256];
            return ParseGeometry(wkb, coordinates);
        }

        // Use ArrayPool for larger geometries
        var pool = ArrayPool<double>.Shared;
        var coordinates = pool.Rent(wkb.Length / 8);
        try
        {
            return ParseGeometry(wkb, coordinates);
        }
        finally
        {
            pool.Return(coordinates);
        }
    }
}

// Pooled string builders for dynamic SQL generation
internal class QueryBuilder
{
    private static readonly ObjectPool<StringBuilder> _builderPool =
        new DefaultObjectPool<StringBuilder>(
            new StringBuilderPooledObjectPolicy
            {
                InitialCapacity = 256,
                MaximumRetainedCapacity = 4096
            });

    public string BuildQuery(/* parameters */)
    {
        var builder = _builderPool.Get();
        try
        {
            // Build query
            return builder.ToString();
        }
        finally
        {
            _builderPool.Return(builder);
        }
    }
}
```

### 4. Caching Strategy

**Multi-Level Caching:**
```csharp
// L1: In-memory response caching (feature flags, layer metadata)
internal class MemoryResponseCache : IResponseCache
{
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _defaultOptions;

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        return _cache.Get<T>(key);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration) where T : class
    {
        _cache.Set(key, value, expiration);
    }
}

// L2: Distributed Redis caching (large query results)
internal class DistributedResponseCache : IResponseCache
{
    private readonly IDistributedCache _cache;
    private readonly JsonSerializerOptions _serializerOptions;

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var cached = await _cache.GetStringAsync(key);
        if (cached == null) return null;

        return JsonSerializer.Deserialize<T>(cached, _serializerOptions);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration) where T : class
    {
        var serialized = JsonSerializer.Serialize(value, _serializerOptions);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration
        };

        await _cache.SetStringAsync(key, serialized, options);
    }
}

// Intelligent cache key generation
internal static class CacheKeyGenerator
{
    public static string ForQuery(int layerId, FeatureQuery query, string format)
    {
        // Create deterministic cache key
        var keyBuilder = new StringBuilder();
        keyBuilder.Append($"query:layer:{layerId}");
        keyBuilder.Append($":format:{format}");

        if (!string.IsNullOrEmpty(query.Where))
        {
            keyBuilder.Append($":where:{query.Where.GetHashCode():X}");
        }

        if (query.SpatialFilter != null)
        {
            keyBuilder.Append($":spatial:{query.SpatialFilter.GetHashCode():X}");
        }

        keyBuilder.Append($":limit:{query.Limit}:offset:{query.Offset}");

        return keyBuilder.ToString();
    }
}

// Cache-aside pattern with circuit breaker
internal class CachedFeatureStore : IFeatureStore
{
    private readonly IFeatureStore _inner;
    private readonly IResponseCache _cache;
    private readonly ILogger<CachedFeatureStore> _logger;

    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query)
    {
        var cacheKey = CacheKeyGenerator.ForQuery(layerId, query, "json");

        try
        {
            // Try cache first
            var cached = await _cache.GetAsync<QueryResult<Feature>>(cacheKey);
            if (cached != null)
            {
                Log.CacheHit(_logger, cacheKey);
                return cached;
            }
        }
        catch (Exception ex)
        {
            Log.CacheError(_logger, ex, cacheKey);
            // Fall through to direct query
        }

        // Cache miss - query database
        var result = await _inner.QueryAsync(layerId, query);

        try
        {
            // Cache successful results
            var expiration = CalculateCacheExpiration(query);
            await _cache.SetAsync(cacheKey, result, expiration);
        }
        catch (Exception ex)
        {
            Log.CacheSetError(_logger, ex, cacheKey);
            // Don't fail query due to cache issues
        }

        return result;
    }

    private static TimeSpan CalculateCacheExpiration(FeatureQuery query)
    {
        // Longer cache for simple queries, shorter for complex ones
        return string.IsNullOrEmpty(query.Where)
            ? TimeSpan.FromMinutes(15)  // Simple queries cached longer
            : TimeSpan.FromMinutes(5);   // Filtered queries cached shorter
    }
}
```

### 5. Serialization Performance

**Protocol-Specific Optimization:**
```csharp
// Format-specific serialization optimizations
internal class OptimizedQueryFormatter : IQueryFormatter
{
    private readonly JsonSerializerOptions _esriOptions;
    private readonly JsonSerializerOptions _geoJsonOptions;

    public (object response, string contentType) FormatQueryResult(
        QueryResult<Feature> result,
        LayerDefinition layer,
        string format,
        bool returnGeometry,
        string[]? outFields)
    {
        return format.ToLowerInvariant() switch
        {
            "json" => FormatEsriJson(result, layer, returnGeometry, outFields),
            "geojson" => FormatGeoJson(result, returnGeometry, outFields),
            "pbf" => FormatProtobuf(result, layer, returnGeometry, outFields),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };
    }

    private (object, string) FormatEsriJson(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        string[]? outFields)
    {
        // Use source-generated JSON context for optimal performance
        var response = new EsriFeatureSet
        {
            ObjectIdFieldName = layer.ObjectIdField,
            GlobalIdFieldName = layer.GlobalIdField,
            GeometryType = layer.GeometryType,
            SpatialReference = new { Wkid = layer.SpatialReference },
            Features = result.Features.Select(f => new EsriFeature
            {
                Geometry = returnGeometry ? ConvertToEsriGeometry(f.Geometry) : null,
                Attributes = FilterAttributes(f.Attributes, outFields)
            }).ToArray(),
            ExceededTransferLimit = result.HasMore
        };

        var json = JsonSerializer.Serialize(response, FeatureServerJsonContext.Default.EsriFeatureSet);
        return (json, "application/json");
    }

    private (object, string) FormatGeoJson(
        QueryResult<Feature> result,
        bool returnGeometry,
        string[]? outFields)
    {
        // Optimized GeoJSON generation
        var features = result.Features.Select(f => new GeoJsonFeature
        {
            Type = "Feature",
            Id = f.Id,
            Geometry = returnGeometry ? ConvertToGeoJsonGeometry(f.Geometry) : null,
            Properties = FilterAttributes(f.Attributes, outFields)
        }).ToArray();

        var featureCollection = new GeoJsonFeatureCollection
        {
            Type = "FeatureCollection",
            Features = features
        };

        var json = JsonSerializer.Serialize(featureCollection, OgcJsonContext.Default.GeoJsonFeatureCollection);
        return (json, "application/geo+json");
    }
}
```

### 6. Connection Pool Optimization

**PostgreSQL Connection Management:**
```csharp
// Optimized connection pool configuration
public static class DatabaseConfiguration
{
    public static void ConfigureNpgsql(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString)
            // Enable PostGIS
            .UsePostGis()
            // Optimize for high-throughput scenarios
            .EnableParameterLogging(false)  // Disable for performance
            .EnableSensitiveDataLogging(false);

        // Connection pool optimization
        var csBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Pool settings for high-concurrency
            MinPoolSize = 5,                    // Keep minimum connections warm
            MaxPoolSize = 100,                  // Allow scaling for load
            ConnectionPruningInterval = 10,     // Clean up idle connections
            ConnectionIdleLifetime = 300,       // 5 minute idle timeout

            // Performance settings
            CommandTimeout = 30,                // 30 second query timeout
            ReadBufferSize = 8192,             // 8KB read buffer
            WriteBufferSize = 8192,            // 8KB write buffer

            // Reduce handshake overhead
            SslMode = SslMode.Disable,         // Use SSL termination at load balancer
            TcpKeepAlive = true,               // Keep connections alive
            KeepaliveInterval = 30,            // TCP keepalive interval

            // Application-level optimizations
            ApplicationName = "HonuaServer",
            SearchPath = "public,postgis"      // Avoid schema search overhead
        };

        dataSourceBuilder.ConnectionStringBuilder.ConnectionString = csBuilder.ToString();

        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);
    }
}
```

### 7. Monitoring and Performance Tracking

**Built-in Performance Monitoring:**
```csharp
internal static partial class PerformanceLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Query completed: Layer={LayerId}, Features={FeatureCount}, " +
                 "Duration={DurationMs}ms, CacheHit={CacheHit}")]
    internal static partial void QueryPerformance(
        ILogger logger, int layerId, int featureCount, double durationMs, bool cacheHit);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Slow query detected: Layer={LayerId}, Duration={DurationMs}ms, " +
                 "Filter={Filter}")]
    internal static partial void SlowQuery(
        ILogger logger, int layerId, double durationMs, string? filter);
}

// Performance middleware
internal class PerformanceTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PerformanceTrackingMiddleware> _logger;

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > 1000)  // Log slow requests
            {
                PerformanceLog.SlowRequest(_logger,
                    context.Request.Method,
                    context.Request.Path,
                    stopwatch.ElapsedMilliseconds);
            }

            // Add performance headers for monitoring
            context.Response.Headers.Add("X-Duration-Ms", stopwatch.ElapsedMilliseconds.ToString());
        }
    }
}

// Custom metrics for APM
internal class GeospatialMetrics
{
    private readonly Counter<long> _queryCounter;
    private readonly Histogram<double> _queryDuration;
    private readonly Histogram<long> _featureCount;

    public void RecordQuery(string protocol, string operation, double durationMs, int featureCount, bool cacheHit)
    {
        _queryCounter.Add(1,
            new("protocol", protocol),
            new("operation", operation),
            new("cache_hit", cacheHit.ToString()));

        _queryDuration.Record(durationMs,
            new("protocol", protocol),
            new("operation", operation));

        _featureCount.Record(featureCount,
            new("protocol", protocol),
            new("operation", operation));
    }
}
```

## Performance Targets

### Response Time Targets
| Operation | Target (P95) | Maximum (P99) |
|-----------|--------------|---------------|
| Simple Query (< 100 features) | 50ms | 100ms |
| Complex Query (< 1000 features) | 200ms | 500ms |
| Large Query (< 10000 features) | 1000ms | 2000ms |
| Layer Metadata | 10ms | 25ms |
| Health Check | 5ms | 10ms |

### Throughput Targets
| Scenario | Target RPS | Resource Usage |
|----------|------------|----------------|
| Cached Queries | 10,000 RPS | < 512MB RAM |
| Database Queries | 1,000 RPS | < 1GB RAM |
| Mixed Workload | 5,000 RPS | < 768MB RAM |

### Resource Efficiency
- **Cold Start**: < 100ms to first request
- **Memory Usage**: < 1GB for 10,000 concurrent requests
- **CPU Efficiency**: > 90% utilization under load
- **Cache Hit Rate**: > 80% for repeated queries

## Consequences

### Positive
- **Fast Cold Starts**: AOT compilation enables sub-100ms startup
- **High Throughput**: Optimized database access and caching support thousands of RPS
- **Memory Efficiency**: Span-based operations and object pooling minimize allocations
- **Predictable Performance**: Comprehensive monitoring and circuit breakers prevent cascade failures
- **Cost Effectiveness**: Lower resource usage reduces cloud hosting costs

### Negative
- **Complexity**: Multiple optimization layers increase implementation complexity
- **Cache Coherency**: Cached responses may become stale
- **Development Overhead**: Performance monitoring adds instrumentation requirements
- **Memory vs Speed Tradeoffs**: Caching uses memory to improve response times

### Mitigation
- **Automated Testing**: Performance regression tests in CI/CD pipeline
- **Cache Invalidation**: Time-based expiration with manual invalidation capabilities
- **Gradual Rollout**: Feature flags for enabling optimizations incrementally
- **Monitoring Alerts**: Automated alerts for performance degradation

## Implementation Phases

### Phase 1: Foundation (Completed)
- ✅ AOT-compatible architecture
- ✅ Raw Npgsql for database access
- ✅ Source-generated JSON serialization
- ✅ Basic response caching

### Phase 2: Advanced Optimization (Planned)
- Connection pool tuning based on production metrics
- Geometry streaming for large datasets
- Distributed caching with Redis
- Advanced query optimization

### Phase 3: Scale Optimization (Future)
- Read replicas for query load distribution
- Horizontal partitioning by layer
- CDN integration for static responses
- Background precomputation of common queries

## Related ADRs
- [ADR-0001](0001-raw-npgsql-no-orm.md): Raw Npgsql provides optimal database performance
- [ADR-0012](0012-clean-architecture-implementation.md): Clean Architecture enables performance layer isolation
- [ADR-0013](0013-minimal-apis-vs-controllers.md): Minimal APIs reduce HTTP overhead
- [ADR-0009](0009-shared-filter-ast.md): Shared filter AST enables query optimization across protocols

## Performance Monitoring

### Key Performance Indicators
- **Response Time**: P95/P99 response times by endpoint
- **Throughput**: Requests per second by protocol
- **Cache Hit Rate**: Percentage of requests served from cache
- **Error Rate**: 4xx/5xx error percentage
- **Resource Utilization**: CPU/Memory usage under load

### Alerting Thresholds
```yaml
alerts:
  - name: HighResponseTime
    condition: p95_response_time > 200ms for 5 minutes
  - name: LowThroughput
    condition: requests_per_second < 100 for 5 minutes
  - name: LowCacheHitRate
    condition: cache_hit_rate < 70% for 10 minutes
  - name: HighErrorRate
    condition: error_rate > 1% for 5 minutes
```

This comprehensive performance strategy ensures Honua Server can efficiently serve geospatial data at scale while maintaining sub-second response times and optimal resource utilization.