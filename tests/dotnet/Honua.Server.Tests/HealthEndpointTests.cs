// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for health endpoints.
/// </summary>
[Protocol(TestProtocols.Health)]
[Collection("Database.CoreEndpoints")]
public sealed class HealthEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessEndpoint_ReturnsHealthy()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/healthz/live");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Healthy");
    }

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/ready")]
    public async Task ReadinessEndpoint_ReturnsReady()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/healthz/ready");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Ready");
    }

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessEndpoint_MultipleRequests_AllSucceed()
    {
        // Arrange
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _fixture.Client.GetAsync("/healthz/live"))
            .ToArray();

        // Act
        var responses = await Task.WhenAll(tasks);

        // Assert
        responses.Should().AllSatisfy(r => r.Be200Ok());
    }
}
