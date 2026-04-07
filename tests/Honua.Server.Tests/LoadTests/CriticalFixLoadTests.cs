// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;
using System.Net;

namespace Honua.Server.Tests.LoadTests;

/// <summary>
/// Load tests that validate critical fixes under production-like scenarios.
/// Tests concurrent user loads, memory stability, and system behavior under stress.
/// </summary>
[Collection("Database")]
public class CriticalFixLoadTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;

    public CriticalFixLoadTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact(DisplayName = "System handles 100 concurrent users with spatial queries")]
    public async Task ConcurrentUsers_SpatialQueries_100Users()
    {
        // Arrange: Simulate 100 concurrent users performing spatial queries
        var concurrentUsers = 100;
        var queriesPerUser = 10;
        var testDuration = TimeSpan.FromMinutes(2);

        var results = new ConcurrentBag<QueryResult>();
        var userTasks = new List<Task>();

        // Act: Simulate concurrent users
        for (int userId = 0; userId < concurrentUsers; userId++)
        {
            var userTask = SimulateUserSession(userId, queriesPerUser, results);
            userTasks.Add(userTask);
        }

        // Wait for all users to complete their sessions
        using var cts = new CancellationTokenSource(testDuration);
        var completedTask = Task.WhenAll(userTasks);

        try
        {
            await completedTask.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Some operations may still be running, which is OK for load test
        }

        // Assert: System should handle the load gracefully
        var allResults = results.ToList();
        allResults.Should().HaveCountGreaterThan(concurrentUsers * queriesPerUser * 0.8,
            "At least 80% of queries should complete successfully");

        var successfulResults = allResults.Where(r => r.Success).ToList();
        var failedResults = allResults.Where(r => !r.Success).ToList();

        // Performance assertions
        if (successfulResults.Any())
        {
            var averageResponseTime = successfulResults.Average(r => r.Duration.TotalMilliseconds);
            var p95ResponseTime = CalculatePercentile(successfulResults.Select(r => r.Duration), 0.95);
            var p99ResponseTime = CalculatePercentile(successfulResults.Select(r => r.Duration), 0.99);

            averageResponseTime.Should().BeLessThan(2000, "Average response time should be under 2 seconds");
            p95ResponseTime.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(5), "P95 should be under 5 seconds");
            p99ResponseTime.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(10), "P99 should be under 10 seconds");
        }

        // Error rate should be acceptable
        var errorRate = (double)failedResults.Count / allResults.Count;
        errorRate.Should().BeLessOrEqualTo(0.05, "Error rate should be under 5%");

        // Memory should remain stable
        var finalMemory = GC.GetTotalMemory(true);
        var memoryMB = finalMemory / (1024.0 * 1024.0);
        memoryMB.Should().BeLessThan(2048, "Memory usage should remain under 2GB after load test");
    }

    [Fact(DisplayName = "Authentication system handles login attempts under load")]
    public async Task AuthenticationSystem_HandlesLoadUnderAttack()
    {
        // Arrange: Simulate authentication load (both valid and invalid attempts)
        var concurrentClients = 50;
        var attemptsPerClient = 20;

        var authResults = new ConcurrentBag<AuthResult>();

        // Act: Simulate mixed authentication attempts
        var authTasks = Enumerable.Range(0, concurrentClients).Select(async clientId =>
        {
            var client = _fixture.CreateClient();

            for (int attempt = 0; attempt < attemptsPerClient; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    // Mix of valid/invalid authentication attempts
                    var isValidAttempt = attempt % 4 == 0; // 25% valid attempts

                    if (isValidAttempt)
                    {
                        // Valid API key
                        client.DefaultRequestHeaders.Clear();
                        client.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");
                    }
                    else
                    {
                        // Invalid API key
                        client.DefaultRequestHeaders.Clear();
                        client.DefaultRequestHeaders.Add("X-API-Key", $"invalid-key-{clientId}-{attempt}");
                    }

                    var response = await client.GetAsync($"/admin/health?client={clientId}&attempt={attempt}");
                    stopwatch.Stop();

                    authResults.Add(new AuthResult
                    {
                        ClientId = clientId,
                        Attempt = attempt,
                        Success = response.StatusCode == HttpStatusCode.OK,
                        StatusCode = response.StatusCode,
                        Duration = stopwatch.Elapsed,
                        IsValidAttempt = isValidAttempt
                    });
                }
                catch (Exception)
                {
                    stopwatch.Stop();
                    authResults.Add(new AuthResult
                    {
                        ClientId = clientId,
                        Attempt = attempt,
                        Success = false,
                        StatusCode = HttpStatusCode.InternalServerError,
                        Duration = stopwatch.Elapsed,
                        IsValidAttempt = false
                    });
                }

                // Small delay between attempts
                await Task.Delay(10);
            }
        });

        await Task.WhenAll(authTasks);

        // Assert: Authentication system should handle load correctly
        var allAuthResults = authResults.ToList();

        var validAttempts = allAuthResults.Where(r => r.IsValidAttempt).ToList();
        var invalidAttempts = allAuthResults.Where(r => !r.IsValidAttempt).ToList();

        // Valid attempts should mostly succeed
        if (validAttempts.Any())
        {
            var validSuccessRate = (double)validAttempts.Count(r => r.Success) / validAttempts.Count;
            validSuccessRate.Should().BeGreaterOrEqualTo(0.95, "Valid authentication attempts should succeed");
        }

        // Invalid attempts should be properly rejected
        if (invalidAttempts.Any())
        {
            var invalidRejectionRate = (double)invalidAttempts.Count(r => !r.Success) / invalidAttempts.Count;
            invalidRejectionRate.Should().BeGreaterOrEqualTo(0.95, "Invalid authentication attempts should be rejected");
        }

        // Response times should remain reasonable even under auth load
        var authDurations = allAuthResults.Select(r => r.Duration);
        var avgAuthTime = TimeSpan.FromMilliseconds(authDurations.Average(d => d.TotalMilliseconds));

        avgAuthTime.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(500),
            "Authentication should remain fast even under load");
    }

    [Fact(DisplayName = "Cache system performance under high request volume")]
    public async Task CacheSystem_PerformanceUnderHighVolume()
    {
        // Arrange: Generate high volume of cache-able requests
        var requestVolume = 1000;
        var concurrency = 20;
        var cacheResults = new ConcurrentBag<CacheTestResult>();

        // Pre-warm some cache entries
        await _client.GetAsync("/rest/services/1/FeatureServer/layers");

        // Act: Generate high volume cache requests
        var batchSize = requestVolume / concurrency;
        var cacheTasks = Enumerable.Range(0, concurrency).Select(async batchId =>
        {
            for (int i = 0; i < batchSize; i++)
            {
                var requestId = batchId * batchSize + i;
                var cacheKey = requestId % 10; // Create cache hit patterns

                var stopwatch = Stopwatch.StartNew();
                var response = await _client.GetAsync($"/rest/services/1/FeatureServer/{cacheKey % 3}/query?where=1=1&resultRecordCount=10&_cache_test={requestId}");
                stopwatch.Stop();

                cacheResults.Add(new CacheTestResult
                {
                    RequestId = requestId,
                    CacheKey = cacheKey,
                    Success = response.StatusCode == HttpStatusCode.OK,
                    Duration = stopwatch.Elapsed,
                    ResponseSize = response.Content.Headers.ContentLength ?? 0
                });
            }
        });

        await Task.WhenAll(cacheTasks);

        // Assert: Cache should provide performance benefits
        var allCacheResults = cacheResults.ToList();

        // Group by cache key to analyze hit patterns
        var cacheKeyGroups = allCacheResults.GroupBy(r => r.CacheKey).ToList();

        foreach (var keyGroup in cacheKeyGroups.Take(5)) // Test first 5 cache keys
        {
            var keyResults = keyGroup.OrderBy(r => r.RequestId).ToList();
            if (keyResults.Count > 1)
            {
                var firstRequest = keyResults.First();
                var subsequentRequests = keyResults.Skip(1).Take(10); // Next 10 requests

                if (subsequentRequests.Any())
                {
                    var avgSubsequentDuration = subsequentRequests.Average(r => r.Duration.TotalMilliseconds);
                    var firstRequestDuration = firstRequest.Duration.TotalMilliseconds;

                    // Subsequent requests should be faster (cache hits)
                    if (firstRequestDuration > 100) // Only test if first request took reasonable time
                    {
                        (avgSubsequentDuration / firstRequestDuration).Should().BeLessThan(0.8,
                            $"Cache should provide performance benefit for key {keyGroup.Key}");
                    }
                }
            }
        }

        // Overall performance should be good
        var successfulResults = allCacheResults.Where(r => r.Success).ToList();
        if (successfulResults.Any())
        {
            var avgResponseTime = successfulResults.Average(r => r.Duration.TotalMilliseconds);
            avgResponseTime.Should().BeLessThan(500, "Average response time should benefit from caching");
        }
    }

    [Fact(DisplayName = "Database connection pool stability under prolonged load")]
    public async Task DatabaseConnectionPool_StabilityUnderProlongedLoad()
    {
        // Arrange: Extended load test for connection pool
        var testDuration = TimeSpan.FromMinutes(3);
        var concurrentConnections = 50;

        var connectionResults = new ConcurrentBag<ConnectionTestResult>();

        using var cts = new CancellationTokenSource(testDuration);

        // Act: Generate sustained database load
        var connectionTasks = Enumerable.Range(0, concurrentConnections).Select(async connectionId =>
        {
            var operationCount = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();

                    // Mix of different database operations
                    var operation = operationCount % 4 switch
                    {
                        0 => "/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=50",
                        1 => "/rest/services/1/FeatureServer/layers",
                        2 => "/ogc/features/v1/collections/test/items?limit=10",
                        _ => "/admin/health"
                    };

                    var response = await _client.GetAsync($"{operation}&conn_test={connectionId}&op={operationCount}");
                    stopwatch.Stop();

                    connectionResults.Add(new ConnectionTestResult
                    {
                        ConnectionId = connectionId,
                        OperationCount = operationCount,
                        Success = response.StatusCode == HttpStatusCode.OK,
                        Duration = stopwatch.Elapsed,
                        Timestamp = DateTime.UtcNow
                    });

                    operationCount++;

                    // Variable delay to simulate realistic usage
                    await Task.Delay(Random.Shared.Next(10, 100), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    connectionResults.Add(new ConnectionTestResult
                    {
                        ConnectionId = connectionId,
                        OperationCount = operationCount,
                        Success = false,
                        Duration = TimeSpan.Zero,
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
        });

        await Task.WhenAll(connectionTasks);

        // Assert: Connection pool should remain stable
        var allConnectionResults = connectionResults.ToList();

        allConnectionResults.Should().HaveCountGreaterThan(100, "Should have processed many operations");

        var successRate = (double)allConnectionResults.Count(r => r.Success) / allConnectionResults.Count;
        successRate.Should().BeGreaterOrEqualTo(0.95, "Success rate should remain high throughout prolonged load");

        // Check for performance degradation over time
        var totalDuration = allConnectionResults.Max(r => r.Timestamp) - allConnectionResults.Min(r => r.Timestamp);
        if (totalDuration > TimeSpan.FromMinutes(1))
        {
            var midPoint = allConnectionResults.Min(r => r.Timestamp).Add(totalDuration.Divide(2));

            var earlyResults = allConnectionResults.Where(r => r.Timestamp <= midPoint && r.Success).ToList();
            var lateResults = allConnectionResults.Where(r => r.Timestamp > midPoint && r.Success).ToList();

            if (earlyResults.Any() && lateResults.Any())
            {
                var earlyAvgTime = earlyResults.Average(r => r.Duration.TotalMilliseconds);
                var lateAvgTime = lateResults.Average(r => r.Duration.TotalMilliseconds);

                var degradationFactor = lateAvgTime / earlyAvgTime;
                degradationFactor.Should().BeLessThan(2.0, "Performance should not significantly degrade over time");
            }
        }
    }

    [Fact(DisplayName = "Memory consumption stability during bulk operations")]
    public async Task MemoryConsumption_StabilityDuringBulkOperations()
    {
        // Arrange: Monitor memory during bulk operations
        var initialMemory = GC.GetTotalMemory(true);
        var memorySnapshots = new ConcurrentBag<MemorySnapshot>();

        // Start memory monitoring
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var memoryMonitorTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                memorySnapshots.Add(new MemorySnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    MemoryUsage = GC.GetTotalMemory(false),
                    Gen0Collections = GC.CollectionCount(0),
                    Gen1Collections = GC.CollectionCount(1),
                    Gen2Collections = GC.CollectionCount(2)
                });

                await Task.Delay(1000, cts.Token);
            }
        }, cts.Token);

        try
        {
            // Act: Perform bulk operations
            var bulkTasks = Enumerable.Range(0, 10).Select(async bulkId =>
            {
                // Large data operations
                var largeDataRequests = Enumerable.Range(0, 20).Select(async i =>
                {
                    var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=1000&bulk_test={bulkId}&req={i}");
                    return response.StatusCode == HttpStatusCode.OK;
                });

                var results = await Task.WhenAll(largeDataRequests);
                return results.Count(r => r);
            });

            var bulkResults = await Task.WhenAll(bulkTasks);

        }
        finally
        {
            cts.Cancel();
            try { await memoryMonitorTask; } catch (OperationCanceledException) { }
        }

        // Assert: Memory should remain stable
        var snapshots = memorySnapshots.OrderBy(s => s.Timestamp).ToList();

        if (snapshots.Count >= 10)
        {
            var maxMemory = snapshots.Max(s => s.MemoryUsage);
            var memoryGrowth = maxMemory - initialMemory;
            var memoryGrowthMB = memoryGrowth / (1024.0 * 1024.0);

            memoryGrowthMB.Should().BeLessThan(1024, "Memory growth should be bounded during bulk operations");

            // Check for memory leaks - should show cleanup over time
            var lastFewSnapshots = snapshots.TakeLast(5).ToList();
            var firstFewSnapshots = snapshots.Take(5).ToList();

            if (lastFewSnapshots.Any() && firstFewSnapshots.Any())
            {
                var endMemory = lastFewSnapshots.Average(s => s.MemoryUsage);
                var startMemory = firstFewSnapshots.Average(s => s.MemoryUsage);

                var sustainedGrowth = (endMemory - startMemory) / (1024.0 * 1024.0);
                sustainedGrowth.Should().BeLessThan(200, "Sustained memory growth should indicate no major leaks");
            }
        }
    }

    #region Helper Methods

    private async Task SimulateUserSession(int userId, int queriesPerUser, ConcurrentBag<QueryResult> results)
    {
        var userClient = _fixture.CreateClient();

        for (int queryId = 0; queryId < queriesPerUser; queryId++)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();

                // Simulate realistic user queries
                var queryType = queryId % 4;
                var query = queryType switch
                {
                    0 => "/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=50",
                    1 => $"/rest/services/1/FeatureServer/0/query?where=id>{userId * 10}&resultRecordCount=100",
                    2 => "/rest/services/1/FeatureServer/layers",
                    _ => $"/ogc/features/v1/collections/test/items?limit=50&offset={queryId * 50}"
                };

                var response = await userClient.GetAsync($"{query}&user={userId}&query={queryId}");
                stopwatch.Stop();

                results.Add(new QueryResult
                {
                    UserId = userId,
                    QueryId = queryId,
                    Success = response.StatusCode == HttpStatusCode.OK,
                    Duration = stopwatch.Elapsed,
                    StatusCode = response.StatusCode
                });

                // Simulate user think time
                await Task.Delay(Random.Shared.Next(100, 1000));
            }
            catch (Exception)
            {
                results.Add(new QueryResult
                {
                    UserId = userId,
                    QueryId = queryId,
                    Success = false,
                    Duration = TimeSpan.Zero,
                    StatusCode = HttpStatusCode.InternalServerError
                });
            }
        }
    }

    private static TimeSpan CalculatePercentile(IEnumerable<TimeSpan> durations, double percentile)
    {
        var sortedDurations = durations.OrderBy(d => d).ToList();
        if (!sortedDurations.Any()) return TimeSpan.Zero;

        var index = (int)Math.Ceiling(sortedDurations.Count * percentile) - 1;
        return sortedDurations[Math.Max(0, Math.Min(index, sortedDurations.Count - 1))];
    }

    #endregion

    #region Result Classes

    private record QueryResult
    {
        public int UserId { get; init; }
        public int QueryId { get; init; }
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public HttpStatusCode StatusCode { get; init; }
    }

    private record AuthResult
    {
        public int ClientId { get; init; }
        public int Attempt { get; init; }
        public bool Success { get; init; }
        public HttpStatusCode StatusCode { get; init; }
        public TimeSpan Duration { get; init; }
        public bool IsValidAttempt { get; init; }
    }

    private record CacheTestResult
    {
        public int RequestId { get; init; }
        public int CacheKey { get; init; }
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public long ResponseSize { get; init; }
    }

    private record ConnectionTestResult
    {
        public int ConnectionId { get; init; }
        public int OperationCount { get; init; }
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public DateTime Timestamp { get; init; }
    }

    private record MemorySnapshot
    {
        public DateTime Timestamp { get; init; }
        public long MemoryUsage { get; init; }
        public int Gen0Collections { get; init; }
        public int Gen1Collections { get; init; }
        public int Gen2Collections { get; init; }
    }

    #endregion
}