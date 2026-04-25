// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for OIDC provider admin endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public class OidcProviderEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "oidc-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public OidcProviderEndpointsTests()
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
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task ListProviders_Empty_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/admin/oidc/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse[]>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task CreateProvider_ValidRequest_ReturnsCreated()
    {
        var request = new CreateOidcProviderRequest
        {
            Name = "Test Okta",
            ProviderType = "Okta",
            Authority = "https://dev-12345.okta.com",
            ClientId = "test-client-id",
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/oidc/providers", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse>>(json, _jsonOptions);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Test Okta", result.Data.Name);
        Assert.Equal("Okta", result.Data.ProviderType);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers/{id}")]
    public async Task GetProvider_ExistingId_ReturnsProvider()
    {
        // Create a provider first
        var createRequest = new CreateOidcProviderRequest
        {
            Name = "Get Test",
            ProviderType = "Generic",
            Authority = "https://auth.example.com",
            ClientId = "client-1",
        };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/oidc/providers", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Get by ID
        var response = await _client.GetAsync($"/api/v1/admin/oidc/providers/{created!.Data!.ProviderId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse>>(json, _jsonOptions);
        Assert.NotNull(result?.Data);
        Assert.Equal("Get Test", result.Data.Name);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers/{id}")]
    public async Task GetProvider_NonExistentId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/oidc/providers/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/oidc/providers/{id}")]
    public async Task UpdateProvider_ValidRequest_ReturnsUpdated()
    {
        // Create first
        var createRequest = new CreateOidcProviderRequest
        {
            Name = "Update Test",
            ProviderType = "AzureAd",
            Authority = "https://login.microsoftonline.com/tenant",
            ClientId = "client-2",
        };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/oidc/providers", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Update
        var updateRequest = new UpdateOidcProviderRequest { Name = "Updated Name" };
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/admin/oidc/providers/{created!.Data!.ProviderId}", updateRequest, _jsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse>>(json, _jsonOptions);
        Assert.Equal("Updated Name", result?.Data?.Name);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/oidc/providers/{id}")]
    public async Task DeleteProvider_ExistingId_ReturnsSuccess()
    {
        // Create first
        var createRequest = new CreateOidcProviderRequest
        {
            Name = "Delete Test",
            ProviderType = "Auth0",
            Authority = "https://dev.auth0.com",
            ClientId = "client-3",
        };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/oidc/providers", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Delete
        var response = await _client.DeleteAsync($"/api/v1/admin/oidc/providers/{created!.Data!.ProviderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify gone
        var getResponse = await _client.GetAsync($"/api/v1/admin/oidc/providers/{created.Data.ProviderId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers/{id}/test")]
    public async Task TestProvider_ExistingId_ReturnsTestResult()
    {
        // Create first
        var createRequest = new CreateOidcProviderRequest
        {
            Name = "Connection Test",
            ProviderType = "Generic",
            Authority = "https://auth.example.com",
            ClientId = "client-4",
            Enabled = true,
        };
        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/oidc/providers", createRequest, _jsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<OidcProviderResponse>>(
            await createResponse.Content.ReadAsStringAsync(), _jsonOptions);

        // Test
        var response = await _client.PostAsync($"/api/v1/admin/oidc/providers/{created!.Data!.ProviderId}/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<OidcProviderTestResponse>>(json, _jsonOptions);

        Assert.NotNull(result?.Data);
        Assert.Equal(created.Data.ProviderId, result.Data.ProviderId);
    }
}
