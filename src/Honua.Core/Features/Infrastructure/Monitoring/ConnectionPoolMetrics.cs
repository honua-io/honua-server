// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Metrics for database connection pool monitoring.
/// Provides insights into connection utilization and acquisition latency.
/// </summary>
/// <remarks>
/// Utilization and connection-acquisition-timeout readings are sourced from the live
/// <see cref="IDatabaseAdmissionPressureSource"/> (the admission gate that actually throttles
/// connection acquisition) when one is registered — i.e. on the Postgres provider. Prior to
/// honua-server#2805 these values came from counters that no production code ever wrote, so the
/// health snapshot reported <c>utilization: null, timeouts: 0</c> even during a real
/// pool-exhaustion incident. When no admission gate is active (non-Postgres providers) utilization
/// is honestly reported as unavailable rather than fabricated.
/// </remarks>
public sealed class ConnectionPoolMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Histogram<double> _connectionAcquisitionLatency;
    private readonly ObservableGauge<int> _activeConnections;
    private readonly ObservableGauge<int> _poolSize;
    private readonly ObservableGauge<double> _poolUtilization;
    private readonly ObservableGauge<long> _connectionTimeouts;

    private readonly IActiveDbConnectionTracker _connectionTracker;
    private readonly IDatabaseAdmissionPressureSource? _pressureSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionPoolMetrics"/> class.
    /// </summary>
    /// <param name="connectionTracker">Connection tracker for active connection monitoring.</param>
    /// <param name="pressureSource">
    /// Live database admission-pressure source (the concurrency/admission gate). Optional: absent for
    /// providers that do not run a bounded admission gate, in which case utilization is reported as
    /// unavailable and the windowed timeout count is zero.
    /// </param>
    public ConnectionPoolMetrics(
        IActiveDbConnectionTracker connectionTracker,
        IDatabaseAdmissionPressureSource? pressureSource = null)
    {
        _connectionTracker = connectionTracker;
        _pressureSource = pressureSource;
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
            () => _pressureSource?.GetPressureReading().CurrentLimit ?? 0,
            description: "Current logical database admission target (concurrent-query limit)");

        _poolUtilization = _meter.CreateObservableGauge<double>(
            "honua_db_pool_utilization_ratio",
            () => TryGetPoolUtilization(out var utilization) ? utilization : 0,
            description: "Database connection pool utilization ratio (0.0-1.0)");

        _connectionTimeouts = _meter.CreateObservableGauge<long>(
            "honua_db_connection_acquisition_timeouts_window",
            () => GetTotalTimeouts(),
            description: "Connection-acquisition timeouts observed within the admission gate's rolling window");
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
        return TryGetPoolUtilization(out var utilization) ? utilization : 0;
    }

    /// <summary>
    /// Tries to get the current pool utilization ratio when an admission gate is active.
    /// </summary>
    /// <param name="utilization">The utilization ratio if available.</param>
    /// <returns>True when utilization is available, otherwise false.</returns>
    public bool TryGetPoolUtilization(out double utilization)
    {
        if (_pressureSource is not null)
        {
            var reading = _pressureSource.GetPressureReading();
            if (reading.HasUtilization)
            {
                utilization = reading.Utilization;
                return true;
            }
        }

        utilization = 0;
        return false;
    }

    /// <summary>
    /// Gets the number of connection-acquisition failures. The admission-gate model surfaces
    /// acquisition timeouts (see <see cref="GetTotalTimeouts"/>) as the actionable pressure signal;
    /// non-timeout acquisition failures are not separately instrumented, so this returns zero.
    /// </summary>
    /// <returns>Total failures count (always zero under the admission-gate model).</returns>
#pragma warning disable CA1822 // Kept as an instance method for API stability across metrics consumers.
    public long GetTotalFailures()
#pragma warning restore CA1822
    {
        return 0;
    }

    /// <summary>
    /// Gets the number of connection-acquisition timeouts observed within the admission gate's
    /// rolling window. This is a windowed signal, not a lifetime-monotonic total, so a burst of
    /// timeouts relaxes back to zero once the window elapses instead of latching until restart.
    /// </summary>
    /// <returns>Windowed timeout count (zero when no admission gate is active).</returns>
    public long GetTotalTimeouts()
    {
        return _pressureSource?.GetPressureReading().AcquisitionTimeoutsInWindow ?? 0;
    }

    /// <summary>
    /// Disposes the metrics and underlying meter.
    /// </summary>
    public void Dispose()
    {
        _meter.Dispose();
    }
}
