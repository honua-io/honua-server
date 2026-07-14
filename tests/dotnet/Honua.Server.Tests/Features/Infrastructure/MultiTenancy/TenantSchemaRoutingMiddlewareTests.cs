// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.MultiTenancy;

/// <summary>
/// Integration tests for <see cref="Honua.Infrastructure.MultiTenancy.TenantSchemaRoutingMiddleware"/>
/// (issue #346). Proves schema-per-tenant isolation (tenant A cannot observe tenant B's schema),
/// that the default single-tenant pipeline is unchanged when routing is disabled, and that the
/// usage-metering counter increments per request.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public class TenantSchemaRoutingMiddlewareTests
{
    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /schema")]
    public async Task RoutingEnabled_DerivesTenantSchemaFromTenantId()
    {
        var principal = AuthenticatedPrincipal(("tid", "acme"));

        var client = await CreateAppAsync(
            principals: new Dictionary<string, ClaimsPrincipal?> { ["default"] = principal },
            defaultPrincipalKey: "default",
            routingEnabled: true);

        var response = await client.GetAsync("/schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("tenant_acme", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /schema")]
    public async Task RoutingDisabled_LeavesSchemaContextNull_DefaultBehaviorUnchanged()
    {
        // The single-tenant default: even with a resolved tenant, no schema override is issued
        // because the middleware is not registered. The connection keeps its default schema.
        var principal = AuthenticatedPrincipal(("tid", "acme"));

        var client = await CreateAppAsync(
            principals: new Dictionary<string, ClaimsPrincipal?> { ["default"] = principal },
            defaultPrincipalKey: "default",
            routingEnabled: false);

        var response = await client.GetAsync("/schema");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("<null>", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /schema")]
    public async Task RoutingEnabled_UnroutedPublicTenant_KeepsDefaultSchema()
    {
        // The anonymous public tenant is excluded from routing so unauthenticated OGC reads
        // continue to use the connection's configured default schema.
        var client = await CreateAppAsync(
            principals: new Dictionary<string, ClaimsPrincipal?> { ["default"] = null },
            defaultPrincipalKey: "default",
            routingEnabled: true);

        var response = await client.GetAsync("/schema");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("<null>", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /schema")]
    public async Task TenantIsolation_TenantACannotReadTenantBSchema()
    {
        // Security property: two interleaved requests as different tenants must each be routed
        // to their own schema only. Neither request may observe the other tenant's schema.
        var principalA = AuthenticatedPrincipal(("tid", "alpha"));
        var principalB = AuthenticatedPrincipal(("tid", "beta"));

        var client = await CreateAppAsync(
            principals: new Dictionary<string, ClaimsPrincipal?>
            {
                ["alpha"] = principalA,
                ["beta"] = principalB,
            },
            routingEnabled: true);

        using var reqA = new HttpRequestMessage(HttpMethod.Get, "/schema");
        reqA.Headers.Add("X-Test-Principal", "alpha");
        using var reqB = new HttpRequestMessage(HttpMethod.Get, "/schema");
        reqB.Headers.Add("X-Test-Principal", "beta");

        var taskA = client.SendAsync(reqA);
        var taskB = client.SendAsync(reqB);
        await Task.WhenAll(taskA, taskB);

        var bodyA = await taskA.Result.Content.ReadAsStringAsync();
        var bodyB = await taskB.Result.Content.ReadAsStringAsync();

        Assert.Equal("tenant_alpha", bodyA);
        Assert.Equal("tenant_beta", bodyB);
        Assert.NotEqual(bodyA, bodyB);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /schema")]
    public async Task TestSchemaHeaderWins_OverTenantRouting()
    {
        // When a pinned schema is already present (e.g. set by the test schema header), tenant
        // routing must never overwrite it — test isolation continues to win.
        var principal = AuthenticatedPrincipal(("tid", "acme"));

        var client = await CreateAppAsync(
            principals: new Dictionary<string, ClaimsPrincipal?> { ["default"] = principal },
            defaultPrincipalKey: "default",
            routingEnabled: true,
            pinnedSchema: "pinned_test_schema");

        var response = await client.GetAsync("/schema");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("pinned_test_schema", body);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /usage")]
    public async Task UsageMeter_IncrementsPerRequest_PerTenant()
    {
        var principal = AuthenticatedPrincipal(("tid", "acme"));

        var client = await CreateAppAsync(
            principals: new Dictionary<string, ClaimsPrincipal?> { ["default"] = principal },
            defaultPrincipalKey: "default",
            routingEnabled: true);

        // Three metered requests for the same tenant. The /usage read below is itself metered
        // as it passes through the same routing middleware, so the observed count is 4.
        await client.GetAsync("/schema");
        await client.GetAsync("/schema");
        await client.GetAsync("/schema");

        var response = await client.GetAsync("/usage");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("acme=4", body);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal((string Type, string Value) claim)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-1"),
            new(claim.Type, claim.Value),
        };
        var identity = new ClaimsIdentity(claims, authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static async Task<HttpClient> CreateAppAsync(
        Dictionary<string, ClaimsPrincipal?> principals,
        bool routingEnabled,
        string? defaultPrincipalKey = null,
        string? pinnedSchema = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var settings = new Dictionary<string, string?>
        {
            ["MultiTenancy:SchemaRouting:Enabled"] = routingEnabled ? "true" : "false",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        builder.Services.AddHonuaTenantContext(config, _ => { });
        builder.Services.AddHonuaTenantSchemaRouting(config);

        // Mirror the production registration of the request-scoped schema context.
        builder.Services.AddScoped<SchemaContext>();
        builder.Services.AddScoped<ISchemaContext>(sp => sp.GetRequiredService<SchemaContext>());

        var app = builder.Build();

        // Test-only principal injection middleware: selects the principal via X-Test-Principal
        // (or the default key) and attaches it to HttpContext.User before tenant resolution.
        app.Use(async (ctx, next) =>
        {
            string? key = ctx.Request.Headers.TryGetValue("X-Test-Principal", out var hv) && hv.Count > 0
                ? hv[0]
                : defaultPrincipalKey;

            if (key is not null && principals.TryGetValue(key, out var principal) && principal is not null)
            {
                ctx.User = principal;
            }

            // Simulate a pinned schema (e.g. test schema header already applied) when requested.
            if (pinnedSchema is not null)
            {
                ctx.RequestServices.GetRequiredService<SchemaContext>().CurrentSchema = pinnedSchema;
            }

            await next();
        });

        app.UseHonuaTenantContext();
        app.UseHonuaTenantSchemaRouting();

        // Report the schema the request was routed to (or <null> when none was set). Reads the
        // same request-scoped schema context the database search-path applier reads.
        app.MapGet("/schema", (ISchemaContext schema) =>
            Results.Text(schema.CurrentSchema ?? "<null>"));

        // Report the usage counter recorded for the resolved tenant.
        app.MapGet("/usage", (ITenantContext tenant, ITenantUsageMeter meter) =>
        {
            var id = tenant.TenantId ?? "<null>";
            foreach (var snapshot in meter.Snapshot().Where(s => string.Equals(s.TenantId, id, StringComparison.Ordinal)))
            {
                return Results.Text($"{id}={snapshot.RequestCount}");
            }

            return Results.Text($"{id}=0");
        });

        await app.StartAsync();
        return app.GetTestClient();
    }
}
