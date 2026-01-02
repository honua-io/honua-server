# Honua Server Performance Monitoring

This document describes the comprehensive performance monitoring system implemented for the Honua Server project. The monitoring system provides detailed insights into application performance, resource usage, and operational health.

## Overview

The performance monitoring system includes:

- **Application Performance Counters** - HTTP request metrics, operation timing
- **Database Query Performance** - Query execution time, record counts, cache metrics
- **Memory Usage Tracking** - GC statistics, memory pressure monitoring
- **Request Duration Metrics** - Request/response timing with percentiles
- **Cache Hit/Miss Metrics** - Cache effectiveness across different types

## Architecture

### Core Components

1. **IPerformanceMonitor** (`Honua.Core`) - Core monitoring interface
2. **DefaultPerformanceMonitor** (`Honua.Core`) - .NET Metrics API implementation
3. **PerformanceMonitoringMiddleware** (`Honua.Server`) - HTTP request tracking
4. **MonitoredFeatureStoreDecorator** (`Honua.Postgres`) - Database query monitoring
5. **MonitoredResponseCacheDecorator** (`Honua.Core`) - Cache operation monitoring
6. **MetricsEndpoints** (`Honua.Server`) - Metrics exposition endpoints

### Integration Points

- **OpenTelemetry** - Automatic export of metrics, traces, and logs
- **Aspire Dashboard** - Built-in UI when OTLP is configured
- **ASP.NET Core** - Middleware pipeline integration
- **Dependency Injection** - Seamless service registration

## Configuration

### Basic Setup

The monitoring system is automatically configured when using `AddServiceDefaults()`:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add service defaults (includes performance monitoring)
builder.AddServiceDefaults();

var app = builder.Build();

// Map default endpoints (includes metrics endpoints)
app.MapDefaultEndpoints();
```

### Advanced Configuration

For custom configuration, use the performance monitoring options:

```csharp
builder.Services.Configure<PerformanceMonitoringOptions>(options =>
{
    options.EnableMemoryTracking = true;
    options.SlowRequestThreshold = TimeSpan.FromMilliseconds(500);
    options.MemorySamplingInterval = 50; // Sample every 50th request
    options.EnableDetailedRequestTracking = true;
});
```

## Available Metrics

### HTTP Request Metrics

- `honua_http_request_duration_ms` - Request duration histogram
- `honua_http_request_total` - Total request count
- `honua_http_active_requests` - Currently active requests

### Database Metrics

- `honua_database_query_duration_ms` - Query execution time
- `honua_database_query_total` - Total query count
- `honua_database_query_records` - Records returned/affected
- `honua_database_cache_hit_rate` - Database cache hit ratio

### Memory Metrics

- `honua_memory_allocated_bytes` - Currently allocated memory
- `honua_memory_heap_size_bytes` - Heap size
- `honua_memory_pressure_percent` - Memory pressure percentage
- `honua_gc_collection_total` - Garbage collection events

### Cache Metrics

- `honua_cache_operation_total` - Cache operations (hit/miss/eviction)
- `honua_cache_hit_ratio` - Cache hit ratio by type

### Operation Metrics

- `honua_operation_duration_ms` - Generic operation timing
- `honua_operation_total` - Operation execution count

## Metrics Endpoints

### Health Metrics (Public)

**Endpoint:** `GET /api/metrics/health`

Returns basic health information without authentication:

```json
{
  "status": "healthy",
  "timestamp": "2025-12-27T10:30:00Z",
  "memoryUsageMB": 125.5,
  "memoryPressurePercent": 15.2,
  "gcCollections": 42
}
```

### Detailed Performance Metrics (Authenticated)

**Endpoint:** `GET /api/metrics/performance`

Returns comprehensive performance data:

```json
{
  "timestamp": "2025-12-27T10:30:00Z",
  "memory": {
    "allocatedBytes": 131657728,
    "heapSizeBytes": 145920000,
    "memoryPressurePercentage": 15.2,
    "gen0Collections": 25,
    "gen1Collections": 12,
    "gen2Collections": 5
  },
  "systemInfo": {
    "processorCount": 8,
    "machineName": "server-01",
    "workingSet": 158720000,
    "frameworkVersion": "8.0.0"
  }
}
```

### Database Metrics (Authenticated)

**Endpoint:** `GET /api/metrics/database`

Returns database performance statistics:

```json
{
  "timestamp": "2025-12-27T10:30:00Z",
  "cacheHitRate": 0.85,
  "cacheHits": 1250,
  "cacheMisses": 220,
  "operations": {
    "query": {
      "count": 1500,
      "totalTimeMs": 45000,
      "maxTimeMs": 2500,
      "avgTimeMs": 30.0
    }
  }
}
```

## Monitoring Integration

### Aspire Dashboard

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to your Aspire dashboard receiver (for example, `http://aspire-dashboard:18889`).
When OTLP is configured, traces, metrics, and logs appear in the Aspire dashboard UI.

### Dashboards

Key metrics to monitor:

**Application Health:**
- Request rate and latency percentiles
- Error rate by endpoint
- Active request count

**Resource Usage:**
- Memory usage and pressure
- GC frequency and duration
- Database query performance

**Cache Effectiveness:**
- Hit/miss ratios by cache type
- Cache operation latencies
- Eviction rates

### Alerting

Configure alerting in your telemetry backend to match your SLOs and operational thresholds.

## Performance Thresholds

### Default Thresholds

- **Slow Request:** 1000ms
- **High Memory Pressure:** 80%
- **Memory Sampling:** Every 100 requests
- **Cache Warning Threshold:** <70% hit rate

### Recommended SLOs

**Response Time:**
- P50 < 100ms
- P95 < 500ms
- P99 < 1000ms

**Availability:**
- 99.9% uptime
- <0.1% error rate

**Resource Usage:**
- Memory pressure <60% normal operation
- Cache hit rate >80%
- Database query P95 <200ms

## Troubleshooting

### High Memory Usage

1. Check `honua_memory_pressure_percent` metric
2. Review GC collection frequency
3. Look for memory leaks in slow endpoints
4. Consider increasing memory limits

### Poor Database Performance

1. Monitor `honua_database_query_duration_ms` percentiles
2. Check cache hit rates by layer
3. Review slow query logs
4. Optimize frequently accessed queries

### Low Cache Effectiveness

1. Review cache hit/miss ratios by type
2. Check cache TTL configurations
3. Monitor eviction rates
4. Consider cache size adjustments

## Best Practices

### For Operations Teams

1. **Set up automated monitoring** using Aspire dashboard or your OTEL backend
2. **Configure meaningful alerts** for SLO violations
3. **Monitor trends** not just point-in-time values
4. **Use correlation** between metrics to diagnose issues

### For Development Teams

1. **Instrument custom operations** using `IPerformanceMonitor`
2. **Add meaningful tags** to metrics for better filtering
3. **Monitor during development** to catch performance regressions
4. **Review metrics in PR reviews** for performance impact

### Custom Instrumentation

```csharp
// Inject the performance monitor
private readonly IPerformanceMonitor _performanceMonitor;

// Use operation scopes for timing
using var scope = _performanceMonitor.StartOperation("custom_operation")
    .WithTag("layer", layerId)
    .WithTag("operation_type", "complex_query");

// Your operation code here
var result = await SomeExpensiveOperation();

// Scope disposal automatically records metrics
```

## Security Considerations

- Health endpoint (`/api/metrics/health`) is public
- Detailed metrics endpoints require authentication in production
- Metrics may contain sensitive information (response times, cache keys)
- Consider network-level restrictions for metrics endpoints
- Use HTTPS for metrics collection in production

## Performance Impact

The monitoring system is designed for minimal overhead:

- **Memory sampling** uses configurable intervals
- **Metrics collection** uses .NET's efficient Metrics API
- **Request tracking** adds <1ms per request
- **Database monitoring** uses decorator pattern for no structural changes
- **Cache monitoring** adds negligible overhead to cache operations

---

For technical implementation details, see the source code documentation in the `Honua.Core.Features.Infrastructure.Monitoring` namespace.
