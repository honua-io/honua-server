// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Infrastructure.Monitoring;

/// <summary>
/// Production memory monitor with GC pressure tracking and automatic memory relief
/// </summary>
internal sealed partial class ProductionMemoryMonitor : IMemoryMonitor, IDisposable
{
    private readonly ILogger<ProductionMemoryMonitor> _logger;
    private readonly Timer _memoryCheckTimer;
    private readonly ConcurrentDictionary<string, long> _allocatedBytes = new();

    private const long HighMemoryThresholdBytes = 1024L * 1024 * 1024; // 1 GB
    private const long CriticalMemoryThresholdBytes = 2048L * 1024 * 1024; // 2 GB
    private const int MemoryCheckIntervalMs = 30000; // 30 seconds

    private volatile bool _disposed;
    private volatile bool _memoryPressureHigh;
    private long _lastGcTimeTicks = DateTimeOffset.MinValue.Ticks;

    public ProductionMemoryMonitor(ILogger<ProductionMemoryMonitor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _memoryCheckTimer = new Timer(CheckMemoryPressure, null, MemoryCheckIntervalMs, MemoryCheckIntervalMs);
    }

    public MemoryUsageSnapshot GetMemoryUsage()
    {
        var process = Process.GetCurrentProcess();

        return new MemoryUsageSnapshot
        {
            TotalMemoryBytes = GC.GetTotalMemory(false),
            WorkingSetBytes = process.WorkingSet64,
            GenerationSizes = new[]
            {
                GC.GetTotalMemory(false) - GC.GetTotalMemory(false), // Approximate Gen 0
                GC.CollectionCount(1) > 0 ? GC.GetTotalMemory(false) / 4 : 0, // Approximate Gen 1
                GC.CollectionCount(2) > 0 ? GC.GetTotalMemory(false) / 2 : 0  // Approximate Gen 2
            },
            LargeObjectHeapBytes = EstimateLohSize(),
            CollectionCounts = new[]
            {
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2)
            },
            IsUnderMemoryPressure = _memoryPressureHigh
        };
    }

    public void RecordAllocation(string source, long bytes)
    {
        if (_disposed || bytes <= 0) return;

        _allocatedBytes.AddOrUpdate(source, bytes, (_, existing) => existing + bytes);

        // Check for immediate pressure on large allocations
        if (bytes > 10 * 1024 * 1024) // 10 MB
        {
            CheckMemoryPressureInternal();
        }
    }

    public void RecordDeallocation(string source, long bytes)
    {
        if (_disposed || bytes <= 0) return;

        _allocatedBytes.AddOrUpdate(source, 0, (_, existing) => Math.Max(0, existing - bytes));
    }

    public bool IsMemoryPressureHigh()
    {
        return _memoryPressureHigh;
    }

    public async Task<bool> TryRelieveMemoryPressureAsync()
    {
        if (_disposed) return false;

        var now = DateTimeOffset.UtcNow;

        // Don't force GC too frequently
        if (now.Ticks - Interlocked.Read(ref _lastGcTimeTicks) < TimeSpan.FromMinutes(1).Ticks)
            return false;

        var memoryBefore = GC.GetTotalMemory(false);

        // Force collection of all generations
        await Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        });

        var memoryAfter = GC.GetTotalMemory(false);
        var freedBytes = memoryBefore - memoryAfter;

        Interlocked.Exchange(ref _lastGcTimeTicks, now.Ticks);

        if (freedBytes > 0)
        {
            MemoryMonitorLog.MemoryPressureRelieved(_logger, freedBytes, memoryBefore, memoryAfter);
            return true;
        }

        return false;
    }

    private void CheckMemoryPressure(object? state)
    {
        if (_disposed) return;
        CheckMemoryPressureInternal();
    }

    private void CheckMemoryPressureInternal()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var totalMemory = GC.GetTotalMemory(false);
            var workingSet = process.WorkingSet64;

            var wasHighPressure = _memoryPressureHigh;
            var isHighPressure = totalMemory > HighMemoryThresholdBytes ||
                                workingSet > HighMemoryThresholdBytes;
            var isCriticalPressure = totalMemory > CriticalMemoryThresholdBytes ||
                                   workingSet > CriticalMemoryThresholdBytes;

            _memoryPressureHigh = isHighPressure;

            // Log transitions
            if (!wasHighPressure && isHighPressure)
            {
                MemoryMonitorLog.MemoryPressureDetected(_logger, totalMemory, workingSet);
            }
            else if (wasHighPressure && !isHighPressure)
            {
                MemoryMonitorLog.MemoryPressureNormalized(_logger, totalMemory, workingSet);
            }

            // Auto-relieve critical pressure
            if (isCriticalPressure)
            {
                MemoryMonitorLog.CriticalMemoryPressureDetected(_logger, totalMemory, workingSet);
                _ = Task.Run(async () => await TryRelieveMemoryPressureAsync());
            }
        }
        catch (Exception ex)
        {
            MemoryMonitorLog.MemoryCheckFailed(_logger, ex);
        }
    }

    private static long EstimateLohSize()
    {
        // Rough estimation based on GC stats - not exact but useful for monitoring
        var gen2Collections = GC.CollectionCount(2);
        return gen2Collections > 0 ? GC.GetTotalMemory(false) / 10 : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _memoryCheckTimer?.Dispose();
        _allocatedBytes.Clear();
    }

    private static partial class MemoryMonitorLog
    {
        [Microsoft.Extensions.Logging.LoggerMessage(
            EventId = 8001,
            Level = LogLevel.Warning,
            Message = "Memory pressure detected - TotalMemory={TotalMemory:N0} bytes, WorkingSet={WorkingSet:N0} bytes")]
        public static partial void MemoryPressureDetected(ILogger logger, long totalMemory, long workingSet);

        [Microsoft.Extensions.Logging.LoggerMessage(
            EventId = 8002,
            Level = LogLevel.Information,
            Message = "Memory pressure normalized - TotalMemory={TotalMemory:N0} bytes, WorkingSet={WorkingSet:N0} bytes")]
        public static partial void MemoryPressureNormalized(ILogger logger, long totalMemory, long workingSet);

        [Microsoft.Extensions.Logging.LoggerMessage(
            EventId = 8003,
            Level = LogLevel.Error,
            Message = "Critical memory pressure detected - TotalMemory={TotalMemory:N0} bytes, WorkingSet={WorkingSet:N0} bytes")]
        public static partial void CriticalMemoryPressureDetected(ILogger logger, long totalMemory, long workingSet);

        [Microsoft.Extensions.Logging.LoggerMessage(
            EventId = 8004,
            Level = LogLevel.Information,
            Message = "Memory pressure relieved - Freed={FreedBytes:N0} bytes, Before={MemoryBefore:N0}, After={MemoryAfter:N0}")]
        public static partial void MemoryPressureRelieved(ILogger logger, long freedBytes, long memoryBefore, long memoryAfter);

        [Microsoft.Extensions.Logging.LoggerMessage(
            EventId = 8005,
            Level = LogLevel.Warning,
            Message = "Memory check failed")]
        public static partial void MemoryCheckFailed(ILogger logger, Exception exception);
    }
}
