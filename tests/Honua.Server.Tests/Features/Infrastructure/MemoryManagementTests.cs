// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure;

/// <summary>
/// Memory management and leak detection tests for critical performance fixes.
/// Tests cache bounds, import service memory usage, and object pool efficiency.
/// </summary>
[Collection("Database")]
public class MemoryManagementTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;

    public MemoryManagementTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact(DisplayName = "Cache memory bounds are enforced under load")]
    public async Task CacheMemoryBounds_EnforcedUnderLoad()
    {
        // Arrange: Get memory cache and configure size limit
        var memoryCache = _fixture.Services.GetRequiredService<IMemoryCache>();
        var initialMemory = GC.GetTotalMemory(true);

        // Act: Fill cache beyond configured limits
        var cacheOverloadTasks = Enumerable.Range(0, 1000).Select(async i =>
        {
            var key = $"large-cache-item-{i}";
            var largeValue = new byte[1024 * 1024]; // 1MB per item
            Random.Shared.NextBytes(largeValue);

            // Cache with sliding expiration
            memoryCache.Set(key, largeValue, TimeSpan.FromMinutes(30));

            // Also test via HTTP to trigger application-level caching
            await _client.GetAsync($"/rest/services/1/FeatureServer/0?_cache_test={i}");

            return largeValue.Length;
        });

        var cacheSizes = await Task.WhenAll(cacheOverloadTasks);
        var totalCachedData = cacheSizes.Sum();

        // Force garbage collection and measure
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var finalMemory = GC.GetTotalMemory(false);
        var memoryIncrease = finalMemory - initialMemory;

        // Assert: Memory should be bounded despite large cache operations
        var memoryIncreaseMB = memoryIncrease / (1024.0 * 1024.0);
        var totalCachedMB = totalCachedData / (1024.0 * 1024.0);

        memoryIncreaseMB.Should().BeLessThan(totalCachedMB * 0.8,
            "Actual memory increase should be less than total cached data due to eviction policies");

        memoryIncreaseMB.Should().BeLessThan(500,
            "Memory increase should be bounded (< 500MB) regardless of cache pressure");
    }

    [Fact(DisplayName = "Import service memory usage remains stable during processing")]
    public async Task ImportService_MemoryUsageStableDuringProcessing()
    {
        // Arrange: Monitor memory baseline
        var initialMemory = GC.GetTotalMemory(true);
        var memoryReadings = new List<(long Memory, DateTime Timestamp)>();

        // Start background memory monitoring
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var memoryMonitorTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                memoryReadings.Add((GC.GetTotalMemory(false), DateTime.UtcNow));
                await Task.Delay(100, cts.Token);
            }
        }, cts.Token);

        // Act: Perform multiple sequential imports
        var importTasks = Enumerable.Range(0, 5).Select(async i =>
        {
            var testData = GenerateTestGeoJsonData(1000, $"import-batch-{i}");
            var content = new MultipartFormDataContent
            {
                { new StringContent(testData), "file", $"test-data-{i}.geojson" },
                { new StringContent($"memory-test-{i}"), "name" }
            };

            var response = await _client.PostAsync("/admin/import/geojson", content);
            return response.StatusCode;
        });

        var results = await Task.WhenAll(importTasks);
        cts.Cancel();

        // Wait for memory monitor to complete
        try { await memoryMonitorTask; } catch (OperationCanceledException) { /* Expected */ }

        // Assert: Memory should remain stable
        results.Should().AllSatisfy(status =>
            status.Should().BeOneOf(System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.Accepted));

        if (memoryReadings.Count > 10)
        {
            var peakMemory = memoryReadings.Max(r => r.Memory);
            var memoryIncreaseAtPeak = peakMemory - initialMemory;
            var peakIncreaseMB = memoryIncreaseAtPeak / (1024.0 * 1024.0);

            peakIncreaseMB.Should().BeLessThan(1000,
                "Peak memory increase during imports should be bounded (< 1GB)");

            // Check for memory leaks - final memory should be reasonable
            var finalReading = memoryReadings.LastOrDefault();
            if (finalReading.Memory > 0)
            {
                var finalIncrease = finalReading.Memory - initialMemory;
                var finalIncreaseMB = finalIncrease / (1024.0 * 1024.0);

                finalIncreaseMB.Should().BeLessThan(200,
                    "Final memory increase should indicate no major leaks (< 200MB retained)");
            }
        }
    }

    [Fact(DisplayName = "Object pool efficiency reduces allocations")]
    public async Task ObjectPool_EfficiencyReducesAllocations()
    {
        // Arrange: Measure initial allocation rate
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var initialAllocations = GC.GetTotalAllocatedBytes();

        // Act: Perform many operations that should benefit from object pooling
        var poolTestTasks = Enumerable.Range(0, 100).Select(async i =>
        {
            // Operations that should use pooled objects (StringBuilder, arrays, etc.)
            var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=id>{i}&outFields=*&f=json");
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("features");

            return content.Length;
        });

        var contentLengths = await Task.WhenAll(poolTestTasks);

        // Measure final allocation metrics
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var finalAllocations = GC.GetTotalAllocatedBytes();

        // Assert: Object pooling should reduce garbage collection pressure
        var totalContentLength = contentLengths.Sum();
        var allocationIncrease = finalAllocations - initialAllocations;
        var allocationRatio = (double)allocationIncrease / totalContentLength;

        allocationRatio.Should().BeLessThan(5.0,
            "Object pooling should keep allocations reasonable relative to output size");

        var gen0Collections = gen0After - gen0Before;
        var gen1Collections = gen1After - gen1Before;

        gen1Collections.Should().BeLessOrEqualTo(5,
            "Object pooling should minimize gen1 garbage collections");

        gen0Collections.Should().BeLessOrEqualTo(50,
            "Object pooling should keep gen0 collections reasonable for 100 operations");
    }

    [Fact(DisplayName = "Long running operations handle memory pressure gracefully")]
    public async Task LongRunningOperations_HandleMemoryPressureGracefully()
    {
        // Arrange: Create memory pressure condition
        var memoryPressureData = new List<byte[]>();

        try
        {
            // Create moderate memory pressure (but not enough to cause OOM)
            for (int i = 0; i < 100; i++)
            {
                memoryPressureData.Add(new byte[10 * 1024 * 1024]); // 10MB chunks
            }

            // Act: Perform operations under memory pressure
            var stressTestTasks = Enumerable.Range(0, 20).Select(async i =>
            {
                var stopwatch = Stopwatch.StartNew();

                // Simulate various types of operations
                var tasks = new[]
                {
                    _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=500&_stress={i}"),
                    _client.GetAsync($"/ogc/features/v1/collections/test/items?limit=100&_stress={i}"),
                    _client.GetAsync($"/rest/services/1/FeatureServer/layers?_stress={i}")
                };

                var responses = await Task.WhenAll(tasks);
                stopwatch.Stop();

                return new
                {
                    TaskId = i,
                    Duration = stopwatch.Elapsed,
                    Responses = responses,
                    Success = responses.All(r => r.StatusCode == System.Net.HttpStatusCode.OK)
                };
            });

            var results = await Task.WhenAll(stressTestTasks);

            // Assert: Operations should complete successfully even under memory pressure
            results.Should().AllSatisfy(result =>
            {
                result.Success.Should().BeTrue($"Task {result.TaskId} should complete successfully under memory pressure");

                result.Duration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(30),
                    $"Task {result.TaskId} should complete within reasonable time even under pressure");
            });

            // Verify no excessive degradation
            var averageDuration = results.Average(r => r.Duration.TotalMilliseconds);
            averageDuration.Should().BeLessThan(10000,
                "Average operation time should remain reasonable under memory pressure");

        }
        finally
        {
            // Cleanup: Release memory pressure
            memoryPressureData.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [Fact(DisplayName = "Memory leaks detected in concurrent scenarios")]
    public async Task MemoryLeaks_DetectedInConcurrentScenarios()
    {
        // Arrange: Baseline memory measurement
        var baselineMemory = await MeasureStableMemory();

        // Act: Run concurrent operations multiple times
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var concurrentTasks = Enumerable.Range(0, 30).Select(async i =>
            {
                // Mix of operations that could potentially leak
                await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=id>{i}&f=json");
                await _client.GetAsync($"/admin/health");

                // Simulate complex object creation/disposal
                using var formContent = new MultipartFormDataContent();
                formContent.Add(new StringContent($"test-{i}"), "test");
                await _client.PostAsync("/admin/test-endpoint", formContent);

                return i;
            });

            await Task.WhenAll(concurrentTasks);

            // Force garbage collection after each cycle
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Task.Delay(100); // Allow cleanup
        }

        // Measure memory after operations
        var finalMemory = await MeasureStableMemory();
        var memoryGrowth = finalMemory - baselineMemory;
        var memoryGrowthMB = memoryGrowth / (1024.0 * 1024.0);

        // Assert: Memory growth should be minimal
        memoryGrowthMB.Should().BeLessThan(50,
            "Memory growth after concurrent operations should indicate no significant leaks (< 50MB)");
    }

    [Fact(DisplayName = "Cache eviction policies work under memory pressure")]
    public async Task CacheEviction_WorksUnderMemoryPressure()
    {
        // Arrange: Get cache service and fill it significantly
        var memoryCache = _fixture.Services.GetRequiredService<IMemoryCache>();
        var cacheOptions = _fixture.Services.GetRequiredService<IOptions<MemoryCacheOptions>>().Value;

        // Fill cache to trigger eviction
        var cacheItemCount = 0;
        var itemsToCache = 500;

        for (int i = 0; i < itemsToCache; i++)
        {
            var key = $"eviction-test-{i}";
            var value = new byte[1024 * 512]; // 512KB per item
            Random.Shared.NextBytes(value);

            memoryCache.Set(key, value, new MemoryCacheEntryOptions
            {
                Priority = CacheItemPriority.Normal,
                SlidingExpiration = TimeSpan.FromMinutes(10)
            });

            cacheItemCount++;

            // Check if item is still accessible
            if (memoryCache.TryGetValue(key, out _))
            {
                // Item is still in cache
            }
        }

        // Act: Create additional memory pressure
        var pressureItems = new List<object>();
        for (int i = 0; i < 100; i++)
        {
            pressureItems.Add(new byte[1024 * 1024]); // 1MB items
        }

        // Force cache evaluation under pressure
        GC.Collect();
        await Task.Delay(100);

        // Test cache access patterns
        var accessibleItems = 0;
        for (int i = 0; i < itemsToCache; i++)
        {
            if (memoryCache.TryGetValue($"eviction-test-{i}", out _))
            {
                accessibleItems++;
            }
        }

        // Assert: Cache should have evicted items under pressure
        accessibleItems.Should().BeLessThan(itemsToCache,
            "Cache eviction should have removed some items under memory pressure");

        var evictedItems = itemsToCache - accessibleItems;
        evictedItems.Should().BeGreaterThan(0,
            "Some cache items should have been evicted");

        // Cleanup
        pressureItems.Clear();
    }

    #region Helper Methods

    private async Task<long> MeasureStableMemory()
    {
        // Force garbage collection and wait for stabilization
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(50);
        }

        return GC.GetTotalMemory(false);
    }

    private static string GenerateTestGeoJsonData(int featureCount, string prefix)
    {
        var features = Enumerable.Range(0, featureCount).Select(i => new
        {
            type = "Feature",
            properties = new
            {
                id = $"{prefix}_{i}",
                name = $"Feature {prefix} {i}",
                category = Random.Shared.Next(1, 10),
                description = new string('x', 100) // Add some data size
            },
            geometry = new
            {
                type = "Point",
                coordinates = new[]
                {
                    -180 + (360.0 * Random.Shared.NextDouble()),
                    -90 + (180.0 * Random.Shared.NextDouble())
                }
            }
        });

        var geoJson = new
        {
            type = "FeatureCollection",
            features = features.Select(f => new
            {
                type = "Feature",
                properties = f.properties,
                geometry = f.geometry
            })
        };

        return System.Text.Json.JsonSerializer.Serialize(geoJson);
    }

    #endregion
}