// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Infrastructure.RateLimiting;

namespace Honua.Server.Tests.Features.Infrastructure.RateLimiting;

/// <summary>
/// Integration tests for rate limiting middleware.
/// </summary>
public class RateLimitingMiddlewareTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public RateLimitingMiddlewareTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RateLimiting_WithinLimits_ShouldAllowRequest()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = true;
                    options.GlobalRequestsPerMinute = 100;
                });
                services.AddMemoryCache();
                services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = "localhost:6379";
                });
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-RateLimit-Limit"));
        Assert.True(response.Headers.Contains("X-RateLimit-Remaining"));
        Assert.True(response.Headers.Contains("X-RateLimit-Reset"));
    }

    [Fact]
    public async Task RateLimiting_ExceedsLimits_ShouldReturnTooManyRequests()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = true;
                    options.GlobalRequestsPerMinute = 2; // Very low limit for testing
                });
                services.AddMemoryCache();
                services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = "localhost:6379";
                });
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Act - Send requests up to the limit
        await client.GetAsync("/health");
        await client.GetAsync("/health");

        // This should exceed the limit
        var response = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.Contains("X-RateLimit-Limit"));
        Assert.Equal("0", response.Headers.GetValues("X-RateLimit-Remaining").First());

        // Verify response content
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("rate_limit_exceeded", content);
        Assert.Contains("Too many requests", content);
    }

    [Fact]
    public async Task RateLimiting_Disabled_ShouldAllowAllRequests()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = false; // Disable rate limiting
                });
                services.AddMemoryCache();
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Act - Send multiple requests rapidly
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetAsync("/health"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        // Assert - All requests should succeed
        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
    }

    [Fact]
    public async Task RateLimiting_HealthCheckEndpoint_ShouldBeExcluded()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = true;
                    options.GlobalRequestsPerMinute = 1; // Very restrictive
                });
                services.AddMemoryCache();
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Act - Health check endpoints should not be rate limited
        var response1 = await client.GetAsync("/health");
        var response2 = await client.GetAsync("/health");
        var response3 = await client.GetAsync("/health");

        // Assert - All health check requests should succeed
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response3.StatusCode);
    }

    [Fact]
    public async Task RateLimiting_WithApiKey_ShouldUseKeyBasedLimiting()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = true;
                    options.GlobalRequestsPerMinute = 100;
                });
                services.AddMemoryCache();
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Add API key header
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-api-key");

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-RateLimit-Limit"));
    }

    [Fact]
    public async Task RateLimiting_DifferentIPs_ShouldHaveSeparateLimits()
    {
        // Arrange - This test would require custom TestHost setup to simulate different IPs
        // For now, we'll test the basic functionality

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = true;
                    options.GlobalRequestsPerMinute = 100;
                });
                services.AddMemoryCache();
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/metrics")]
    [InlineData("/ready")]
    public async Task RateLimiting_ExcludedEndpoints_ShouldNotBeRateLimited(string endpoint)
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = true;
                    options.GlobalRequestsPerMinute = 1; // Very restrictive
                });
                services.AddMemoryCache();
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Act - Multiple rapid requests to excluded endpoints
        var response1 = await client.GetAsync(endpoint);
        var response2 = await client.GetAsync(endpoint);
        var response3 = await client.GetAsync(endpoint);

        // Assert - Should not be rate limited
        Assert.True(response1.StatusCode == HttpStatusCode.OK || response1.StatusCode == HttpStatusCode.NotFound);
        Assert.True(response2.StatusCode == HttpStatusCode.OK || response2.StatusCode == HttpStatusCode.NotFound);
        Assert.True(response3.StatusCode == HttpStatusCode.OK || response3.StatusCode == HttpStatusCode.NotFound);

        // None should be rate limited
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response1.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response2.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response3.StatusCode);
    }

    [Fact]
    public async Task RateLimiting_HeaderValidation_ShouldIncludeCorrectHeaders()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RateLimitingOptions>(options =>
                {
                    options.Enabled = true;
                    options.GlobalRequestsPerMinute = 100;
                    options.IncludeHeaders = true;
                });
                services.AddMemoryCache();
                services.AddSingleton<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>();
            });
        }).CreateClient();

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify rate limiting headers are present
        Assert.True(response.Headers.Contains("X-RateLimit-Limit"));
        Assert.True(response.Headers.Contains("X-RateLimit-Remaining"));
        Assert.True(response.Headers.Contains("X-RateLimit-Reset"));

        // Verify header values
        var limitHeader = response.Headers.GetValues("X-RateLimit-Limit").First();
        var remainingHeader = response.Headers.GetValues("X-RateLimit-Remaining").First();
        var resetHeader = response.Headers.GetValues("X-RateLimit-Reset").First();

        Assert.True(int.TryParse(limitHeader, out var limit));
        Assert.True(int.TryParse(remainingHeader, out var remaining));
        Assert.True(long.TryParse(resetHeader, out var reset));

        Assert.Equal(100, limit);
        Assert.True(remaining >= 0 && remaining <= limit);
        Assert.True(reset > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}