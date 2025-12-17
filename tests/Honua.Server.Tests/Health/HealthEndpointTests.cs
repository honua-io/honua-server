// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Xunit;

namespace Honua.Server.Tests.Health;

/// <summary>
/// Integration tests for health check endpoints.
/// Validates liveness and readiness probes for Kubernetes deployments.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Health)]
public class HealthEndpointTests : IAsyncLifetime
{
    private WebAppFixture _fixture = null!;

    public HealthEndpointTests(PostgresFixture postgres)
    {
        // PostgresFixture is injected by xUnit's ICollectionFixture
        // but not directly used in this test class since WebAppFixture manages it
        _ = postgres;
    }

    public async Task InitializeAsync()
    {
        _fixture = new WebAppFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.LivenessCheck)]
    [Endpoint("GET /healthz/live")]
    public async Task LivenessCheck_ReturnsOk()
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
    public async Task ReadinessCheck_ReturnsOk()
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
    public async Task LivenessCheck_MultipleRequests_AllSucceed()
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
