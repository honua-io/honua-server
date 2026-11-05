// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Middleware;

internal sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityHeadersMiddleware> _logger;
    private readonly SecurityHeadersOptions _options;
    private readonly (string headerName, string policy) _defaultCspHeader;

    public SecurityHeadersMiddleware(
        RequestDelegate next,
        ILogger<SecurityHeadersMiddleware> logger,
        IOptions<SecurityHeadersOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        // Options are fixed for the process lifetime, so build (and validate) the
        // default CSP once instead of reconstructing the policy builder and logging
        // identical validation warnings on every request.
        _defaultCspHeader = BuildContentSecurityPolicy();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Apply security headers to all responses, respecting HTTPS gating,
        // HEAD/OPTIONS shortcuts, and per-route CSP overrides.
        ApplySecurityHeaders(context);

        // Log security headers application for debugging in development
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var requestPath = context.Request.Path.Value ?? "/";
            var requestMethod = context.Request.Method;
            SecurityHeadersLog.SecurityHeadersApplied(_logger, requestPath, requestMethod);
        }

        // Continue to next middleware
        await _next(context);
    }

    private void ApplySecurityHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        var method = context.Request.Method;
        var isPreflightLike = HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var skipBodyRelevantHeaders = _options.SkipHeadersForPreflight && isPreflightLike;

        // Strict-Transport-Security (HSTS) - Force HTTPS for specified duration.
        // Per RFC 6797, HSTS MUST only be sent over a secure transport. We gate it
        // accordingly so HTTP responses (e.g. behind a reverse proxy that already
        // terminates TLS but forwards as HTTP) don't ship a meaningless header.
        if (_options.EnableHsts && (!_options.HstsHttpsOnly || context.Request.IsHttps))
        {
            var hstsValue = $"max-age={_options.HstsMaxAge}";
            if (_options.HstsIncludeSubdomains)
                hstsValue += "; includeSubDomains";
            if (_options.HstsPreload)
                hstsValue += "; preload";

            headers["Strict-Transport-Security"] = hstsValue;
        }

        // Content-Security-Policy - Prevent XSS and injection attacks. CSP, COEP,
        // and Permissions-Policy are only meaningful for resource-bearing responses,
        // so we skip them for HEAD/OPTIONS where the body is empty by definition.
        if (!skipBodyRelevantHeaders)
        {
            var routeOverride = ResolveRouteOverride(context.Request.Path);
            var cspHeader = ResolveContentSecurityPolicy(routeOverride);
            if (!string.IsNullOrEmpty(cspHeader.policy))
            {
                headers[cspHeader.headerName] = cspHeader.policy;
            }
        }

        // X-Frame-Options - Prevent clickjacking attacks
        headers["X-Frame-Options"] = _options.XFrameOptions;

        // X-Content-Type-Options - Prevent MIME-type confusion attacks
        headers["X-Content-Type-Options"] = "nosniff";

        // Referrer-Policy - Control referrer information leakage
        headers["Referrer-Policy"] = _options.ReferrerPolicy;

        // X-XSS-Protection - Enable XSS filtering in older browsers (legacy support)
        if (_options.EnableXssProtection)
        {
            headers["X-XSS-Protection"] = "1; mode=block";
        }

        // Cross-Origin-Opener-Policy - Isolate browsing context
        if (!string.IsNullOrEmpty(_options.CrossOriginOpenerPolicy))
        {
            headers["Cross-Origin-Opener-Policy"] = _options.CrossOriginOpenerPolicy;
        }

        // Cross-Origin-Embedder-Policy - Control cross-origin resource loading
        if (!skipBodyRelevantHeaders && !string.IsNullOrEmpty(_options.CrossOriginEmbedderPolicy))
        {
            headers["Cross-Origin-Embedder-Policy"] = _options.CrossOriginEmbedderPolicy;
        }

        // Permissions-Policy - Restrict access to browser features
        if (!skipBodyRelevantHeaders && !string.IsNullOrEmpty(_options.PermissionsPolicy))
        {
            headers["Permissions-Policy"] = _options.PermissionsPolicy;
        }

        // Custom security headers
        if (_options.CustomHeaders != null)
        {
            foreach (var customHeader in _options.CustomHeaders)
            {
                headers[customHeader.Key] = customHeader.Value;
            }
        }
    }

    private SecurityHeadersRouteOverride? ResolveRouteOverride(PathString path)
    {
        if (_options.RouteOverrides == null || _options.RouteOverrides.Count == 0 || !path.HasValue)
        {
            return null;
        }

        SecurityHeadersRouteOverride? match = null;
        var matchedPrefixLength = -1;

        foreach (var entry in _options.RouteOverrides)
        {
            if (string.IsNullOrEmpty(entry.Key))
            {
                continue;
            }

            if (path.StartsWithSegments(new PathString(entry.Key), StringComparison.OrdinalIgnoreCase)
                && entry.Key.Length > matchedPrefixLength)
            {
                match = entry.Value;
                matchedPrefixLength = entry.Key.Length;
            }
        }

        return match;
    }

    private (string headerName, string policy) ResolveContentSecurityPolicy(SecurityHeadersRouteOverride? routeOverride)
    {
        // Route-level override wins, regardless of global settings, so callers can
        // loosen CSP just for an admin/Swagger sub-tree without rewriting globals.
        if (routeOverride != null && !string.IsNullOrWhiteSpace(routeOverride.ContentSecurityPolicy))
        {
            var overrideHeader = routeOverride.ReportOnly
                ? "Content-Security-Policy-Report-Only"
                : "Content-Security-Policy";
            return (overrideHeader, routeOverride.ContentSecurityPolicy);
        }

        return _defaultCspHeader;
    }

    private (string headerName, string policy) BuildContentSecurityPolicy()
    {
        // If CSP config is not provided, use the simple string policy
        if (_options.Csp == null)
        {
            return ("Content-Security-Policy", _options.ContentSecurityPolicy);
        }

        var config = _options.Csp;
        var headerName = config.ReportOnly ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy";

        ContentSecurityPolicyBuilder builder;

        // Create builder based on policy type
        switch (config.PolicyType)
        {
            case CspPolicyType.ApiOnly:
                builder = ContentSecurityPolicyBuilder.ForApiOnly();
                break;

            case CspPolicyType.GeospatialApi:
                builder = ContentSecurityPolicyBuilder.ForGeospatialApi(config.AllowDevelopmentFeatures);
                break;

            case CspPolicyType.Custom:
                builder = new ContentSecurityPolicyBuilder(config.AllowDevelopmentFeatures);

                // For custom type, start with no defaults and only use provided directives
                if (config.CustomDirectives != null)
                {
                    foreach (var directive in config.CustomDirectives)
                    {
                        builder.AddDirective(directive.Key, directive.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    }
                }
                break;

            default:
                // Fallback to geospatial API policy
                builder = ContentSecurityPolicyBuilder.ForGeospatialApi(config.AllowDevelopmentFeatures);
                break;
        }

        // Apply configuration customizations
        if (config.TrustedTileServers.Length > 0)
        {
            builder.AllowTileServers(config.TrustedTileServers);
        }

        if (config.TrustedCdns.Length > 0)
        {
            builder.AllowMappingCdns(config.TrustedCdns);
        }

        if (config.TrustedGeospatialApis.Length > 0)
        {
            builder.AllowGeospatialApis(config.TrustedGeospatialApis);
        }

        if (config.WebSocketUrls.Length > 0)
        {
            builder.AllowWebSockets(config.WebSocketUrls);
        }

        if (config.AllowedStyleHashes.Length > 0)
        {
            builder.AllowInlineStyleHashes(config.AllowedStyleHashes);
        }

        if (config.AllowedScriptHashes.Length > 0)
        {
            builder.AllowInlineScriptHashes(config.AllowedScriptHashes);
        }

        // Apply custom directives (except for Custom policy type which was handled above)
        if (config.PolicyType != CspPolicyType.Custom && config.CustomDirectives != null)
        {
            foreach (var directive in config.CustomDirectives)
            {
                builder.AddDirective(directive.Key, directive.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        // Add report URI if specified
        if (!string.IsNullOrEmpty(config.ReportUri))
        {
            builder.AddDirective("report-uri", config.ReportUri);
        }

        // Build and validate the policy
        var policy = builder.Build();
        var warnings = builder.Validate();

        // Log warnings for policy validation issues
        foreach (var warning in warnings)
        {
            SecurityHeadersLog.SecurityHeadersConfigurationWarning(_logger, warning);
        }

        return (headerName, policy);
    }
}

public sealed class SecurityHeadersOptions
{
    public const string SectionName = "SecurityHeaders";

    public bool EnableHsts { get; set; } = true;

    public int HstsMaxAge { get; set; } = 63072000; // 2 years

    public bool HstsIncludeSubdomains { get; set; } = true;

    // Operators must own the apex domain and accept the lock-in before enabling HSTS preload.
    public bool HstsPreload { get; set; } = true;

    // HSTS stays limited to HTTPS, and a TLS-terminating proxy should correct the perceived scheme instead of turning this off.
    public bool HstsHttpsOnly { get; set; } = true;

    public bool SkipHeadersForPreflight { get; set; } = true;

    public string ContentSecurityPolicy { get; set; } =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; media-src 'self'; object-src 'none'; " +
        "frame-ancestors 'none'; form-action 'self'; base-uri 'self'";

    public ContentSecurityPolicyConfig? Csp { get; set; }

    public string XFrameOptions { get; set; } = "DENY";

    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    public bool EnableXssProtection { get; set; } = true;

    public string CrossOriginOpenerPolicy { get; set; } = "same-origin";

    // The embedder policy stays relaxed because a stricter value breaks the API docs and map tile providers.
    public string CrossOriginEmbedderPolicy { get; set; } = "unsafe-none";

    public string PermissionsPolicy { get; set; } =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=(), " +
        "magnetometer=(), gyroscope=(), accelerometer=(), ambient-light-sensor=(), " +
        "autoplay=(), encrypted-media=(), fullscreen=(), picture-in-picture=()";

    public Dictionary<string, string>? CustomHeaders { get; set; }

    public Dictionary<string, SecurityHeadersRouteOverride>? RouteOverrides { get; set; }
}

public sealed class SecurityHeadersRouteOverride
{
    public string? ContentSecurityPolicy { get; set; }

    public bool ReportOnly { get; set; }
}

public sealed class ContentSecurityPolicyConfig
{
    public CspPolicyType PolicyType { get; set; } = CspPolicyType.GeospatialApi;

    public bool AllowDevelopmentFeatures { get; set; }

    public string[] TrustedTileServers { get; set; } = Array.Empty<string>();

    public string[] TrustedCdns { get; set; } = Array.Empty<string>();

    public string[] TrustedGeospatialApis { get; set; } = Array.Empty<string>();

    public string[] WebSocketUrls { get; set; } = Array.Empty<string>();

    public string[] AllowedStyleHashes { get; set; } = Array.Empty<string>();

    public string[] AllowedScriptHashes { get; set; } = Array.Empty<string>();

    public Dictionary<string, string>? CustomDirectives { get; set; }

    public string? ReportUri { get; set; }

    public bool ReportOnly { get; set; }
}

public enum CspPolicyType
{
    ApiOnly = 0,

    GeospatialApi = 1,

    Custom = 2
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IServiceCollection AddSecurityHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind configuration with validation
        services.Configure<SecurityHeadersOptions>(options =>
        {
            configuration.GetSection(SecurityHeadersOptions.SectionName).Bind(options);

            // Validate critical configuration during startup
            ValidateSecurityHeadersOptions(options);
        });

        return services;
    }

    public static IServiceCollection AddSecurityHeaders(
        this IServiceCollection services,
        Action<SecurityHeadersOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);
        return services;
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    public static IApplicationBuilder UseHonuaSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    private static void ValidateSecurityHeadersOptions(SecurityHeadersOptions options)
    {
        var errors = new List<string>();

        // Validate HSTS configuration
        if (options.EnableHsts && options.HstsMaxAge < 86400) // Less than 1 day
        {
            errors.Add("HSTS max-age should be at least 86400 seconds (1 day) for effective security");
        }

        // Validate CSP is not empty
        if (string.IsNullOrWhiteSpace(options.ContentSecurityPolicy))
        {
            errors.Add("Content Security Policy should not be empty for XSS protection");
        }

        // Validate X-Frame-Options
        if (string.IsNullOrWhiteSpace(options.XFrameOptions))
        {
            errors.Add("X-Frame-Options should not be empty for clickjacking protection");
        }

        // Validate custom headers don't override security headers
        if (options.CustomHeaders != null)
        {
            var securityHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Strict-Transport-Security", "Content-Security-Policy", "X-Frame-Options",
                "X-Content-Type-Options", "Referrer-Policy", "X-XSS-Protection"
            };

            foreach (var customHeader in options.CustomHeaders.Keys.Where(securityHeaders.Contains))
            {
                errors.Add($"Custom header '{customHeader}' conflicts with built-in security header");
            }
        }

        if (errors.Count > 0)
        {
            var errorMessage = "Invalid security headers configuration:" + Environment.NewLine +
                              string.Join(Environment.NewLine, errors);
            throw new InvalidOperationException(errorMessage);
        }
    }
}

internal static partial class SecurityHeadersLog
{
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Debug,
        Message = "Security headers applied to response: {RequestMethod} {RequestPath}")]
    public static partial void SecurityHeadersApplied(ILogger logger, string requestPath, string requestMethod);

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Security headers configuration validated successfully")]
    public static partial void SecurityHeadersConfigurationValidated(ILogger logger);

    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Warning,
        Message = "Security headers configuration warning: {Warning}")]
    public static partial void SecurityHeadersConfigurationWarning(ILogger logger, string warning);
}
