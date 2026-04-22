# Memory Management and Performance Guide

This document outlines the memory management improvements implemented to prevent OutOfMemoryException scenarios and optimize performance in production environments.

## Overview

The Honua server implements comprehensive memory management strategies across several key components:

1. **Cache Memory Management** - Bounded caches with LRU eviction and cleanup
2. **Bulk Import Operations** - Efficient batch processing to reduce memory pressure  
3. **Object Pooling** - Managed pools with capacity limits to prevent LOH pressure
4. **Memory Monitoring** - Proactive monitoring and automatic pressure relief

## Key Components

### 1. Enhanced FeatureCacheManager

**Location**: `src/Honua.Postgres/Features/FeatureStore/Services/FeatureCacheManager.cs`

**Improvements**:
- Bounded cache sizes with configurable limits (default: 5000 entries)
- LRU eviction when cache approaches capacity
- Periodic cleanup with stale entry removal
- Cache stampede prevention using TaskCompletionSource locks
- Performance metrics with bounded memory usage
- Memory utilization tracking and alerting

**Configuration**:
```csharp
services.Configure<MemoryManagementOptions>(options =>
{
    options.MaxLayerSridCacheEntries = 5000;
    options.CacheCleanupThreshold = 4000;
    options.CacheCleanupIntervalMs = 30000;
});
```

### 2. Bulk Import Operations

**Location**: `src/Honua.Postgres/Features/Import/BulkImportExtensions.cs`

**Improvements**:
- Array-based bulk inserts for batches > 500 features
- COPY-based imports for very large datasets
- Parallel WKB serialization for CPU efficiency
- Sub-batch processing to reduce memory spikes
- Automatic fallback to individual inserts for error handling

**SQL Functions**:
- `honua.bulk_insert_import_features()` - Array-based bulk insert
- `honua.prepare_bulk_copy_table()` - COPY preparation
- `honua.finalize_bulk_copy()` - COPY finalization

### 3. Improved Object Pooling

**Location**: `src/Honua.Postgres/Features/FeatureStore/Services/PooledObjectPolicies.cs`

**StringBuilder Pool Improvements**:
- Capacity management to prevent LOH allocations (>85KB)
- Automatic capacity reset for oversized builders
- Conservative pool retention limits

**Dictionary Pool Improvements**:
- Capacity trimming after use
- Reasonable size limits with TrimExcess calls
- Better memory locality through managed growth

### 4. Memory Monitoring Service

**Location**: `src/Honua.Postgres/Features/Infrastructure/Monitoring/MemoryMonitor.cs`

**Features**:
- Real-time memory usage tracking
- GC pressure monitoring
- Automatic memory pressure relief
- Allocation/deallocation tracking by source
- Configurable thresholds and alerts

**Usage**:
```csharp
services.AddMemoryManagement(options =>
{
    options.HighMemoryThresholdBytes = 1024L * 1024 * 1024; // 1GB
    options.CriticalMemoryThresholdBytes = 2048L * 1024 * 1024; // 2GB
    options.EnableAutoMemoryRelief = true;
});
```

## Memory Thresholds and Alerts

### Default Thresholds

| Threshold | Value | Action |
|-----------|-------|---------|
| High Memory Pressure | 1 GB | Log warning, enable monitoring |
| Critical Memory Pressure | 2 GB | Force GC, log error |
| Cache Cleanup | 4000 entries | Start proactive cleanup |
| Cache Maximum | 5000 entries | Force LRU eviction |
| StringBuilder Reset | 4 KB capacity | Reset to 2KB capacity |

### Monitoring Metrics

The system provides several metrics for memory monitoring:

```csharp
// Cache statistics
var cacheStats = cacheManager.GetCacheStatistics();
Console.WriteLine($"Cache utilization: {cacheStats.CacheUtilizationRatio:P}");

// Memory usage
var memoryStats = memoryMonitor.GetMemoryUsage();
Console.WriteLine($"Total memory: {memoryStats.TotalMemoryBytes:N0} bytes");
```

## Performance Optimizations

### 1. Bulk Operations Strategy

| Batch Size | Strategy | Memory Impact |
|------------|----------|---------------|
| < 100 | Individual inserts | Low |
| 100-499 | Sub-batched inserts | Medium |
| 500+ | Array bulk insert | High efficiency |
| 10,000+ | COPY-based import | Streaming |

### 2. Cache Stampede Prevention

Multiple threads requesting the same layer SRID will:
1. Check if another thread is already fetching
2. Wait for the existing fetch to complete
3. Share the cached result across all waiting threads

This prevents database flooding and reduces memory allocation.

### 3. Object Pool Management

```csharp
// Configured with conservative limits
var poolProvider = new DefaultObjectPoolProvider
{
    MaximumRetained = Math.Min(Environment.ProcessorCount * 8, 32)
};
```

## Testing and Validation

### Memory Leak Tests

Run comprehensive tests to ensure no memory leaks:

```bash
dotnet test tests/dotnet/Honua.Core.Tests/Infrastructure/Monitoring/MemoryManagementTests.cs
```

### Load Testing Recommendations

1. **Large Dataset Import** - Test with 1M+ features
2. **Concurrent Cache Access** - Multiple threads accessing same layers
3. **Long-running Operations** - 24+ hour continuous operation
4. **Memory Pressure Scenarios** - Simulate high memory usage

### Monitoring in Production

1. **Enable memory monitoring**:
   ```csharp
   services.AddMemoryManagement();
   ```

2. **Configure logging** for memory events:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "Honua.Postgres.Features.Infrastructure.Monitoring.MemoryMonitor": "Information"
       }
     }
   }
   ```

3. **Set up alerts** for critical memory pressure events.

## Troubleshooting

### High Memory Usage

1. **Check cache utilization**:
   ```csharp
   var stats = cacheManager.GetCacheStatistics();
   if (stats.IsNearCapacity) {
       // Consider reducing MaxLayerSridCacheEntries
   }
   ```

2. **Monitor GC pressure**:
   ```csharp
   var memory = memoryMonitor.GetMemoryUsage();
   Console.WriteLine($"Gen 2 collections: {memory.CollectionCounts[2]}");
   ```

3. **Review bulk operation thresholds** if import operations are using too much memory.

### Cache Performance Issues

1. **Check for cache stampede** - high ActiveLockCount indicates contention
2. **Review cleanup intervals** - too frequent cleanup can hurt performance
3. **Adjust cache size limits** based on available memory

## Best Practices

### For Developers

1. **Use bulk operations** for large datasets (>500 records)
2. **Monitor memory usage** during development and testing
3. **Configure appropriate limits** based on deployment environment
4. **Test memory behavior** under load conditions

### For Operations

1. **Set memory alerts** at 70% and 90% of available memory
2. **Monitor cache hit rates** and adjust sizes accordingly
3. **Review GC logs** for pressure patterns
4. **Plan memory capacity** based on expected dataset sizes

### Configuration Examples

**Development Environment**:
```csharp
services.Configure<MemoryManagementOptions>(options =>
{
    options.MaxLayerSridCacheEntries = 1000;
    options.HighMemoryThresholdBytes = 512L * 1024 * 1024; // 512MB
    options.EnableAutoMemoryRelief = true;
});
```

**Production Environment**:
```csharp
services.Configure<MemoryManagementOptions>(options =>
{
    options.MaxLayerSridCacheEntries = 10000;
    options.HighMemoryThresholdBytes = 2048L * 1024 * 1024; // 2GB
    options.CriticalMemoryThresholdBytes = 4096L * 1024 * 1024; // 4GB
    options.BulkOperationThreshold = 1000;
});
```

## Migration Guide

### Existing Deployments

1. **Update database** with new migration:
   ```sql
   -- Run migration 019_AddBulkImportFunctions.sql
   ```

2. **Update service registration**:
   ```csharp
   // Add to Startup.cs or Program.cs
   services.AddMemoryManagement();
   ```

3. **Monitor logs** for memory pressure events after deployment

4. **Adjust configuration** based on observed behavior

### Breaking Changes

- None - all improvements are backward compatible
- New bulk import functions are optional and fall back gracefully

## Conclusion

These memory management improvements provide:

- **Elimination of cache memory leaks** through bounded sizes and cleanup
- **Significant reduction in import memory pressure** via bulk operations
- **Prevention of cache stampede scenarios** through proper synchronization
- **Proactive memory monitoring** with automatic pressure relief
- **Production-ready memory management** with comprehensive monitoring

The implementation maintains backward compatibility while providing substantial performance and stability improvements for production workloads.