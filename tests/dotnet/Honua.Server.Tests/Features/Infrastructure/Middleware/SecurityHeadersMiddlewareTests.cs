// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Middleware;

/// <summary>
/// Unit tests for the SecurityHeadersMiddleware audit-fix surface (#1144):
/// validates default header set on HTTPS, HSTS gating on HTTP, and CSP override
/// via <see cref="SecurityHeadersOptions"/>.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "Security")]
[Trait("Feature", "SecurityHeaders")]
public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task Get_OverHttps_EmitsAllExpectedSecurityHeaders()
    {
        // Arrange
        var middleware = BuildMiddleware(new SecurityHeadersOptions());
        var context = NewContext("GET", isHttps: true);

        // Act
        await middleware.InvokeAsync(context);

        // Assert - audit-required header set
        Assert.Equal(
            "max-age=63072000; includeSubDomains; preload",
            context.Response.Headers["Strict-Transport-Security"].ToString());
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal(
            "strict-origin-when-cross-origin",
            context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"].ToString());

        var csp = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    [Fact]
    public async Task Get_OverHttp_OmitsHstsButKeepsOtherHeaders()
    {
        // Arrange
        var middleware = BuildMiddleware(new SecurityHeadersOptions());
        var context = NewContext("GET", isHttps: false);

        // Act
        await middleware.InvokeAsync(context);

        // Assert - HSTS is silently dropped on plaintext HTTP per RFC 6797 §7.2
        Assert.False(
            context.Response.Headers.ContainsKey("Strict-Transport-Security"),
            "HSTS must not be sent over HTTP");

        // Other transport-agnostic headers must still ship to defend error pages
        // served over HTTP redirects, misconfigured proxies, etc.
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.False(string.IsNullOrEmpty(context.Response.Headers["Content-Security-Policy"].ToString()));
    }

    [Fact]
    public async Task Options_CanOverrideCsp()
    {
        // Arrange - simulate operator-supplied CSP for a deployment that embeds Grafana panels.
        const string customCsp =
            "default-src 'self'; frame-src https://grafana.example.com; frame-ancestors 'none'";
        var options = new SecurityHeadersOptions
        {
            ContentSecurityPolicy = customCsp,
            Csp = null, // ensure the raw string path is taken, not the builder path
        };
        var middleware = BuildMiddleware(options);
        var context = NewContext("GET", isHttps: true);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(customCsp, context.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public async Task RouteOverride_AppliesLooserCspForMatchingPrefix()
    {
        // Arrange - global stays tight, /docs gets a loosened policy for Scalar UI.
        const string docsCsp =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'";
        var options = new SecurityHeadersOptions
        {
            Csp = null,
            RouteOverrides = new Dictionary<string, SecurityHeadersRouteOverride>
            {
                ["/docs"] = new SecurityHeadersRouteOverride { ContentSecurityPolicy = docsCsp },
            },
        };
        var middleware = BuildMiddleware(options);

        // Act - request inside the override prefix
        var docsContext = NewContext("GET", isHttps: true, path: "/docs/scalar");
        await middleware.InvokeAsync(docsContext);

        // Act - request outside the prefix
        var apiContext = NewContext("GET", isHttps: true, path: "/api/v1/features");
        await middleware.InvokeAsync(apiContext);

        // Assert
        Assert.Equal(docsCsp, docsContext.Response.Headers["Content-Security-Policy"].ToString());
        Assert.NotEqual(docsCsp, apiContext.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Contains(
            "default-src 'self'",
            apiContext.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public async Task OptionsPreflight_SkipsBodyRelevantHeaders()
    {
        // Arrange
        var middleware = BuildMiddleware(new SecurityHeadersOptions());
        var context = NewContext("OPTIONS", isHttps: true);

        // Act
        await middleware.InvokeAsync(context);

        // Assert - CSP and Permissions-Policy are wasted bytes on preflight
        Assert.False(
            context.Response.Headers.ContainsKey("Content-Security-Policy"),
            "CSP must be skipped on OPTIONS preflight");
        Assert.False(
            context.Response.Headers.ContainsKey("Permissions-Policy"),
            "Permissions-Policy must be skipped on OPTIONS preflight");

        // But the cheap transport headers still apply so the preflight 204 is hardened
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"].ToString());
    }

    [Fact]
    public async Task UseHonuaSecurityHeaders_IsExposedForIntegration()
    {
        // Arrange - sanity check that the audit-fix extension method is reachable.
        // The integration agent (#1144) will call this; we assert it compiles and
        // returns the same builder for chaining.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<SecurityHeadersOptions>();
        var sp = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(sp);

        // Act
        var result = builder.UseHonuaSecurityHeaders();

        // Assert
        Assert.Same(builder, result);
        await Task.CompletedTask;
    }

    private static SecurityHeadersMiddleware BuildMiddleware(SecurityHeadersOptions options)
    {
        return new SecurityHeadersMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<SecurityHeadersMiddleware>.Instance,
            options: Options.Create(options));
    }

    private static DefaultHttpContext NewContext(string method, bool isHttps, string path = "/")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = isHttps ? "https" : "http";
        context.Request.Path = path;
        context.Response.Headers.Clear();
        return context;
    }
}
