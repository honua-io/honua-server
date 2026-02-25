// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class HostValidationMiddlewareTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostValidation:Enabled"] = "true",
                ["Public:BaseUrl"] = "https://api.honua.test"
            })));

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task Request_WithConfiguredPublicHost_AllowsRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = "api.honua.test";

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task Request_WithForgedHostHeader_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = "attacker.example";

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid Host header");
    }
}
