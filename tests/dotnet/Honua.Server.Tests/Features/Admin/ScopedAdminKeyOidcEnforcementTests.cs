// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Regression coverage for scoped admin API-key enforcement while OIDC is enabled.
/// </summary>
/// <remarks>
/// When <c>Oidc:Enabled=true</c>, <c>AddOidcAuthorization</c> rebuilds the general
/// <c>Admin</c>-family policies so they accept composite OIDC/session/operator-bearer
/// principals. The rebuilt policies originally kept only the admin-role assertion and
/// dropped <c>AdminPermissionRequirement</c>, so a scoped key (which the API-key handler
/// stamps with the <c>admin</c> role because its grants are <c>admin:</c>-prefixed)
/// silently regained full mutating authority whenever OIDC was turned on. These tests
/// pin the scope ceiling in the OIDC-enabled configuration; the OIDC-disabled
/// counterpart lives in <see cref="ProposalEndpointsTests"/>.
/// </remarks>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class ScopedAdminKeyOidcEnforcementTests : IAsyncLifetime
{
    private const string AdminPassword = "oidc-scoped-key-bootstrap-key";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private readonly WebAppFixture _fixture;
    private HttpClient _bootstrapClient = null!;

    public ScopedAdminKeyOidcEnforcementTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                // Enable OIDC so AddOidcAuthorization rebuilds the Admin-family
                // policies through the composite scheme — the configuration under
                // test. The provider is never contacted: every request here carries
                // X-API-Key (no Bearer token), so the composite scheme forwards to
                // API-key authentication and no metadata discovery occurs.
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", "https://idp.invalid");
                builder.UseSetting("Oidc:Generic:ClientId", "test-client");
            })
            .ConfigureServices(services =>
            {
                // The production validator rejects unresolvable authorities during
                // startup validation paths; the fake authority above is intentionally
                // never contacted, so keep startup deterministic here.
                services.RemoveAll<IValidateOptions<Honua.Infrastructure.Authentication.OidcAuthenticationOptions>>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _bootstrapClient = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("PUT /api/v1/admin/services/{serviceId}/access-policy")]
    public async Task ScopedApproveKey_WithOidcEnabled_IsDeniedGeneralAdminMutation()
    {
        var key = await CreateApiKeyAsync("oidc-console-read-approve", ["admin:read", "admin:approve"]);
        using var client = CreateApiKeyClient(key.Key);

        // The scoped key still authenticates through the OIDC composite scheme and
        // passes safe admin reads.
        (await client.GetAsync("/api/v1/admin/api-keys")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The scope ceiling must hold under OIDC exactly as it does without it: a
        // read+approve key cannot perform an unrelated admin mutation.
        var forbidden = await client.PutAsJsonAsync(
            "/api/v1/admin/services/x/access-policy",
            new { allowAnonymous = true });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceId}/access-policy")]
    public async Task ReadOnlyScopedKey_WithOidcEnabled_IsDeniedGeneralAdminMutation()
    {
        var key = await CreateApiKeyAsync("oidc-console-read-only", ["admin:read"]);
        using var client = CreateApiKeyClient(key.Key);

        var forbidden = await client.PutAsJsonAsync(
            "/api/v1/admin/services/x/access-policy",
            new { allowAnonymous = true });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceId}/access-policy")]
    public async Task FullAdminKey_WithOidcEnabled_IsNotLockedOutOfAdminMutation()
    {
        // Guard against over-tightening: the bootstrap full admin (no permission
        // claims) must still pass authorization for mutating admin requests when the
        // OIDC-rebuilt policy carries AdminPermissionRequirement. The service does
        // not exist, so anything but 401/403 proves authorization admitted the call.
        var response = await _bootstrapClient.PutAsJsonAsync(
            "/api/v1/admin/services/x/access-policy",
            new { allowAnonymous = true });

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/services")]
    public async Task ApprovedOperationKey_WithOidcEnabled_AllowsOnlyExactOperation()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var issued = await store.CreateAsync(
            "approved-operation:oidc-test-proposal",
            [AdminApiKeyPermission.CreateApprovedOperationGrant("GET", "/api/v1/admin/api-keys")],
            DateTimeOffset.UtcNow.AddMinutes(5),
            "test-requester",
            CancellationToken.None);
        using var operationClient = CreateApiKeyClient(issued.Key);

        var exactOperation = await operationClient.GetAsync("/api/v1/admin/api-keys");
        var unrelatedAdminOperation = await operationClient.GetAsync("/api/v1/admin/services");

        exactOperation.StatusCode.Should().Be(HttpStatusCode.OK);
        unrelatedAdminOperation.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<AdminApiKeySecretResponse> CreateApiKeyAsync(string name, IReadOnlyList<string> permissions)
    {
        var response = await _bootstrapClient.PostAsJsonAsync(
            "/api/v1/admin/api-keys",
            new CreateAdminApiKeyRequest { Name = name, Permissions = permissions },
            JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        return result!.Data!;
    }

    private HttpClient CreateApiKeyClient(string apiKey)
        => _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", apiKey));
}
