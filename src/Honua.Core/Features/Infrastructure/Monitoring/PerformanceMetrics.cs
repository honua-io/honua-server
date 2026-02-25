// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    // Note: Shares meter name "Honua" with HonuaTelemetry.Meter in ServiceDefaults
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

    #region Memory Pressure Metrics

    /// <summary>
    /// Gauge for memory pressure percentage.
    /// </summary>
    public static readonly ObservableGauge<double> MemoryPressurePercentage = Meter.CreateObservableGauge<double>(
        "honua_memory_pressure_percent",
        () => MemoryMonitor.GetMemoryUsage().MemoryPressurePercentage,
        "percent",
        "Current memory pressure as percentage of available memory");

    /// <summary>
    /// Counter for high memory pressure alerts.
    /// </summary>
    public static readonly Counter<long> HighMemoryPressureAlerts = Meter.CreateCounter<long>(
        "honua_memory_pressure_alerts_total",
        "alerts",
        "Total number of high memory pressure alerts triggered");

    /// <summary>
    /// Histogram for allocated memory in megabytes.
    /// </summary>
    public static readonly Histogram<long> AllocatedMemoryMB = Meter.CreateHistogram<long>(
        "honua_memory_allocated_mb",
        "megabytes",
        "Allocated memory in megabytes");

    #endregion

    #region Enhanced Cache Metrics

    /// <summary>
    /// Histogram for detailed cache hit ratio by cache type.
    /// </summary>
    public static readonly Histogram<double> DetailedCacheHitRatio = Meter.CreateHistogram<double>(
        "honua_cache_hit_ratio_detailed",
        "ratio",
        "Detailed cache hit ratio by cache type and time window");

    /// <summary>
    /// Counter for cache errors.
    /// </summary>
    public static readonly Counter<long> CacheErrors = Meter.CreateCounter<long>(
        "honua_cache_errors_total",
        "errors",
        "Total number of cache operation errors");

    #endregion

    #region Enhanced Geospatial Metrics

    /// <summary>
    /// Histogram for spatial query latency.
    /// </summary>
    public static readonly Histogram<double> SpatialQueryDuration = Meter.CreateHistogram<double>(
        "honua_spatial_query_duration_ms",
        "ms",
        "Duration of spatial query operations in milliseconds");

    /// <summary>
    /// Counter for spatial query operations.
    /// </summary>
    public static readonly Counter<long> SpatialQueryCount = Meter.CreateCounter<long>(
        "honua_spatial_query_total",
        "queries",
        "Total number of spatial query operations");

    /// <summary>
    /// Histogram for coordinate transformation performance.
    /// </summary>
    public static readonly Histogram<double> CoordinateTransformDuration = Meter.CreateHistogram<double>(
        "honua_coordinate_transform_duration_ms",
        "ms",
        "Duration of coordinate transformation operations in milliseconds");

    /// <summary>
    /// Counter for coordinate transformation operations.
    /// </summary>
    public static readonly Counter<long> CoordinateTransformCount = Meter.CreateCounter<long>(
        "honua_coordinate_transform_total",
        "transforms",
        "Total number of coordinate transformation operations");

    /// <summary>
    /// Histogram for spatial filter operations.
    /// </summary>
    public static readonly Histogram<double> SpatialFilterDuration = Meter.CreateHistogram<double>(
        "honua_spatial_filter_duration_ms",
        "ms",
        "Duration of spatial filter operations in milliseconds");

    /// <summary>
    /// Counter for spatial filter operations.
    /// </summary>
    public static readonly Counter<long> SpatialFilterCount = Meter.CreateCounter<long>(
        "honua_spatial_filter_total",
        "filters",
        "Total number of spatial filter operations");

    #endregion

    #region Error Tracking Metrics

    /// <summary>
    /// Counter for application errors by type.
    /// </summary>
    public static readonly Counter<long> ApplicationErrors = Meter.CreateCounter<long>(
        "honua_application_errors_total",
        "errors",
        "Total number of application errors by type and operation");

    /// <summary>
    /// Histogram for error recovery time.
    /// </summary>
    public static readonly Histogram<double> ErrorRecoveryDuration = Meter.CreateHistogram<double>(
        "honua_error_recovery_duration_ms",
        "ms",
        "Time taken to recover from errors in milliseconds");

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
