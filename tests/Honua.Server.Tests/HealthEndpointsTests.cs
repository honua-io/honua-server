// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for health check endpoints
/// </summary>
public class HealthEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public HealthEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessProbe_ReturnsHealthy()
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
    [Endpoint("GET /healthz/ready")]
    public async Task ReadinessProbe_ReturnsHealthy()
    {
        // Act
        var response = await _client.GetAsync("/healthz/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.ToString().Should().Be("text/plain; charset=utf-8");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Ready");
    }

    [IntegrationTest]
    [Endpoint("POST /healthz/live")]
    public async Task LivenessProbe_PostMethod_Returns405()
    {
        // Act
        var response = await _client.PostAsync("/healthz/live", new StringContent(""));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Endpoint("POST /healthz/ready")]
    public async Task ReadinessProbe_PostMethod_Returns405()
    {
        // Act
        var response = await _client.PostAsync("/healthz/ready", new StringContent(""));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
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