// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Streaming)]
public sealed class AdminLiveEndpointTests
{
    private const string AdminPassword = "admin-live-test-password";

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task GetLive_Anonymous_ReturnsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/admin/live");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task GetLive_UnknownTopic_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        using var response = await client.GetAsync("/api/v1/admin/live?topics=secrets");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task GetLive_WrongFamilyCursor_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        client.DefaultRequestHeaders.Add("Last-Event-ID", "features:1");
        using var response = await client.GetAsync("/api/v1/admin/live?topics=proposals");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static WebApplicationFactory<Program> CreateFactory()
        => new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["HONUA_ADMIN_PASSWORD"] = AdminPassword }));
        });
}
