// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
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
    private const string SourceRevision = "0123456789abcdef0123456789abcdef01234567";

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
                builder.UseSetting("Deployment:Revision", SourceRevision);
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

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/version")]
    public async Task GetVersion_ReturnsExactSourceRevisionSeparateFromReleaseVersion()
    {
        var response = await _client.GetAsync("/api/v1/admin/version");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("sourceRevision").GetString().Should().Be(SourceRevision);
        data.GetProperty("version").GetString().Should().NotBe(SourceRevision);
    }
}
