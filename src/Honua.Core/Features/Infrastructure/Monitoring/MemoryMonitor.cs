// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Monitors memory usage and garbage collection statistics.
/// </summary>
/// <remarks>
/// This class provides utilities for tracking memory usage patterns and GC performance
/// to help identify memory-related performance issues.
/// </remarks>
public static class MemoryMonitor
{
    /// <summary>
    /// Gets the current memory usage statistics.
    /// </summary>
    /// <returns>Current memory usage information</returns>
    public static MemoryUsage GetMemoryUsage()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();

            return new MemoryUsage
            {
                AllocatedBytes = GC.GetTotalMemory(forceFullCollection: false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                HeapSizeBytes = gcInfo.HeapSizeBytes,
                HighMemoryLoadThresholdBytes = gcInfo.HighMemoryLoadThresholdBytes,
                MemoryLoadBytes = gcInfo.MemoryLoadBytes,
                TotalAvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (NotSupportedException)
        {
            var allocatedBytes = GC.GetTotalMemory(forceFullCollection: false);
            return new MemoryUsage
            {
                AllocatedBytes = allocatedBytes,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                HeapSizeBytes = allocatedBytes,
                HighMemoryLoadThresholdBytes = 0,
                MemoryLoadBytes = allocatedBytes,
                TotalAvailableMemoryBytes = 0,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>
    /// Gets the total memory allocated since the last call, useful for tracking memory pressure.
    /// </summary>
    /// <param name="forceFullCollection">Whether to force a full garbage collection before measurement</param>
    /// <returns>Total allocated memory in bytes</returns>
    public static long GetAllocatedMemory(bool forceFullCollection = false)
    {
        return GC.GetTotalMemory(forceFullCollection);
    }

    /// <summary>
    /// Forces a garbage collection and returns the memory usage after cleanup.
    /// </summary>
    /// <returns>Memory usage after forced garbage collection</returns>
    public static MemoryUsage ForceGarbageCollectionAndMeasure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return GetMemoryUsage();
    }

    /// <summary>
    /// Calculates memory pressure as a percentage of available memory.
    /// </summary>
    /// <param name="memoryUsage">Current memory usage</param>
    /// <returns>Memory pressure percentage (0-100)</returns>
    public static double CalculateMemoryPressure(MemoryUsage memoryUsage)
    {
        if (memoryUsage.TotalAvailableMemoryBytes <= 0)
        {
            return 0.0;
        }

        var pressureRatio = (double)memoryUsage.MemoryLoadBytes / memoryUsage.TotalAvailableMemoryBytes;
        return Math.Min(100.0, pressureRatio * 100.0);
    }
}

/// <summary>
/// Represents memory usage statistics at a point in time.
/// </summary>
public readonly struct MemoryUsage
{
    /// <summary>
    /// Total allocated memory in bytes.
    /// </summary>
    public required long AllocatedBytes { get; init; }

    /// <summary>
    /// Generation 0 garbage collection count.
    /// </summary>
    public required int Gen0Collections { get; init; }

    /// <summary>
    /// Generation 1 garbage collection count.
    /// </summary>
    public required int Gen1Collections { get; init; }

    /// <summary>
    /// Generation 2 garbage collection count.
    /// </summary>
    public required int Gen2Collections { get; init; }

    /// <summary>
    /// Total heap size in bytes.
    /// </summary>
    public required long HeapSizeBytes { get; init; }

    /// <summary>
    /// High memory load threshold in bytes.
    /// </summary>
    public required long HighMemoryLoadThresholdBytes { get; init; }

    /// <summary>
    /// Current memory load in bytes.
    /// </summary>
    public required long MemoryLoadBytes { get; init; }

    /// <summary>
    /// Total available memory in bytes.
    /// </summary>
    public required long TotalAvailableMemoryBytes { get; init; }

    /// <summary>
    /// Timestamp when the measurement was taken.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets the memory pressure as a percentage of available memory.
    /// </summary>
    public double MemoryPressurePercentage => MemoryMonitor.CalculateMemoryPressure(this);

    /// <summary>
    /// Indicates if the system is under high memory pressure (>80%).
    /// </summary>
    public bool IsHighMemoryPressure => MemoryPressurePercentage > 80.0;

    /// <summary>
    /// Total garbage collection count across all generations.
    /// </summary>
    public int TotalGCCollections => Gen0Collections + Gen1Collections + Gen2Collections;
}
