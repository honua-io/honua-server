// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests;

[Protocol(TestProtocols.Health)]
[Collection("Database.CoreEndpoints")]
public sealed class DeepHealthEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/deep")]
    public async Task DeepHealthEndpoint_DatabaseAndCatalogAvailable_ReturnsUncachedReady()
    {
        using var response = await _fixture.Client.GetAsync("/healthz/deep");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Be("Ready");
    }
}
