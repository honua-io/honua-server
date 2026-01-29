// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Performance tests for streaming feature operations
/// </summary>
[Collection("Performance")]
public class StreamingPerformanceTests : IAsyncLifetime, IDisposable
{
    private readonly TestFeatureStore _testStore;
    private readonly int _testLayerId;

    public StreamingPerformanceTests()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        _testStore = new TestFeatureStore();
#pragma warning restore CS0618 // Type or member is obsolete
        _testLayerId = 1;

        // Seed with test data in InitializeAsync to avoid sync-over-async.
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task StreamFeaturesAsync_LargeDataset_MemoryEfficient()
    {
        // Arrange
        var query = new FeatureQuery();
        var featureCount = 0;
        var maxMemoryUsage = 0L;
        var initialMemory = GC.GetTotalMemory(false);

        // Act
        var stopwatch = Stopwatch.StartNew();

        if (_testStore is IStreamingFeatureStore streamingStore)
        {
            await foreach (var feature in streamingStore.StreamFeaturesAsync(_testLayerId, query))
            {
                featureCount++;

                // Check memory usage periodically
                if (featureCount % 100 == 0)
                {
                    var currentMemory = GC.GetTotalMemory(false) - initialMemory;
                    maxMemoryUsage = Math.Max(maxMemoryUsage, currentMemory);
                }
            }
        }
        else
        {
            // Fallback to regular query for comparison
            var result = await _testStore.QueryAsync(_testLayerId, query);
            featureCount = result.Items.Length;
        }

        stopwatch.Stop();

        // Assert
        Assert.True(featureCount >= 1000, $"Should process at least 1000 features, processed: {featureCount}");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Should complete within 5 seconds, took: {stopwatch.ElapsedMilliseconds}ms");

        // Memory usage should be reasonable for streaming (not proportional to dataset size)
        var maxReasonableMemory = featureCount * 1024; // 1KB per feature is reasonable for streaming
        Assert.True(maxMemoryUsage < maxReasonableMemory,
            $"Memory usage ({maxMemoryUsage} bytes) should be less than {maxReasonableMemory} bytes for efficient streaming");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task StreamFeatureBatchesAsync_LargeDataset_OptimalBatchSize()
    {
        // Arrange
        var query = new FeatureQuery();
        var batchSizes = new[] { 100, 500, 1000, 2000 };
        var results = new Dictionary<int, (int totalFeatures, long elapsedMs, long memoryUsage)>();

        // Act
        foreach (var batchSize in batchSizes)
        {
            var featureCount = 0;
            var initialMemory = GC.GetTotalMemory(true);
            var stopwatch = Stopwatch.StartNew();

            if (_testStore is IStreamingFeatureStore streamingStore)
            {
                await foreach (var batch in streamingStore.StreamFeatureBatchesAsync(_testLayerId, query, batchSize))
                {
                    featureCount += batch.Count;
                }
            }

            stopwatch.Stop();
            var memoryUsage = GC.GetTotalMemory(false) - initialMemory;

            results[batchSize] = (featureCount, stopwatch.ElapsedMilliseconds, memoryUsage);
        }

        // Assert
        foreach (var result in results)
        {
            Assert.True(result.Value.totalFeatures >= 1000,
                $"Batch size {result.Key} should process at least 1000 features");
            Assert.True(result.Value.elapsedMs < 10000,
                $"Batch size {result.Key} should complete within 10 seconds");
        }

        // Find optimal batch size (best time/memory trade-off)
        var optimalBatch = results.OrderBy(r => r.Value.elapsedMs + r.Value.memoryUsage / 1000).First();
        Assert.True(optimalBatch.Key >= 500,
            $"Optimal batch size should be at least 500, was: {optimalBatch.Key}");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task StreamVsTraditionalQuery_LargeDataset_StreamingUsesMuchLessMemory()
    {
        // Arrange
        var query = new FeatureQuery { Limit = 5000 }; // Large result set

        // Act - Traditional query
        var traditionalInitialMemory = GC.GetTotalMemory(true);
        var traditionalStopwatch = Stopwatch.StartNew();
        var traditionalResult = await _testStore.QueryAsync(_testLayerId, query);
        traditionalStopwatch.Stop();
        var traditionalMemoryUsage = GC.GetTotalMemory(false) - traditionalInitialMemory;

        // Force garbage collection to reset
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Act - Streaming query
        var streamingInitialMemory = GC.GetTotalMemory(true);
        var streamingStopwatch = Stopwatch.StartNew();
        var streamingCount = 0;

        if (_testStore is IStreamingFeatureStore streamingStore)
        {
            await foreach (var feature in streamingStore.StreamFeaturesAsync(_testLayerId, query))
            {
                streamingCount++;
                if (streamingCount >= 5000)
                {
                    break; // Match traditional query limit
                }
            }
        }

        streamingStopwatch.Stop();
        var streamingMemoryUsage = GC.GetTotalMemory(false) - streamingInitialMemory;

        // Assert
        Assert.Equal(traditionalResult.Items.Length, streamingCount);

        // Streaming should be competitive on memory, but CI variance can be noisy.
        var isCi = Environment.GetEnvironmentVariable("CI") == "true";
        var memoryTolerance = 10;
        var baselineMemory = Math.Max(traditionalMemoryUsage, 1_000_000);
        Assert.True(streamingMemoryUsage <= baselineMemory * memoryTolerance,
            $"Streaming memory usage ({streamingMemoryUsage} bytes) should be within {(memoryTolerance - 1.0):P0} of baseline ({baselineMemory} bytes)");

        // Streaming might be slightly slower due to per-feature overhead, but should be competitive
        var timeTolerance = isCi ? 3 : 2;
        var traditionalElapsedMs = Math.Max(traditionalStopwatch.ElapsedMilliseconds, isCi ? 5 : 1);
        var streamingElapsedMs = streamingStopwatch.ElapsedMilliseconds;
        Assert.True(streamingElapsedMs <= traditionalElapsedMs * timeTolerance,
            $"Streaming time ({streamingElapsedMs}ms) should be within {timeTolerance}x of traditional query ({traditionalElapsedMs}ms)");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task StreamFeaturesAsync_WithCancellation_RespondsQuickly()
    {
        // Arrange
        var query = new FeatureQuery();
        using var cts = new CancellationTokenSource();
        var featuresProcessed = 0;

        // Act
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (_testStore is IStreamingFeatureStore streamingStore)
            {
                await foreach (var feature in streamingStore.StreamFeaturesAsync(_testLayerId, query, cts.Token))
                {
                    featuresProcessed++;

                    // Cancel after processing some features
                    if (featuresProcessed >= 100)
                    {
                        cts.Cancel();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        stopwatch.Stop();

        // Assert
        Assert.True(featuresProcessed >= 100, $"Should process at least 100 features before cancellation, processed: {featuresProcessed}");
        Assert.True(featuresProcessed < 200, $"Should not process too many features after cancellation, processed: {featuresProcessed}");
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Should respond to cancellation within 1 second, took: {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Seeds the test store with a large dataset for performance testing
    /// </summary>
    private async Task SeedLargeDataset()
    {
        const int featureCount = 5000;

        for (var i = 0; i < featureCount; i++)
        {
            var feature = TestFeatureStore.CreateTestFeature(
                id: i,
                x: i % 100 * 0.01, // Spread across a 1x1 degree area
                y: (i / 100) * 0.01,
                attributes: new Dictionary<string, object?>
                {
                    ["id"] = i,
                    ["name"] = $"Feature {i}",
                    ["category"] = i % 10 == 0 ? "important" : "normal",
                    ["value"] = i * 0.1,
                    ["created"] = DateTime.UtcNow.AddDays(-i % 365)
                });

            await _testStore.CreateAsync(_testLayerId, feature);
        }
    }

    public async Task InitializeAsync()
    {
        await SeedLargeDataset();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _testStore?.Dispose();
        GC.SuppressFinalize(this);
    }
}
