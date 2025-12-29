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
public sealed class PerformanceMetrics
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
