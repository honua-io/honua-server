// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Integration tests for Rate Limiting Middleware ensuring proper protection against abuse.
/// </summary>
[Collection("Database")]
public class RateLimitingMiddlewareTests : IAsyncLifetime
{
    private const string RateLimitedEndpoint = "/ogc/features";
    private const string HealthEndpoint = "/healthz/live";
    private const string ExceedsIp = "203.0.113.10";
    private const string HeaderIp = "203.0.113.11";
    private const string HealthIp = "203.0.113.12";
    private const string RetryAfterIp = "203.0.113.13";

    private readonly ITestOutputHelper _output;
    private readonly WebAppFixture _fixture;

    public RateLimitingMiddlewareTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture = new WebAppFixture(
            builder =>
            {
                // Configure rate limiting for testing
                builder.UseSetting("RateLimit:MaxRequestsPerWindow", "5");
                builder.UseSetting("RateLimit:WindowSize", "00:01:00"); // 1 minute window
                builder.UseEnvironment(Environments.Production); // Enable rate limiting
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendRequestAsync(string endpoint, string ipAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ipAddress);
        return await _fixture.Client.SendAsync(request);
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task RateLimit_ExceedsMaxRequests_Returns429()
    {
        // Act - Make more requests than allowed
        var tasks = new List<Task<HttpResponseMessage>>();

        for (int i = 0; i < 7; i++) // Exceed the limit of 5
        {
            tasks.Add(SendRequestAsync(RateLimitedEndpoint, ExceedsIp));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert - Some requests should be rate limited
        var rateLimitedResponses = responses.Where(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests);
        var successfulResponses = responses.Where(r => r.StatusCode == System.Net.HttpStatusCode.OK);

        Assert.True(rateLimitedResponses.Any(), "Should have rate-limited responses");
        Assert.Equal(5, successfulResponses.Count()); // Should allow exactly 5 requests
        Assert.Equal(2, rateLimitedResponses.Count()); // Should reject 2 requests

        _output.WriteLine($"Successful: {successfulResponses.Count()}, Rate Limited: {rateLimitedResponses.Count()}");
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task RateLimit_Response_ContainsProperHeaders()
    {
        // Act
        var response = await SendRequestAsync(RateLimitedEndpoint, HeaderIp);

        // Assert - Rate limit headers should be present
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.Contains("X-RateLimit-Limit"), "Missing rate limit header");
        Assert.True(response.Headers.Contains("X-RateLimit-Remaining"), "Missing remaining requests header");
        Assert.True(response.Headers.Contains("X-RateLimit-Reset"), "Missing reset time header");

        var limitHeader = response.Headers.GetValues("X-RateLimit-Limit").First();
        Assert.Equal("5", limitHeader);

        _output.WriteLine($"Rate Limit Headers: Limit={limitHeader}");
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task RateLimit_HealthEndpoint_IsExempt()
    {
        // Act - Health endpoints should be exempt from rate limiting
        var tasks = new List<Task<HttpResponseMessage>>();

        for (int i = 0; i < 10; i++) // Well over the limit
        {
            tasks.Add(SendRequestAsync(HealthEndpoint, HealthIp));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert - All health check requests should succeed (exempted from rate limiting)
        var successCount = responses.Count(r => r.StatusCode == System.Net.HttpStatusCode.OK);
        Assert.Equal(10, successCount); // All should succeed

        _output.WriteLine($"All {successCount} health check requests succeeded (exempted from rate limiting)");
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task RateLimit_429Response_ContainsRetryAfterHeader()
    {
        // Arrange - First, exhaust the rate limit
        for (int i = 0; i < 5; i++)
        {
            await SendRequestAsync(RateLimitedEndpoint, RetryAfterIp);
        }

        // Act - Make one more request to trigger rate limiting
        var response = await SendRequestAsync(RateLimitedEndpoint, RetryAfterIp);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"), "Missing Retry-After header");

        var retryAfter = response.Headers.GetValues("Retry-After").First();
        Assert.True(int.Parse(retryAfter, CultureInfo.InvariantCulture) > 0, "Retry-After should be positive");

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Rate limit exceeded", content);

        _output.WriteLine($"Rate limit response: {response.StatusCode}, Retry-After: {retryAfter}");
    }
}

/// <summary>
/// Security test attribute for categorizing security-related tests.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SecurityTestAttribute : Attribute
{
}
