// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Features.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.MultiTenancy;

/// <summary>
/// Integration tests for <see cref="Honua.Server.Features.Infrastructure.Middleware.TenantContextMiddleware"/>
/// covering claim-based resolution, header overrides gated on multi-tenant admin role,
/// default-tenant fallback for anonymous reads, and per-request isolation.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public class TenantContextMiddlewareTests
{
    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task ClaimBased_TidClaim_PopulatesTenantContextFromClaim()
    {
        var principal = AuthenticatedPrincipal(claims: ("tid", "tenant-a"));

        var client = CreateApp(principal: principal);
        var response = await client.GetAsync("/tenant");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Claim:tenant-a", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task ClaimBased_TenantIdClaim_PopulatesTenantContextWhenTidMissing()
    {
        var principal = AuthenticatedPrincipal(claims: ("tenant_id", "tenant-b"));

        var client = CreateApp(principal: principal);
        var response = await client.GetAsync("/tenant");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Claim:tenant-b", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task HeaderOverride_AcceptedWhenPrincipalHasMultiTenantAdminRole()
    {
        var principal = AuthenticatedPrincipal(
            claims: ("tid", "tenant-home"),
            roles: ["multi_tenant_admin"]);

        var client = CreateApp(principal: principal);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tenant");
        request.Headers.Add(TenantContextOptions.TenantHeaderName, "tenant-target");

        var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Header:tenant-target", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task HeaderOverride_RejectedWhenPrincipalLacksMultiTenantAdminRole()
    {
        var principal = AuthenticatedPrincipal(
            claims: ("tid", "tenant-home"),
            roles: ["user"]);

        var client = CreateApp(principal: principal);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/tenant");
        request.Headers.Add(TenantContextOptions.TenantHeaderName, "tenant-target");

        var response = await client.SendAsync(request);

        // Header is ignored — falls back to the claim resolution.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Claim:tenant-home", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task Anonymous_FallsBackToConfiguredDefaultTenant()
    {
        var client = CreateApp(principal: null);
        var response = await client.GetAsync("/tenant");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Default:public", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task Anonymous_NoDefaultConfigured_LeavesTenantContextAnonymous()
    {
        var client = CreateApp(principal: null, configureOptions: opt => opt.DefaultTenantId = null);
        var response = await client.GetAsync("/tenant");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Anonymous:<null>", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task RequireTenantId_ReturnsFalseWithReason_WhenAnonymousAndNoDefault()
    {
        var client = CreateApp(principal: null, configureOptions: opt => opt.DefaultTenantId = null);
        var response = await client.GetAsync("/tenant-require");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("anonymous request", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /tenant")]
    public async Task PerRequestIsolation_NoLeakBetweenConcurrentTenants()
    {
        // Send two interleaved requests with different principals. Because the
        // tenant context is scoped per request, neither response should observe
        // the other's tenant id.
        var principalA = AuthenticatedPrincipal(claims: ("tid", "tenant-alpha"));
        var principalB = AuthenticatedPrincipal(claims: ("tid", "tenant-beta"));

        // The injection middleware uses the X-Test-Principal header to select which
        // principal to attach so a single TestServer can serve both requests.
        var client = CreateMultiPrincipalApp(
            principals: new Dictionary<string, ClaimsPrincipal?>
            {
                ["alpha"] = principalA,
                ["beta"] = principalB,
            });

        using var reqA = new HttpRequestMessage(HttpMethod.Get, "/tenant");
        reqA.Headers.Add("X-Test-Principal", "alpha");
        using var reqB = new HttpRequestMessage(HttpMethod.Get, "/tenant");
        reqB.Headers.Add("X-Test-Principal", "beta");

        var taskA = client.SendAsync(reqA);
        var taskB = client.SendAsync(reqB);
        await Task.WhenAll(taskA, taskB);

        var bodyA = await taskA.Result.Content.ReadAsStringAsync();
        var bodyB = await taskB.Result.Content.ReadAsStringAsync();

        Assert.Equal("Claim:tenant-alpha", bodyA);
        Assert.Equal("Claim:tenant-beta", bodyB);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(
        (string Type, string Value) claims,
        string[]? roles = null)
        => AuthenticatedPrincipal(new[] { claims }, roles);

    private static ClaimsPrincipal AuthenticatedPrincipal(
        IEnumerable<(string Type, string Value)> claims,
        string[]? roles = null)
    {
        var claimList = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-1"),
        };
        foreach (var (type, value) in claims)
        {
            claimList.Add(new Claim(type, value));
        }
        if (roles is not null)
        {
            foreach (var role in roles)
            {
                claimList.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(claimList, authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static HttpClient CreateApp(
        ClaimsPrincipal? principal,
        Action<TenantContextOptions>? configureOptions = null)
        => CreateMultiPrincipalApp(
            principals: new Dictionary<string, ClaimsPrincipal?> { ["default"] = principal },
            defaultPrincipalKey: "default",
            configureOptions: configureOptions);

    private static HttpClient CreateMultiPrincipalApp(
        Dictionary<string, ClaimsPrincipal?> principals,
        string? defaultPrincipalKey = null,
        Action<TenantContextOptions>? configureOptions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var config = new ConfigurationBuilder().Build();
        builder.Services.AddHonuaTenantContext(config, opt =>
        {
            // Defaults already exist; allow individual tests to override.
            configureOptions?.Invoke(opt);
        });

        var app = builder.Build();

        // Test-only principal injection middleware. Selects the principal via the
        // X-Test-Principal header (or the default key when absent) and attaches it
        // to HttpContext.User before the tenant middleware runs.
        app.Use(async (ctx, next) =>
        {
            string? key = ctx.Request.Headers.TryGetValue("X-Test-Principal", out var hv) && hv.Count > 0
                ? hv[0]
                : defaultPrincipalKey;

            if (key is not null && principals.TryGetValue(key, out var principal) && principal is not null)
            {
                ctx.User = principal;
            }

            await next();
        });

        app.UseHonuaTenantContext();

        app.MapGet("/tenant", (ITenantContext tenant) =>
        {
            var id = tenant.TenantId ?? "<null>";
            return Results.Text($"{tenant.Source}:{id}");
        });

        app.MapGet("/tenant-require", (ITenantContext tenant) =>
        {
            if (!tenant.RequireTenantId(out var tenantId, out var reason))
            {
                return Results.BadRequest(reason);
            }

            return Results.Text(tenantId);
        });

        app.StartAsync().GetAwaiter().GetResult();
        return app.GetTestClient();
    }
}
