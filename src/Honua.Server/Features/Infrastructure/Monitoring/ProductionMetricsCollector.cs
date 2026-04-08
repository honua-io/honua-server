// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Collects comprehensive production metrics for monitoring and alerting.
/// </summary>
internal sealed class ProductionMetricsCollector : IDisposable
{
    private readonly Meter _meter;
    private readonly IMemoryCache _memoryCache;
    private readonly ConnectionPoolMetrics _connectionPoolMetrics;
    private readonly IActiveDbConnectionTracker _connectionTracker;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<ProductionMetricsCollector> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Metrics instruments
    private readonly Histogram<double> _queryLatency;
    private readonly Counter<long> _queryCount;
    private readonly Counter<long> _errorCount;
    private readonly ObservableGauge<double> _memoryUsage;
    private readonly ObservableGauge<double> _cacheHitRatio;
    private readonly ObservableGauge<int> _cacheEntryCount;
    private readonly Counter<long> _rateLimitViolations;
    private readonly Histogram<double> _fileUploadDuration;
    private readonly ObservableGauge<int> _uploadQueueDepth;

    // State tracking
    private long _totalQueries;
    private long _totalErrors;
    private long _cacheHits;
    private long _cacheMisses;
    private long _rateLimitViolationsCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProductionMetricsCollector"/> class.
    /// </summary>
    /// <param name="memoryCache">Memory cache instance.</param>
    /// <param name="connectionPoolMetrics">Connection pool metrics.</param>
    /// <param name="connectionTracker">Connection tracker.</param>
    /// <param name="cacheOptions">Cache options.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="serviceProvider">Service provider for accessing upload service.</param>
    public ProductionMetricsCollector(
        IMemoryCache memoryCache,
        ConnectionPoolMetrics connectionPoolMetrics,
        IActiveDbConnectionTracker connectionTracker,
        IOptions<CacheOptions> cacheOptions,
        ILogger<ProductionMetricsCollector> logger,
        IServiceProvider serviceProvider)
    {
        _memoryCache = memoryCache;
        _connectionPoolMetrics = connectionPoolMetrics;
        _connectionTracker = connectionTracker;
        _cacheOptions = cacheOptions.Value;
        _logger = logger;
        _serviceProvider = serviceProvider;

        _meter = new Meter("Honua.Production.Metrics");

        // Initialize metrics instruments
        _queryLatency = _meter.CreateHistogram<double>(
            "honua_query_duration_ms",
            "milliseconds",
            "Query execution time");

        _queryCount = _meter.CreateCounter<long>(
            "honua_queries_total",
            description: "Total number of queries processed");

        _errorCount = _meter.CreateCounter<long>(
            "honua_errors_total",
            description: "Total number of errors");

        _memoryUsage = _meter.CreateObservableGauge<double>(
            "honua_memory_usage_bytes",
            () => GC.GetTotalMemory(false),
            description: "Current memory usage in bytes");

        _cacheHitRatio = _meter.CreateObservableGauge<double>(
            "honua_cache_hit_ratio",
            () => CalculateCacheHitRatio(),
            description: "Cache hit ratio (0.0-1.0)");

        _cacheEntryCount = _meter.CreateObservableGauge<int>(
            "honua_cache_entries",
            () => GetCacheEntryCount(),
            description: "Number of entries in memory cache");

        _rateLimitViolations = _meter.CreateCounter<long>(
            "honua_rate_limit_violations_total",
            description: "Total number of rate limit violations");

        _fileUploadDuration = _meter.CreateHistogram<double>(
            "honua_file_upload_duration_ms",
            "milliseconds",
            "File upload processing time");

        _uploadQueueDepth = _meter.CreateObservableGauge<int>(
            "honua_upload_queue_depth",
            () => GetUploadQueueDepth(),
            description: "Current upload queue depth");
    }

    /// <summary>
    /// Records a query execution.
    /// </summary>
    /// <param name="duration">Query duration.</param>
    /// <param name="protocol">Protocol used (e.g., "FeatureServer", "OGC", "OData").</param>
    /// <param name="operation">Operation type (e.g., "Query", "GetFeature").</param>
    /// <param name="success">Whether the query was successful.</param>
    public void RecordQuery(TimeSpan duration, string protocol, string operation, bool success)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("protocol", protocol),
            new("operation", operation),
            new("success", success.ToString().ToLowerInvariant())
        };

        _queryLatency.Record(duration.TotalMilliseconds, tags);
        _queryCount.Add(1, tags);

        Interlocked.Increment(ref _totalQueries);

        if (!success)
        {
            _errorCount.Add(1, tags);
            Interlocked.Increment(ref _totalErrors);
        }
    }

    /// <summary>
    /// Records a cache hit.
    /// </summary>
    /// <param name="cacheType">Type of cache (e.g., "Layer", "Service", "Query").</param>
    public void RecordCacheHit(string cacheType)
    {
        Interlocked.Increment(ref _cacheHits);
    }

    /// <summary>
    /// Records a cache miss.
    /// </summary>
    /// <param name="cacheType">Type of cache (e.g., "Layer", "Service", "Query").</param>
    public void RecordCacheMiss(string cacheType)
    {
        Interlocked.Increment(ref _cacheMisses);
    }

    /// <summary>
    /// Records a rate limit violation.
    /// </summary>
    /// <param name="reason">Reason for rate limiting.</param>
    /// <param name="clientId">Client identifier.</param>
    public void RecordRateLimitViolation(string reason, string? clientId = null)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("reason", reason)
        };

        if (!string.IsNullOrEmpty(clientId))
        {
            tags.Add(new("client_id", clientId));
        }

        _rateLimitViolations.Add(1, tags.ToArray());
        Interlocked.Increment(ref _rateLimitViolationsCount);
    }

    /// <summary>
    /// Records file upload processing time.
    /// </summary>
    /// <param name="duration">Upload duration.</param>
    /// <param name="fileSizeBytes">File size in bytes.</param>
    /// <param name="success">Whether upload was successful.</param>
    public void RecordFileUpload(TimeSpan duration, long fileSizeBytes, bool success)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("success", success.ToString().ToLowerInvariant()),
            new("size_category", CategorizeFileSize(fileSizeBytes))
        };

        _fileUploadDuration.Record(duration.TotalMilliseconds, tags);
    }

    /// <summary>
    /// Records an application error.
    /// </summary>
    /// <param name="errorType">Type of error.</param>
    /// <param name="source">Source of the error.</param>
    /// <param name="severity">Error severity.</param>
    public void RecordError(string errorType, string source, string severity = "Error")
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("error_type", errorType),
            new("source", source),
            new("severity", severity)
        };

        _errorCount.Add(1, tags);
        Interlocked.Increment(ref _totalErrors);
    }

    /// <summary>
    /// Gets production health metrics for alerting.
    /// </summary>
    /// <returns>Health metrics snapshot.</returns>
    public ProductionHealthMetrics GetHealthMetrics()
    {
        var memoryUsage = GC.GetTotalMemory(false);
        var poolUtilization = _connectionPoolMetrics.GetPoolUtilization();
        var cacheHitRatio = CalculateCacheHitRatio();

        return new ProductionHealthMetrics
        {
            MemoryUsageBytes = memoryUsage,
            MemoryPressureLevel = GetMemoryPressureLevel(memoryUsage),
            DatabaseConnectionPoolUtilization = poolUtilization,
            CacheHitRatio = cacheHitRatio,
            TotalQueries = Volatile.Read(ref _totalQueries),
            TotalErrors = Volatile.Read(ref _totalErrors),
            ErrorRate = CalculateErrorRate(),
            ActiveConnections = _connectionTracker.GetActiveCount(),
            ConnectionAcquisitionFailures = _connectionPoolMetrics.GetTotalFailures(),
            RateLimitViolations = Volatile.Read(ref _rateLimitViolationsCount),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Calculates the current cache hit ratio.
    /// </summary>
    /// <returns>Cache hit ratio (0.0-1.0).</returns>
    private double CalculateCacheHitRatio()
    {
        var hits = Volatile.Read(ref _cacheHits);
        var misses = Volatile.Read(ref _cacheMisses);
        var total = hits + misses;

        return total > 0 ? (double)hits / total : 0.0;
    }

    /// <summary>
    /// Gets the current error rate.
    /// </summary>
    /// <returns>Error rate (0.0-1.0).</returns>
    private double CalculateErrorRate()
    {
        var errors = Volatile.Read(ref _totalErrors);
        var queries = Volatile.Read(ref _totalQueries);

        return queries > 0 ? (double)errors / queries : 0.0;
    }

    /// <summary>
    /// Gets the number of entries in the memory cache.
    /// </summary>
    /// <returns>Cache entry count.</returns>
    private int GetCacheEntryCount()
    {
        try
        {
            if (_memoryCache is MemoryCache memoryCache)
            {
                var field = typeof(MemoryCache).GetField("_count",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(memoryCache) as int? ?? 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get memory cache entry count");
        }

        return 0;
    }

    /// <summary>
    /// Gets the current upload queue depth.
    /// </summary>
    /// <returns>Upload queue depth.</returns>
    private int GetUploadQueueDepth()
    {
        try
        {
            // Try to get the upload service from DI container
            var uploadService = _serviceProvider.GetService<Honua.Server.Features.Import.StreamingFileUploadService>();
            if (uploadService != null)
            {
                var queueMetrics = uploadService.GetQueueMetrics();
                return queueMetrics.QueueDepth;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get upload queue depth from StreamingFileUploadService");
        }

        return 0;
    }

    /// <summary>
    /// Categorizes file size for metrics.
    /// </summary>
    /// <param name="sizeBytes">File size in bytes.</param>
    /// <returns>Size category.</returns>
    private static string CategorizeFileSize(long sizeBytes)
    {
        return sizeBytes switch
        {
            < 1024 * 1024 => "small",        // < 1MB
            < 10 * 1024 * 1024 => "medium",  // < 10MB
            < 100 * 1024 * 1024 => "large",  // < 100MB
            _ => "very_large"                 // >= 100MB
        };
    }

    /// <summary>
    /// Gets memory pressure level for alerting.
    /// </summary>
    /// <param name="memoryUsage">Current memory usage.</param>
    /// <returns>Memory pressure level.</returns>
    private static string GetMemoryPressureLevel(long memoryUsage)
    {
        var memoryMB = memoryUsage / (1024 * 1024);

        return memoryMB switch
        {
            < 500 => "low",
            < 1000 => "medium",
            < 2000 => "high",
            _ => "critical"
        };
    }

    /// <summary>
    /// Disposes the metrics collector.
    /// </summary>
    public void Dispose()
    {
        _meter.Dispose();
    }
}

/// <summary>
/// Production health metrics for monitoring and alerting.
/// </summary>
public sealed class ProductionHealthMetrics
{
    /// <summary>
    /// Gets or sets current memory usage in bytes.
    /// </summary>
    public required long MemoryUsageBytes { get; set; }

    /// <summary>
    /// Gets or sets memory pressure level.
    /// </summary>
    public required string MemoryPressureLevel { get; set; }

    /// <summary>
    /// Gets or sets database connection pool utilization ratio.
    /// </summary>
    public required double DatabaseConnectionPoolUtilization { get; set; }

    /// <summary>
    /// Gets or sets cache hit ratio.
    /// </summary>
    public required double CacheHitRatio { get; set; }

    /// <summary>
    /// Gets or sets total number of queries processed.
    /// </summary>
    public required long TotalQueries { get; set; }

    /// <summary>
    /// Gets or sets total number of errors.
    /// </summary>
    public required long TotalErrors { get; set; }

    /// <summary>
    /// Gets or sets current error rate.
    /// </summary>
    public required double ErrorRate { get; set; }

    /// <summary>
    /// Gets or sets number of active database connections.
    /// </summary>
    public required int ActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets connection acquisition failures.
    /// </summary>
    public required long ConnectionAcquisitionFailures { get; set; }

    /// <summary>
    /// Gets or sets rate limit violations.
    /// </summary>
    public required long RateLimitViolations { get; set; }

    /// <summary>
    /// Gets or sets timestamp of the metrics snapshot.
    /// </summary>
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Determines if the system is healthy based on configured thresholds.
    /// </summary>
    /// <returns>True if healthy, false otherwise.</returns>
    public bool IsHealthy()
    {
        return CacheHitRatio >= 0.8 &&
               DatabaseConnectionPoolUtilization <= 0.8 &&
               ErrorRate <= 0.05 &&
               MemoryPressureLevel != "critical";
    }

    /// <summary>
    /// Gets alert conditions that are currently triggered.
    /// </summary>
    /// <returns>List of alert conditions.</returns>
    public List<string> GetAlertConditions()
    {
        var alerts = new List<string>();

        if (CacheHitRatio < 0.8)
        {
            alerts.Add($"Low cache hit ratio: {CacheHitRatio:P2}");
        }

        if (DatabaseConnectionPoolUtilization > 0.8)
        {
            alerts.Add($"High database connection pool utilization: {DatabaseConnectionPoolUtilization:P2}");
        }

        if (ErrorRate > 0.05)
        {
            alerts.Add($"High error rate: {ErrorRate:P2}");
        }

        if (MemoryPressureLevel == "critical")
        {
            alerts.Add($"Critical memory pressure: {MemoryUsageBytes / (1024 * 1024)}MB");
        }

        if (ConnectionAcquisitionFailures > 0)
        {
            alerts.Add($"Database connection acquisition failures: {ConnectionAcquisitionFailures}");
        }

        return alerts;
    }
}
