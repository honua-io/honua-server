// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the activated app-level rate-limit policy admin surface (#355).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
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
    [Endpoint("GET /api/v1/admin/rate-limits")]
    public async Task ListPolicies_Activated_ReturnsSuccessEnvelope()
    {
        var response = await _client.GetAsync("/api/v1/admin/rate-limits");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/rate-limits")]
    [Endpoint("GET /api/v1/admin/rate-limits/{id}")]
    [Endpoint("PUT /api/v1/admin/rate-limits/{id}")]
    [Endpoint("DELETE /api/v1/admin/rate-limits/{id}")]
    public async Task PolicyCrud_CreateGetUpdateDelete_CompletesLifecycle()
    {
        var createPayload = new
        {
            name = $"tenant-cap-{Guid.NewGuid():N}",
            scope = "tenant",
            key = $"tenant-{Guid.NewGuid():N}",
            requestsPerWindow = 1000,
            windowDurationSeconds = 60d,
            enabled = true,
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", createPayload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        createDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var policyId = createDocument.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();
        policyId.Should().NotBeEmpty();

        var getResponse = await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()))
        {
            getDocument.RootElement.GetProperty("data").GetProperty("requestsPerWindow").GetInt32().Should().Be(1000);
        }

        var updatePayload = new { requestsPerWindow = 250, enabled = false };
        var updateResponse = await _client.PutAsJsonAsync($"/api/v1/admin/rate-limits/{policyId}", updatePayload);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var updateDocument = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync()))
        {
            var data = updateDocument.RootElement.GetProperty("data");
            data.GetProperty("requestsPerWindow").GetInt32().Should().Be(250);
            data.GetProperty("enabled").GetBoolean().Should().BeFalse();
        }

        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/rate-limits/{policyId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getAfterDelete = await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}");
        getAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/status")]
    public async Task GetStatus_MissingKey_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/rate-limits/status");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/status")]
    public async Task GetStatus_ForKnownPolicyKey_ReturnsStatusEnvelope()
    {
        var key = $"tenant-{Guid.NewGuid():N}";
        var createPayload = new
        {
            name = $"status-policy-{Guid.NewGuid():N}",
            scope = "tenant",
            key,
            requestsPerWindow = 500,
            windowDurationSeconds = 60d,
            enabled = true,
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", createPayload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var statusResponse = await _client.GetAsync($"/api/v1/admin/rate-limits/status?key={Uri.EscapeDataString(key)}");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var statusDocument = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        statusDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        statusDocument.RootElement.GetProperty("data").GetProperty("key").GetString().Should().Be(key);
    }
}
