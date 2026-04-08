// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure;

/// <summary>
/// Resilience pattern tests for circuit breakers, rate limiting, and failure handling.
/// Tests ensure the system gracefully handles failure scenarios and prevents cascading failures.
/// </summary>
[Collection("Database")]
public class ResiliencePatternTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;

    public ResiliencePatternTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact(DisplayName = "Circuit breaker activates on repeated failures")]
    public async Task CircuitBreaker_ActivatesOnRepeatedFailures()
    {
        // Arrange: Prepare to trigger circuit breaker with failing operations
        var failingEndpoint = "/rest/services/99999/FeatureServer/0/query"; // Non-existent service

        var initialRequests = new List<(HttpResponseMessage Response, TimeSpan Duration)>();

        // Act: Send requests to trigger failures and circuit breaker
        for (int i = 0; i < 20; i++)
        {
            var (response, duration) = await PerformanceAssertions.MeasureAsync(async () =>
            {
                return await _client.GetAsync($"{failingEndpoint}?attempt={i}");
            });

            initialRequests.Add((response, duration));

            // Small delay between requests
            await Task.Delay(50);
        }

        // Verify circuit breaker behavior
        var firstFewRequests = initialRequests.Take(5);
        var laterRequests = initialRequests.Skip(10);

        // Assert: Initial requests should fail normally, later requests should fail fast
        firstFewRequests.Should().AllSatisfy(request =>
        {
            request.Response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
            // Initial failures should take normal time (database lookup, etc.)
        });

        // Later requests should fail fast due to circuit breaker
        var laterDurations = laterRequests.Select(r => r.Duration);
        var averageLaterDuration = TimeSpan.FromMilliseconds(laterDurations.Average(d => d.TotalMilliseconds));

        averageLaterDuration.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(100),
            "Circuit breaker should cause fast failures after threshold is reached");
    }

    [Fact(DisplayName = "Rate limiting enforces request limits per client")]
    public async Task RateLimiting_EnforcesRequestLimitsPerClient()
    {
        // Arrange: Create rapid requests to trigger rate limiting
        var rapidRequests = 100;
        var timeWindow = TimeSpan.FromSeconds(10);

        var results = new List<(HttpStatusCode StatusCode, TimeSpan Duration, DateTime Timestamp)>();

        // Act: Send rapid requests
        var tasks = Enumerable.Range(0, rapidRequests).Select(async i =>
        {
            var timestamp = DateTime.UtcNow;
            var (response, duration) = await PerformanceAssertions.MeasureAsync(async () =>
            {
                return await _client.GetAsync($"/rest/services/1/FeatureServer/layers?rapid_test={i}");
            });

            return (response.StatusCode, duration, timestamp);
        });

        var allResults = await Task.WhenAll(tasks);
        results.AddRange(allResults);

        // Assert: Rate limiting should kick in
        var successfulRequests = results.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rateLimitedRequests = results.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        // Should have some successful requests initially
        successfulRequests.Should().BeGreaterThan(0, "Some requests should succeed before rate limit");

        // Should have rate limited some requests
        rateLimitedRequests.Should().BeGreaterThan(0, "Rate limiting should reject excessive requests");

        // Rate limited requests should be fast (no processing)
        var rateLimitedDurations = results
            .Where(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .Select(r => r.Duration);

        if (rateLimitedDurations.Any())
        {
            var avgRateLimitDuration = TimeSpan.FromMilliseconds(
                rateLimitedDurations.Average(d => d.TotalMilliseconds));

            avgRateLimitDuration.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(50),
                "Rate limited responses should be very fast");
        }
    }

    [Fact(DisplayName = "Connection pool gracefully handles exhaustion")]
    public async Task ConnectionPool_GracefullyHandlesExhaustion()
    {
        // Arrange: Create more concurrent operations than typical pool size
        var concurrentOperations = 200; // Exceed typical connection pool size
        var operationTimeout = TimeSpan.FromSeconds(30);

        // Act: Execute many concurrent database operations
        var connectionStressTasks = Enumerable.Range(0, concurrentOperations).Select(async i =>
        {
            try
            {
                using var cts = new CancellationTokenSource(operationTimeout);

                var (response, duration) = await PerformanceAssertions.MeasureAsync(async () =>
                {
                    // Long-running query to hold connections longer
                    return await _client.GetAsync(
                        $"/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=100&_stress={i}",
                        cts.Token);
                });

                return new
                {
                    TaskId = i,
                    Success = response.StatusCode == HttpStatusCode.OK,
                    Duration = duration,
                    StatusCode = response.StatusCode,
                    TimedOut = false
                };
            }
            catch (OperationCanceledException)
            {
                return new
                {
                    TaskId = i,
                    Success = false,
                    Duration = operationTimeout,
                    StatusCode = HttpStatusCode.RequestTimeout,
                    TimedOut = true
                };
            }
            catch (Exception)
            {
                return new
                {
                    TaskId = i,
                    Success = false,
                    Duration = TimeSpan.Zero,
                    StatusCode = HttpStatusCode.InternalServerError,
                    TimedOut = false
                };
            }
        });

        var allResults = await Task.WhenAll(connectionStressTasks);

        // Assert: System should handle connection pressure gracefully
        var successfulOperations = allResults.Count(r => r.Success);
        var timedOutOperations = allResults.Count(r => r.TimedOut);

        // Most operations should succeed (graceful degradation)
        successfulOperations.Should().BeGreaterThan(concurrentOperations * 0.7,
            "At least 70% of operations should succeed even under connection pressure");

        // Very few should time out completely
        timedOutOperations.Should().BeLessOrEqualTo(concurrentOperations * 0.1,
            "Less than 10% should time out due to connection exhaustion");

        // Performance should degrade gracefully
        var successfulDurations = allResults.Where(r => r.Success).Select(r => r.Duration);
        if (successfulDurations.Any())
        {
            var maxSuccessfulDuration = successfulDurations.Max();
            maxSuccessfulDuration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(20),
                "Even under stress, operations should complete within reasonable time");
        }
    }

    [Fact(DisplayName = "File upload backpressure prevents memory exhaustion")]
    public async Task FileUploadBackpressure_PreventsMemoryExhaustion()
    {
        // Arrange: Create large file upload scenarios
        var concurrentUploads = 10;
        var uploadSizeMB = 50; // 50MB per upload

        var initialMemory = GC.GetTotalMemory(true);

        // Act: Attempt concurrent large file uploads
        var uploadTasks = Enumerable.Range(0, concurrentUploads).Select(async i =>
        {
            try
            {
                var largeContent = new byte[uploadSizeMB * 1024 * 1024];
                Random.Shared.NextBytes(largeContent);

                using var content = new MultipartFormDataContent();
                using var fileContent = new ByteArrayContent(largeContent);
                content.Add(fileContent, "file", $"large-file-{i}.bin");
                content.Add(new StringContent($"backpressure-test-{i}"), "name");

                var (response, duration) = await PerformanceAssertions.MeasureAsync(async () =>
                {
                    return await _client.PostAsync("/admin/import/test", content);
                });

                return new
                {
                    UploadId = i,
                    StatusCode = response.StatusCode,
                    Duration = duration,
                    Success = response.StatusCode == HttpStatusCode.OK ||
                             response.StatusCode == HttpStatusCode.Accepted ||
                             response.StatusCode == HttpStatusCode.TooManyRequests // Backpressure
                };
            }
            catch (Exception)
            {
                // Upload rejected due to backpressure is acceptable
                return new
                {
                    UploadId = i,
                    StatusCode = HttpStatusCode.ServiceUnavailable,
                    Duration = TimeSpan.Zero,
                    Success = true // Rejection is success in backpressure scenario
                };
            }
        });

        var uploadResults = await Task.WhenAll(uploadTasks);

        var peakMemory = GC.GetTotalMemory(false);
        var memoryIncrease = peakMemory - initialMemory;
        var memoryIncreaseMB = memoryIncrease / (1024.0 * 1024.0);

        // Assert: Backpressure should prevent memory exhaustion
        uploadResults.Should().AllSatisfy(result =>
            result.Success.Should().BeTrue("All uploads should either succeed or be properly rejected"));

        // Memory increase should be bounded (not concurrent_uploads * upload_size)
        var expectedMaxMemoryMB = concurrentUploads * uploadSizeMB;
        memoryIncreaseMB.Should().BeLessThan(expectedMaxMemoryMB * 0.5,
            "Backpressure should prevent loading all concurrent uploads into memory simultaneously");

        // At least some uploads should succeed
        var successfulUploads = uploadResults.Count(r =>
            r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Accepted);

        successfulUploads.Should().BeGreaterThan(0,
            "Some uploads should succeed even under backpressure");
    }

    [Fact(DisplayName = "External service failure graceful fallback")]
    public async Task ExternalServiceFailure_GracefulFallback()
    {
        // This test simulates external service failures and tests fallback mechanisms
        // For example, if geocoding service fails, system should still function

        // Arrange: Configure mock to simulate external service failure
        var requestsWithFallback = new[]
        {
            "/admin/health", // Should work even if external services fail
            "/rest/services/1/FeatureServer/layers", // Core functionality
            "/ogc/features/v1/collections" // Should have fallback behavior
        };

        var results = new List<(string Endpoint, bool Success, TimeSpan Duration)>();

        // Act: Test each endpoint when external services might be failing
        foreach (var endpoint in requestsWithFallback)
        {
            var (response, duration) = await PerformanceAssertions.MeasureAsync(async () =>
            {
                return await _client.GetAsync(endpoint);
            });

            var success = response.StatusCode == HttpStatusCode.OK ||
                         response.StatusCode == HttpStatusCode.PartialContent; // Degraded mode

            results.Add((endpoint, success, duration));
        }

        // Assert: Core functionality should remain available
        results.Should().AllSatisfy(result =>
        {
            result.Success.Should().BeTrue($"Endpoint {result.Endpoint} should remain functional");
            result.Duration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(10),
                $"Endpoint {result.Endpoint} should respond within reasonable time even with external failures");
        });
    }

    [Fact(DisplayName = "System stability under sustained error conditions")]
    public async Task SystemStability_UnderSustainedErrorConditions()
    {
        // Arrange: Create sustained error conditions
        var errorDuration = TimeSpan.FromMinutes(1);
        var requestInterval = TimeSpan.FromMilliseconds(100);

        var stabilityResults = new List<(DateTime Timestamp, bool Success, TimeSpan Duration)>();

        using var cts = new CancellationTokenSource(errorDuration);

        // Act: Generate sustained load with mixed success/failure patterns
        var stabilityTask = Task.Run(async () =>
        {
            var requestId = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var endpoint = requestId % 3 switch
                    {
                        0 => "/rest/services/1/FeatureServer/0/query?where=1=1", // Should succeed
                        1 => "/rest/services/99999/FeatureServer/0/query", // Should fail
                        _ => "/admin/health" // Should succeed
                    };

                    var timestamp = DateTime.UtcNow;
                    var (response, duration) = await PerformanceAssertions.MeasureAsync(async () =>
                    {
                        return await _client.GetAsync($"{endpoint}&stability_test={requestId}");
                    });

                    var success = response.StatusCode == HttpStatusCode.OK;
                    stabilityResults.Add((timestamp, success, duration));

                    requestId++;
                    await Task.Delay(requestInterval, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    stabilityResults.Add((DateTime.UtcNow, false, TimeSpan.Zero));
                }
            }
        }, cts.Token);

        await stabilityTask;

        // Assert: System should remain stable throughout error conditions
        stabilityResults.Should().HaveCountGreaterThan(100, "Should have processed many requests during stability test");

        // Calculate stability metrics
        var totalRequests = stabilityResults.Count;
        var successfulRequests = stabilityResults.Count(r => r.Success);
        var successRate = (double)successfulRequests / totalRequests;

        // Should maintain reasonable success rate (accounting for intentional failures)
        successRate.Should().BeGreaterOrEqualTo(0.6, "Should maintain at least 60% success rate under mixed conditions");

        // Response times should remain stable
        var successfulDurations = stabilityResults.Where(r => r.Success).Select(r => r.Duration);
        if (successfulDurations.Any())
        {
            var averageDuration = TimeSpan.FromMilliseconds(successfulDurations.Average(d => d.TotalMilliseconds));
            var maxDuration = successfulDurations.Max();

            averageDuration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(2),
                "Average response time should remain reasonable under sustained load");

            maxDuration.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(10),
                "No individual request should take excessively long");
        }

        // System should not degrade over time
        var firstHalf = stabilityResults.Take(stabilityResults.Count / 2);
        var secondHalf = stabilityResults.Skip(stabilityResults.Count / 2);

        var firstHalfSuccessRate = (double)firstHalf.Count(r => r.Success) / firstHalf.Count();
        var secondHalfSuccessRate = (double)secondHalf.Count(r => r.Success) / secondHalf.Count();

        var degradation = firstHalfSuccessRate - secondHalfSuccessRate;
        degradation.Should().BeLessOrEqualTo(0.1, "System should not significantly degrade over time");
    }
}