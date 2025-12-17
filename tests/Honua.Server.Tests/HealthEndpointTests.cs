// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for health endpoints.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HealthEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task LivenessEndpoint_ReturnsHealthy()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/healthz/live");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task ReadinessEndpoint_ReturnsReady()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/healthz/ready");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Ready", content);
    }
}
