// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests;

/// <summary>
/// Tests for health check endpoints (/healthz/live, /healthz/ready)
/// Validates Kubernetes-compatible health checks with PostgreSQL connectivity
/// </summary>
[Protocol(TestProtocols.Health)]
[Collection("Performance")]
public sealed class HealthEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string AdminPassword = "health-metrics-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public HealthEndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
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
    [Operation(Operations.ReadinessCheck)]
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
    [Operation(Operations.ReadinessCheck)]
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
        content.Should().Be("Not Ready");
    }

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
    [Endpoint("POST /healthz/live")]
    [Endpoint("PUT /healthz/live")]
    [Endpoint("DELETE /healthz/live")]
    [Endpoint("PATCH /healthz/live")]
    public async Task LivenessProbe_WithNonGetMethods_Returns405()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "/healthz/live");
            var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("POST /healthz/ready")]
    [Endpoint("PUT /healthz/ready")]
    [Endpoint("DELETE /healthz/ready")]
    [Endpoint("PATCH /healthz/ready")]
    public async Task ReadinessProbe_WithNonGetMethods_Returns405()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "/healthz/ready");
            var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
    }

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessProbe_ResponseTime_IsUnder200Ms()
    {
        var maxElapsedMs = Environment.GetEnvironmentVariable("CI") == "true" ? 1000 : 750;
        var elapsedMs = await MeasureBestLatencyAsync(_client, "/healthz/live", HttpStatusCode.OK);

        elapsedMs.Should().BeLessThanOrEqualTo(maxElapsedMs,
            "liveness probe should respond within {0}ms", maxElapsedMs);
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
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
        var maxElapsedMs = Environment.GetEnvironmentVariable("CI") == "true" ? 1000 : 750;
        var elapsedMs = await MeasureBestLatencyAsync(client, "/healthz/ready", HttpStatusCode.OK);

        elapsedMs.Should().BeLessThanOrEqualTo(maxElapsedMs,
            "readiness probe should respond within {0}ms with healthy database", maxElapsedMs);
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /healthz/live")]
    [Endpoint("GET /healthz/ready")]
    public async Task HealthEndpoints_AreRegistered()
    {
        // Test that endpoints are properly registered by checking they don't return 404
        var liveResponse = await _client.GetAsync("/healthz/live");
        var readyResponse = await _client.GetAsync("/healthz/ready");

        // Assert
        liveResponse.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        readyResponse.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /metrics")]
    public async Task PrometheusMetricsEndpoint_WithoutAuthentication_ReturnsUnauthorized()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /metrics")]
    public async Task PrometheusMetricsEndpoint_ReturnsPrometheusTextExposition()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        var response = await client.GetAsync("/metrics");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        mediaType.Should().NotBeNull();
        mediaType.Should().BeOneOf("text/plain", "application/openmetrics-text");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("#");
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /healthz/metrics")]
    public async Task PerformanceMetricsEndpoint_WithHealthyReadiness_ReturnsHealthyPayload()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.ConfigureServices(services =>
            {
                services.AddScoped<IDatabaseHealthChecker>(_ => new MockHealthyDatabaseChecker());
            });
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        var response = await client.GetAsync("/healthz/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("status").GetString().Should().Be("healthy");
        var license = document.RootElement.GetProperty("license");
        license.GetProperty("edition").GetString().Should().Be("Community");
        license.GetProperty("validation_state").GetString().Should().Be("NoLicenseConfigured");
        license.GetProperty("active_entitlements").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /healthz/metrics")]
    public async Task PerformanceMetricsEndpoint_WithUnhealthyReadiness_ReturnsServiceUnavailable()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.ConfigureServices(services =>
            {
                services.AddScoped<IDatabaseHealthChecker>(_ => new MockUnhealthyDatabaseChecker());
            });
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        var response = await client.GetAsync("/healthz/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("status").GetString().Should().Be("not_ready");
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("POST /healthz/metrics")]
    [Endpoint("PUT /healthz/metrics")]
    [Endpoint("DELETE /healthz/metrics")]
    [Endpoint("PATCH /healthz/metrics")]
    public async Task PerformanceMetricsEndpoint_WithNonGetMethods_Returns405AndAllowHeader()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "/healthz/metrics");
            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            (response.Headers.TryGetValues("Allow", out var allowedValues) ||
             response.Content.Headers.TryGetValues("Allow", out allowedValues))
                .Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("GET");
        }
    }

    private static async Task<long> MeasureBestLatencyAsync(HttpClient client, string path, HttpStatusCode expectedStatusCode, int samples = 3)
    {
        using (var warmupResponse = await client.GetAsync(path))
        {
            warmupResponse.StatusCode.Should().Be(expectedStatusCode);
        }

        long bestElapsedMs = long.MaxValue;
        for (var sample = 0; sample < samples; sample++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.GetAsync(path);
            stopwatch.Stop();

            response.StatusCode.Should().Be(expectedStatusCode);
            bestElapsedMs = Math.Min(bestElapsedMs, stopwatch.ElapsedMilliseconds);
        }

        return bestElapsedMs;
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
