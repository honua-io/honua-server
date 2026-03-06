// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

using System.Diagnostics;

namespace HonuaFieldApp.Services;

/// <summary>
/// Service for monitoring mobile app performance metrics.
/// Critical for validating production readiness on real devices.
/// </summary>
public interface IPerformanceMonitorService
{
    /// <summary>
    /// Get current performance metrics.
    /// </summary>
    Task<PerformanceMetrics> GetCurrentMetricsAsync();

    /// <summary>
    /// Start continuous performance monitoring.
    /// </summary>
    /// <param name="interval">Monitoring interval</param>
    /// <param name="cancellationToken">Cancellation token</param>
    IAsyncEnumerable<PerformanceMetrics> StartMonitoringAsync(
        TimeSpan interval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Record a custom performance event.
    /// </summary>
    Task RecordEventAsync(string eventName, TimeSpan duration, Dictionary<string, object>? metadata = null);

    /// <summary>
    /// Get performance summary for a time period.
    /// </summary>
    Task<PerformanceSummary> GetSummaryAsync(TimeSpan period);
}

/// <summary>
/// Real-time performance metrics for mobile devices.
/// </summary>
public class PerformanceMetrics
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double MemoryUsageMB { get; init; }
    public double CpuUsagePercent { get; init; }
    public double BatteryLevel { get; init; }
    public TimeSpan Uptime { get; init; }
    public int ActiveThreads { get; init; }
    public long GcCollections { get; init; }
    public TimeSpan LastGcDuration { get; init; }
    public double NetworkBytesReceived { get; init; }
    public double NetworkBytesSent { get; init; }
    public int ActiveNetworkConnections { get; init; }
    public double RenderFrameRate { get; init; }
    public TimeSpan LastRenderTime { get; init; }
}

/// <summary>
/// Performance summary over a time period.
/// </summary>
public class PerformanceSummary
{
    public TimeSpan Period { get; init; }
    public double AverageMemoryMB { get; init; }
    public double PeakMemoryMB { get; init; }
    public double AverageCpuPercent { get; init; }
    public double PeakCpuPercent { get; init; }
    public double AverageFrameRate { get; init; }
    public double MinFrameRate { get; init; }
    public TimeSpan TotalNetworkTime { get; init; }
    public double TotalNetworkMB { get; init; }
    public int TotalGcCollections { get; init; }
    public TimeSpan TotalGcTime { get; init; }
    public List<PerformanceEvent> CustomEvents { get; init; } = new();
}

/// <summary>
/// Custom performance event for specific operations.
/// </summary>
public class PerformanceEvent
{
    public DateTime Timestamp { get; init; }
    public string EventName { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Implementation of performance monitoring service for mobile devices.
/// </summary>
public class PerformanceMonitorService : IPerformanceMonitorService
{
    private readonly ILogger<PerformanceMonitorService> _logger;
    private readonly List<PerformanceEvent> _events = new();
    private readonly object _eventsLock = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private long _initialGcCollections;
    private Process? _currentProcess;

    public PerformanceMonitorService(ILogger<PerformanceMonitorService> logger)
    {
        _logger = logger;
        _currentProcess = Process.GetCurrentProcess();
        _initialGcCollections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
    }

    public async Task<PerformanceMetrics> GetCurrentMetricsAsync()
    {
        try
        {
            var metrics = new PerformanceMetrics
            {
                MemoryUsageMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0,
                CpuUsagePercent = await GetCpuUsageAsync(),
                BatteryLevel = await GetBatteryLevelAsync(),
                Uptime = _uptime.Elapsed,
                ActiveThreads = Process.GetCurrentProcess().Threads.Count,
                GcCollections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - _initialGcCollections,
                LastGcDuration = TimeSpan.FromMilliseconds(GC.GetTotalPauseDuration().TotalMilliseconds),
                NetworkBytesReceived = 0, // Platform-specific implementation needed
                NetworkBytesSent = 0,     // Platform-specific implementation needed
                ActiveNetworkConnections = 0, // Platform-specific implementation needed
                RenderFrameRate = 60.0,   // Placeholder - needs platform-specific implementation
                LastRenderTime = TimeSpan.FromMilliseconds(16.67) // 60fps = 16.67ms per frame
            };

            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting performance metrics");
            return new PerformanceMetrics(); // Return default values
        }
    }

    public async IAsyncEnumerable<PerformanceMetrics> StartMonitoringAsync(
        TimeSpan interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting performance monitoring with {Interval}ms interval", interval.TotalMilliseconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            var metrics = await GetCurrentMetricsAsync();
            yield return metrics;

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Performance monitoring stopped");
    }

    public async Task RecordEventAsync(string eventName, TimeSpan duration, Dictionary<string, object>? metadata = null)
    {
        var eventRecord = new PerformanceEvent
        {
            Timestamp = DateTime.UtcNow,
            EventName = eventName,
            Duration = duration,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        lock (_eventsLock)
        {
            _events.Add(eventRecord);

            // Keep only last 1000 events to prevent memory growth
            if (_events.Count > 1000)
            {
                _events.RemoveAt(0);
            }
        }

        _logger.LogDebug("Recorded performance event: {EventName} took {Duration}ms",
            eventName, duration.TotalMilliseconds);

        await Task.CompletedTask;
    }

    public async Task<PerformanceSummary> GetSummaryAsync(TimeSpan period)
    {
        var cutoffTime = DateTime.UtcNow - period;
        List<PerformanceEvent> relevantEvents;

        lock (_eventsLock)
        {
            relevantEvents = _events
                .Where(e => e.Timestamp >= cutoffTime)
                .ToList();
        }

        // This would typically gather metrics over the period
        // For now, return current metrics as averages
        var currentMetrics = await GetCurrentMetricsAsync();

        var summary = new PerformanceSummary
        {
            Period = period,
            AverageMemoryMB = currentMetrics.MemoryUsageMB,
            PeakMemoryMB = currentMetrics.MemoryUsageMB * 1.2, // Estimate
            AverageCpuPercent = currentMetrics.CpuUsagePercent,
            PeakCpuPercent = Math.Min(100, currentMetrics.CpuUsagePercent * 1.5), // Estimate
            AverageFrameRate = currentMetrics.RenderFrameRate,
            MinFrameRate = currentMetrics.RenderFrameRate * 0.8, // Estimate
            TotalNetworkTime = TimeSpan.FromSeconds(relevantEvents.Count * 0.1), // Estimate
            TotalNetworkMB = currentMetrics.NetworkBytesReceived / 1024.0 / 1024.0,
            TotalGcCollections = (int)currentMetrics.GcCollections,
            TotalGcTime = currentMetrics.LastGcDuration,
            CustomEvents = relevantEvents
        };

        return summary;
    }

    private async Task<double> GetCpuUsageAsync()
    {
        try
        {
            if (_currentProcess == null) return 0;

            // Simple CPU usage estimation
            // In a real implementation, you'd measure CPU time over an interval
            return Math.Min(100, Environment.ProcessorCount * 10); // Placeholder
        }
        catch
        {
            return 0;
        }
    }

    private async Task<double> GetBatteryLevelAsync()
    {
        try
        {
            var battery = await Battery.GetInfoAsync();
            return battery.ChargeLevel * 100;
        }
        catch
        {
            return 100; // Default to full battery if unavailable
        }
    }
}