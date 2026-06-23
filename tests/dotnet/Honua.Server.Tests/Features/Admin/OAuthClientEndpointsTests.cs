// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the first-class OAuth2 client-registry and scope-catalogue
/// admin endpoints (ADR-0053 Increment 2, #1888): client CRUD, scope catalogue
/// CRUD, secret returned only at create (never on list), and admin authorization
/// required on the management surface.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class OAuthClientEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "oauth-client-admin-bootstrap-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public OAuthClientEndpointsTests()
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
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oauth-clients")]
    [Endpoint("GET /api/v1/admin/oauth-clients")]
    public async Task RegisterAndListClient_ReturnsSecretOnlyAtCreation()
    {
        var created = await CreateClientAsync("etl-worker", ["client_credentials"], ["features:read"]);

        Assert.StartsWith("client_", created.Client.ClientId, StringComparison.Ordinal);
        Assert.StartsWith("secret_", created.ClientSecret!, StringComparison.Ordinal);
        Assert.Equal("confidential", created.Client.ClientType);

        var response = await _client.GetAsync("/api/v1/admin/oauth-clients");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        // The secret is returned exactly once (at create); it must never appear in list.
        Assert.DoesNotContain(created.ClientSecret!, json, StringComparison.Ordinal);

        var list = JsonSerializer.Deserialize<ApiResponse<OAuthClientResponse[]>>(json, _jsonOptions);
        Assert.NotNull(list?.Data);
        var listed = Assert.Single(list.Data, client => client.Id == created.Client.Id);
        Assert.Equal("active", listed.Status);
        Assert.Contains("features:read", listed.AllowedScopes);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oauth-clients/{id}")]
    [Endpoint("DELETE /api/v1/admin/oauth-clients/{id}")]
    public async Task GetAndDeleteClient_RemovesRegistration()
    {
        var created = await CreateClientAsync("deletable", ["client_credentials"], []);

        var get = await _client.GetAsync($"/api/v1/admin/oauth-clients/{created.Client.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var delete = await _client.DeleteAsync($"/api/v1/admin/oauth-clients/{created.Client.Id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var afterDelete = await _client.GetAsync($"/api/v1/admin/oauth-clients/{created.Client.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/oauth-scopes")]
    [Endpoint("GET /api/v1/admin/oauth-scopes")]
    [Endpoint("DELETE /api/v1/admin/oauth-scopes/{scope}")]
    public async Task DefineListDeleteScope_RoundTrips()
    {
        var define = await _client.PutAsJsonAsync(
            "/api/v1/admin/oauth-scopes",
            new DefineOAuthScopeRequest
            {
                Scope = "features:read",
                Description = "Read features",
                Permissions = ["services:read"],
            },
            _jsonOptions);
        Assert.Equal(HttpStatusCode.OK, define.StatusCode);

        var list = await _client.GetFromJsonAsync<ApiResponse<OAuthScopeResponse[]>>(
            "/api/v1/admin/oauth-scopes", _jsonOptions);
        Assert.NotNull(list?.Data);
        var scope = Assert.Single(list.Data, s => s.Scope == "features:read");
        Assert.Contains("services:read", scope.Permissions);

        var delete = await _client.DeleteAsync("/api/v1/admin/oauth-scopes/features:read");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oauth-clients")]
    public async Task RegisterClient_WithInvalidClientType_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/oauth-clients",
            new CreateOAuthClientRequest
            {
                Name = "bad-type",
                ClientType = "machine",
            },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oauth-clients")]
    public async Task ManagementSurface_RequiresAdminAuthorization()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/admin/oauth-clients");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<OAuthClientSecretResponse> CreateClientAsync(
        string name,
        IReadOnlyList<string> grantTypes,
        IReadOnlyList<string> scopes)
    {
        var request = new CreateOAuthClientRequest
        {
            Name = name,
            ClientType = "confidential",
            AllowedGrantTypes = grantTypes,
            AllowedScopes = scopes,
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/oauth-clients", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = JsonSerializer.Deserialize<ApiResponse<OAuthClientSecretResponse>>(
            await response.Content.ReadAsStringAsync(),
            _jsonOptions);

        Assert.NotNull(result?.Data);
        return result.Data;
    }
}
