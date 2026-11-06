// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.Core.Features.Infrastructure.Monitoring;

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
    private volatile bool _hasPoolSize;
    private long _acquisitionFailures;
    private long _timeouts;

    public ConnectionPoolMetrics(IActiveDbConnectionTracker connectionTracker)
    {
        _connectionTracker = connectionTracker;
        _meter = new Meter("Honua.Database.ConnectionPool");

        // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
        // Prometheus exporter derives the exported series name from the instrument name AND its unit:
        // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
        // out from under every dashboard and alert rule without breaking a single build. A PromQL query
        // against the absent name returns an empty vector, so the panel is blank and the alert never
        // fires, silently. Units are documented in the instrument name and description instead. See the
        // SLO-contract comment block in HonuaTelemetry.cs, observability/metric-name-contract.json, and
        // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

        _connectionAcquisitionLatency = _meter.CreateHistogram<double>(
            "honua_db_connection_acquisition_duration_ms",
            unit: null,
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

    public void RecordConnectionAcquisitionLatency(TimeSpan latency, params KeyValuePair<string, object?>[] tags)
    {
        _connectionAcquisitionLatency.Record(latency.TotalMilliseconds, tags);
    }

    public void RecordConnectionAcquisitionFailure(string reason)
    {
        _connectionAcquisitionFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
        Interlocked.Increment(ref _acquisitionFailures);
    }

    public void RecordConnectionTimeout()
    {
        _connectionTimeouts.Add(1);
        Interlocked.Increment(ref _timeouts);
    }

    public void UpdatePoolSize(int poolSize)
    {
        _currentPoolSize = Math.Max(0, poolSize);
        _hasPoolSize = _currentPoolSize > 0;
    }

#pragma warning disable CA1822 // Member does not access instance data and can be marked as static
    public void Increment()
#pragma warning restore CA1822
    {
        // This is a pass-through method for compatibility with the decorator
        // The actual tracking is done by the IActiveDbConnectionTracker
    }

#pragma warning disable CA1822 // Member does not access instance data and can be marked as static
    public void Decrement()
#pragma warning restore CA1822
    {
        // This is a pass-through method for compatibility with the decorator
        // The actual tracking is done by the IActiveDbConnectionTracker
    }

    public double GetPoolUtilization()
    {
        return TryGetPoolUtilization(out var utilization) ? utilization : 0;
    }

    public bool TryGetPoolUtilization(out double utilization)
    {
        if (_hasPoolSize && _currentPoolSize > 0)
        {
            utilization = (double)_connectionTracker.GetActiveCount() / _currentPoolSize;
            return true;
        }

        utilization = 0;
        return false;
    }

    public long GetTotalFailures()
    {
        return Volatile.Read(ref _acquisitionFailures);
    }

    public long GetTotalTimeouts()
    {
        return Volatile.Read(ref _timeouts);
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}
