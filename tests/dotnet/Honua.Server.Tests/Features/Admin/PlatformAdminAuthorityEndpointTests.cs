// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class PlatformAdminAuthorityEndpointTests : IClassFixture<PlatformAdminAuthorityEndpointTests.PlatformAuthorityHost>
{
    private readonly IAdminApiKeyStore _keys;
    private readonly IWorkflowOperationStore _workflows;
    private readonly WebAppFixture _fixture;

    public PlatformAdminAuthorityEndpointTests(PlatformAuthorityHost host)
    {
        _keys = host.Keys;
        _workflows = host.Workflows;
        _fixture = host.Fixture;
        // The stores outlive a case; each case asserts only on its own calls.
        _keys.ClearReceivedCalls();
        _workflows.ClearReceivedCalls();
    }

    // One host for the class: the stores are substitutes and every case is an authorization outcome.
    public sealed class PlatformAuthorityHost : IAsyncLifetime
    {
        public PlatformAuthorityHost()
        {
            Keys.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<AdminApiKeyRecord>());
            Fixture = new WebAppFixture()
                .UseSeed("tests/seed/server.yaml")
                .ConfigureWebHost(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", "platform-authority-test-bootstrap");
                })
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IAdminApiKeyStore>();
                    services.AddSingleton(Keys);
                    services.RemoveAll<IWorkflowOperationStore>();
                    services.AddSingleton(Workflows);
                    services.RemoveAll<MetadataReleaseControlService>();
                    services.AddSingleton<MetadataReleaseControlService>();
                    services.Configure<TenantContextOptions>(options => options.Enabled = true);
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PlatformAdminTestAuthentication>(
                        PlatformAdminTestAuthentication.SchemeName, _ => { });
                    services.PostConfigure<AuthorizationOptions>(options =>
                    {
                        var policy = new AuthorizationPolicyBuilder(PlatformAdminTestAuthentication.SchemeName)
                            .RequireAuthenticatedUser().RequireRole("admin").Build();
                        options.AddPolicy(AuthenticationExtensions.AdminPolicy, policy);
                        options.AddPolicy(AuthenticationExtensions.AdminPolicyAlias, policy);
                        options.AddPolicy(AuthenticationExtensions.AdminCredentialManagementPolicy, policy);
                        options.AddPolicy(AuthenticationExtensions.OpsReadPolicy, policy);
                    });
                });
        }

        internal IAdminApiKeyStore Keys { get; } = Substitute.For<IAdminApiKeyStore>();

        internal IWorkflowOperationStore Workflows { get; } = Substitute.For<IWorkflowOperationStore>();

        internal WebAppFixture Fixture { get; }

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public Task DisposeAsync() => Fixture.DisposeAsync();
    }

    [IntegrationTheory]
    [InlineData("api-keys")]
    [InlineData("tenants")]
    [InlineData("license")]
    [InlineData("license/status")]
    [InlineData("identity/providers")]
    [InlineData("metadata/release-packages")]
    [InlineData("metadata/releases/test-package/operation")]
    [InlineData("oauth-clients")]
    [InlineData("oauth-scopes")]
    [InlineData("oidc/providers")]
    [InlineData("security/client-certificates/profiles")]
    [InlineData("users")]
    [InlineData("agent-authority?principalId=agent-1")]
    [InlineData("embed/keys")]
    [InlineData("embed/usage")]
    [InlineData("rate-limits")]
    [InlineData("connections")]
    [InlineData("roles")]
    [InlineData("rls-policies")]
    [InlineData("field-mask-policies")]
    [InlineData("observability/ops-health")]
    [InlineData("observability/errors")]
    [InlineData("observability/events")]
    [InlineData("compliance/dashboard")]
    [InlineData("configuration/discover")]
    [InlineData("cache/status")]
    [InlineData("operations/cache/health")]
    [InlineData("gp/process-releases")]
    [InlineData("geocoding/providers")]
    [InlineData("operations/geocoding/providers")]
    [InlineData("geoprocessing/tools/usage-ranking")]
    [InlineData("investigations")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/tenants")]
    [Endpoint("GET /api/v1/admin/license")]
    [Endpoint("GET /api/v1/admin/license/status")]
    [Endpoint("GET /api/v1/admin/identity/providers")]
    [Endpoint("GET /api/v1/admin/metadata/release-packages")]
    [Endpoint("GET /api/v1/admin/metadata/releases/{packageId}/operation")]
    [Endpoint("GET /api/v1/admin/oauth-clients")]
    [Endpoint("GET /api/v1/admin/oauth-scopes")]
    [Endpoint("GET /api/v1/admin/oidc/providers")]
    [Endpoint("GET /api/v1/admin/security/client-certificates/profiles")]
    [Endpoint("GET /api/v1/admin/users")]
    [Endpoint("GET /api/v1/admin/agent-authority")]
    [Endpoint("GET /api/v1/admin/embed/keys")]
    [Endpoint("GET /api/v1/admin/embed/usage")]
    [Endpoint("GET /api/v1/admin/rate-limits")]
    [Endpoint("GET /api/v1/admin/connections")]
    [Endpoint("GET /api/v1/admin/roles")]
    [Endpoint("GET /api/v1/admin/rls-policies")]
    [Endpoint("GET /api/v1/admin/field-mask-policies")]
    [Endpoint("GET /api/v1/admin/observability/ops-health")]
    [Endpoint("GET /api/v1/admin/observability/errors")]
    [Endpoint("GET /api/v1/admin/observability/events")]
    [Endpoint("GET /api/v1/admin/compliance/dashboard")]
    [Endpoint("GET /api/v1/admin/configuration/discover")]
    [Endpoint("GET /api/v1/admin/cache/status")]
    [Endpoint("GET /api/v1/admin/operations/cache/health")]
    [Endpoint("GET /api/v1/admin/gp/process-releases")]
    [Endpoint("GET /api/v1/admin/geocoding/providers")]
    [Endpoint("GET /api/v1/admin/operations/geocoding/providers")]
    [Endpoint("GET /api/v1/admin/geoprocessing/tools/usage-ranking")]
    [Endpoint("GET /api/v1/admin/investigations")]
    public async Task GlobalAdministration_TenantAdmin_RequiresPlatformAuthority(string path)
    {
        using var client = CreateClient("tenant-admin");

        using var response = await client.GetAsync($"/api/v1/admin/{path}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("platform_admin_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await _keys.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/releases/operations")]
    public async Task SubmitMetadataRelease_TenantAdmin_RejectsBeforeStoreAccess()
    {
        using var client = CreateClient("tenant-admin");
        _workflows.ClearReceivedCalls();

        using var response = await client.PostAsJsonAsync("/api/v1/admin/metadata/releases/operations", new
        {
            packageId = "test-package",
            targetEnvironment = "test",
            resourceSemanticId = "parcels",
            newFieldName = "test_field",
            newFieldType = "String"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("platform_admin_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_workflows.ReceivedCalls());
    }

    [IntegrationTheory]
    [InlineData("POST", "api-keys")]
    [InlineData("POST", "api-keys/11111111-1111-1111-1111-111111111111/rotate")]
    [InlineData("POST", "api-keys/11111111-1111-1111-1111-111111111111/revoke")]
    [InlineData("PATCH", "api-keys/11111111-1111-1111-1111-111111111111/restrictions")]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    [Endpoint("PATCH /api/v1/admin/api-keys/{id}/restrictions")]
    public async Task ManageApiKeys_TenantAdmin_RejectsBeforeStoreAccess(string method, string path)
    {
        using var client = CreateClient("tenant-admin");
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/admin/{path}")
        {
            Content = new StringContent(
                """{"name":"test-key","permissions":["admin:*"],"allowedReferrers":[],"allowedIpRanges":[]}""",
                Encoding.UTF8, "application/json")
        };
        _keys.ClearReceivedCalls();

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("platform_admin_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_keys.ReceivedCalls());
    }

    [IntegrationTheory]
    [InlineData("PUT", "users/victim/roles", """{"roles":["platform_admin"]}""")]
    [InlineData("DELETE", "users/victim", null)]
    [InlineData("POST", "connections/object-storage/setup?bucket=customer-imports&prefix=parcels", null)]
    [InlineData("GET", "connections/11111111-1111-1111-1111-111111111111/objects", null)]
    [Endpoint("PUT /api/v1/admin/users/{id}/roles")]
    [Endpoint("DELETE /api/v1/admin/users/{id}")]
    [Endpoint("POST /api/v1/admin/connections/object-storage/setup")]
    [Endpoint("GET /api/v1/admin/connections/{id}/objects")]
    public async Task GlobalIdentityAndObjectStorageMutations_TenantAdmin_RequirePlatformAuthority(
        string method,
        string path,
        string? body)
    {
        using var client = CreateClient("tenant-admin");
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/admin/{path}");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("platform_admin_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [IntegrationTheory]
    [InlineData("platform-admin")]
    [InlineData("unbound-admin")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task ListApiKeys_PlatformAuthority_PreservesAccess(string identity)
    {
        using var client = CreateClient(identity);

        using var response = await client.GetAsync("/api/v1/admin/api-keys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _keys.Received().ListAsync(Arg.Any<CancellationToken>());
    }

    private HttpClient CreateClient(string identity) => _fixture.CreateClient(client =>
        client.DefaultRequestHeaders.Add(PlatformAdminTestAuthentication.IdentityHeader, identity));
}

internal sealed class PlatformAdminTestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "PlatformAdminTest";
    internal const string IdentityHeader = "X-Test-Identity";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = Request.Headers[IdentityHeader].ToString();
        if (string.IsNullOrEmpty(identity))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, identity), new(ClaimTypes.Role, "admin") };
        if (identity != "unbound-admin")
        {
            claims.Add(new Claim("tid", "test-tenant"));
        }

        if (identity == "platform-admin")
        {
            claims.Add(new Claim(ClaimTypes.Role, "platform_admin"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
