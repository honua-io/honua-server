// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.ServiceDefaults;

namespace Honua.Infrastructure.Monitoring;

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
            // Error rate is a WINDOWED signal (#2809): a lifetime ratio (cumulative errors over cumulative
            // requests since process start) dilutes toward zero and never reflects a current spike. Source it
            // from the rolling serving-latency window instead; the cumulative totals above remain as counters.
            ErrorRate = CalculateWindowedErrorRate(),
            ActiveConnections = _connectionTracker.GetActiveCount(),
            ConnectionAcquisitionTimeouts = _connectionPoolMetrics.GetTotalTimeouts(),
            ConnectionAcquisitionFailures = _connectionPoolMetrics.GetTotalFailures(),
            RateLimitViolations = 0,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private double CalculateCacheHitRatio()
    {
        var snapshot = _cacheMetricsSnapshotProvider.GetCacheMetricsSnapshot();
        var hits = snapshot.TotalHits;
        var misses = snapshot.TotalMisses;
        var total = hits + misses;

        return total > 0 ? (double)hits / total : 0.0;
    }

    private static double CalculateWindowedErrorRate()
    {
        var serving = HonuaTelemetry.GetServingLatencySnapshot();
        long windowRequests = 0;
        long windowErrors = 0;
        foreach (var protocol in serving.Protocols)
        {
            windowRequests += protocol.RequestCount;
            windowErrors += protocol.ErrorCount;
        }

        return windowRequests > 0 ? (double)windowErrors / windowRequests : 0.0;
    }

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
        // Intentional broad catch: this is a best-effort metrics lookup; a failure to read the
        // upload queue depth must not break the rest of metrics collection, so it is logged and a
        // zero depth is reported instead.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MonitoringLog.UploadQueueDepthRetrievalFailed(_logger, ex);
        }

        return 0;
    }

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

    public void Dispose()
    {
        _meter.Dispose();
    }
}

public sealed class ProductionHealthMetrics
{
    public required long MemoryUsageBytes { get; set; }

    public required string MemoryPressureLevel { get; set; }

    public required double DatabaseConnectionPoolUtilization { get; set; }

    public required bool HasDatabaseConnectionPoolUtilization { get; set; }

    public required double CacheHitRatio { get; set; }

    public required long TotalQueries { get; set; }

    public required long TotalErrors { get; set; }

    public required double ErrorRate { get; set; }

    public required int ActiveConnections { get; set; }

    public required long ConnectionAcquisitionTimeouts { get; set; }

    public required long ConnectionAcquisitionFailures { get; set; }

    public required long RateLimitViolations { get; set; }

    public required DateTimeOffset Timestamp { get; set; }

    public bool IsHealthy()
    {
        return CacheHitRatio >= 0.8 &&
               (!HasDatabaseConnectionPoolUtilization || DatabaseConnectionPoolUtilization <= 0.8) &&
               ErrorRate <= 0.05 &&
               MemoryPressureLevel != "critical";
    }

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
