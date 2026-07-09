// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class OpsAutonomyPolicyEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "ops-autonomy-policy-admin-key";
    private const string Rule = "alert-dispatch-backlog";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public OpsAutonomyPolicyEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOpsAutonomyPolicyStore>();
                services.AddSingleton<InMemoryOpsAutonomyPolicyStore>();
                services.AddSingleton<IOpsAutonomyPolicyStore>(
                    sp => sp.GetRequiredService<InMemoryOpsAutonomyPolicyStore>());
            })
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
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/observability/autonomy/policies/{rule}")]
    [Endpoint("GET /api/v1/admin/observability/autonomy/policies/{rule}")]
    [Endpoint("GET /api/v1/admin/observability/autonomy/policies")]
    [Endpoint("PUT /api/v1/admin/observability/autonomy/settings")]
    [Endpoint("GET /api/v1/admin/observability/autonomy/settings")]
    public async Task PolicyCrudAndSettings_RoundTrip()
    {
        var policyResponse = await _client.PutAsJsonAsync(
            $"/api/v1/admin/observability/autonomy/policies/{Rule}",
            new
            {
                mode = "AutoApply",
                maxAutoActionsPerWindow = 2,
                windowSeconds = 600,
                maxBlastRadius = 4,
                reason = "graduated after clean proposals",
            });

        Assert.Equal(HttpStatusCode.OK, policyResponse.StatusCode);
        using var policyDocument = JsonDocument.Parse(await policyResponse.Content.ReadAsStringAsync());
        var policy = policyDocument.RootElement;
        Assert.Equal(Rule, policy.GetProperty("rule").GetString());
        Assert.Equal("AutoApply", policy.GetProperty("mode").GetString());
        Assert.Equal(2, policy.GetProperty("maxAutoActionsPerWindow").GetInt32());
        Assert.Equal(600, policy.GetProperty("windowSeconds").GetInt32());
        Assert.Equal(4, policy.GetProperty("maxBlastRadius").GetInt32());
        Assert.True(policy.TryGetProperty("trackRecord", out _));

        var getResponse = await _client.GetAsync($"/api/v1/admin/observability/autonomy/policies/{Rule}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal("AutoApply", getDocument.RootElement.GetProperty("mode").GetString());

        var listResponse = await _client.GetAsync("/api/v1/admin/observability/autonomy/policies");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listed = Assert.Single(listDocument.RootElement.GetProperty("policies").EnumerateArray());
        Assert.Equal(Rule, listed.GetProperty("rule").GetString());

        var settingsResponse = await _client.PutAsJsonAsync(
            "/api/v1/admin/observability/autonomy/settings",
            new
            {
                killSwitchEnabled = true,
                reason = "incident freeze",
            });

        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        using var settingsDocument = JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync());
        Assert.True(settingsDocument.RootElement.GetProperty("killSwitchEnabled").GetBoolean());

        var getSettingsResponse = await _client.GetAsync("/api/v1/admin/observability/autonomy/settings");
        Assert.Equal(HttpStatusCode.OK, getSettingsResponse.StatusCode);
        using var getSettingsDocument = JsonDocument.Parse(await getSettingsResponse.Content.ReadAsStringAsync());
        Assert.True(getSettingsDocument.RootElement.GetProperty("killSwitchEnabled").GetBoolean());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/observability/autonomy/policies/{rule}")]
    public async Task PolicyCrud_InvalidMode_ReturnsBadRequest()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/observability/autonomy/policies/{Rule}",
            new { mode = "Autopilot" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
