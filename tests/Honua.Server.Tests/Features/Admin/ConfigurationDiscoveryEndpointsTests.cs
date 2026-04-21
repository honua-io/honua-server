// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ConfigurationDiscoveryEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "configuration-discovery-admin-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ConfigurationDiscoveryEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/configuration/discover")]
    public async Task DiscoverConfiguration_WithAdminAuth_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/configuration/discover");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
