// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using System.Net;

namespace Honua.Server.Tests.LoadTests;

/// <summary>
/// Basic load validation tests that verify system stability under moderate concurrent load.
/// Designed to be safe for CI/CD environments with conservative load parameters.
/// </summary>
[Collection("Database")]
public class BasicLoadValidationTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;

    public BasicLoadValidationTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact(DisplayName = "System handles moderate concurrent user load")]
    public async Task SystemHandles_ModerateConcurrentUserLoad()
    {
        // Arrange: Conservative parameters for CI/CD safety
        var concurrentUsers = 20;
        var operationsPerUser = 5;
        var results = new ConcurrentBag<LoadTestResult>();

        // Act: Simulate moderate concurrent load
        var userTasks = Enumerable.Range(0, concurrentUsers).Select(async userId =>
        {
            for (int op = 0; op < operationsPerUser; op++)
            {
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    // Mix of different operation types
                    var operation = op % 3 switch
                    {
                        0 => $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer",
                        1 => $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}",
                        _ => $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=10"
                    };

                    var response = await _client.GetAsync($"{operation}&user={userId}&op={op}");
                    stopwatch.Stop();

                    results.Add(new LoadTestResult
                    {
                        UserId = userId,
                        Operation = op,
                        Success = response.StatusCode == HttpStatusCode.OK,
                        Duration = stopwatch.Elapsed,
                        StatusCode = response.StatusCode
                    });
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    results.Add(new LoadTestResult
                    {
                        UserId = userId,
                        Operation = op,
                        Success = false,
                        Duration = stopwatch.Elapsed,
                        StatusCode = HttpStatusCode.InternalServerError,
                        Error = ex.Message
                    });
                }

                // Realistic user delay
                await Task.Delay(Random.Shared.Next(100, 500));
            }
        });

        await Task.WhenAll(userTasks);

        // Assert: System should handle moderate load gracefully
        var allResults = results.ToList();
        var totalOperations = concurrentUsers * operationsPerUser;

        allResults.Should().HaveCount(totalOperations, "All operations should complete");

        var successRate = (double)allResults.Count(r => r.Success) / allResults.Count;
        successRate.Should().BeGreaterOrEqualTo(0.95, "Success rate should be at least 95%");

        // Performance should remain reasonable
        var successfulResults = allResults.Where(r => r.Success).ToList();
        if (successfulResults.Any())
        {
            var averageTime = successfulResults.Average(r => r.Duration.TotalMilliseconds);
            var maxTime = successfulResults.Max(r => r.Duration.TotalMilliseconds);

            averageTime.Should().BeLessThan(3000, "Average response time should be under 3 seconds");
            maxTime.Should().BeLessThan(10000, "Maximum response time should be under 10 seconds");
        }
    }

    [Fact(DisplayName = "Database connection pool remains stable under load")]
    public async Task DatabaseConnectionPool_RemainsStableUnderLoad()
    {
        // Arrange: Monitor connection pool behavior under sustained load
        var sustainedDuration = TimeSpan.FromMinutes(1); // Conservative for CI/CD
        var concurrentConnections = 15;
        var results = new ConcurrentBag<ConnectionResult>();

        using var cts = new CancellationTokenSource(sustainedDuration);

        // Act: Generate sustained database load
        var connectionTasks = Enumerable.Range(0, concurrentConnections).Select(async connectionId =>
        {
            var operationCount = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();

                    var endpoint = operationCount % 3 switch
                    {
                        0 => $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=10",
                        1 => $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/layers",
                        _ => "/admin/health"
                    };

                    var response = await _client.GetAsync($"{endpoint}&conn={connectionId}&op={operationCount}");
                    stopwatch.Stop();

                    results.Add(new ConnectionResult
                    {
                        ConnectionId = connectionId,
                        OperationCount = operationCount,
                        Success = response.StatusCode == HttpStatusCode.OK,
                        Duration = stopwatch.Elapsed,
                        Timestamp = DateTime.UtcNow
                    });

                    operationCount++;
                    await Task.Delay(Random.Shared.Next(50, 200), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    results.Add(new ConnectionResult
                    {
                        ConnectionId = connectionId,
                        OperationCount = operationCount,
                        Success = false,
                        Duration = TimeSpan.Zero,
                        Error = ex.Message,
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
        });

        await Task.WhenAll(connectionTasks);

        // Assert: Connection pool should handle sustained load
        var allResults = results.ToList();

        allResults.Should().HaveCountGreaterThan(50, "Should have processed substantial number of operations");

        var successRate = (double)allResults.Count(r => r.Success) / allResults.Count;
        successRate.Should().BeGreaterOrEqualTo(0.95, "Connection pool should maintain high success rate");

        // Check for performance degradation over time
        if (allResults.Count > 20)
        {
            var orderedResults = allResults.Where(r => r.Success).OrderBy(r => r.Timestamp).ToList();

            if (orderedResults.Count > 10)
            {
                var firstHalf = orderedResults.Take(orderedResults.Count / 2);
                var secondHalf = orderedResults.Skip(orderedResults.Count / 2);

                var firstHalfAvg = firstHalf.Average(r => r.Duration.TotalMilliseconds);
                var secondHalfAvg = secondHalf.Average(r => r.Duration.TotalMilliseconds);

                var degradationRatio = secondHalfAvg / firstHalfAvg;
                degradationRatio.Should().BeLessThan(3.0, "Performance should not significantly degrade over time");
            }
        }
    }

    [Fact(DisplayName = "Memory consumption remains stable during load")]
    public async Task MemoryConsumption_RemainsStableDuringLoad()
    {
        // Arrange: Monitor memory during load test
        var initialMemory = GC.GetTotalMemory(true);
        var memoryReadings = new ConcurrentBag<long>();

        // Start memory monitoring
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var memoryMonitorTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                memoryReadings.Add(GC.GetTotalMemory(false));
                await Task.Delay(2000, cts.Token); // Every 2 seconds
            }
        }, cts.Token);

        // Act: Generate memory load
        var memoryLoadTasks = Enumerable.Range(0, 10).Select(async batchId =>
        {
            for (int i = 0; i < 20; i++)
            {
                var response = await _client.GetAsync(
                    $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=100&batch={batchId}&item={i}");

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                await Task.Delay(50); // Small delay
            }
        });

        await Task.WhenAll(memoryLoadTasks);
        cts.Cancel();

        try { await memoryMonitorTask; } catch (OperationCanceledException) { /* Expected */ }

        // Final memory measurement
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var finalMemory = GC.GetTotalMemory(false);

        // Assert: Memory should remain stable
        var memoryIncrease = finalMemory - initialMemory;
        var memoryIncreaseMB = memoryIncrease / (1024.0 * 1024.0);

        memoryIncreaseMB.Should().BeLessThan(200, "Memory increase should be bounded (< 200MB)");

        // Check for memory spikes during load
        var readings = memoryReadings.ToList();
        if (readings.Count > 5)
        {
            var maxMemory = readings.Max();
            var memorySpike = (maxMemory - initialMemory) / (1024.0 * 1024.0);

            memorySpike.Should().BeLessThan(500, "Memory spikes should be controlled (< 500MB)");
        }
    }

    [Fact(DisplayName = "Error handling remains effective under load")]
    public async Task ErrorHandling_RemainsEffectiveUnderLoad()
    {
        // Test that error handling doesn't degrade under load
        var mixedRequests = new[]
        {
            // Valid requests
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer",
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}",
            "/admin/health",

            // Invalid requests (should be handled gracefully)
            "/rest/services/nonexistent/FeatureServer",
            "/rest/services/1/FeatureServer/999/query",
            "/admin/nonexistent"
        };

        var concurrentErrorTests = Enumerable.Range(0, 30).Select(async i =>
        {
            var endpoint = mixedRequests[i % mixedRequests.Length];
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var response = await _client.GetAsync($"{endpoint}?error_test={i}");
                stopwatch.Stop();

                return new
                {
                    RequestId = i,
                    Endpoint = endpoint,
                    StatusCode = response.StatusCode,
                    Duration = stopwatch.Elapsed,
                    IsExpectedValid = i % mixedRequests.Length < 3, // First 3 are valid
                    Success = true
                };
            }
            catch (Exception)
            {
                stopwatch.Stop();
                return new
                {
                    RequestId = i,
                    Endpoint = endpoint,
                    StatusCode = HttpStatusCode.InternalServerError,
                    Duration = stopwatch.Elapsed,
                    IsExpectedValid = i % mixedRequests.Length < 3,
                    Success = false
                };
            }
        });

        var results = await Task.WhenAll(concurrentErrorTests);

        // Assert: Error handling should work correctly under load
        var validRequests = results.Where(r => r.IsExpectedValid).ToList();
        var invalidRequests = results.Where(r => !r.IsExpectedValid).ToList();

        // Valid requests should mostly succeed
        if (validRequests.Any())
        {
            var validSuccessRate = (double)validRequests.Count(r => r.StatusCode == HttpStatusCode.OK) / validRequests.Count;
            validSuccessRate.Should().BeGreaterOrEqualTo(0.9, "Valid requests should mostly succeed under load");
        }

        // Invalid requests should be handled appropriately (not crash)
        invalidRequests.Should().AllSatisfy(r =>
        {
            r.Success.Should().BeTrue("Error handling should not throw exceptions");
            r.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError);
        });

        // Response times should remain reasonable even for errors
        var allDurations = results.Select(r => r.Duration);
        var maxDuration = allDurations.Max();

        maxDuration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(10),
            "Error responses should not take excessively long");
    }

    #region Helper Classes

    private record LoadTestResult
    {
        public int UserId { get; init; }
        public int Operation { get; init; }
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public HttpStatusCode StatusCode { get; init; }
        public string? Error { get; init; }
    }

    private record ConnectionResult
    {
        public int ConnectionId { get; init; }
        public int OperationCount { get; init; }
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public DateTime Timestamp { get; init; }
        public string? Error { get; init; }
    }

    #endregion
}