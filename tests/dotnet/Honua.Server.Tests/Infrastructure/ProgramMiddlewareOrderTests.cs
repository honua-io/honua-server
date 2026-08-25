// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure;

public sealed class ProgramMiddlewareOrderTests
{
    [UnitTest]
    public void Program_RegistersSerilogRequestLogging_BeforeShortCircuitingMiddleware()
    {
        var source = File.ReadAllText(ResolveProgramPath());

        var serilogIndex = source.IndexOf("app.UseSerilogRequestLogging(", StringComparison.Ordinal);
        var exceptionIndex = source.IndexOf("app.UseGlobalExceptionHandling();", StringComparison.Ordinal);
        var authIndex = source.IndexOf("app.UseApiKeyAuthentication();", StringComparison.Ordinal);
        // Output caching is wired as an entitlement-gated `app.UseWhen` branch (#2998), so the
        // call sits on the branch builder as `entitled.UseOutputCache())` — an expression-bodied
        // lambda argument with no trailing semicolon. Match without one, or this assertion silently
        // stops finding the call it exists to order.
        var outputCacheIndex = source.IndexOf("UseOutputCache()", StringComparison.Ordinal);

        serilogIndex.Should().BeGreaterThan(-1);
        exceptionIndex.Should().BeGreaterThan(-1);
        authIndex.Should().BeGreaterThan(-1);
        outputCacheIndex.Should().BeGreaterThan(-1);

        serilogIndex.Should().BeLessThan(exceptionIndex);
        serilogIndex.Should().BeLessThan(authIndex);
        serilogIndex.Should().BeLessThan(outputCacheIndex);
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
        statusIndex.Should().BeLessThan(normalRateLimitIndex);
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
