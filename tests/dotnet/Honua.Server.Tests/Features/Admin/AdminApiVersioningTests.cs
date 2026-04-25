// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class AdminApiVersioningTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-api-versioning-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public AdminApiVersioningTests()
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
    [Endpoint("GET /api/v1/admin/license")]
    public async Task GetAdminEndpoint_WithUnpublishedApiVersion_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v2/admin/license");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
