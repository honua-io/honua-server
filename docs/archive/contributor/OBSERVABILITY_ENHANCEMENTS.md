# Enhanced Observability and Monitoring Implementation Guide

## Overview

This document outlines the comprehensive observability enhancements implemented for the Honua Server to support production deployment. These enhancements provide detailed performance monitoring, memory pressure tracking, cache optimization insights, and geospatial operation visibility.

## Implementation Summary

### 1. Enhanced Latency Tracking (P50, P95, P99)

**Location**: `src/Honua.Core/Features/Infrastructure/Monitoring/DefaultPerformanceMonitor.cs`

**Features**:
- **LatencyTracker class**: Calculates P50, P95, P99 percentiles for all operations
- **Per-operation tracking**: Separate latency tracking for different operation types
- **Real-time percentile calculation**: Efficient sliding window approach

**Usage**:
```csharp
// Automatic tracking through operation scopes
using var scope = performanceMonitor.StartOperation("database_query")
    .WithTag("query_type", "spatial")
    .WithTag("layer_id", layerId.ToString());

// Manual tracking with enhanced context
performanceMonitor.RecordGeospatialOperation(
    "coordinate_transform",
    duration,
    coordinateCount,
    fromSrid,
    toSrid);
```

### 2. Memory Pressure Monitoring

**Location**: `src/Honua.Core/Features/Infrastructure/Monitoring/MemoryPressureMonitoringService.cs`

**Features**:
- **Background monitoring**: Continuous memory pressure tracking every 30 seconds
- **Alert thresholds**: Warning at 80%, Critical at 90% memory usage
- **GC efficiency tracking**: Monitors garbage collection effectiveness
- **Memory leak detection**: Identifies patterns suggesting memory leaks

**Metrics**:
- `honua_memory_pressure_percent`: Current memory pressure percentage
- `honua_memory_pressure_alerts_total`: Count of high memory pressure alerts
- `honua_memory_allocated_mb`: Current allocated memory in MB

### 3. Enhanced Cache Monitoring

**Location**: Updated `src/Honua.Server/Features/Infrastructure/Caching/RedisCacheService.cs`

**Features**:
- **Operation latency tracking**: P50/P95/P99 for cache operations
- **Hit/miss ratio analysis**: Detailed cache performance metrics
- **Source tracking**: Distinguishes between Redis and fallback cache hits
- **Error tracking**: Monitors cache operation failures

**Metrics**:
- `honua_cache_operation_duration_ms`: Cache operation latency histogram
- `honua_cache_hit_ratio_detailed`: Enhanced hit ratio tracking
- `honua_cache_errors_total`: Cache operation error counter

### 4. Geospatial Performance Counters

**Location**: `src/Honua.Server/Features/MapServer/Rendering/MonitoredCoordinateTransformer.cs`

**Features**:
- **Coordinate transformation monitoring**: Tracks all SRID conversions
- **Spatial query performance**: Monitors spatial filter operations
- **Geometry complexity tracking**: Records coordinate count for operations
- **Transformation failure tracking**: Detailed error context for debugging

**Metrics**:
- `honua_coordinate_transform_duration_ms`: Coordinate transformation latency
- `honua_spatial_query_duration_ms`: Spatial query operation latency
- `honua_spatial_filter_duration_ms`: Spatial filter operation latency
- `honua_geometry_complexity_coordinates`: Geometry complexity tracking

### 5. Structured Error Logging

**Location**: `src/Honua.Core/Features/Infrastructure/Monitoring/StructuredLoggingEnhancements.cs`

**Features**:
- **Contextual logging scopes**: Pre-built scopes for different operation types
- **Error enrichment**: Enhanced error context with operation details
- **Request correlation**: Links logs to distributed traces
- **Performance logging**: Automatic slow operation detection

**Usage Examples**:
```csharp
// Spatial query context
using var scope = logger.BeginSpatialQueryScope(
    layerId: 123,
    spatialFilter: "intersects",
    boundingBox: "BBOX(1,2,3,4)",
    protocol: "WFS");

// Cache operation context
using var cacheScope = logger.BeginCacheOperationScope(
    "layer-metadata",
    "get",
    cacheKey,
    TimeSpan.FromMinutes(5));

// Error logging with context
logger.LogErrorWithContext(
    exception,
    "coordinate_transform",
    new Dictionary<string, object>
    {
        ["from_srid"] = 4326,
        ["to_srid"] = 3857,
        ["coordinate_count"] = 1000
    });
```

## Service Registration

**Location**: `src/Honua.Core/Features/Infrastructure/Monitoring/PerformanceMonitoringServiceCollectionExtensions.cs`

**New Registration Method**:
```csharp
// Add to Program.cs or Startup.cs
services.AddEnhancedPerformanceMonitoring();
```

This registers:
- Enhanced DefaultPerformanceMonitor
- MemoryPressureMonitoringService
- All existing monitoring interfaces

## Production Deployment Considerations

### 1. Metric Storage and Retention
- Configure appropriate retention policies for histogram metrics
- Consider sampling rates for high-volume operations
- Set up alerting thresholds based on baseline measurements

### 2. Performance Overhead
- Memory monitoring runs every 30 seconds (configurable)
- Latency tracking uses efficient circular buffers (100 samples per operation type)
- Cache monitoring has minimal overhead (<1ms per operation)

### 3. Alert Configuration

**Recommended Alert Thresholds**:
- Memory pressure > 85% for 5 minutes
- P95 database query latency > 1000ms
- Cache hit ratio < 80% for layer metadata
- Coordinate transformation P99 > 10ms

### 4. Log Volume Management
- Structured logging adds context but minimal volume
- Slow operation logging only triggers above thresholds
- Error context is only added for actual errors

## Metrics Integration

### OpenTelemetry Export
All metrics are compatible with OpenTelemetry and can be exported to:
- Prometheus
- Azure Monitor
- AWS CloudWatch
- DataDog
- New Relic

### Example Prometheus Queries
```promql
# P95 database query latency
histogram_quantile(0.95, rate(honua_database_query_duration_ms_bucket[5m]))

# Cache hit ratio trend
rate(honua_cache_operation_total{operation="hit"}[5m]) /
rate(honua_cache_operation_total{operation=~"hit|miss"}[5m])

# Memory pressure alerts per hour
increase(honua_memory_pressure_alerts_total[1h])

# Coordinate transformation performance by SRID pair
histogram_quantile(0.99,
  rate(honua_coordinate_transform_duration_ms_bucket[5m]) by (from_srid, to_srid)
)
```

## Troubleshooting Production Issues

### High Memory Pressure
1. Check `honua_memory_allocated_mb` trend
2. Review GC efficiency logs
3. Look for memory leak patterns in allocation rates
4. Check for cache eviction patterns

### Slow Spatial Operations
1. Monitor `honua_coordinate_transform_duration_ms` by SRID pairs
2. Check `honua_spatial_query_duration_ms` by layer
3. Review geometry complexity metrics
4. Analyze spatial filter performance

### Cache Performance Issues
1. Review hit/miss ratios by cache type
2. Check cache operation latency percentiles
3. Monitor Redis vs fallback usage patterns
4. Analyze cache error rates

### Database Performance
1. Monitor query latency by type and layer
2. Check for slow query log entries with context
3. Review transaction performance metrics
4. Analyze connection usage patterns

## Future Enhancements

1. **Adaptive Sampling**: Implement intelligent sampling based on operation volume
2. **Machine Learning Anomaly Detection**: Add ML-based performance anomaly detection
3. **Custom Metric Dashboards**: Pre-built Grafana dashboards for common scenarios
4. **Performance Budgets**: Automated performance regression detection
5. **Distributed Tracing**: Enhanced correlation across microservices

## Testing the Implementation

### Unit Tests
- Memory pressure detection logic
- Latency percentile calculations
- Cache metrics accuracy
- Error context enrichment

### Integration Tests
- End-to-end metric collection
- Background service reliability
- Performance overhead validation
- Alert threshold accuracy

### Load Tests
- Metric collection under high load
- Memory monitoring stability
- Cache monitoring accuracy at scale
- Geospatial operation tracking reliability