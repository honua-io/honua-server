// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

public static class MemoryMonitor
{
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

    public static long GetAllocatedMemory(bool forceFullCollection = false)
    {
        return GC.GetTotalMemory(forceFullCollection);
    }

    public static MemoryUsage ForceGarbageCollectionAndMeasure()
    {
        _ = GC.GetTotalMemory(forceFullCollection: true);
        GC.WaitForPendingFinalizers();
        _ = GC.GetTotalMemory(forceFullCollection: true);

        return GetMemoryUsage();
    }

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

public readonly struct MemoryUsage
{
    public required long AllocatedBytes { get; init; }

    public required int Gen0Collections { get; init; }

    public required int Gen1Collections { get; init; }

    public required int Gen2Collections { get; init; }

    public required long HeapSizeBytes { get; init; }

    public required long HighMemoryLoadThresholdBytes { get; init; }

    public required long MemoryLoadBytes { get; init; }

    public required long TotalAvailableMemoryBytes { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public double MemoryPressurePercentage => MemoryMonitor.CalculateMemoryPressure(this);

    public bool IsHighMemoryPressure => MemoryPressurePercentage > 80.0;

    public int TotalGCCollections => Gen0Collections + Gen1Collections + Gen2Collections;
}
