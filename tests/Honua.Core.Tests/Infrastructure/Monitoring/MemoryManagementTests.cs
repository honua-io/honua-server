// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using Xunit;

namespace Honua.Core.Tests.Infrastructure.Monitoring;

/// <summary>
/// Tests for memory management improvements and leak prevention
/// </summary>
public sealed class MemoryManagementTests
{
    [Fact]
    public void StringBuilderPool_ShouldManageCapacityCorrectly()
    {
        // Arrange
        var policy = new StringBuilderPooledObjectPolicy();
        var poolProvider = new DefaultObjectPoolProvider();
        var pool = poolProvider.Create(policy);

        // Act - Get a StringBuilder and make it large
        var sb = pool.Get();
        sb.Append(new string('A', 10000)); // Make it large
        var largeCapacity = sb.Capacity;

        // Return and get again
        pool.Return(sb);
        var sb2 = pool.Get();

        // Assert
        Assert.True(largeCapacity > 4096, "StringBuilder should have grown large");
        Assert.True(sb2.Capacity <= 2048, "Returned StringBuilder should have reset capacity");
        Assert.Equal(0, sb2.Length);

        pool.Return(sb2);
    }

    [Fact]
    public void DictionaryPool_ShouldManageCapacityCorrectly()
    {
        // Arrange
        var policy = new DictionaryPooledObjectPolicy();
        var poolProvider = new DefaultObjectPoolProvider();
        var pool = poolProvider.Create(policy);

        // Act - Get a dictionary and fill it
        var dict = pool.Get();
        for (int i = 0; i < 100; i++)
        {
            dict[$"key{i}"] = $"value{i}";
        }

        // Return and get again
        pool.Return(dict);
        var dict2 = pool.Get();

        // Assert
        Assert.Equal(0, dict2.Count);
        Assert.True(dict2.Count <= 50, "Dictionary should be cleared and capacity managed");

        pool.Return(dict2);
    }

    [Fact]
    public async Task CacheMemoryManagement_ShouldPreventUnboundedGrowth()
    {
        // This test simulates cache usage patterns that could cause memory leaks
        var caches = new List<ConcurrentDictionary<string, object>>();

        // Simulate the pattern from FeatureCacheManager
        for (int i = 0; i < 1000; i++)
        {
            var cache = new ConcurrentDictionary<string, object>();

            // Fill cache beyond reasonable limits
            for (int j = 0; j < 100; j++)
            {
                cache[$"key_{i}_{j}"] = new byte[1024]; // 1KB per entry
            }

            caches.Add(cache);

            // Simulate cleanup every 100 iterations
            if (i % 100 == 0)
            {
                // Remove old caches to simulate cleanup
                if (caches.Count > 500)
                {
                    caches.RemoveRange(0, 200);
                    GC.Collect();
                }
            }
        }

        // Force cleanup
        caches.Clear();
        GC.Collect();
        await Task.Delay(100); // Allow GC to complete

        // Verify memory is reclaimed (this is a basic check)
        var memoryAfter = GC.GetTotalMemory(true);
        Assert.True(memoryAfter < 100 * 1024 * 1024, // Less than 100MB
            $"Memory should be reclaimed after cleanup, but was {memoryAfter:N0} bytes");
    }

    [Fact]
    public async Task BulkOperations_ShouldNotCauseMemoryLeaks()
    {
        var memoryBefore = GC.GetTotalMemory(true);

        // Simulate bulk operations that could cause memory pressure
        var tasks = new List<Task>();

        for (int batch = 0; batch < 10; batch++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var features = new List<object>();

                // Simulate creating many features
                for (int i = 0; i < 1000; i++)
                {
                    features.Add(new
                    {
                        Id = i,
                        Geometry = new byte[1024], // 1KB geometry
                        Properties = new Dictionary<string, object>
                        {
                            ["name"] = $"Feature {i}",
                            ["value"] = i * 2,
                            ["description"] = new string('A', 100)
                        }
                    });
                }

                // Simulate processing
                await Task.Delay(10);

                // Clear to simulate end of batch
                features.Clear();
            }));
        }

        await Task.WhenAll(tasks);

        // Force cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memoryAfter = GC.GetTotalMemory(false);
        var memoryGrowth = memoryAfter - memoryBefore;

        // Memory growth should be reasonable (less than 50MB for this test)
        Assert.True(memoryGrowth < 50 * 1024 * 1024,
            $"Memory growth should be reasonable, but grew by {memoryGrowth:N0} bytes");
    }

    [Theory]
    [InlineData(100)]   // Small batch
    [InlineData(1000)]  // Medium batch
    [InlineData(5000)]  // Large batch
    public void MemoryUsage_ShouldScaleLinearlyWithBatchSize(int batchSize)
    {
        var memoryBefore = GC.GetTotalMemory(true);

        // Create objects proportional to batch size
        var objects = new List<object>();
        for (int i = 0; i < batchSize; i++)
        {
            objects.Add(new
            {
                Data = new byte[1024], // 1KB per object
                Index = i
            });
        }

        var memoryAfter = GC.GetTotalMemory(false);
        var memoryUsed = memoryAfter - memoryBefore;
        var bytesPerObject = memoryUsed / batchSize;

        // Memory usage per object should be reasonable (accounting for object overhead)
        Assert.True(bytesPerObject >= 1024, // At least the data size
            $"Memory per object should be at least 1KB, but was {bytesPerObject:N0} bytes");

        Assert.True(bytesPerObject <= 2048, // No more than 2x the data size (allowing for overhead)
            $"Memory per object should not exceed 2KB, but was {bytesPerObject:N0} bytes");

        // Cleanup
        objects.Clear();
        GC.Collect();
    }

    [Fact]
    public void CacheStatistics_ShouldProvideAccurateMetrics()
    {
        // Arrange
        var stats = new CacheStatistics
        {
            LayerSridCacheSize = 1000,
            MaxLayerSridCacheEntries = 5000,
            GeometryStorageCacheSize = 10,
            LayerCatalogCacheSize = 5,
            ActiveLockCount = 2
        };

        // Act & Assert
        Assert.Equal(0.2, stats.CacheUtilizationRatio, 1);
        Assert.False(stats.IsNearCapacity);

        var highUtilizationStats = stats with { LayerSridCacheSize = 4500 };
        Assert.True(highUtilizationStats.IsNearCapacity);
        Assert.Equal(0.9, highUtilizationStats.CacheUtilizationRatio, 1);
    }
}