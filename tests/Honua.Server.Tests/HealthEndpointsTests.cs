// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests;

/// <summary>
/// Tests for health check endpoints (/healthz/live, /healthz/ready)
/// Validates Kubernetes-compatible health checks with PostgreSQL connectivity
/// </summary>
[Protocol("Infrastructure")]
public sealed class HealthEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public HealthEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [IntegrationTest]
    [Operation("HealthCheck")]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessProbe_Always_Returns200AndHealthy()
    {
        // Act
        var response = await _client.GetAsync("/healthz/live");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.ToString().Should().Be("text/plain; charset=utf-8");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Healthy");
    }

    [IntegrationTest]
    [Operation("HealthCheck")]
    [Endpoint("GET /healthz/ready")]
    public async Task ReadinessProbe_WithHealthyDatabase_Returns200AndReady()
    {
        // Arrange
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the real health checker with a healthy mock
                services.AddScoped<IDatabaseHealthChecker>(_ => new MockHealthyDatabaseChecker());
            });
        });

        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/healthz/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.ToString().Should().Be("text/plain; charset=utf-8");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Ready");
    }

    [IntegrationTest]
    [Operation("HealthCheck")]
    [Endpoint("GET /healthz/ready")]
    public async Task ReadinessProbe_WithUnhealthyDatabase_Returns503()
    {
        // Arrange
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the real health checker with an unhealthy mock
                services.AddScoped<IDatabaseHealthChecker>(_ => new MockUnhealthyDatabaseChecker());
            });
        });

        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/healthz/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.ToString().Should().Be("text/plain; charset=utf-8");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Not Ready - Database unavailable");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [Operation("HealthCheck")]
    [Endpoint("* /healthz/live")]
    public async Task LivenessProbe_WithNonGetMethod_Returns405(string method)
    {
        // Arrange
        using var request = new HttpRequestMessage(new HttpMethod(method), "/healthz/live");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [Operation("HealthCheck")]
    [Endpoint("* /healthz/ready")]
    public async Task ReadinessProbe_WithNonGetMethod_Returns405(string method)
    {
        // Arrange
        using var request = new HttpRequestMessage(new HttpMethod(method), "/healthz/ready");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation("HealthCheck")]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessProbe_ResponseTime_IsUnder200Ms()
    {
        // Arrange
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var response = await _client.GetAsync("/healthz/live");
        stopwatch.Stop();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stopwatch.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(200,
            "liveness probe should respond within 200ms");
    }

    [IntegrationTest]
    [Operation("HealthCheck")]
    [Endpoint("GET /healthz/ready")]
    public async Task ReadinessProbe_WithHealthyDatabase_ResponseTime_IsUnder200Ms()
    {
        // Arrange
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace with fast healthy mock
                services.AddScoped<IDatabaseHealthChecker>(_ => new MockHealthyDatabaseChecker());
            });
        });

        using var client = factory.CreateClient();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var response = await client.GetAsync("/healthz/ready");
        stopwatch.Stop();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stopwatch.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(200,
            "readiness probe should respond within 200ms with healthy database");
    }

    [IntegrationTest]
    [Operation("HealthCheck")]
    public async Task HealthEndpoints_AreRegistered()
    {
        // Test that endpoints are properly registered by checking they don't return 404
        var liveResponse = await _client.GetAsync("/healthz/live");
        var readyResponse = await _client.GetAsync("/healthz/ready");

        // Assert
        liveResponse.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        readyResponse.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

}

/// <summary>
/// Mock implementation that always returns healthy
/// </summary>
internal sealed class MockHealthyDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}

/// <summary>
/// Mock implementation that always returns unhealthy
/// </summary>
internal sealed class MockUnhealthyDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
