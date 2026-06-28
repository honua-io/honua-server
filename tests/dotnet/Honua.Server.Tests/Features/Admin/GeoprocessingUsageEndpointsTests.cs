// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ProcessDiscovery)]
public sealed class GeoprocessingUsageEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "geoprocessing-usage-admin-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public GeoprocessingUsageEndpointsTests()
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
    [Endpoint("GET /api/v1/admin/geoprocessing/tools/usage-ranking")]
    public async Task GetUsageRanking_AfterRecordingInvocations_RanksMostInvokedFirst()
    {
        // The endpoint reads the real singleton telemetry store the dispatcher
        // records into, so recording here is reflected on the next read (#2144).
        var telemetry = _fixture.Services.GetRequiredService<IProcessUsageTelemetry>();
        var popular = $"popular-tool-{Guid.NewGuid():N}";
        var rare = $"rare-tool-{Guid.NewGuid():N}";

        telemetry.RecordInvocation(rare, succeeded: true);
        for (var i = 0; i < 3; i++)
        {
            telemetry.RecordInvocation(popular, succeeded: i % 2 == 0);
        }

        var response = await _client.GetAsync("/api/v1/admin/geoprocessing/tools/usage-ranking");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = json.RootElement.GetProperty("data");
        data.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(2);

        var tools = data.GetProperty("tools").EnumerateArray().ToArray();
        var popularEntry = tools.Single(t => t.GetProperty("processId").GetString() == popular);
        var rareEntry = tools.Single(t => t.GetProperty("processId").GetString() == rare);

        popularEntry.GetProperty("invocationCount").GetInt64().Should().Be(3);
        popularEntry.GetProperty("successCount").GetInt64().Should().Be(2);
        popularEntry.GetProperty("failureCount").GetInt64().Should().Be(1);
        rareEntry.GetProperty("invocationCount").GetInt64().Should().Be(1);

        // The more-invoked tool must rank ahead of (a lower rank number than) the rarer one.
        popularEntry.GetProperty("rank").GetInt32().Should()
            .BeLessThan(rareEntry.GetProperty("rank").GetInt32());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/geoprocessing/tools/usage-ranking")]
    public async Task GetUsageRanking_WithLimit_TruncatesToRequestedCount()
    {
        var telemetry = _fixture.Services.GetRequiredService<IProcessUsageTelemetry>();
        for (var i = 0; i < 4; i++)
        {
            telemetry.RecordInvocation($"limit-tool-{Guid.NewGuid():N}", succeeded: true);
        }

        var response = await _client.GetAsync("/api/v1/admin/geoprocessing/tools/usage-ranking?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("data").GetProperty("count").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("data").GetProperty("tools").GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/geoprocessing/tools/usage-ranking")]
    public async Task GetUsageRanking_WithNonPositiveLimit_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/admin/geoprocessing/tools/usage-ranking?limit=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/geoprocessing/tools/usage-ranking")]
    public async Task GetUsageRanking_WithoutAdminAuth_IsUnauthorized()
    {
        using var anonymousClient = _fixture.CreateClient();

        var response = await anonymousClient.GetAsync("/api/v1/admin/geoprocessing/tools/usage-ranking");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
