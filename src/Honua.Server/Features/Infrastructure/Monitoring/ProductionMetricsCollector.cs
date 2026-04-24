// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Collects comprehensive production metrics for monitoring and alerting.
/// </summary>
internal sealed class ProductionMetricsCollector : IDisposable
{
    private readonly Meter _meter;
    private readonly ConnectionPoolMetrics _connectionPoolMetrics;
    private readonly IActiveDbConnectionTracker _connectionTracker;
    private readonly ICacheMetricsSnapshotProvider _cacheMetricsSnapshotProvider;
    private readonly IHttpRequestMetricsSnapshotProvider _httpRequestMetricsSnapshotProvider;
    private readonly ILogger<ProductionMetricsCollector> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Metrics instruments
    private readonly ObservableGauge<double> _memoryUsage;
    private readonly ObservableGauge<double> _cacheHitRatio;
    private readonly ObservableGauge<int> _uploadQueueDepth;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProductionMetricsCollector"/> class.
    /// </summary>
    /// <param name="connectionPoolMetrics">Connection pool metrics.</param>
    /// <param name="connectionTracker">Connection tracker.</param>
    /// <param name="cacheMetricsSnapshotProvider">Live cache metrics snapshot provider.</param>
    /// <param name="httpRequestMetricsSnapshotProvider">Live HTTP request metrics snapshot provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="serviceProvider">Service provider for accessing upload service.</param>
    public ProductionMetricsCollector(
        ConnectionPoolMetrics connectionPoolMetrics,
        IActiveDbConnectionTracker connectionTracker,
        ICacheMetricsSnapshotProvider cacheMetricsSnapshotProvider,
        IHttpRequestMetricsSnapshotProvider httpRequestMetricsSnapshotProvider,
        ILogger<ProductionMetricsCollector> logger,
        IServiceProvider serviceProvider)
    {
        _connectionPoolMetrics = connectionPoolMetrics;
        _connectionTracker = connectionTracker;
        _cacheMetricsSnapshotProvider = cacheMetricsSnapshotProvider;
        _httpRequestMetricsSnapshotProvider = httpRequestMetricsSnapshotProvider;
        _logger = logger;
        _serviceProvider = serviceProvider;

        _meter = new Meter("Honua.Production.Metrics");

        _memoryUsage = _meter.CreateObservableGauge<double>(
            "honua_memory_usage_bytes",
            () => GC.GetTotalMemory(false),
            description: "Current memory usage in bytes");

        _cacheHitRatio = _meter.CreateObservableGauge<double>(
            "honua_cache_hit_ratio",
            () => CalculateCacheHitRatio(),
            description: "Cache hit ratio (0.0-1.0)");

        _uploadQueueDepth = _meter.CreateObservableGauge<int>(
            "honua_upload_queue_depth",
            () => GetUploadQueueDepth(),
            description: "Current upload queue depth");
    }

    /// <summary>
    /// Gets production health metrics for alerting.
    /// </summary>
    /// <returns>Health metrics snapshot.</returns>
    public ProductionHealthMetrics GetHealthMetrics()
    {
        var memoryUsage = GC.GetTotalMemory(false);
        var hasPoolUtilization = _connectionPoolMetrics.TryGetPoolUtilization(out var utilization);
        var cacheHitRatio = CalculateCacheHitRatio();
        var requestMetrics = _httpRequestMetricsSnapshotProvider.GetHttpRequestMetricsSnapshot();
        var totalRequests = requestMetrics.TotalRequests;
        var totalServerErrors = requestMetrics.TotalServerErrors;

        return new ProductionHealthMetrics
        {
            MemoryUsageBytes = memoryUsage,
            MemoryPressureLevel = GetMemoryPressureLevel(memoryUsage),
            DatabaseConnectionPoolUtilization = hasPoolUtilization ? utilization : 0.0,
            HasDatabaseConnectionPoolUtilization = hasPoolUtilization,
            CacheHitRatio = cacheHitRatio,
            TotalQueries = totalRequests,
            TotalErrors = totalServerErrors,
            ErrorRate = CalculateErrorRate(totalRequests, totalServerErrors),
            ActiveConnections = _connectionTracker.GetActiveCount(),
            ConnectionAcquisitionTimeouts = _connectionPoolMetrics.GetTotalTimeouts(),
            ConnectionAcquisitionFailures = _connectionPoolMetrics.GetTotalFailures(),
            RateLimitViolations = 0,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Calculates the current cache hit ratio.
    /// </summary>
    /// <returns>Cache hit ratio (0.0-1.0).</returns>
    private double CalculateCacheHitRatio()
    {
        var snapshot = _cacheMetricsSnapshotProvider.GetCacheMetricsSnapshot();
        var hits = snapshot.TotalHits;
        var misses = snapshot.TotalMisses;
        var total = hits + misses;

        return total > 0 ? (double)hits / total : 0.0;
    }

    /// <summary>
    /// Gets the current error rate.
    /// </summary>
    /// <param name="totalRequests">Total number of live HTTP requests observed.</param>
    /// <param name="totalServerErrors">Total number of live HTTP server errors observed.</param>
    /// <returns>Error rate (0.0-1.0).</returns>
    private static double CalculateErrorRate(long totalRequests, long totalServerErrors)
    {
        return totalRequests > 0 ? (double)totalServerErrors / totalRequests : 0.0;
    }

    /// <summary>
    /// Gets the current upload queue depth.
    /// </summary>
    /// <returns>Upload queue depth.</returns>
    private int GetUploadQueueDepth()
    {
        try
        {
            var uploadQueueMetricsProvider = _serviceProvider.GetService<Abstractions.IUploadQueueMetricsProvider>();
            if (uploadQueueMetricsProvider != null)
            {
                return uploadQueueMetricsProvider.GetQueueSnapshot().QueueDepth;
            }
        }
        catch (Exception ex)
        {
            MonitoringLog.UploadQueueDepthRetrievalFailed(_logger, ex);
        }

        return 0;
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
    /// Gets or sets a value indicating whether database connection pool utilization is available.
    /// </summary>
    public required bool HasDatabaseConnectionPoolUtilization { get; set; }

    /// <summary>
    /// Gets or sets cache hit ratio.
    /// </summary>
    public required double CacheHitRatio { get; set; }

    /// <summary>
    /// Gets or sets total number of live HTTP requests processed.
    /// </summary>
    public required long TotalQueries { get; set; }

    /// <summary>
    /// Gets or sets total number of live HTTP server errors.
    /// </summary>
    public required long TotalErrors { get; set; }

    /// <summary>
    /// Gets or sets the live HTTP server error rate.
    /// </summary>
    public required double ErrorRate { get; set; }

    /// <summary>
    /// Gets or sets number of active database connections.
    /// </summary>
    public required int ActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets connection acquisition timeouts.
    /// </summary>
    public required long ConnectionAcquisitionTimeouts { get; set; }

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
               (!HasDatabaseConnectionPoolUtilization || DatabaseConnectionPoolUtilization <= 0.8) &&
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

        if (HasDatabaseConnectionPoolUtilization && DatabaseConnectionPoolUtilization > 0.8)
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

        return alerts;
    }
}
