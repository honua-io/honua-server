// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure;

public sealed class ProgramMiddlewareOrderTests
{
    [UnitTest]
    public void Program_RegistersSerilogRequestLogging_BeforeShortCircuitingMiddleware()
    {
        var source = File.ReadAllText(ResolveProgramPath());

        var serilogIndex = source.IndexOf("app.UseSerilogRequestLogging(", StringComparison.Ordinal);
        var exceptionIndex = source.IndexOf("app.UseGlobalExceptionHandling();", StringComparison.Ordinal);
        var authIndex = source.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var portalIndex = source.IndexOf("app.UsePortalTokenAuthentication();", StringComparison.Ordinal);
        var tenantIndex = source.IndexOf("app.UseHonuaTenantContext();", StringComparison.Ordinal);
        var normalRateLimitIndex = source.LastIndexOf("app.UseRateLimiting();", StringComparison.Ordinal);
        var authorizationIndex = source.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);
        var schemaIndex = source.IndexOf("app.UseHonuaTenantSchemaRouting();", StringComparison.Ordinal);
        // Output caching is wired as an entitlement-gated `app.UseWhen` branch, so the
        // call sits on the branch builder as `entitled.UseOutputCache())` — an expression-bodied
        // lambda argument with no trailing semicolon. Match without one, or this assertion silently
        // stops finding the call it exists to order.
        var outputCacheIndex = source.IndexOf("UseOutputCache()", StringComparison.Ordinal);

        serilogIndex.Should().BeGreaterThan(-1);
        exceptionIndex.Should().BeGreaterThan(-1);
        authIndex.Should().BeGreaterThan(-1);
        portalIndex.Should().BeGreaterThan(authIndex);
        tenantIndex.Should().BeGreaterThan(portalIndex,
            "tenant context must see the hydrated portal principal");
        normalRateLimitIndex.Should().BeGreaterThan(tenantIndex,
            "rate limiting partitions by resolved tenant and authenticated actor");
        authorizationIndex.Should().BeGreaterThan(normalRateLimitIndex,
            "rate limiting must run before authorization so 401/403 brute-force traffic consumes caller buckets");
        authorizationIndex.Should().BeLessThan(schemaIndex,
            "authorization must run before tenant schema routing");
        outputCacheIndex.Should().BeGreaterThan(-1);

        serilogIndex.Should().BeLessThan(exceptionIndex);
        serilogIndex.Should().BeLessThan(authIndex);
        serilogIndex.Should().BeLessThan(outputCacheIndex);
    }

    [UnitTest]
    public void Program_RegistersSecurityHeaders_ImmediatelyAfterForwardedHeadersAndBeforeHostValidation()
    {
        var source = File.ReadAllText(ResolveProgramPath());

        var forwardedHeadersIndex = source.IndexOf("app.UseForwardedHeaders();", StringComparison.Ordinal);
        var securityHeadersIndex = source.IndexOf("app.UseSecurityHeaders();", StringComparison.Ordinal);
        var hostValidationIndex = source.IndexOf("app.UseHostValidation();", StringComparison.Ordinal);

        forwardedHeadersIndex.Should().BeGreaterThan(-1);
        securityHeadersIndex.Should().BeGreaterThan(forwardedHeadersIndex,
            "security headers must observe the forwarded HTTPS scheme for HSTS");
        securityHeadersIndex.Should().BeLessThan(hostValidationIndex,
            "host validation and HTTPS redirection responses must emit security headers");
    }

    [UnitTest]
    public void Program_RegistersDoubleSlashNormalizationStartupFilter_BeforeImplicitRouting()
    {
        var source = File.ReadAllText(ResolveProgramPath());

        var pathBaseFilterIndex = source.IndexOf("GeoServicesPathBaseStartupFilter", StringComparison.Ordinal);
        var doubleSlashFilterIndex = source.IndexOf("DoubleSlashPathNormalizationStartupFilter", StringComparison.Ordinal);

        pathBaseFilterIndex.Should().BeGreaterThan(-1);
        doubleSlashFilterIndex.Should().BeGreaterThan(pathBaseFilterIndex,
            "double-slash normalization must run as an IStartupFilter ahead of implicit UseRouting");
    }

    [UnitTest]
    public async Task DoubleSlashPathNormalizationStartupFilter_NormalizesRegularRoutesBeforeRouting_AndPreservesEmptyAdminConnectionId()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, DoubleSlashPathNormalizationStartupFilter>();

        await using var app = builder.Build();
        app.MapGet("/healthz/live", () => Results.Ok("ok"));
        app.Use((context, next) =>
        {
            var path = context.Request.Path.Value;
            if (!string.IsNullOrEmpty(path) &&
                path.Contains("//", StringComparison.Ordinal) &&
                DoubleSlashPathNormalizationStartupFilter.IsMissingAdminConnectionIdPath(path))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }

            return next();
        });
        await app.StartAsync();

        var client = app.GetTestClient();
        var normalized = await client.GetAsync("/healthz///live");
        normalized.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var missingConnectionId = await client.GetAsync("/api/v1/admin/connections//tables");
        missingConnectionId.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [UnitTest]
    public void Program_AuthenticatesAndRejectsMcpBearer_BeforeTenantAuthorityBoundary()
    {
        var source = File.ReadAllText(ResolveProgramPath());

        var authenticateIndex = source.IndexOf(".UseMcpBearerAuthentication(app);", StringComparison.Ordinal);
        var auditIndex = source.IndexOf("app.UseHonuaAuditLog();", StringComparison.Ordinal);
        var invalidRateLimitIndex = source.IndexOf("invalidBearer.UseRateLimiting();", StringComparison.Ordinal);
        var rejectIndex = source.IndexOf(".UseMcpBearerAuthenticationRejection(invalidBearer);", StringComparison.Ordinal);
        var tenantIndex = source.IndexOf("app.UseHonuaTenantContext();", StringComparison.Ordinal);
        var schemaIndex = source.IndexOf("app.UseHonuaTenantSchemaRouting();", StringComparison.Ordinal);
        var statusIndex = source.IndexOf("app.UseHonuaTenantStatusEnforcement();", StringComparison.Ordinal);
        var normalRateLimitIndex = source.LastIndexOf("app.UseRateLimiting();", StringComparison.Ordinal);

        authenticateIndex.Should().BeGreaterThan(-1);
        auditIndex.Should().BeGreaterThan(authenticateIndex,
            "audit must observe the canonical principal established by MCP bearer validation");
        invalidRateLimitIndex.Should().BeGreaterThan(auditIndex,
            "invalid credentials must enter the shared rate limiter inside the audit boundary");
        rejectIndex.Should().BeGreaterThan(invalidRateLimitIndex);
        rejectIndex.Should().BeLessThan(tenantIndex,
            "invalid credentials must be rejected before tenant resolution");
        tenantIndex.Should().BeLessThan(schemaIndex);
        schemaIndex.Should().BeLessThan(statusIndex);
        tenantIndex.Should().BeLessThan(normalRateLimitIndex,
            "rate limiting needs the resolved tenant identity");
        normalRateLimitIndex.Should().BeLessThan(schemaIndex,
            "failed schema routes must consume the tenant request budget before returning 503");
    }

    private static string ResolveProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            // False positive: all later segments are fixed relative literals, never absolute.
            var candidate = Path.Join(directory.FullName, "src", "Honua.Server", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Honua.Server/Program.cs from the test base directory.");
    }
}
