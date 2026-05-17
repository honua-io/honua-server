// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin API key lifecycle endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApiKeyManagement)]
public sealed class AdminApiKeyEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "api-key-admin-bootstrap-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public AdminApiKeyEndpointsTests()
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
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task CreateAndListApiKeys_ReturnsPlaintextOnlyAtCreation()
    {
        var created = await CreateApiKeyAsync("list-contract-key");

        Assert.StartsWith("hnua_", created.Key, StringComparison.Ordinal);
        Assert.Equal("list-contract-key", created.ApiKey.Name);
        Assert.Equal("admin:*", Assert.Single(created.ApiKey.Permissions));

        var response = await _client.GetAsync("/api/v1/admin/api-keys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.Key, json, StringComparison.Ordinal);

        var list = JsonSerializer.Deserialize<ApiResponse<AdminApiKeyResponse[]>>(json, _jsonOptions);
        Assert.NotNull(list?.Data);
        var listed = Assert.Single(list.Data, key => key.Id == created.ApiKey.Id);
        Assert.Equal(created.ApiKey.KeyPrefix, listed.KeyPrefix);
        Assert.Equal("active", listed.Status);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task RevokeApiKey_PreventsFurtherAuthentication()
    {
        var created = await CreateApiKeyAsync("revoke-contract-key");
        using var keyClient = CreateApiKeyClient(created.Key);

        var beforeRevoke = await keyClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);

        var response = await _client.PostAsync($"/api/v1/admin/api-keys/{created.ApiKey.Id}/revoke", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var revoked = JsonSerializer.Deserialize<ApiResponse<AdminApiKeyResponse>>(
            await response.Content.ReadAsStringAsync(),
            _jsonOptions);
        Assert.Equal("revoked", revoked?.Data?.Status);

        var afterRevoke = await keyClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    public async Task RotateApiKey_ReturnsNewPlaintextAndInvalidatesOldKey()
    {
        var created = await CreateApiKeyAsync("rotate-contract-key");

        var response = await _client.PostAsync($"/api/v1/admin/api-keys/{created.ApiKey.Id}/rotate", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(),
            _jsonOptions);

        Assert.NotNull(rotated?.Data);
        Assert.NotEqual(created.Key, rotated.Data.Key);
        Assert.Equal(created.ApiKey.Id, rotated.Data.ApiKey.Id);
        Assert.Equal("active", rotated.Data.ApiKey.Status);
        Assert.NotNull(rotated.Data.ApiKey.RotatedAt);

        using var oldKeyClient = CreateApiKeyClient(created.Key);
        using var newKeyClient = CreateApiKeyClient(rotated.Data.Key);

        var oldKeyResponse = await oldKeyClient.GetAsync("/api/v1/admin/api-keys");
        var newKeyResponse = await newKeyClient.GetAsync("/api/v1/admin/api-keys");

        Assert.Equal(HttpStatusCode.Unauthorized, oldKeyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newKeyResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    public async Task ExpiredApiKey_CannotAuthenticateAndReportsExpiredPermissions()
    {
        var created = await CreateApiKeyAsync(
            "expired-contract-key",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            ["admin:read"]);

        using var expiredClient = CreateApiKeyClient(created.Key);
        var authResponse = await expiredClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, authResponse.StatusCode);

        var permissionsResponse = await _client.GetAsync(
            $"/api/v1/admin/api-keys/{created.ApiKey.Id}/effective-permissions");

        Assert.Equal(HttpStatusCode.OK, permissionsResponse.StatusCode);
        var permissions = JsonSerializer.Deserialize<ApiResponse<AdminApiKeyEffectivePermissionsResponse>>(
            await permissionsResponse.Content.ReadAsStringAsync(),
            _jsonOptions);

        Assert.NotNull(permissions?.Data);
        Assert.Equal("expired", permissions.Data.Status);
        Assert.False(permissions.Data.CanAuthenticate);
        Assert.Equal("admin:read", Assert.Single(permissions.Data.Permissions));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task CreateApiKey_WithNullPermissions_ReturnsBadRequest()
    {
        string[] payloads =
        [
            """{"name":"null-permissions-key","permissions":null}""",
            """{"name":"null-permission-entry-key","permissions":[null]}""",
        ];

        foreach (var payload in payloads)
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("/api/v1/admin/api-keys", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("permission", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<AdminApiKeySecretResponse> CreateApiKeyAsync(
        string name,
        DateTimeOffset? expiresAt = null,
        IReadOnlyList<string>? permissions = null)
    {
        var request = new CreateAdminApiKeyRequest
        {
            Name = name,
            ExpiresAt = expiresAt,
            Permissions = permissions ?? [],
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/api-keys", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(),
            _jsonOptions);

        Assert.NotNull(result?.Data);
        return result.Data;
    }

    private HttpClient CreateApiKeyClient(string apiKey)
    {
        return _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", apiKey));
    }
}
