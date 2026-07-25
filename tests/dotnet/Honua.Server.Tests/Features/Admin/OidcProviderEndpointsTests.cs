// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
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
        _fixture = CreateFixture(HonuaEdition.Enterprise);
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    public async Task ListProviders_CommunityLicense_ReturnsPaymentRequired()
    {
        await using var fixture = CreateFixture(HonuaEdition.Community);
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        var response = await client.GetAsync("/api/v1/admin/oidc/providers");

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Contains("identity.oidc", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

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
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task CreateProvider_ProLicenseRejectsSecondProvider()
    {
        await using var fixture = CreateFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        var first = await client.PostAsJsonAsync(
            "/api/v1/admin/oidc/providers",
            CreateRequest("First"),
            _jsonOptions);
        var second = await client.PostAsJsonAsync(
            "/api/v1/admin/oidc/providers",
            CreateRequest("Second"),
            _jsonOptions);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Contains(
            "identity.oidc-multi-provider",
            await second.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task CreateProvider_ProLicenseConcurrentRequests_AtomicallyAllowsOne()
    {
        await using var fixture = CreateFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        var requests = new[]
        {
            client.PostAsJsonAsync(
                "/api/v1/admin/oidc/providers",
                CreateRequest("Concurrent One"),
                _jsonOptions),
            client.PostAsJsonAsync(
                "/api/v1/admin/oidc/providers",
                CreateRequest("Concurrent Two"),
                _jsonOptions),
        };
        var responses = await Task.WhenAll(requests);

        responses.Select(static response => response.StatusCode)
            .Should().BeEquivalentTo(
                [HttpStatusCode.Created, HttpStatusCode.PaymentRequired]);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task CreateProvider_ProLicenseWithStaticProvider_RejectsFirstRuntimeProvider()
    {
        await using var fixture = CreateFixture(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", "https://login.example.com");
                builder.UseSetting("Oidc:Generic:ClientId", "static-client");
                builder.UseSetting("Oidc:Generic:ClientSecret", "static-secret-value-minimum-length");
            });
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/oidc/providers",
            CreateRequest("Runtime"),
            _jsonOptions);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task CreateProvider_EnterpriseLicenseAllowsMultipleProviders()
    {
        var first = await _client.PostAsJsonAsync(
            "/api/v1/admin/oidc/providers",
            CreateRequest("First Enterprise"),
            _jsonOptions);
        var second = await _client.PostAsJsonAsync(
            "/api/v1/admin/oidc/providers",
            CreateRequest("Second Enterprise"),
            _jsonOptions);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
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
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
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

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.Equal(created.Data.ProviderId, result.Data.ProviderId);
    }

    private static WebAppFixture CreateFixture(HonuaEdition edition)
        => new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .WithTestLicense(edition);

    private static CreateOidcProviderRequest CreateRequest(string name)
        => new()
        {
            Name = name,
            ProviderType = "Generic",
            Authority = "https://identity.example.com",
            ClientId = $"{name.Replace(' ', '-').ToLowerInvariant()}-client",
        };
}
