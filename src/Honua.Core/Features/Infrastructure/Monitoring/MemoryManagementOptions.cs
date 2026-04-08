// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Configuration options for memory management and cache behavior
/// </summary>
public sealed class MemoryManagementOptions
{
    /// <summary>
    /// Maximum number of entries in the layer SRID cache before cleanup is triggered
    /// Default: 5000
    /// </summary>
    public int MaxLayerSridCacheEntries { get; set; } = 5000;

    /// <summary>
    /// Number of entries that triggers proactive cache cleanup (before hitting max)
    /// Default: 4000
    /// </summary>
    public int CacheCleanupThreshold { get; set; } = 4000;

    /// <summary>
    /// How often to check for cache cleanup (in milliseconds)
    /// Default: 30000 (30 seconds)
    /// </summary>
    public int CacheCleanupIntervalMs { get; set; } = 30000;

    /// <summary>
    /// Maximum capacity for pooled StringBuilders before resetting
    /// Default: 4096 (4KB)
    /// </summary>
    public int MaxStringBuilderCapacity { get; set; } = 4096;

    /// <summary>
    /// Maximum entries for pooled dictionaries before triggering trimming
    /// Default: 50
    /// </summary>
    public int MaxDictionaryCapacity { get; set; } = 50;

    /// <summary>
    /// Maximum number of objects retained in object pools
    /// Default: ProcessorCount * 8 (capped at 32)
    /// </summary>
    public int MaxPoolRetainedObjects { get; set; } = Math.Min(Environment.ProcessorCount * 8, 32);

    /// <summary>
    /// Batch size threshold for using bulk operations instead of individual inserts
    /// Default: 500
    /// </summary>
    public int BulkOperationThreshold { get; set; } = 500;

    /// <summary>
    /// Memory threshold in bytes that triggers high memory pressure alerts
    /// Default: 1GB
    /// </summary>
    public long HighMemoryThresholdBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Memory threshold in bytes that triggers critical memory pressure and auto-GC
    /// Default: 2GB
    /// </summary>
    public long CriticalMemoryThresholdBytes { get; set; } = 2048L * 1024 * 1024;

    /// <summary>
    /// Minimum interval between forced garbage collections (in minutes)
    /// Default: 1 minute
    /// </summary>
    public int MinGcIntervalMinutes { get; set; } = 1;

    /// <summary>
    /// Whether to enable automatic memory pressure relief through forced GC
    /// Default: true
    /// </summary>
    public bool EnableAutoMemoryRelief { get; set; } = true;

    /// <summary>
    /// Whether to enable cache stampede prevention using locks
    /// Default: true
    /// </summary>
    public bool EnableCacheStampedePrevention { get; set; } = true;

    /// <summary>
    /// Validates the configuration and throws if invalid
    /// </summary>
    public void Validate()
    {
        if (MaxLayerSridCacheEntries <= 0)
            throw new ArgumentException("MaxLayerSridCacheEntries must be positive", nameof(MaxLayerSridCacheEntries));

        if (CacheCleanupThreshold >= MaxLayerSridCacheEntries)
            throw new ArgumentException("CacheCleanupThreshold must be less than MaxLayerSridCacheEntries", nameof(CacheCleanupThreshold));

        if (CacheCleanupIntervalMs <= 0)
            throw new ArgumentException("CacheCleanupIntervalMs must be positive", nameof(CacheCleanupIntervalMs));

        if (MaxStringBuilderCapacity <= 0)
            throw new ArgumentException("MaxStringBuilderCapacity must be positive", nameof(MaxStringBuilderCapacity));

        if (MaxDictionaryCapacity <= 0)
            throw new ArgumentException("MaxDictionaryCapacity must be positive", nameof(MaxDictionaryCapacity));

        if (MaxPoolRetainedObjects <= 0)
            throw new ArgumentException("MaxPoolRetainedObjects must be positive", nameof(MaxPoolRetainedObjects));

        if (BulkOperationThreshold <= 0)
            throw new ArgumentException("BulkOperationThreshold must be positive", nameof(BulkOperationThreshold));

        if (HighMemoryThresholdBytes <= 0)
            throw new ArgumentException("HighMemoryThresholdBytes must be positive", nameof(HighMemoryThresholdBytes));

        if (CriticalMemoryThresholdBytes <= HighMemoryThresholdBytes)
            throw new ArgumentException("CriticalMemoryThresholdBytes must be greater than HighMemoryThresholdBytes", nameof(CriticalMemoryThresholdBytes));

        if (MinGcIntervalMinutes <= 0)
            throw new ArgumentException("MinGcIntervalMinutes must be positive", nameof(MinGcIntervalMinutes));
    }
}