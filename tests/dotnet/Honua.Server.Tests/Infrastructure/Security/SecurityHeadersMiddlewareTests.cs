// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Tests for the SecurityHeadersMiddleware class.
/// Verifies security header generation including enhanced CSP functionality.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "Security")]
[Trait("Feature", "SecurityHeaders")]
public class SecurityHeadersMiddlewareTests
{
    private readonly ListLogger<SecurityHeadersMiddleware> _logger;

    public SecurityHeadersMiddlewareTests()
    {
        _logger = new ListLogger<SecurityHeadersMiddleware>();
    }

    [Fact]
    public async Task InvokeAsync_WithDefaultOptions_AppliesBasicSecurityHeaders()
    {
        // Arrange
        var options = CreateDefaultOptions();
        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal("max-age=63072000; includeSubDomains; preload", context.Response.Headers["Strict-Transport-Security"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("1; mode=block", context.Response.Headers["X-XSS-Protection"]);
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"]);
        Assert.Equal("unsafe-none", context.Response.Headers["Cross-Origin-Embedder-Policy"]);
        Assert.True(context.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.True(context.Response.Headers.ContainsKey("Permissions-Policy"));
    }

    [Fact]
    public async Task InvokeAsync_WithHstsDisabled_DoesNotApplyHsts()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.EnableHsts = false;
        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task InvokeAsync_WithCspConfig_UsesEnhancedCspBuilder()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.GeospatialApi,
            AllowDevelopmentFeatures = false,
            TrustedTileServers = new[] { "tiles.example.com" },
            TrustedCdns = new[] { "cdn.example.com" }
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var cspHeader = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("tiles.example.com", cspHeader);
        Assert.Contains("cdn.example.com", cspHeader);
        Assert.Contains("img-src 'self' data: blob:", cspHeader);
        Assert.Contains("script-src 'self'", cspHeader);
        Assert.DoesNotContain("'unsafe-eval'", cspHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithDevelopmentFeatures_AllowsUnsafeDirectives()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.GeospatialApi,
            AllowDevelopmentFeatures = true
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var cspHeader = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("'unsafe-eval'", cspHeader);
        Assert.Contains("localhost:", cspHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithApiOnlyPolicy_CreatesRestrictivePolicy()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.ApiOnly
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var cspHeader = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("default-src 'none'", cspHeader);
        Assert.Contains("script-src 'none'", cspHeader);
        Assert.Contains("img-src 'none'", cspHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithReportOnlyMode_UsesReportOnlyHeader()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.GeospatialApi,
            ReportOnly = true,
            ReportUri = "/csp-report"
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(context.Response.Headers.ContainsKey("Content-Security-Policy-Report-Only"));
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));

        var reportOnlyHeader = context.Response.Headers["Content-Security-Policy-Report-Only"].ToString();
        Assert.Contains("report-uri /csp-report", reportOnlyHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithCustomPolicy_UsesCustomDirectives()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.Custom,
            CustomDirectives = new Dictionary<string, string>
            {
                { "default-src", "'self'" },
                { "script-src", "'self' 'unsafe-eval'" },
                { "custom-directive", "custom-value" }
            }
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var cspHeader = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("default-src 'self'", cspHeader);
        Assert.Contains("script-src 'self' 'unsafe-eval'", cspHeader);
        Assert.Contains("custom-directive custom-value", cspHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithWebSocketUrls_AddsWebSocketsToConnectSrc()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.GeospatialApi,
            WebSocketUrls = new[] { "https://api.example.com", "http://localhost:3000" }
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var cspHeader = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("wss://api.example.com", cspHeader);
        Assert.Contains("ws://localhost:3000", cspHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithScriptHashes_AddsHashesToScriptSrc()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.GeospatialApi,
            AllowedScriptHashes = new[] { "abc123def456", "'sha256-xyz789'" }
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var cspHeader = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains("'sha256-abc123def456'", cspHeader);
        Assert.Contains("'sha256-xyz789'", cspHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithCustomHeaders_AppliesCustomHeaders()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.CustomHeaders = new Dictionary<string, string>
        {
            { "X-Custom-Header", "custom-value" },
            { "X-Another-Header", "another-value" }
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal("custom-value", context.Response.Headers["X-Custom-Header"]);
        Assert.Equal("another-value", context.Response.Headers["X-Another-Header"]);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidCspConfiguration_LogsWarnings()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.Csp = new ContentSecurityPolicyConfig
        {
            PolicyType = CspPolicyType.GeospatialApi,
            AllowDevelopmentFeatures = false,
            CustomDirectives = new Dictionary<string, string>
            {
                { "script-src", "'self' 'unsafe-eval'" } // Unsafe in production
            }
        };

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        // Verify that warnings were logged (this is a basic check - in a real test,
        // you'd verify the exact log messages using a test logger)
        Assert.Contains(_logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("'unsafe-eval'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_WithHstsPreload_IncludesPreloadDirective()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.HstsPreload = true;

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        var hstsHeader = context.Response.Headers["Strict-Transport-Security"].ToString();
        Assert.Contains("preload", hstsHeader);
    }

    [Fact]
    public async Task InvokeAsync_WithXssProtectionDisabled_DoesNotApplyXssHeader()
    {
        // Arrange
        var options = CreateDefaultOptions();
        options.Value.EnableXssProtection = false;

        var middleware = CreateMiddleware(options);
        var context = CreateHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.False(context.Response.Headers.ContainsKey("X-XSS-Protection"));
    }

    [Fact]
    public void SecurityHeadersOptions_Validate_WithValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecurityHeaders:EnableHsts"] = "true",
                ["SecurityHeaders:HstsMaxAge"] = "86400",
                ["SecurityHeaders:ContentSecurityPolicy"] = "default-src 'self'",
                ["SecurityHeaders:XFrameOptions"] = "DENY"
            })
            .Build();

        var services = new ServiceCollection();

        // Act & Assert - Should not throw
        var exception = Record.Exception(() => services.AddSecurityHeaders(configuration));
        Assert.Null(exception);
    }

    private IOptions<SecurityHeadersOptions> CreateDefaultOptions()
    {
        var options = new SecurityHeadersOptions();
        return Options.Create(options);
    }

    private SecurityHeadersMiddleware CreateMiddleware(IOptions<SecurityHeadersOptions> options)
    {
        var next = new RequestDelegate(context => Task.CompletedTask);
        return new SecurityHeadersMiddleware(next, _logger, options);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers.Clear();
        // Default to HTTPS so HSTS-gating doesn't suppress headers in legacy unit tests.
        context.Request.Scheme = "https";
        return context;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
