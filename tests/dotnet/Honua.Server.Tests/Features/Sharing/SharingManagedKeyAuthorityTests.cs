// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// Neither managed-key exchange (the local admin credential bridge or the OAuth2
/// client_credentials API-key fallback) may widen a constrained key (#4577).
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingManagedKeyAuthorityTests : IAsyncLifetime
{
    private const string Referer = "https://managed-key.example.test";
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
            builder.UseSetting("Authentication:PortalToken:OAuth2:EnableClientCredentials", "true");
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_ConstrainedManagedKeys_RejectsAdminPromotion()
    {
        string[][] grants =
        [
            ["read:allowed-service"],
            ["write:allowed-service/1"],
            ["admin:read"],
            ["admin:read", "admin:approve"],
            ["ops:read"],
            ["unrecognized:permission"]
        ];
        await AssertRejectedAsync(grants);
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_ApprovedOperationKeys_RejectsLossOfOperationAndTenantBinding()
    {
        var operation = AdminApiKeyPermission.CreateApprovedOperationGrants(
            "PUT", "/api/v1/admin/metadata/layers/1/filter", "tenant-a");
        await AssertRejectedAsync([operation.ToArray(), [.. operation, "admin:*"]]);
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_FullAdminManagedKeys_PreservesSupportedAdminExchange()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var issuer = _fixture.Services.GetRequiredService<IPortalTokenIssuer>();
        using var client = _fixture.CreateClient();
        string[][] grants = [[], ["admin"], ["*"], ["admin:*"], ["admin:write"], ["admin:manage"]];
        foreach (var permissions in grants)
        {
            var key = await store.CreateAsync("full-admin-control", permissions,
                DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
            foreach (var username in new[] { "admin", "desktop-user" })
            {
                using var response = await ExchangeAsync(client, username, key.Key);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var token = body.RootElement.GetProperty("token").GetString();
                Assert.False(string.IsNullOrWhiteSpace(token));
                var validation = await issuer.ValidateAsync(token!, new PortalTokenBinding(Referer, null), CancellationToken.None);
                Assert.NotNull(validation);
                Assert.True(validation.Principal.IsInRole("admin"));
                await issuer.RevokeAsync(token!, CancellationToken.None);
            }
            await store.RevokeAsync(key.Record.Id, CancellationToken.None);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_RevokedAndExpiredManagedKeys_RejectsExchange()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var revoked = await store.CreateAsync("revoked-admin", ["admin:*"],
            DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
        await store.RevokeAsync(revoked.Record.Id, CancellationToken.None);
        var expired = await store.CreateAsync("expired-admin", ["admin:*"],
            DateTimeOffset.UtcNow.AddMinutes(-1), "test", CancellationToken.None);
        using var client = _fixture.CreateClient();
        foreach (var password in new[] { revoked.Key, expired.Key, "invalid-managed-credential" })
        {
            foreach (var username in new[] { "admin", "desktop-user" })
            {
                using var response = await ExchangeAsync(client, username, password);
                await AssertExchangeRejectedAsync(response);
            }
        }
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task ClientCredentials_ConstrainedManagedKeys_RefusesRoleProjection()
    {
        var operation = AdminApiKeyPermission.CreateApprovedOperationGrants(
            "PUT", "/api/v1/admin/metadata/layers/1/filter", "tenant-a");
        string[][] grants =
        [
            ["field-editor"],
            ["read:allowed-service"],
            ["write:allowed-service/1"],
            ["admin:read"],
            ["admin:read", "admin:approve"],
            ["ops:read"],
            ["unrecognized:permission"],
            operation.ToArray(),
            [.. operation, "admin:*"]
        ];
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        using var client = _fixture.CreateClient();
        foreach (var permissions in grants)
        {
            var key = await store.CreateAsync("constrained-client", permissions,
                DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
            using var response = await ClientCredentialsAsync(client, key.Key);
            await AssertClientCredentialsErrorAsync(response, "unauthorized_client");
            await store.RevokeAsync(key.Record.Id, CancellationToken.None);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task ClientCredentials_FullAdminManagedKeys_PassAuthorityCheck()
    {
        // The in-process TestServer has no client IP, so a key that passes the
        // authority check stops at token binding with invalid_request. The minted
        // admin-role token is asserted in PortalOAuthClientCredentialsTests.
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        using var client = _fixture.CreateClient();
        string[][] grants = [[], ["admin"], ["*"], ["admin:*"], ["admin:write"], ["admin:manage"], ["admin:*", "field-editor"]];
        foreach (var permissions in grants)
        {
            var key = await store.CreateAsync("full-admin-client", permissions,
                DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
            using var response = await ClientCredentialsAsync(client, key.Key);
            await AssertClientCredentialsErrorAsync(response, "invalid_request");
            await store.RevokeAsync(key.Record.Id, CancellationToken.None);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task ClientCredentials_RevokedAndExpiredManagedKeys_RejectsExchange()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var revoked = await store.CreateAsync("revoked-client", ["admin:*"],
            DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
        await store.RevokeAsync(revoked.Record.Id, CancellationToken.None);
        var expired = await store.CreateAsync("expired-client", ["admin:*"],
            DateTimeOffset.UtcNow.AddMinutes(-1), "test", CancellationToken.None);
        using var client = _fixture.CreateClient();
        foreach (var secret in new[] { revoked.Key, expired.Key, "invalid-managed-credential" })
        {
            using var response = await ClientCredentialsAsync(client, secret);
            await AssertClientCredentialsErrorAsync(response, "invalid_client");
        }
    }

    private static async Task AssertClientCredentialsErrorAsync(HttpResponseMessage response, string expectedError)
    {
        // Check for a token before anything that could echo the body.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("access_token", out _));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedError, body.RootElement.GetProperty("error").GetString());
    }

    private static async Task<HttpResponseMessage> ClientCredentialsAsync(HttpClient client, string secret)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "managed-key-client",
            ["client_secret"] = secret
        });
        return await client.PostAsync("/sharing/rest/oauth2/token", content);
    }

    private async Task AssertRejectedAsync(IEnumerable<string[]> grantSets)
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        using var client = _fixture.CreateClient();
        foreach (var permissions in grantSets)
        {
            var key = await store.CreateAsync("constrained-control", permissions,
                DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
            foreach (var username in new[] { "admin", "desktop-user" })
            {
                using var response = await ExchangeAsync(client, username, key.Key);
                await AssertExchangeRejectedAsync(response);
            }
            await store.RevokeAsync(key.Record.Id, CancellationToken.None);
        }
    }

    private static async Task AssertExchangeRejectedAsync(HttpResponseMessage response)
    {
        // Do not include a success-shaped response in a failing assertion: it
        // can contain a newly issued bearer credential.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("token", out _));
        Assert.True(body.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(400, error.GetProperty("code").GetInt32());
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string username, string password)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["client"] = "referer",
            ["referer"] = Referer,
            ["f"] = "json"
        });
        return await client.PostAsync("/sharing/rest/generateToken", content);
    }
}
