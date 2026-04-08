// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Interface for monitoring memory usage and GC pressure
/// </summary>
public interface IMemoryMonitor
{
    /// <summary>
    /// Gets current memory usage statistics
    /// </summary>
    MemoryUsageSnapshot GetMemoryUsage();

    /// <summary>
    /// Records a memory allocation for tracking
    /// </summary>
    void RecordAllocation(string source, long bytes);

    /// <summary>
    /// Records a memory deallocation
    /// </summary>
    void RecordDeallocation(string source, long bytes);

    /// <summary>
    /// Checks if memory usage is approaching dangerous levels
    /// </summary>
    bool IsMemoryPressureHigh();

    /// <summary>
    /// Forces a garbage collection if memory pressure is high
    /// </summary>
    Task<bool> TryRelieveMemoryPressureAsync();
}

/// <summary>
/// Memory usage snapshot for monitoring
/// </summary>
public sealed class MemoryUsageSnapshot
{
    /// <summary>
    /// Total memory allocated by the process in bytes
    /// </summary>
    public long TotalMemoryBytes { get; init; }

    /// <summary>
    /// Working set size in bytes
    /// </summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>
    /// GC heap size for each generation
    /// </summary>
    public long[] GenerationSizes { get; init; } = Array.Empty<long>();

    /// <summary>
    /// Large object heap size in bytes
    /// </summary>
    public long LargeObjectHeapBytes { get; init; }

    /// <summary>
    /// Number of GC collections for each generation
    /// </summary>
    public int[] CollectionCounts { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Whether the system is under memory pressure
    /// </summary>
    public bool IsUnderMemoryPressure { get; init; }

    /// <summary>
    /// Timestamp when the snapshot was taken
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}