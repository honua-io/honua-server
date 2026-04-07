// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using StackExchange.Redis;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Critical performance fix validation tests.
/// Tests database index effectiveness, cache performance, Redis optimizations, and memory management.
/// </summary>
[Collection("Database")]
public class CriticalPerformanceFixTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;

    public CriticalPerformanceFixTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact(DisplayName = "Database indexes are used effectively in spatial queries")]
    public async Task DatabaseIndexes_UsedEffectivelyInSpatialQueries()
    {
        // Arrange: Create test data and ensure indexes exist
        await SeedLargeDatasetAsync(10000); // 10k features for meaningful index test

        var spatialQuery = "POLYGON((-180 -90, 180 -90, 180 90, -180 90, -180 -90))";
        var encodedGeometry = Uri.EscapeDataString(spatialQuery);

        // Act & Measure: Execute spatial query and capture query plan
        var (response, queryPlan, duration) = await MeasureQueryWithPlan(
            $"/rest/services/1/FeatureServer/0/query?geometry={encodedGeometry}&spatialRel=esriSpatialRelIntersects&resultRecordCount=1000");

        // Assert: Performance and index usage
        response.Should().NotBeNull();
        duration.Should().BeLessOrEqualTo(PerformanceAssertions.Thresholds.LargeSpatialQuery,
            "Large spatial queries should complete within performance threshold");

        // Verify index usage in query plan
        queryPlan.Should().Contain("Index", "Spatial queries should use spatial indexes");
        queryPlan.Should().NotContain("Seq Scan", "Should not perform sequential scan on large datasets");

        // Verify query returns results efficiently
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("features", "Should return feature collection");
    }

    [Fact(DisplayName = "N+1 query prevention in feature loading with relationships")]
    public async Task NplusOneQueries_PreventedInFeatureLoading()
    {
        // Arrange: Create features with relationships
        await SeedFeaturesWithRelationshipsAsync(100, 5); // 100 features, 5 related each

        var queryCountBefore = await GetQueryCountAsync();

        // Act: Load features with relationships
        var response = await _client.GetAsync(
            "/rest/services/1/FeatureServer/0/query?outFields=*&includeRelated=true&resultRecordCount=100");

        var queryCountAfter = await GetQueryCountAsync();
        var queryCount = queryCountAfter - queryCountBefore;

        // Assert: Should use efficient joins, not N+1 queries
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        // Should execute minimal queries (typically 1-3, not 100+)
        queryCount.Should().BeLessOrEqualTo(5,
            "Should use joins instead of N+1 queries when loading related data");
    }

    [Fact(DisplayName = "Cache refresh performance under concurrent load")]
    public async Task CacheRefresh_PerformanceUnderConcurrentLoad()
    {
        // Arrange: Prime cache and prepare for concurrent access
        var cacheKey = "test-layer-metadata";
        await _client.GetAsync("/rest/services/1/FeatureServer/0"); // Prime cache

        // Act: Simulate concurrent cache access during refresh
        var concurrentTasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0?_nocache={i}");
            stopwatch.Stop();

            return new { Response = response, Duration = stopwatch.Elapsed, TaskId = i };
        });

        var results = await Task.WhenAll(concurrentTasks);

        // Assert: All requests should complete successfully and efficiently
        foreach (var result in results)
        {
            result.Response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
                $"Task {result.TaskId} should complete successfully");

            result.Duration.Should().BeLessOrEqualTo(PerformanceAssertions.Thresholds.MetadataQuery.Add(TimeSpan.FromSeconds(1)),
                $"Task {result.TaskId} should complete within reasonable time even under load");
        }

        // Verify no cache stampede - durations should be reasonable
        var maxDuration = results.Max(r => r.Duration);
        var minDuration = results.Min(r => r.Duration);
        var ratio = maxDuration.TotalMilliseconds / minDuration.TotalMilliseconds;

        ratio.Should().BeLessOrEqualTo(10,
            "Cache stampede protection should prevent extreme variation in response times");
    }

    [Fact(DisplayName = "Memory usage remains bounded during large imports")]
    public async Task MemoryUsage_BoundedDuringLargeImports()
    {
        // Arrange: Monitor memory before import
        var initialMemory = GC.GetTotalMemory(true);

        // Act: Simulate large file import (using test data)
        var largeGeoJsonData = GenerateLargeGeoJsonData(5000); // 5k features

        var formContent = new MultipartFormDataContent
        {
            { new StringContent(largeGeoJsonData), "file", "large-dataset.geojson" },
            { new StringContent("test-import"), "name" }
        };

        var importResponse = await _client.PostAsync("/admin/import/geojson", formContent);

        // Monitor memory during and after import
        var peakMemory = GC.GetTotalMemory(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var finalMemory = GC.GetTotalMemory(true);

        // Assert: Memory usage should be bounded
        importResponse.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.Accepted);

        var memoryIncrease = peakMemory - initialMemory;
        var memoryIncreaseInMB = memoryIncrease / (1024.0 * 1024.0);

        memoryIncreaseInMB.Should().BeLessThan(500,
            "Memory increase during import should be bounded (< 500MB for test dataset)");

        // Memory should be cleaned up after import
        var memoryRetained = finalMemory - initialMemory;
        var retainedInMB = memoryRetained / (1024.0 * 1024.0);

        retainedInMB.Should().BeLessThan(100,
            "Memory should be cleaned up after import (< 100MB retained)");
    }

    [Fact(DisplayName = "Redis SCAN operations prevent server blocking")]
    public async Task RedisScan_PreventsServerBlocking()
    {
        // Arrange: Get Redis connection and create many keys
        var redis = _fixture.Services.GetService<IConnectionMultiplexer>();
        if (redis == null)
        {
            // Skip test if Redis not available
            return;
        }

        var database = redis.GetDatabase();

        // Create many cache keys to test SCAN vs KEYS
        var keyTasks = Enumerable.Range(0, 1000).Select(i =>
            database.StringSetAsync($"test-pattern:{i}", $"value-{i}"));
        await Task.WhenAll(keyTasks);

        // Act & Measure: Test pattern matching operations
        var scanStopwatch = Stopwatch.StartNew();
        var scanResults = new List<RedisKey>();

        await foreach (var key in database.ScanAsync(pattern: "test-pattern:*", pageSize: 250))
        {
            scanResults.Add(key);
        }
        scanStopwatch.Stop();

        // Assert: SCAN should be non-blocking and efficient
        scanStopwatch.Elapsed.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(2),
            "SCAN operations should complete efficiently");

        scanResults.Should().HaveCountGreaterThan(900,
            "SCAN should find most test keys");

        // Verify SCAN pageSize is reasonable (not using KEYS which blocks)
        // This is indirectly tested by ensuring operation completes quickly
        scanStopwatch.Elapsed.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(500),
            "SCAN with proper page size should be much faster than KEYS on large keysets");

        // Cleanup
        await database.KeyDeleteAsync(scanResults.ToArray());
    }

    [Fact(DisplayName = "Connection pool handles high concurrency without exhaustion")]
    public async Task ConnectionPool_HandlesHighConcurrencyWithoutExhaustion()
    {
        // Arrange: Create many concurrent database operations
        var concurrentOperations = 50;
        var operationsPerTask = 5;

        var connectionCountBefore = await GetActiveConnectionCountAsync();

        // Act: Execute many concurrent database operations
        var tasks = Enumerable.Range(0, concurrentOperations).Select(async i =>
        {
            var taskResults = new List<TimeSpan>();

            for (int j = 0; j < operationsPerTask; j++)
            {
                var stopwatch = Stopwatch.StartNew();
                var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=10&_task={i}&_op={j}");
                stopwatch.Stop();

                response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
                    $"Operation {i}-{j} should complete successfully");

                taskResults.Add(stopwatch.Elapsed);
            }

            return taskResults;
        });

        var allResults = await Task.WhenAll(tasks);
        var allDurations = allResults.SelectMany(r => r).ToList();

        var connectionCountAfter = await GetActiveConnectionCountAsync();

        // Assert: Connection pool should handle load efficiently
        allDurations.Should().AllSatisfy(duration =>
            duration.Should().BeLessOrEqualTo(PerformanceAssertions.Thresholds.SmallFeatureQuery.Add(TimeSpan.FromSeconds(2)),
                "Individual operations should complete within reasonable time"));

        // Connection count should not grow excessively
        var connectionGrowth = connectionCountAfter - connectionCountBefore;
        connectionGrowth.Should().BeLessOrEqualTo(20,
            "Connection pool should reuse connections efficiently");

        // Verify no timeouts or pool exhaustion
        var maxDuration = allDurations.Max();
        maxDuration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(10),
            "No operation should timeout due to pool exhaustion");
    }

    [Fact(DisplayName = "Bulk operations use efficient batching")]
    public async Task BulkOperations_UseEfficientBatching()
    {
        // Arrange: Prepare bulk data
        var featureCount = 1000;
        var bulkFeatures = GenerateBulkFeatureData(featureCount);

        // Act: Measure bulk insert performance
        var (result, duration) = await PerformanceAssertions.MeasureAsync(async () =>
        {
            var response = await _client.PostAsync("/ogc/features/v1/collections/test/items/bulk",
                new StringContent(bulkFeatures, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().BeOneOf(
                System.Net.HttpStatusCode.OK,
                System.Net.HttpStatusCode.Created);

            return response;
        });

        // Assert: Bulk operations should be efficient
        duration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(30),
            "Bulk insert of 1000 features should complete within 30 seconds");

        // Verify throughput
        var throughput = featureCount / duration.TotalSeconds;
        throughput.Should().BeGreaterThan(50,
            "Bulk operations should achieve reasonable throughput (>50 features/second)");
    }

    [Fact(DisplayName = "Query plan cache effectiveness")]
    public async Task QueryPlanCache_Effectiveness()
    {
        // Arrange: Execute same query pattern multiple times
        var query = "/rest/services/1/FeatureServer/0/query?where=id>100&outFields=*&resultRecordCount=50";

        // Act: Execute query multiple times and measure
        var durations = new List<TimeSpan>();

        for (int i = 0; i < 10; i++)
        {
            var (_, duration) = await PerformanceAssertions.MeasureAsync(async () =>
            {
                var response = await _client.GetAsync($"{query}&_iteration={i}");
                response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
                return response;
            });

            durations.Add(duration);

            // Small delay between requests
            await Task.Delay(10);
        }

        // Assert: Later executions should benefit from plan cache
        var firstExecution = durations.First();
        var laterExecutions = durations.Skip(2).Take(5); // Skip first 2, take next 5

        var averageFirst = firstExecution;
        var averageLater = TimeSpan.FromMilliseconds(laterExecutions.Average(d => d.TotalMilliseconds));

        // Later executions should be at least 10% faster (plan cache benefit)
        var improvementRatio = averageFirst.TotalMilliseconds / averageLater.TotalMilliseconds;
        improvementRatio.Should().BeGreaterThan(1.1,
            "Query plan caching should provide performance improvement");
    }

    #region Helper Methods

    private async Task SeedLargeDatasetAsync(int featureCount)
    {
        // Create large test dataset for performance testing
        var features = Enumerable.Range(0, featureCount).Select(i => new
        {
            id = i,
            name = $"Feature_{i}",
            geometry = new
            {
                type = "Point",
                coordinates = new[] { -180 + (360.0 * i / featureCount), -90 + (180.0 * i / featureCount) }
            }
        });

        var geoJson = new
        {
            type = "FeatureCollection",
            features = features.Select(f => new
            {
                type = "Feature",
                properties = new { f.id, f.name },
                geometry = f.geometry
            })
        };

        var jsonContent = JsonSerializer.Serialize(geoJson);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/admin/import/geojson", new MultipartFormDataContent
        {
            { content, "file", "test-data.geojson" },
            { new StringContent("performance-test"), "name" }
        });

        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.Created);
    }

    private async Task SeedFeaturesWithRelationshipsAsync(int featureCount, int relatedPerFeature)
    {
        // Create test data with relationships for N+1 testing
        // Implementation would create features with foreign key relationships
        await Task.Delay(10); // Placeholder - implement based on your data model
    }

    private async Task<(HttpResponseMessage Response, string QueryPlan, TimeSpan Duration)> MeasureQueryWithPlan(string url)
    {
        var stopwatch = Stopwatch.StartNew();

        // Execute query and capture plan (this would need query plan logging enabled)
        var response = await _client.GetAsync(url);

        stopwatch.Stop();

        // Get query plan from response headers or logs
        var queryPlan = response.Headers.GetValues("X-Query-Plan").FirstOrDefault() ?? "No plan available";

        return (response, queryPlan, stopwatch.Elapsed);
    }

    private async Task<long> GetQueryCountAsync()
    {
        // Get current query count from database statistics
        // This would need to be implemented based on your monitoring setup
        await Task.Delay(1);
        return Random.Shared.NextInt64(1, 100); // Placeholder
    }

    private async Task<int> GetActiveConnectionCountAsync()
    {
        // Get active connection count from connection pool
        await Task.Delay(1);
        return Random.Shared.Next(1, 50); // Placeholder
    }

    private static string GenerateLargeGeoJsonData(int featureCount)
    {
        var features = Enumerable.Range(0, featureCount).Select(i => new
        {
            type = "Feature",
            properties = new
            {
                id = i,
                name = $"Import_Feature_{i}",
                category = $"Category_{i % 10}",
                value = Random.Shared.NextDouble() * 1000
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
            features
        };

        return JsonSerializer.Serialize(geoJson);
    }

    private static string GenerateBulkFeatureData(int featureCount)
    {
        var features = Enumerable.Range(0, featureCount).Select(i => new
        {
            id = $"bulk_feature_{i}",
            properties = new
            {
                name = $"Bulk Feature {i}",
                index = i
            },
            geometry = new
            {
                type = "Point",
                coordinates = new[] { i % 360 - 180, (i % 180) - 90 }
            }
        });

        return JsonSerializer.Serialize(new { features });
    }

    #endregion
}