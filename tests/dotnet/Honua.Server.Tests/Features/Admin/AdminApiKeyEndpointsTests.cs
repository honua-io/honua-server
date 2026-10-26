// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.NotNull(list);
        Assert.NotNull(list.Data);
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

        Assert.NotNull(rotated);
        Assert.NotNull(rotated.Data);
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

        Assert.NotNull(permissions);
        Assert.NotNull(permissions.Data);
        Assert.Equal("expired", permissions.Data.Status);
        Assert.False(permissions.Data.CanAuthenticate);
        Assert.Equal("admin:read", Assert.Single(permissions.Data.Permissions));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task ReadOnlyScopedKey_CanReadAdminSurface()
    {
        // #1985: a key scoped to admin:read must still be able to perform safe
        // (GET) admin reads — scoped enforcement narrows, it does not lock out.
        var created = await CreateApiKeyAsync("scoped-read-key", permissions: ["admin:read"]);
        using var readKeyClient = CreateApiKeyClient(created.Key);

        var response = await readKeyClient.GetAsync("/api/v1/admin/api-keys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task ReadOnlyScopedKey_CannotPerformMutatingAdminOperations()
    {
        // #1985 regression: before scoped enforcement an admin:read key was stamped
        // Role=admin and silently passed every admin endpoint, including mutating
        // ones. It must now be Forbidden (403) from creating, rotating, or revoking.
        var created = await CreateApiKeyAsync("scoped-read-key-mutation", permissions: ["admin:read"]);
        var victim = await CreateApiKeyAsync("scoped-read-key-victim", permissions: ["admin:read"]);
        using var readKeyClient = CreateApiKeyClient(created.Key);

        var createRequest = new CreateAdminApiKeyRequest
        {
            Name = "should-not-be-created",
            Permissions = ["admin:*"],
        };
        var createResponse = await readKeyClient.PostAsJsonAsync("/api/v1/admin/api-keys", createRequest, _jsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);

        var rotateResponse = await readKeyClient.PostAsync(
            $"/api/v1/admin/api-keys/{created.ApiKey.Id}/rotate", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, rotateResponse.StatusCode);

        var revokeResponse = await readKeyClient.PostAsync(
            $"/api/v1/admin/api-keys/{victim.ApiKey.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, revokeResponse.StatusCode);

        // The privilege-escalation attempt must not have mutated anything: the
        // victim key still authenticates.
        using var victimClient = CreateApiKeyClient(victim.Key);
        var victimStillWorks = await victimClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, victimStillWorks.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task FullAdminScopedKey_RetainsFullAdminAuthority()
    {
        // #1985: the default-minted key carries admin:* and must keep full admin
        // (read AND write) so existing keys are never locked out by enforcement.
        var created = await CreateApiKeyAsync("scoped-full-admin-key", permissions: ["admin:*"]);
        using var fullKeyClient = CreateApiKeyClient(created.Key);

        var listResponse = await fullKeyClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var createRequest = new CreateAdminApiKeyRequest
        {
            Name = "created-by-full-admin",
            Permissions = ["admin:read"],
        };
        var createResponse = await fullKeyClient.PostAsJsonAsync("/api/v1/admin/api-keys", createRequest, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task OperationalWriteKey_CannotManageCredentials()
    {
        // The installer's operational key (admin:read, admin:write, read:*) has general
        // admin write. Creating, rotating and revoking keys requires full-admin authority,
        // so each of those requests returns 403 and leaves the key store unchanged.
        var operational = await CreateApiKeyAsync("installer-operational-key", permissions: ["admin:read", "admin:write", "read:*"]);
        var fullAdmin = await CreateApiKeyAsync("existing-full-admin-key", permissions: ["admin:*"]);
        using var operationalClient = CreateApiKeyClient(operational.Key);

        foreach (var requested in new[] { new[] { "admin:approve" }, new[] { "admin:read", "admin:write", "read:*" }, new[] { "admin:*" } })
        {
            var create = await operationalClient.PostAsJsonAsync(
                "/api/v1/admin/api-keys",
                new CreateAdminApiKeyRequest { Name = "operational-child", Permissions = requested },
                _jsonOptions);
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        var rotateFullAdmin = await operationalClient.PostAsync($"/api/v1/admin/api-keys/{fullAdmin.ApiKey.Id}/rotate", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, rotateFullAdmin.StatusCode);

        var revokeFullAdmin = await operationalClient.PostAsync($"/api/v1/admin/api-keys/{fullAdmin.ApiKey.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, revokeFullAdmin.StatusCode);

        // Nothing was created or changed: the full-admin key still authenticates with its
        // original secret, and the operational key keeps its read and general write role.
        using var fullAdminClient = CreateApiKeyClient(fullAdmin.Key);
        Assert.Equal(HttpStatusCode.OK, (await fullAdminClient.GetAsync("/api/v1/admin/api-keys")).StatusCode);
        var list = await _client.GetStringAsync("/api/v1/admin/api-keys");
        Assert.DoesNotContain("operational-child", list, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await operationalClient.GetAsync("/api/v1/admin/api-keys")).StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task FullAdminKeyAndBootstrap_RetainCredentialManagement()
    {
        var fullAdmin = await CreateApiKeyAsync("credential-manager-full-admin", permissions: ["admin:*"]);
        using var fullAdminClient = CreateApiKeyClient(fullAdmin.Key);

        var minted = await fullAdminClient.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new CreateAdminApiKeyRequest { Name = "minted-by-full-admin", Permissions = ["admin:approve"] },
            _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, minted.StatusCode);
        var mintedKey = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await minted.Content.ReadAsStringAsync(), _jsonOptions)!.Data!;

        Assert.Equal(HttpStatusCode.OK, (await fullAdminClient.PostAsync($"/api/v1/admin/api-keys/{mintedKey.ApiKey.Id}/rotate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/v1/admin/api-keys/{mintedKey.ApiKey.Id}/revoke", null)).StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task ApprovedOperationKey_ForKeyCreation_MayCreateOnlyThroughItsExactOperation()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var issued = await store.CreateAsync(
            "approved-operation:create-key",
            AdminApiKeyPermission.CreateApprovedOperationGrants("POST", "/api/v1/admin/api-keys", "public"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "test-requester",
            CancellationToken.None);
        var other = await CreateApiKeyAsync("approved-operation-rotate-target", permissions: ["admin:read"]);
        using var operationClient = CreateApiKeyClient(issued.Key);

        var create = await operationClient.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new CreateAdminApiKeyRequest { Name = "approved-create", Permissions = ["admin:read"] },
            _jsonOptions);
        var rotate = await operationClient.PostAsync($"/api/v1/admin/api-keys/{other.ApiKey.Id}/rotate", null);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rotate.StatusCode);
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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task CreateApiKey_WithReservedApprovedOperationGrant_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new CreateAdminApiKeyRequest
            {
                Name = "forged-operation-key",
                Permissions = ["admin:operation:POST:/api/v1/admin/connections"],
            },
            _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "minted internally",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task GenuinelyScopedApiKey_IsDeniedAdminEndpoint()
    {
        // A key whose grants are genuinely scoped — neither a full-admin grant
        // (admin / * / admin:*) nor a layer-scoped write: grant — must NOT be
        // silently elevated to full admin (issue #1985). It authenticates but is
        // forbidden from the admin surface guarded by RequireAdminAuthorization.
        var scoped = await CreateApiKeyAsync("scoped-read-key", permissions: ["read:layers"]);
        using var scopedClient = CreateApiKeyClient(scoped.Key);

        var response = await scopedClient.GetAsync("/api/v1/admin/api-keys");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/studio/content-items")]
    public async Task ApprovedOperationKey_AllowsExactOperation_ButRoleOnlySurfaceRefusesIt()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var issued = await store.CreateAsync(
            "approved-operation:test-proposal",
            AdminApiKeyPermission.CreateApprovedOperationGrants("GET", "/api/v1/admin/api-keys", "public"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "test-requester",
            CancellationToken.None);
        using var operationClient = CreateApiKeyClient(issued.Key);

        var exactOperation = await operationClient.GetAsync("/api/v1/admin/api-keys");
        var unrelatedRoleOnlySurface = await operationClient.GetAsync("/api/v1/studio/content-items");

        Assert.Equal(HttpStatusCode.OK, exactOperation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unrelatedRoleOnlySurface.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    public async Task InternalReplayCredential_CannotBeDiscoveredOrReissuedThroughKeyManagement()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var issued = await store.CreateAsync("approved-operation:other-tenant-proposal",
            AdminApiKeyPermission.CreateApprovedOperationGrants("PUT", "/api/v1/admin/metadata/layers/1/filter", "tenant-a"),
            DateTimeOffset.UtcNow.AddMinutes(5), "requester", CancellationToken.None);

        var list = await _client.GetStringAsync("/api/v1/admin/api-keys");
        Assert.DoesNotContain(issued.Record.Id.ToString(), list, StringComparison.Ordinal);
        var rotate = await _client.PostAsync($"/api/v1/admin/api-keys/{issued.Record.Id}/rotate", null);
        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
        var permissions = await _client.GetAsync($"/api/v1/admin/api-keys/{issued.Record.Id}/effective-permissions");
        Assert.Equal(HttpStatusCode.NotFound, permissions.StatusCode);
        Assert.NotNull(await store.ValidateAsync(issued.Key, CancellationToken.None));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task FullAdminApiKey_PassesAdminEndpoint()
    {
        // Backward compatibility: a key carrying a full-admin grant (and the
        // default unscoped key, normalized to admin:*) still passes every admin
        // endpoint (issue #1985).
        var explicitAdmin = await CreateApiKeyAsync("explicit-admin-key", permissions: ["admin:*"]);
        var defaultAdmin = await CreateApiKeyAsync("default-admin-key");
        Assert.Equal("admin:*", Assert.Single(defaultAdmin.ApiKey.Permissions));

        using var explicitAdminClient = CreateApiKeyClient(explicitAdmin.Key);
        using var defaultAdminClient = CreateApiKeyClient(defaultAdmin.Key);

        var explicitResponse = await explicitAdminClient.GetAsync("/api/v1/admin/api-keys");
        var defaultResponse = await defaultAdminClient.GetAsync("/api/v1/admin/api-keys");

        Assert.Equal(HttpStatusCode.OK, explicitResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    public async Task RotateApiKey_AfterExpiry_IsRejectedRatherThanIssuingUnusableMaterial()
    {
        // Rotation preserves ExpiresAt, so a "successful" rotation of an expired key would
        // hand back a credential authentication rejects on the very next request.
        var created = await CreateApiKeyAsync("expired-rotate-key", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal("expired", created.ApiKey.Status);

        var rotate = await _client.PostAsync($"/api/v1/admin/api-keys/{created.ApiKey.Id}/rotate", content: null);

        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
        var effective = await _client.GetFromJsonAsync<ApiResponse<AdminApiKeyEffectivePermissionsResponse>>(
            $"/api/v1/admin/api-keys/{created.ApiKey.Id}/effective-permissions", _jsonOptions);
        Assert.NotNull(effective);
        Assert.NotNull(effective.Data);
        Assert.Equal("expired", effective.Data.Status);
        Assert.False(effective.Data.CanAuthenticate);

        using var expiredClient = CreateApiKeyClient(created.Key);
        var denied = await expiredClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    public async Task CreateApiKey_WithReadWildcardGrants_RoundTripsThroughEffectivePermissions()
    {
        string[] grants = ["admin:read", "admin:write", "read:*", "read:catalog/*"];
        var created = await CreateApiKeyAsync("agent-read-wildcard-key", permissions: grants);
        Assert.Equal(grants, created.ApiKey.Permissions);

        var response = await _client.GetAsync($"/api/v1/admin/api-keys/{created.ApiKey.Id}/effective-permissions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var effective = JsonSerializer.Deserialize<ApiResponse<AdminApiKeyEffectivePermissionsResponse>>(
            await response.Content.ReadAsStringAsync(),
            _jsonOptions);
        Assert.NotNull(effective);
        Assert.NotNull(effective.Data);
        Assert.True(effective.Data.CanAuthenticate);
        Assert.Equal(grants, effective.Data.Permissions);

        using var agentClient = CreateApiKeyClient(created.Key);
        var adminRead = await agentClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, adminRead.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task CreateApiKey_WithMalformedResourceGrant_ReturnsBadRequest()
    {
        foreach (var grant in new[] { "read:", "read:*/roads", "read:catalog/", "write:*", "write:catalog/*", "execute:catalog", "execute:catalog/*", "execute:*/geometry.area", "execute:catalog/geometry.area/extra" })
        {
            var response = await _client.PostAsJsonAsync(
                "/api/v1/admin/api-keys",
                new CreateAdminApiKeyRequest { Name = "malformed-grant-key", Permissions = ["admin:read", grant] },
                _jsonOptions);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("read:*", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task CreateApiKey_WithReferrerRestriction_IsNormalizedListedAndEnforced()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new CreateAdminApiKeyRequest
            {
                Name = "browser-restricted-key",
                Permissions = ["admin:read"],
                AllowedReferrers = ["HTTPS://Console.Example.com/", "https://*.example.org", "https://console.example.com"],
            },
            _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(), _jsonOptions)!.Data!;
        Assert.Equal(new[] { "https://console.example.com", "https://*.example.org" }, created.ApiKey.AllowedReferrers);
        Assert.Null(created.ApiKey.AllowedIpRanges);

        Assert.Equal(HttpStatusCode.OK, await ListWithKeyAsync(created.Key, origin: "https://console.example.com"));
        Assert.Equal(HttpStatusCode.OK, await ListWithKeyAsync(created.Key, origin: "https://ops.example.org"));
        Assert.Equal(HttpStatusCode.Unauthorized, await ListWithKeyAsync(created.Key, origin: "https://evil.example.net"));
        Assert.Equal(HttpStatusCode.Unauthorized, await ListWithKeyAsync(created.Key, origin: null));

        var list = JsonSerializer.Deserialize<ApiResponse<AdminApiKeyResponse[]>>(
            await _client.GetStringAsync("/api/v1/admin/api-keys"), _jsonOptions);
        var listed = Assert.Single(list!.Data!, key => key.Id == created.ApiKey.Id);
        Assert.Equal(created.ApiKey.AllowedReferrers, listed.AllowedReferrers);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task CreateApiKey_WithIpRestriction_RefusesClientsOutsideTheRange()
    {
        // 192.0.2.0/24 is TEST-NET-1: the in-process test client is never inside it.
        var created = await CreateApiKeyAsync("network-restricted-key", permissions: ["admin:read"],
            allowedIpRanges: ["192.0.2.0/24"]);
        var unrestricted = await CreateApiKeyAsync("unrestricted-key", permissions: ["admin:read"]);

        Assert.Equal(new[] { "192.0.2.0/24" }, created.ApiKey.AllowedIpRanges);
        Assert.Equal(HttpStatusCode.Unauthorized, await ListWithKeyAsync(created.Key, origin: null));
        Assert.Equal(HttpStatusCode.OK, await ListWithKeyAsync(unrestricted.Key, origin: null));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task CreateApiKey_WithMalformedRestriction_ReturnsBadRequest()
    {
        var invalid = new[]
        {
            new CreateAdminApiKeyRequest { Name = "bad-referrer", AllowedReferrers = ["https://app.example.com/path"] },
            new CreateAdminApiKeyRequest { Name = "bad-referrer", AllowedReferrers = ["app.example.com"] },
            new CreateAdminApiKeyRequest { Name = "bad-referrer", AllowedReferrers = ["https://*.com"] },
            new CreateAdminApiKeyRequest { Name = "bad-range", AllowedIpRanges = ["203.0.113.7/24"] },
            new CreateAdminApiKeyRequest { Name = "bad-range", AllowedIpRanges = ["not-an-ip"] },
            new CreateAdminApiKeyRequest { Name = "too-many", AllowedIpRanges = Enumerable.Range(0, 51).Select(i => $"10.0.{i}.0/24").ToArray() },
        };

        foreach (var request in invalid)
        {
            var response = await _client.PostAsJsonAsync("/api/v1/admin/api-keys", request, _jsonOptions);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private async Task<HttpStatusCode> ListWithKeyAsync(string key, string? origin)
    {
        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/api-keys");
        request.Headers.Add("X-API-Key", key);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<AdminApiKeySecretResponse> CreateApiKeyAsync(
        string name,
        DateTimeOffset? expiresAt = null,
        IReadOnlyList<string>? permissions = null,
        IReadOnlyList<string>? allowedIpRanges = null)
    {
        var request = new CreateAdminApiKeyRequest
        {
            Name = name,
            ExpiresAt = expiresAt,
            Permissions = permissions ?? [],
            AllowedIpRanges = allowedIpRanges,
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/api-keys", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(),
            _jsonOptions);

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        return result.Data;
    }

    private HttpClient CreateApiKeyClient(string apiKey)
    {
        return _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", apiKey));
    }
}
