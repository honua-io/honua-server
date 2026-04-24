// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests confirming the MVP runtime does not expose application rate-limit admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class RateLimitEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "ratelimit-admin-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public RateLimitEndpointsTests()
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
    [Operation(Operations.Security)]
    [Endpoint("GET /api/v1/admin/rate-limits")]
    public async Task GetRateLimitPolicies_MvpRuntime_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/rate-limits");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
