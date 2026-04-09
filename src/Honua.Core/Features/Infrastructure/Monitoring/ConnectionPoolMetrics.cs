// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Metrics for database connection pool monitoring.
/// Provides insights into connection utilization and acquisition latency.
/// </summary>
public sealed class ConnectionPoolMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Histogram<double> _connectionAcquisitionLatency;
    private readonly ObservableGauge<int> _activeConnections;
    private readonly ObservableGauge<int> _poolSize;
    private readonly ObservableGauge<double> _poolUtilization;
    private readonly Counter<long> _connectionAcquisitionFailures;
    private readonly Counter<long> _connectionTimeouts;

    private readonly IActiveDbConnectionTracker _connectionTracker;
    private volatile int _currentPoolSize;
    private long _acquisitionFailures;
    private long _timeouts;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionPoolMetrics"/> class.
    /// </summary>
    /// <param name="connectionTracker">Connection tracker for active connection monitoring.</param>
    public ConnectionPoolMetrics(IActiveDbConnectionTracker connectionTracker)
    {
        _connectionTracker = connectionTracker;
        _meter = new Meter("Honua.Database.ConnectionPool");

        _connectionAcquisitionLatency = _meter.CreateHistogram<double>(
            "honua_db_connection_acquisition_duration_ms",
            "milliseconds",
            "Duration of database connection acquisition");

        _activeConnections = _meter.CreateObservableGauge<int>(
            "honua_db_active_connections",
            () => _connectionTracker.GetActiveCount(),
            description: "Number of active database connections");

        _poolSize = _meter.CreateObservableGauge<int>(
            "honua_db_pool_size",
            () => _currentPoolSize,
            description: "Total size of database connection pool");

        _poolUtilization = _meter.CreateObservableGauge<double>(
            "honua_db_pool_utilization_ratio",
            () => _currentPoolSize > 0 ? (double)_connectionTracker.GetActiveCount() / _currentPoolSize : 0,
            description: "Database connection pool utilization ratio (0.0-1.0)");

        _connectionAcquisitionFailures = _meter.CreateCounter<long>(
            "honua_db_connection_acquisition_failures_total",
            description: "Total number of database connection acquisition failures");

        _connectionTimeouts = _meter.CreateCounter<long>(
            "honua_db_connection_timeouts_total",
            description: "Total number of database connection timeouts");
    }

    /// <summary>
    /// Records the latency of a database connection acquisition.
    /// </summary>
    /// <param name="latency">The acquisition latency.</param>
    /// <param name="tags">Additional tags for the metric.</param>
    public void RecordConnectionAcquisitionLatency(TimeSpan latency, params KeyValuePair<string, object?>[] tags)
    {
        _connectionAcquisitionLatency.Record(latency.TotalMilliseconds, tags);
    }

    /// <summary>
    /// Records a database connection acquisition failure.
    /// </summary>
    /// <param name="reason">The reason for the failure.</param>
    public void RecordConnectionAcquisitionFailure(string reason)
    {
        _connectionAcquisitionFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
        Interlocked.Increment(ref _acquisitionFailures);
    }

    /// <summary>
    /// Records a database connection timeout.
    /// </summary>
    public void RecordConnectionTimeout()
    {
        _connectionTimeouts.Add(1);
        Interlocked.Increment(ref _timeouts);
    }

    /// <summary>
    /// Updates the pool size for utilization calculation.
    /// </summary>
    /// <param name="poolSize">The current pool size.</param>
    public void UpdatePoolSize(int poolSize)
    {
        _currentPoolSize = poolSize;
    }

    /// <summary>
    /// Increments the active connection count (delegated from tracker).
    /// </summary>
#pragma warning disable CA1822 // Member does not access instance data and can be marked as static
    public void Increment()
#pragma warning restore CA1822
    {
        // This is a pass-through method for compatibility with the decorator
        // The actual tracking is done by the IActiveDbConnectionTracker
    }

    /// <summary>
    /// Decrements the active connection count (delegated from tracker).
    /// </summary>
#pragma warning disable CA1822 // Member does not access instance data and can be marked as static
    public void Decrement()
#pragma warning restore CA1822
    {
        // This is a pass-through method for compatibility with the decorator
        // The actual tracking is done by the IActiveDbConnectionTracker
    }

    /// <summary>
    /// Gets the current pool utilization ratio (0.0-1.0).
    /// </summary>
    /// <returns>Pool utilization ratio.</returns>
    public double GetPoolUtilization()
    {
        return _currentPoolSize > 0 ? (double)_connectionTracker.GetActiveCount() / _currentPoolSize : 0;
    }

    /// <summary>
    /// Gets the total number of connection acquisition failures.
    /// </summary>
    /// <returns>Total failures count.</returns>
    public long GetTotalFailures()
    {
        return Volatile.Read(ref _acquisitionFailures);
    }

    /// <summary>
    /// Gets the total number of connection timeouts.
    /// </summary>
    /// <returns>Total timeouts count.</returns>
    public long GetTotalTimeouts()
    {
        return Volatile.Read(ref _timeouts);
    }

    /// <summary>
    /// Disposes the metrics and underlying meter.
    /// </summary>
    public void Dispose()
    {
        _meter.Dispose();
    }
}
