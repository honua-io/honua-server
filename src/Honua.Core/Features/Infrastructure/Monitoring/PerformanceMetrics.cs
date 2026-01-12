// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Provides standardized performance metrics for the Honua Server application using .NET Metrics API.
/// </summary>
/// <remarks>
/// This class defines all performance counters and histograms used throughout the application
/// for consistent monitoring and telemetry collection.
/// </remarks>
internal sealed class PerformanceMetrics
{
    /// <summary>
    /// The meter name for Honua Server metrics.
    /// </summary>
    public const string MeterName = "Honua";

    /// <summary>
    /// The meter instance for creating metrics instruments.
    /// </summary>
    public static readonly Meter Meter = new(MeterName);

    #region Database Metrics

    /// <summary>
    /// Histogram for database query duration in milliseconds.
    /// </summary>
    public static readonly Histogram<double> DatabaseQueryDuration = Meter.CreateHistogram<double>(
        "honua_database_query_duration_ms",
        "ms",
        "Duration of database queries in milliseconds");

    /// <summary>
    /// Counter for database query operations.
    /// </summary>
    public static readonly Counter<long> DatabaseQueryCount = Meter.CreateCounter<long>(
        "honua_database_query_total",
        "queries",
        "Total number of database queries executed");

    /// <summary>
    /// Histogram for database query record count.
    /// </summary>
    public static readonly Histogram<int> DatabaseQueryRecordCount = Meter.CreateHistogram<int>(
        "honua_database_query_records",
        "records",
        "Number of records returned or affected by database queries");

    #endregion

    #region Transaction Metrics

    /// <summary>
    /// Histogram for database transaction duration in milliseconds.
    /// </summary>
    public static readonly Histogram<double> TransactionDuration = Meter.CreateHistogram<double>(
        "honua_transaction_duration_ms",
        "ms",
        "Duration of database transactions in milliseconds");

    /// <summary>
    /// Counter for database transactions.
    /// </summary>
    public static readonly Counter<long> TransactionCount = Meter.CreateCounter<long>(
        "honua_transaction_total",
        "transactions",
        "Total number of database transactions");

    #endregion

    #region HTTP Request Metrics

    /// <summary>
    /// Histogram for HTTP request duration in milliseconds.
    /// </summary>
    public static readonly Histogram<double> HttpRequestDuration = Meter.CreateHistogram<double>(
        "honua_http_request_duration_ms",
        "ms",
        "Duration of HTTP requests in milliseconds");

    /// <summary>
    /// Counter for HTTP requests.
    /// </summary>
    public static readonly Counter<long> HttpRequestCount = Meter.CreateCounter<long>(
        "honua_http_request_total",
        "requests",
        "Total number of HTTP requests processed");

    /// <summary>
    /// Gauge for active HTTP requests.
    /// </summary>
    public static readonly UpDownCounter<int> ActiveHttpRequests = Meter.CreateUpDownCounter<int>(
        "honua_http_active_requests",
        "requests",
        "Number of currently active HTTP requests");

    #endregion

    #region Memory Metrics

    /// <summary>
    /// Gauge for allocated memory in bytes.
    /// </summary>
    public static readonly ObservableGauge<long> AllocatedMemoryBytes = Meter.CreateObservableGauge<long>(
        "honua_memory_allocated_bytes",
        () => MemoryMonitor.GetMemoryUsage().AllocatedBytes,
        "bytes",
        "Currently allocated memory in bytes");

    /// <summary>
    /// Counter for garbage collection events.
    /// </summary>
    public static readonly Counter<long> GarbageCollectionCount = Meter.CreateCounter<long>(
        "honua_gc_collection_total",
        "collections",
        "Total number of garbage collection events by generation");

    #endregion

    #region Cache Metrics

    /// <summary>
    /// Counter for cache operations.
    /// </summary>
    public static readonly Counter<long> CacheOperationCount = Meter.CreateCounter<long>(
        "honua_cache_operation_total",
        "operations",
        "Total number of cache operations (hit/miss/eviction)");

    /// <summary>
    /// Histogram for cache hit ratio.
    /// </summary>
    public static readonly Histogram<double> CacheHitRatio = Meter.CreateHistogram<double>(
        "honua_cache_hit_ratio",
        "ratio",
        "Cache hit ratio as a percentage");

    /// <summary>
    /// Histogram for cache operation latency by type and operation.
    /// </summary>
    public static readonly Histogram<double> CacheOperationLatency = Meter.CreateHistogram<double>(
        "honua_cache_operation_duration_ms",
        "ms",
        "Cache operation latency by cache type and operation");

    #endregion

    #region Operation Metrics

    /// <summary>
    /// Histogram for generic operation duration in milliseconds.
    /// </summary>
    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "honua_operation_duration_ms",
        "ms",
        "Duration of operations in milliseconds");

    /// <summary>
    /// Counter for operation executions.
    /// </summary>
    public static readonly Counter<long> OperationCount = Meter.CreateCounter<long>(
        "honua_operation_total",
        "operations",
        "Total number of operations executed");

    #endregion

    #region Geometry Metrics

    /// <summary>
    /// Histogram for geometry processing operation duration.
    /// </summary>
    public static readonly Histogram<double> GeometryOperationDuration = Meter.CreateHistogram<double>(
        "honua_geometry_operation_duration_ms",
        "ms",
        "Duration of geometry processing operations in milliseconds");

    /// <summary>
    /// Counter for geometry operations.
    /// </summary>
    public static readonly Counter<long> GeometryOperationCount = Meter.CreateCounter<long>(
        "honua_geometry_operation_total",
        "operations",
        "Total number of geometry processing operations");

    /// <summary>
    /// Histogram for geometry complexity (coordinate count).
    /// </summary>
    public static readonly Histogram<int> GeometryComplexity = Meter.CreateHistogram<int>(
        "honua_geometry_complexity_coordinates",
        "coordinates",
        "Number of coordinates in processed geometries");

    /// <summary>
    /// Counter for geometry transformation operations.
    /// </summary>
    public static readonly Counter<long> GeometryTransformationCount = Meter.CreateCounter<long>(
        "honua_geometry_transformation_total",
        "transformations",
        "Total number of geometry coordinate transformations");

    /// <summary>
    /// Histogram for geometry transformation duration.
    /// </summary>
    public static readonly Histogram<double> GeometryTransformationDuration = Meter.CreateHistogram<double>(
        "honua_geometry_transformation_duration_ms",
        "ms",
        "Duration of geometry coordinate transformations in milliseconds");

    #endregion

    #region Custom Metrics

    /// <summary>
    /// Creates a custom counter metric.
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="unit">Unit of measurement</param>
    /// <param name="description">Metric description</param>
    /// <returns>Counter instance</returns>
    public static Counter<long> CreateCounter(string name, string unit = "", string description = "")
    {
        return Meter.CreateCounter<long>(name, unit, description);
    }

    /// <summary>
    /// Creates a custom histogram metric.
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="unit">Unit of measurement</param>
    /// <param name="description">Metric description</param>
    /// <returns>Histogram instance</returns>
    public static Histogram<double> CreateHistogram(string name, string unit = "", string description = "")
    {
        return Meter.CreateHistogram<double>(name, unit, description);
    }

    /// <summary>
    /// Creates a custom gauge metric.
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="unit">Unit of measurement</param>
    /// <param name="description">Metric description</param>
    /// <returns>UpDownCounter instance</returns>
    public static UpDownCounter<long> CreateGauge(string name, string unit = "", string description = "")
    {
        return Meter.CreateUpDownCounter<long>(name, unit, description);
    }

    #endregion
}

/// <summary>
/// Advanced metrics framework with business intelligence and enterprise monitoring capabilities.
/// Provides comprehensive telemetry including business KPIs, technical metrics, and security analytics.
/// </summary>
public static class BusinessIntelligenceMetrics
{
    /// <summary>
    /// The meter instance for business intelligence metrics.
    /// </summary>
    public static readonly Meter BusinessMeter = new("Honua.BusinessIntelligence");

    #region Business Metrics

    /// <summary>
    /// Counter for API endpoint usage by protocol and endpoint.
    /// </summary>
    public static readonly Counter<long> ApiEndpointUsage = BusinessMeter.CreateCounter<long>(
        "honua_api_endpoint_usage_total",
        "requests",
        "Total API endpoint usage by protocol and path");

    /// <summary>
    /// Histogram for user session duration in minutes.
    /// </summary>
    public static readonly Histogram<double> UserSessionDuration = BusinessMeter.CreateHistogram<double>(
        "honua_user_session_duration_minutes",
        "minutes",
        "Duration of user sessions in minutes");

    /// <summary>
    /// Counter for feature adoption tracking.
    /// </summary>
    public static readonly Counter<long> FeatureAdoption = BusinessMeter.CreateCounter<long>(
        "honua_feature_adoption_total",
        "usages",
        "Feature adoption and usage tracking");

    /// <summary>
    /// Gauge for active concurrent users.
    /// </summary>
    public static readonly UpDownCounter<int> ConcurrentUsers = BusinessMeter.CreateUpDownCounter<int>(
        "honua_concurrent_users",
        "users",
        "Number of concurrent active users");

    /// <summary>
    /// Counter for geographic data access by region.
    /// </summary>
    public static readonly Counter<long> GeographicDataAccess = BusinessMeter.CreateCounter<long>(
        "honua_geographic_data_access_total",
        "requests",
        "Geographic data access by region and data type");

    #endregion

    #region Technical Excellence Metrics

    /// <summary>
    /// Histogram for SLA compliance percentage.
    /// </summary>
    public static readonly Histogram<double> SlaCompliance = BusinessMeter.CreateHistogram<double>(
        "honua_sla_compliance_percentage",
        "percent",
        "SLA compliance percentage by service");

    /// <summary>
    /// Counter for error types and severity levels.
    /// </summary>
    public static readonly Counter<long> ErrorSeverity = BusinessMeter.CreateCounter<long>(
        "honua_error_severity_total",
        "errors",
        "Error count by type and severity level");

    /// <summary>
    /// Gauge for real-time performance score (0-100).
    /// </summary>
    public static readonly ObservableGauge<int> PerformanceScore = BusinessMeter.CreateObservableGauge<int>(
        "honua_performance_score",
        () => CalculatePerformanceScore(),
        "score",
        "Real-time performance score from 0-100");

    /// <summary>
    /// Histogram for data throughput in MB/s.
    /// </summary>
    public static readonly Histogram<double> DataThroughput = BusinessMeter.CreateHistogram<double>(
        "honua_data_throughput_mbps",
        "MB/s",
        "Data throughput in megabytes per second");

    #endregion

    #region Security Intelligence Metrics

    /// <summary>
    /// Counter for authentication events by type and result.
    /// </summary>
    public static readonly Counter<long> AuthenticationEvents = BusinessMeter.CreateCounter<long>(
        "honua_auth_events_total",
        "events",
        "Authentication events by type and success/failure");

    /// <summary>
    /// Counter for security threats detected by severity.
    /// </summary>
    public static readonly Counter<long> SecurityThreats = BusinessMeter.CreateCounter<long>(
        "honua_security_threats_total",
        "threats",
        "Security threats detected by type and severity");

    /// <summary>
    /// Gauge for security posture score (0-100).
    /// </summary>
    public static readonly ObservableGauge<int> SecurityPosture = BusinessMeter.CreateObservableGauge<int>(
        "honua_security_posture_score",
        () => CalculateSecurityPosture(),
        "score",
        "Security posture score from 0-100");

    #endregion

    #region Infrastructure Intelligence Metrics

    /// <summary>
    /// Histogram for database connection pool efficiency.
    /// </summary>
    public static readonly Histogram<double> DatabasePoolEfficiency = BusinessMeter.CreateHistogram<double>(
        "honua_db_pool_efficiency_percentage",
        "percent",
        "Database connection pool efficiency percentage");

    /// <summary>
    /// Counter for cache performance by tier and operation.
    /// </summary>
    public static readonly Counter<long> CachePerformance = BusinessMeter.CreateCounter<long>(
        "honua_cache_performance_total",
        "operations",
        "Cache performance by tier and operation type");

    /// <summary>
    /// Gauge for resource utilization prediction.
    /// </summary>
    public static readonly ObservableGauge<double> ResourceUtilizationPrediction = BusinessMeter.CreateObservableGauge<double>(
        "honua_resource_utilization_prediction_percentage",
        () => PredictResourceUtilization(),
        "percent",
        "Predicted resource utilization for next hour");

    #endregion

    #region Cost Analytics Metrics

    /// <summary>
    /// Histogram for cost per request in microunits.
    /// </summary>
    public static readonly Histogram<double> CostPerRequest = BusinessMeter.CreateHistogram<double>(
        "honua_cost_per_request_microunits",
        "microunits",
        "Cost per request in microunits");

    /// <summary>
    /// Counter for resource efficiency tracking.
    /// </summary>
    public static readonly Counter<long> ResourceEfficiency = BusinessMeter.CreateCounter<long>(
        "honua_resource_efficiency_total",
        "operations",
        "Resource efficiency by operation type");

    #endregion

    #region Anomaly Detection Metrics

    /// <summary>
    /// Counter for anomalies detected by category.
    /// </summary>
    public static readonly Counter<long> AnomaliesDetected = BusinessMeter.CreateCounter<long>(
        "honua_anomalies_detected_total",
        "anomalies",
        "Anomalies detected by category and severity");

    /// <summary>
    /// Histogram for confidence score of anomaly detection.
    /// </summary>
    public static readonly Histogram<double> AnomalyConfidence = BusinessMeter.CreateHistogram<double>(
        "honua_anomaly_confidence_score",
        "score",
        "Confidence score of anomaly detection (0-1)");

    #endregion

    #region Helper Methods

    /// <summary>
    /// Calculates the current performance score based on multiple factors.
    /// </summary>
    /// <returns>Performance score from 0-100.</returns>
    public static int CalculatePerformanceScore()
    {
        // Implementation would analyze various performance metrics
        // This is a simplified calculation
        var memoryUsage = MemoryMonitor.GetMemoryUsage();
        var memoryScore = Math.Max(0, 100 - (int)(memoryUsage.MemoryPressurePercentage * 100));

        // In a real implementation, this would combine multiple metrics
        return Math.Min(100, memoryScore);
    }

    /// <summary>
    /// Calculates the current security posture score.
    /// </summary>
    /// <returns>Security posture score from 0-100.</returns>
    public static int CalculateSecurityPosture()
    {
        // This would analyze security metrics in a real implementation
        // For now, return a baseline score
        return 85; // Placeholder indicating good security posture
    }

    /// <summary>
    /// Predicts resource utilization for the next hour.
    /// </summary>
    /// <returns>Predicted resource utilization percentage.</returns>
    private static double PredictResourceUtilization()
    {
        // This would use ML algorithms in a real implementation
        // For now, return current usage with a small increase
        var currentMemory = MemoryMonitor.GetMemoryUsage().MemoryPressurePercentage;
        return Math.Min(100.0, currentMemory * 100.0 + 5.0); // Add 5% prediction
    }

    #endregion
}

/// <summary>
/// Real-time streaming metrics for live dashboard updates.
/// Provides WebSocket-compatible metrics streaming for executive dashboards.
/// </summary>
public static class StreamingMetrics
{
    // Timer is intentionally kept alive for periodic execution
#pragma warning disable IDE0052
    private static readonly Timer _metricsTimer;
#pragma warning restore IDE0052
    private static readonly ConcurrentQueue<MetricSnapshot> _metricsQueue = new();

    static StreamingMetrics()
    {
        _metricsTimer = new Timer(CaptureSnapshot, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Gets the latest metrics snapshot for streaming.
    /// </summary>
    /// <returns>Latest metrics snapshot or null if none available.</returns>
    public static MetricSnapshot? GetLatestSnapshot()
    {
        _metricsQueue.TryDequeue(out var snapshot);
        return snapshot;
    }

    /// <summary>
    /// Captures a metrics snapshot for streaming.
    /// </summary>
    private static void CaptureSnapshot(object? state)
    {
        try
        {
            var snapshot = new MetricSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow,
                MemoryUsageMB = MemoryMonitor.GetMemoryUsage().AllocatedBytes / (1024.0 * 1024.0),
                PerformanceScore = BusinessIntelligenceMetrics.CalculatePerformanceScore(),
                SecurityScore = BusinessIntelligenceMetrics.CalculateSecurityPosture(),
                ActiveUsers = GetActiveUserCount(),
                ThroughputMBps = CalculateThroughput(),
                ErrorRate = CalculateErrorRate()
            };

            _metricsQueue.Enqueue(snapshot);

            // Keep only last 20 snapshots to prevent memory buildup
            while (_metricsQueue.Count > 20)
            {
                _metricsQueue.TryDequeue(out _);
            }
        }
        catch
        {
            // Silently handle errors in metrics capture to avoid impacting the main application
        }
    }

    private static int GetActiveUserCount()
    {
        // This would query actual user sessions in a real implementation
        return Random.Shared.Next(10, 100);
    }

    private static double CalculateThroughput()
    {
        // This would calculate actual throughput in a real implementation
        return Random.Shared.NextDouble() * 50.0; // 0-50 MB/s
    }

    private static double CalculateErrorRate()
    {
        // This would calculate actual error rate in a real implementation
        return Random.Shared.NextDouble() * 2.0; // 0-2% error rate
    }
}

/// <summary>
/// Represents a point-in-time metrics snapshot for streaming dashboards.
/// </summary>
public sealed record MetricSnapshot
{
    /// <summary>
    /// The timestamp when this snapshot was taken.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Current memory usage in megabytes.
    /// </summary>
    public double MemoryUsageMB { get; init; }

    /// <summary>
    /// Current performance score (0-100 scale).
    /// </summary>
    public int PerformanceScore { get; init; }

    /// <summary>
    /// Current security score (0-100 scale).
    /// </summary>
    public int SecurityScore { get; init; }

    /// <summary>
    /// Number of active concurrent users.
    /// </summary>
    public int ActiveUsers { get; init; }

    /// <summary>
    /// Current throughput in megabytes per second.
    /// </summary>
    public double ThroughputMBps { get; init; }

    /// <summary>
    /// Current error rate as a percentage (0.0-1.0).
    /// </summary>
    public double ErrorRate { get; init; }
}
