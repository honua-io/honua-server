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
        // Record a uniquely named invocation so the ranking the admin principal can read is
        // non-empty. The denial must therefore disclose nothing, not merely refuse an empty
        // surface; the authenticated-success cases above pin the same endpoint's real result.
        var telemetry = _fixture.Services.GetRequiredService<IProcessUsageTelemetry>();
        var privateTool = $"denial-probe-tool-{Guid.NewGuid():N}";
        telemetry.RecordInvocation(privateTool, succeeded: true);

        using var anonymousClient = _fixture.CreateClient();

        var response = await anonymousClient.GetAsync("/api/v1/admin/geoprocessing/tools/usage-ranking");

        // The admin policy names the ApiKey scheme, so an unauthenticated caller is challenged:
        // exactly 401, never 403 (which would mean the caller was authenticated but unprivileged).
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Nothing is disclosed: no ranking envelope and no recorded tool name.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(privateTool);
        body.Should().NotContain("\"tools\"");
        body.Should().NotContain("\"invocationCount\"");

        // And the admin principal still sees the recorded invocation, so the denial above is a
        // refusal of the caller and not an endpoint that is broken for everyone.
        var adminResponse = await _client.GetAsync("/api/v1/admin/geoprocessing/tools/usage-ranking?limit=1000");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await adminResponse.Content.ReadAsStringAsync()).Should().Contain(privateTool);
    }
}
