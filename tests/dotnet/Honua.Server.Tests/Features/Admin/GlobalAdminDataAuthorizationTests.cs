// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class GlobalAdminDataAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "global-admin-data-test-bootstrap");
        })
        .ConfigureServices(services =>
        {
            services.Configure<TenantContextOptions>(options =>
            {
                options.Enabled = true;
                options.TenantClaimTypes = ["category"];
            });
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                RlsClaimsTestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(options => options.AddPolicy(
                AuthenticationExtensions.AdminPolicy,
                new AuthorizationPolicyBuilder(RlsClaimsTestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser().RequireRole("admin").Build()));
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("GET", "/api/v1/admin/feature-events/replay")]
    [InlineData("GET", "/api/v1/admin/observability/audit")]
    [InlineData("GET", "/api/v1/admin/observability/audit/export")]
    [InlineData("GET", "/api/v1/admin/observability/audit/verify")]
    [InlineData("POST", "/api/v1/admin/observability/audit/gap-attestations")]
    [InlineData("GET", "/api/v1/analysis/content/items")]
    [InlineData("POST", "/api/v1/analysis/content/items")]
    [InlineData("GET", "/api/v1/analysis/content/items/test")]
    [InlineData("GET", "/api/v1/analysis/content/items/test/versions/latest")]
    [InlineData("GET", "/api/v1/analysis/content/items/test/versions/1")]
    [InlineData("POST", "/api/v1/analysis/content/items/test/versions")]
    [InlineData("POST", "/api/v1/analysis/content/items/test/versions/1/estimate")]
    [InlineData("POST", "/api/v1/analysis/content/items/test/versions/1/preview")]
    [InlineData("POST", "/api/v1/analysis/content/items/test/versions/1/runs")]
    [InlineData("POST", "/api/v1/analysis/content/items/test/versions/1/reruns")]
    [InlineData("GET", "/api/v1/analysis/artifacts/test")]
    [InlineData("GET", "/api/v1/analysis/jobs/test/logs")]
    [InlineData("GET", "/api/v1/analysis/jobs/test/failure")]
    [InlineData("POST", "/hubs/admin/negotiate?negotiateVersion=1")]
    [Endpoint("GET /api/v1/admin/feature-events/replay")]
    [Endpoint("GET /api/v1/admin/observability/audit")]
    [Endpoint("POST /api/v1/analysis/content/items")]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task GlobalAdministration_TenantAdministrator_IsForbidden(string method, string path)
    {
        using var client = CreateClient("admin", "tenant-a");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [IntegrationTheory]
    [InlineData("admin,platform_admin", "tenant-a")]
    [InlineData("admin", null)]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task AdminHub_PlatformOrUnboundAdministrator_CanConnect(string roles, string? tenant)
    {
        using var client = CreateClient(roles, tenant);

        using var response = await client.PostAsync("/hubs/admin/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient CreateClient(string roles, string? tenant) => _fixture.CreateClient(client =>
    {
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "global-admin-test");
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, roles);
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.CategoryHeader, tenant);
        }
    });
}
