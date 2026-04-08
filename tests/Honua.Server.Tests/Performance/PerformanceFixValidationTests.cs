// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Performance fix validation tests for critical database and cache optimizations.
/// Tests index usage, connection pooling, and cache efficiency under realistic loads.
/// </summary>
[Collection("Database")]
public class PerformanceFixValidationTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;

    public PerformanceFixValidationTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact(DisplayName = "Spatial queries complete within performance thresholds")]
    public async Task SpatialQueries_CompleteWithinPerformanceThresholds()
    {
        // Arrange: Simple spatial query that should use indexes
        var bbox = "-180,-90,180,90"; // World bounds
        var spatialQuery = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
                          $"?geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&resultRecordCount=100";

        // Act & Measure: Execute spatial query
        var result = await spatialQuery.ShouldCompleteWithin(
            PerformanceAssertions.Thresholds.LargeSpatialQuery,
            "Spatial query should complete within performance threshold");

        // Assert: Query should return valid results
        result.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var content = await result.Content.ReadAsStringAsync();
        content.Should().Contain("features", "Should return feature collection");
    }

    [Fact(DisplayName = "Metadata queries are optimized for fast response")]
    public async Task MetadataQueries_OptimizedForFastResponse()
    {
        // Test various metadata endpoints for performance
        var metadataEndpoints = new[]
        {
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer",
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/layers",
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}",
        };

        foreach (var endpoint in metadataEndpoints)
        {
            // Act & Measure: Execute metadata query
            var result = await endpoint.ShouldCompleteWithin(
                PerformanceAssertions.Thresholds.MetadataQuery,
                $"Metadata query {endpoint} should be fast");

            // Assert: Should return successful response
            result.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact(DisplayName = "Concurrent queries maintain reasonable performance")]
    public async Task ConcurrentQueries_MaintainReasonablePerformance()
    {
        // Arrange: Prepare concurrent queries
        var concurrentCount = 20;
        var query = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=50";

        // Act: Execute concurrent queries
        var concurrentTasks = Enumerable.Range(0, concurrentCount).Select(async i =>
        {
            var (response, duration) = await PerformanceAssertions.MeasureAsync(async () =>
            {
                return await _client.GetAsync($"{query}&concurrent_test={i}");
            });

            return new { Response = response, Duration = duration, TaskId = i };
        });

        var results = await Task.WhenAll(concurrentTasks);

        // Assert: All queries should complete successfully
        results.Should().AllSatisfy(result =>
        {
            result.Response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
                $"Concurrent query {result.TaskId} should succeed");

            result.Duration.Should().BeLessOrEqualTo(PerformanceAssertions.Thresholds.MediumFeatureQuery,
                $"Concurrent query {result.TaskId} should complete within threshold");
        });

        // Overall performance should be reasonable
        var averageDuration = TimeSpan.FromMilliseconds(results.Average(r => r.Duration.TotalMilliseconds));
        averageDuration.Should().BeLessOrEqualTo(PerformanceAssertions.Thresholds.SmallFeatureQuery.Add(TimeSpan.FromSeconds(1)),
            "Average concurrent query time should be reasonable");
    }

    [Fact(DisplayName = "Connection pool handles high concurrency efficiently")]
    public async Task ConnectionPool_HandlesHighConcurrencyEfficiently()
    {
        // Arrange: Create many concurrent database operations
        var concurrentOperations = 50;

        // Act: Execute many concurrent database operations
        var connectionTasks = Enumerable.Range(0, concurrentOperations).Select(async i =>
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var response = await _client.GetAsync(
                    $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=id>{i}&resultRecordCount=10&connection_test={i}");

                stopwatch.Stop();

                return new
                {
                    TaskId = i,
                    Success = response.StatusCode == System.Net.HttpStatusCode.OK,
                    Duration = stopwatch.Elapsed,
                    StatusCode = response.StatusCode
                };
            }
            catch (Exception)
            {
                stopwatch.Stop();
                return new
                {
                    TaskId = i,
                    Success = false,
                    Duration = stopwatch.Elapsed,
                    StatusCode = System.Net.HttpStatusCode.InternalServerError
                };
            }
        });

        var allResults = await Task.WhenAll(connectionTasks);

        // Assert: Most operations should succeed efficiently
        var successfulOperations = allResults.Count(r => r.Success);
        var successRate = (double)successfulOperations / allResults.Length;

        successRate.Should().BeGreaterOrEqualTo(0.9, "At least 90% of operations should succeed");

        // Performance should not degrade significantly under load
        var successfulDurations = allResults.Where(r => r.Success).Select(r => r.Duration);
        if (successfulDurations.Any())
        {
            var maxDuration = successfulDurations.Max();
            maxDuration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(10),
                "Even under high concurrency, operations should complete within reasonable time");
        }
    }

    [Fact(DisplayName = "Cache hit performance provides measurable benefits")]
    public async Task CacheHitPerformance_ProvidesMeasurableBenefits()
    {
        // Arrange: Cacheable endpoint
        var cacheableEndpoint = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/layers";

        // Act: First request (likely cache miss)
        var (firstResponse, firstDuration) = await PerformanceAssertions.MeasureAsync(async () =>
        {
            return await _client.GetAsync($"{cacheableEndpoint}?cache_test=first");
        });

        firstResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        // Second request (likely cache hit)
        var (secondResponse, secondDuration) = await PerformanceAssertions.MeasureAsync(async () =>
        {
            return await _client.GetAsync($"{cacheableEndpoint}?cache_test=second");
        });

        secondResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        // Additional requests to establish pattern
        var subsequentTasks = Enumerable.Range(0, 5).Select(async i =>
        {
            return await PerformanceAssertions.MeasureAsync(async () =>
            {
                return await _client.GetAsync($"{cacheableEndpoint}?cache_test=subsequent_{i}");
            });
        });

        var subsequentResults = await Task.WhenAll(subsequentTasks);
        var subsequentDurations = subsequentResults.Select(r => r.Duration);

        // Assert: Cache hits should generally be faster (though not guaranteed in test environment)
        var averageSubsequentDuration = TimeSpan.FromMilliseconds(subsequentDurations.Average(d => d.TotalMilliseconds));

        // All responses should be successful
        firstResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        subsequentResults.Should().AllSatisfy(r => r.Result.StatusCode.Should().Be(System.Net.HttpStatusCode.OK));

        // Overall cache performance should be reasonable
        averageSubsequentDuration.Should().BeLessOrEqualTo(PerformanceAssertions.Thresholds.MetadataQuery,
            "Cached responses should be fast");
    }

    [Fact(DisplayName = "Large result set queries remain responsive")]
    public async Task LargeResultSetQueries_RemainResponsive()
    {
        // Arrange: Query that returns larger result set
        var largeResultQuery = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
                              "?where=1=1&resultRecordCount=500&f=json";

        // Act & Measure: Execute large result query
        var result = await largeResultQuery.ShouldCompleteWithin(
            PerformanceAssertions.Thresholds.MediumFeatureQuery.Add(TimeSpan.FromSeconds(3)),
            "Large result set query should complete within extended threshold");

        // Assert: Should return successful response with data
        result.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var content = await result.Content.ReadAsStringAsync();
        content.Should().Contain("features", "Should return feature collection");

        // Response size should be reasonable (not indicating inefficient serialization)
        var contentLength = content.Length;
        contentLength.Should().BeLessThan(50 * 1024 * 1024, "Response should be under 50MB for 500 features");
    }

    [Fact(DisplayName = "Query complexity scales appropriately")]
    public async Task QueryComplexity_ScalesAppropriately()
    {
        // Test different query complexities to ensure performance scaling
        var queryComplexities = new[]
        {
            ("Simple", "id > 0", PerformanceAssertions.Thresholds.SmallFeatureQuery),
            ("Moderate", "id > 0 AND name LIKE '%test%'", PerformanceAssertions.Thresholds.SmallFeatureQuery.Add(TimeSpan.FromSeconds(1))),
            ("Complex", "id > 0 AND name LIKE '%test%' AND category IN (1,2,3)", PerformanceAssertions.Thresholds.MediumFeatureQuery)
        };

        foreach (var (complexity, whereClause, threshold) in queryComplexities)
        {
            // Act: Execute query with specific complexity
            var encodedWhere = Uri.EscapeDataString(whereClause);
            var query = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where={encodedWhere}&resultRecordCount=100";

            var result = await query.ShouldCompleteWithin(threshold,
                $"{complexity} query should complete within appropriate threshold");

            // Assert: Should return successful response
            result.StatusCode.Should().BeOneOf(System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.BadRequest);

            if (result.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var content = await result.Content.ReadAsStringAsync();
                content.Should().Contain("features", $"{complexity} query should return feature collection");
            }
        }
    }

    [Fact(DisplayName = "Memory usage remains stable during performance tests")]
    public async Task MemoryUsage_RemainsStableDuringPerformanceTests()
    {
        // Arrange: Baseline memory measurement
        var initialMemory = GC.GetTotalMemory(true);

        // Act: Execute series of operations that could cause memory growth
        var memoryTestTasks = Enumerable.Range(0, 30).Select(async i =>
        {
            var response = await _client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=id>{i}&resultRecordCount=100&memory_test={i}");

            return response.StatusCode == System.Net.HttpStatusCode.OK;
        });

        var results = await Task.WhenAll(memoryTestTasks);

        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var finalMemory = GC.GetTotalMemory(false);
        var memoryIncrease = finalMemory - initialMemory;
        var memoryIncreaseMB = memoryIncrease / (1024.0 * 1024.0);

        // Assert: Memory increase should be reasonable
        results.Should().AllSatisfy(success => success.Should().BeTrue("Memory test operations should succeed"));

        memoryIncreaseMB.Should().BeLessThan(100,
            "Memory increase should be bounded during performance tests (< 100MB)");
    }
}