// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

// Settings history and one-step undo on the rate-limit and OIDC provider admin surfaces.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class SettingsRevisionEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "settings-revision-admin-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public SettingsRevisionEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/{id}/revisions")]
    [Endpoint("POST /api/v1/admin/rate-limits/{id}/undo")]
    public async Task RateLimitUndo_AfterBadUpdate_RestoresThePreviousPolicy()
    {
        var policyId = await CreatePolicyAsync(1000);
        (await _client.PutAsJsonAsync($"/api/v1/admin/rate-limits/{policyId}", new { requestsPerWindow = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var revisions = await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}/revisions"));
        revisions.GetArrayLength().Should().Be(2);
        revisions[0].GetProperty("change").GetString().Should().Be("updated");
        revisions[0].TryGetProperty("payload", out _).Should().BeFalse();
        var bad = revisions[0].GetProperty("revisionId").GetInt64();

        var undo = await _client.PostAsJsonAsync($"/api/v1/admin/rate-limits/{policyId}/undo", new { expectedRevisionId = bad });

        var restored = await ReadDataAsync(undo);
        restored.GetProperty("restoredFrom").GetInt64().Should().Be(bad);
        restored.GetProperty("revisionId").GetInt64().Should().BeGreaterThan(bad);
        var policy = await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}"));
        policy.GetProperty("requestsPerWindow").GetInt32().Should().Be(1000);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/rate-limits/{id}/undo")]
    public async Task RateLimitUndo_StaleOrMissingExpectedRevision_IsRefused()
    {
        var policyId = await CreatePolicyAsync(1000);
        var created = (await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}/revisions")))[0]
            .GetProperty("revisionId").GetInt64();
        (await _client.PutAsJsonAsync($"/api/v1/admin/rate-limits/{policyId}", new { requestsPerWindow = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync($"/api/v1/admin/rate-limits/{policyId}/undo", new { }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"/api/v1/admin/rate-limits/{policyId}/undo", new { expectedRevisionId = created }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var policy = await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}"));
        policy.GetProperty("requestsPerWindow").GetInt32().Should().Be(5);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/rate-limits/{id}/undo")]
    public async Task RateLimitUndo_OfDelete_RestoresThePolicy()
    {
        var policyId = await CreatePolicyAsync(700);
        (await _client.DeleteAsync($"/api/v1/admin/rate-limits/{policyId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var deleted = (await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}/revisions")))[0]
            .GetProperty("revisionId").GetInt64();

        var undo = await _client.PostAsJsonAsync($"/api/v1/admin/rate-limits/{policyId}/undo", new { expectedRevisionId = deleted });

        (await ReadDataAsync(undo)).GetProperty("change").GetString().Should().Be("created");
        var policy = await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/rate-limits/{policyId}"));
        policy.GetProperty("requestsPerWindow").GetInt32().Should().Be(700);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/rate-limits/{id}/revisions")]
    public async Task RateLimitRevisions_UnknownPolicy_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/rate-limits/{Guid.NewGuid()}/revisions");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers/{id}/revisions")]
    [Endpoint("POST /api/v1/admin/oidc/providers/{id}/undo")]
    public async Task OidcUndo_AfterDisable_ReenablesTheProvider()
    {
        var providerId = await CreateProviderAsync();
        (await _client.PutAsJsonAsync($"/api/v1/admin/oidc/providers/{providerId}", new { enabled = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var revisions = await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/oidc/providers/{providerId}/revisions"));
        var disabled = revisions[0].GetProperty("revisionId").GetInt64();

        var undo = await _client.PostAsJsonAsync($"/api/v1/admin/oidc/providers/{providerId}/undo", new { expectedRevisionId = disabled });

        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        var provider = await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/oidc/providers/{providerId}"));
        provider.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers/{id}/undo")]
    public async Task OidcUndo_OfDelete_DoesNotRecreateTheProvider()
    {
        var providerId = await CreateProviderAsync();
        (await _client.DeleteAsync($"/api/v1/admin/oidc/providers/{providerId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var deleted = (await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/oidc/providers/{providerId}/revisions")))[0]
            .GetProperty("revisionId").GetInt64();

        var undo = await _client.PostAsJsonAsync($"/api/v1/admin/oidc/providers/{providerId}/undo", new { expectedRevisionId = deleted });

        undo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.GetAsync($"/api/v1/admin/oidc/providers/{providerId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers/{id}/undo")]
    public async Task OidcUndo_AgentAdministrator_IsForbidden()
    {
        var providerId = await CreateProviderAsync();
        (await _client.PutAsJsonAsync($"/api/v1/admin/oidc/providers/{providerId}", new { enabled = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var disabled = (await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/oidc/providers/{providerId}/revisions")))[0]
            .GetProperty("revisionId").GetInt64();
        var key = await _fixture.Services.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            "settings-agent", ["admin", "agent"], null, "test", CancellationToken.None);
        using var agent = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key.Key));

        var undo = await agent.PostAsJsonAsync($"/api/v1/admin/oidc/providers/{providerId}/undo", new { expectedRevisionId = disabled });

        undo.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var provider = await ReadDataAsync(await _client.GetAsync($"/api/v1/admin/oidc/providers/{providerId}"));
        provider.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    private async Task<Guid> CreatePolicyAsync(int requestsPerWindow)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/rate-limits", new
        {
            name = $"revision-policy-{Guid.NewGuid():N}",
            scope = "tenant",
            key = $"tenant-{Guid.NewGuid():N}",
            requestsPerWindow,
            windowDurationSeconds = 60d,
            enabled = true,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadDataAsync(response)).GetProperty("policyId").GetGuid();
    }

    private async Task<Guid> CreateProviderAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/oidc/providers", new
        {
            name = $"revision-provider-{Guid.NewGuid():N}",
            providerType = "Generic",
            authority = "https://auth.example.com",
            clientId = "revision-client",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadDataAsync(response)).GetProperty("providerId").GetGuid();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }
}
