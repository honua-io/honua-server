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
/// Integration tests for the tenant lifecycle admin surface (issue #2156): create, list, get,
/// suspend, resume, delete, and the per-tenant billing usage export.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class TenantAdminEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "tenant-admin-key";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public TenantAdminEndpointsTests()
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
    [Endpoint("POST /api/v1/admin/tenants")]
    [Endpoint("GET /api/v1/admin/tenants")]
    [Endpoint("GET /api/v1/admin/tenants/{tenantId}")]
    [Endpoint("POST /api/v1/admin/tenants/{tenantId}/suspend")]
    [Endpoint("POST /api/v1/admin/tenants/{tenantId}/resume")]
    [Endpoint("DELETE /api/v1/admin/tenants/{tenantId}")]
    public async Task TenantLifecycle_CreateUseSuspendResumeDelete_CompletesLifecycle()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var createPayload = new { tenantId, displayName = "Acme Inc", plan = "pro" };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/tenants", createPayload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using (var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()))
        {
            createDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            createDoc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Active");
        }

        var listResponse = await _client.GetAsync("/api/v1/admin/tenants");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()))
        {
            listDoc.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
        }

        var getResponse = await _client.GetAsync($"/api/v1/admin/tenants/{tenantId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var suspendResponse = await _client.PostAsync($"/api/v1/admin/tenants/{tenantId}/suspend", content: null);
        suspendResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var suspendDoc = JsonDocument.Parse(await suspendResponse.Content.ReadAsStringAsync()))
        {
            suspendDoc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Suspended");
        }

        // Suspending an already-suspended tenant is an invalid transition (409).
        var suspendAgain = await _client.PostAsync($"/api/v1/admin/tenants/{tenantId}/suspend", content: null);
        suspendAgain.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var resumeResponse = await _client.PostAsync($"/api/v1/admin/tenants/{tenantId}/resume", content: null);
        resumeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var resumeDoc = JsonDocument.Parse(await resumeResponse.Content.ReadAsStringAsync()))
        {
            resumeDoc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Active");
        }

        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/tenants/{tenantId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var deleteDoc = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync()))
        {
            deleteDoc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Deleted");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tenants")]
    public async Task CreateTenant_Duplicate_ReturnsConflict()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var payload = new { tenantId, displayName = "Dup" };

        (await _client.PostAsJsonAsync("/api/v1/admin/tenants", payload)).StatusCode
            .Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync("/api/v1/admin/tenants", payload)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tenants/usage")]
    public async Task TenantUsage_ReturnsBillingUsageEnvelope()
    {
        var response = await _client.GetAsync("/api/v1/admin/tenants/usage");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("data").GetProperty("records").ValueKind.Should().Be(JsonValueKind.Array);
    }
}
