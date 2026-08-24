// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// HEAD must not become an authentication bypass: an unauthenticated HEAD against an admin
/// endpoint has to fail exactly like the unauthenticated GET does (401), not 200 and not 405
/// (#3389).
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class HeadMethodAuthorizationTests : IAsyncLifetime
{
    private const string AdminPassword = "head-auth-test-key";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });

    private HttpClient _unauthenticatedClient = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _unauthenticatedClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task Head_AdminServicesWithoutApiKey_Returns401LikeGet()
    {
        using var getResponse = await _unauthenticatedClient.GetAsync("/api/v1/admin/services");
        getResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/api/v1/admin/services");
        using var headResponse = await _unauthenticatedClient.SendAsync(headRequest);

        headResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "HEAD is routed to the GET endpoint but authorization still runs (it was 405 before #3389)");
    }
}
