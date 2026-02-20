// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Lightweight system metrics collector for adaptive sampling decisions.
/// Provides real-time system performance data without external dependencies.
/// Designed for self-hosted installations with minimal overhead.
/// </summary>
public sealed class SystemMetricsCollector : ISystemMetricsCollector, IDisposable
{
    private readonly Timer _metricsTimer;
    private readonly ConcurrentQueue<(DateTime Timestamp, bool IsError)> _recentRequests = new();
    private readonly TimeSpan _errorWindow;

    private volatile SystemMetrics _currentMetrics = new();
    private volatile int _activeRequests;
    private double _totalResponseTime;
    private volatile int _responseCount;
    private volatile bool _disposed;
    private readonly object _cpuLock = new();
    private readonly object _responseLock = new();
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleTime;

    /// <summary>
    /// Initializes a new SystemMetricsCollector with 10-second collection intervals.
    /// </summary>
    public SystemMetricsCollector(IOptions<AdaptiveSamplingOptions>? options = null)
    {
        var windowMinutes = options?.Value.Error.ErrorWindowMinutes ?? 5;
        _errorWindow = TimeSpan.FromMinutes(Math.Max(1, windowMinutes));
        _metricsTimer = new Timer(CollectMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Gets the current system performance metrics snapshot.
    /// </summary>
    public SystemMetrics GetCurrentMetrics()
    {
        ThrowIfDisposed();
        return _currentMetrics;
    }

    /// <summary>
    /// Records the start of a request for active request tracking.
    /// </summary>
    public IDisposable TrackRequest()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _activeRequests);
        return new RequestScope(this);
    }

    /// <summary>
    /// Records a completed request with response time and error status.
    /// </summary>
    public void RecordRequest(TimeSpan responseTime, bool isError)
    {
        if (_disposed)
        {
            return;
        }

        // Update response time metrics
        var responseMs = responseTime.TotalMilliseconds;
        lock (_responseLock)
        {
            _totalResponseTime += responseMs;
            _responseCount++;
        }

        // Track error for error rate calculation
        _recentRequests.Enqueue((DateTime.UtcNow, isError));

        // Clean old entries periodically (every 10th request to avoid overhead)
        if (_responseCount % 10 == 0)
        {
            CleanOldErrorEntries();
        }
    }

    /// <summary>
    /// Calculates the current error rate over the configured error window.
    /// </summary>
    public double GetCurrentErrorRate()
    {
        if (_disposed)
        {
            return 0.0;
        }

        CleanOldErrorEntries();

        var allRequests = _recentRequests.ToArray();
        if (allRequests.Length == 0)
        {
            return 0.0;
        }

        var errorCount = allRequests.Count(r => r.IsError);
        return (double)errorCount / allRequests.Length * 100.0; // Return as percentage
    }

    private void CollectMetrics(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var process = Process.GetCurrentProcess();

            // Get GC memory info
            var gcInfo = GC.GetGCMemoryInfo();
            var totalMemory = GC.GetTotalMemory(false);

            // Calculate process CPU usage based on delta samples.
            var cpuUsage = GetProcessCpuUsage(process);

            // Calculate average response time
            double avgResponseTime;
            lock (this)
            {
                avgResponseTime = _responseCount > 0 ? _totalResponseTime / _responseCount : 0.0;
            }

            _currentMetrics = new SystemMetrics
            {
                CpuUsagePercentage = cpuUsage,
                MemoryUsagePercentage = CalculateMemoryUsagePercentage(gcInfo, totalMemory),
                ActiveRequests = _activeRequests,
                AverageResponseTimeMs = avgResponseTime,
                TotalMemoryMB = totalMemory / (1024.0 * 1024.0),
                AvailableMemoryMB = GetAvailableMemoryMB(),
                GcCollections = GetGcCollectionCounts(),
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception)
        {
            // Don't let metrics collection crash the application
            // Keep the previous metrics if collection fails
        }
    }

    private double GetProcessCpuUsage(Process process)
    {
        try
        {
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;

            lock (_cpuLock)
            {
                if (_lastCpuSampleTime == default)
                {
                    _lastCpuSampleTime = now;
                    _lastCpuTime = cpuTime;
                    return 0.0;
                }

                var cpuDelta = cpuTime - _lastCpuTime;
                var timeDelta = now - _lastCpuSampleTime;

                _lastCpuSampleTime = now;
                _lastCpuTime = cpuTime;

                if (timeDelta.TotalMilliseconds <= 0 || Environment.ProcessorCount <= 0)
                {
                    return 0.0;
                }

                var usage = cpuDelta.TotalMilliseconds / (timeDelta.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
                return Math.Clamp(usage, 0.0, 100.0);
            }
        }
        catch
        {
            return 0.0; // Fallback if CPU usage cannot be determined
        }
    }

    private static double CalculateMemoryUsagePercentage(GCMemoryInfo gcInfo, long totalMemory)
    {
        try
        {
            if (gcInfo.TotalAvailableMemoryBytes == 0)
            {
                return 0.0;
            }

            var usedMemory = totalMemory;
            var availableMemory = gcInfo.TotalAvailableMemoryBytes;

            return Math.Min((double)usedMemory / availableMemory * 100.0, 100.0);
        }
        catch
        {
            return 0.0;
        }
    }

    private static double GetAvailableMemoryMB()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0);
        }
        catch
        {
            return 0.0;
        }
    }

    private static GcCollectionInfo GetGcCollectionCounts()
    {
        try
        {
            return new GcCollectionInfo
            {
                Gen0 = GC.CollectionCount(0),
                Gen1 = GC.CollectionCount(1),
                Gen2 = GC.CollectionCount(2)
            };
        }
        catch
        {
            return new GcCollectionInfo();
        }
    }

    private void CleanOldErrorEntries()
    {
        var cutoff = DateTime.UtcNow - _errorWindow;
        while (_recentRequests.TryPeek(out var entry) && entry.Timestamp < cutoff)
        {
            _recentRequests.TryDequeue(out _);
        }
    }

    private void EndRequest()
    {
        Interlocked.Decrement(ref _activeRequests);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(SystemMetricsCollector));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _metricsTimer?.Dispose();
    }

    private sealed class RequestScope : IDisposable
    {
        private readonly SystemMetricsCollector _collector;
        private bool _disposed;

        public RequestScope(SystemMetricsCollector collector)
        {
            _collector = collector;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _collector.EndRequest();
        }
    }
}

/// <summary>
/// Interface for system metrics collection used by adaptive sampling.
/// </summary>
public interface ISystemMetricsCollector
{
    /// <summary>
    /// Gets the current system performance metrics.
    /// </summary>
    SystemMetrics GetCurrentMetrics();

    /// <summary>
    /// Tracks an active request, returning a disposable scope.
    /// </summary>
    IDisposable TrackRequest();

    /// <summary>
    /// Records a completed request with timing and error information.
    /// </summary>
    void RecordRequest(TimeSpan responseTime, bool isError);

    /// <summary>
    /// Gets the current error rate as a percentage.
    /// </summary>
    double GetCurrentErrorRate();
}

/// <summary>
/// Snapshot of current system performance metrics.
/// </summary>
public sealed record SystemMetrics
{
    /// <summary>
    /// CPU usage percentage (0-100).
    /// </summary>
    public double CpuUsagePercentage { get; init; }

    /// <summary>
    /// Memory usage percentage (0-100).
    /// </summary>
    public double MemoryUsagePercentage { get; init; }

    /// <summary>
    /// Number of currently active HTTP requests.
    /// </summary>
    public int ActiveRequests { get; init; }

    /// <summary>
    /// Average response time in milliseconds.
    /// </summary>
    public double AverageResponseTimeMs { get; init; }

    /// <summary>
    /// Total memory usage in megabytes.
    /// </summary>
    public double TotalMemoryMB { get; init; }

    /// <summary>
    /// Available memory in megabytes.
    /// </summary>
    public double AvailableMemoryMB { get; init; }

    /// <summary>
    /// Garbage collection statistics.
    /// </summary>
    public GcCollectionInfo GcCollections { get; init; } = new();

    /// <summary>
    /// Timestamp when metrics were collected.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Garbage collection metrics for system monitoring.
/// </summary>
public sealed record GcCollectionInfo
{
    /// <summary>Generation 0 garbage collection count.</summary>
    public int Gen0 { get; init; }

    /// <summary>Generation 1 garbage collection count.</summary>
    public int Gen1 { get; init; }

    /// <summary>Generation 2 garbage collection count.</summary>
    public int Gen2 { get; init; }
}
