// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Middleware that adds comprehensive security headers to protect against common web vulnerabilities.
/// Implements defense-in-depth security practices including CSP, HSTS, frame protection, and more.
/// </summary>
/// <remarks>
/// Security headers added:
/// - Strict-Transport-Security (HSTS): Enforces HTTPS connections
/// - Content-Security-Policy (CSP): Prevents XSS and injection attacks
/// - X-Frame-Options: Prevents clickjacking attacks
/// - X-Content-Type-Options: Prevents MIME-type confusion attacks
/// - Referrer-Policy: Controls referrer information leakage
/// - X-XSS-Protection: Enables XSS filtering in older browsers
/// - Cross-Origin-Opener-Policy (COOP): Isolates browsing context
/// - Cross-Origin-Embedder-Policy (COEP): Controls cross-origin resource loading
/// - Permissions-Policy: Restricts access to browser features
///
/// This middleware should be placed early in the pipeline to ensure all responses
/// receive security headers, including error responses.
/// </remarks>
internal sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityHeadersMiddleware> _logger;
    private readonly SecurityHeadersOptions _options;

    public SecurityHeadersMiddleware(
        RequestDelegate next,
        ILogger<SecurityHeadersMiddleware> logger,
        IOptions<SecurityHeadersOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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

    /// <summary>
    /// Applies comprehensive security headers to the HTTP response, taking into
    /// account HTTPS gating for HSTS, HEAD/OPTIONS short-circuits, and route-prefix
    /// CSP overrides defined in <see cref="SecurityHeadersOptions.RouteOverrides"/>.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
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
            var cspHeader = BuildContentSecurityPolicy(routeOverride);
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

    /// <summary>
    /// Resolves the most-specific route override (by longest path-prefix match)
    /// for the supplied request path. Returns <c>null</c> when no override applies.
    /// </summary>
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

    /// <summary>
    /// Builds the Content Security Policy header based on configuration, optionally
    /// substituting a route-specific override (e.g. a looser policy for admin/Swagger UI).
    /// </summary>
    /// <param name="routeOverride">Optional per-route CSP override.</param>
    /// <returns>A tuple containing the header name and policy value</returns>
    private (string headerName, string policy) BuildContentSecurityPolicy(SecurityHeadersRouteOverride? routeOverride = null)
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

/// <summary>
/// Configuration options for security headers middleware.
/// Provides comprehensive security header configuration with secure defaults.
/// </summary>
public sealed class SecurityHeadersOptions
{
    /// <summary>
    /// Configuration section name for binding from appsettings.json.
    /// </summary>
    public const string SectionName = "SecurityHeaders";

    /// <summary>
    /// Enable HTTP Strict Transport Security (HSTS) header.
    /// Default: true (recommended for production).
    /// </summary>
    public bool EnableHsts { get; set; } = true;

    /// <summary>
    /// HSTS max-age directive in seconds.
    /// Default: 63072000 (2 years), matching hstspreload.org submission requirements.
    /// </summary>
    public int HstsMaxAge { get; set; } = 63072000; // 2 years

    /// <summary>
    /// Include subdomains in HSTS policy.
    /// Default: true (recommended for complete coverage).
    /// </summary>
    public bool HstsIncludeSubdomains { get; set; } = true;

    /// <summary>
    /// Enable HSTS preload (eligible for browser preload-list submission).
    /// Default: true. Operators MUST own the apex domain and accept the lock-in
    /// before enabling preload in production.
    /// </summary>
    public bool HstsPreload { get; set; } = true;

    /// <summary>
    /// Restrict HSTS emission to HTTPS requests only, per RFC 6797 §7.2.
    /// Default: true. Operators behind TLS-terminating proxies that forward HTTP
    /// internally should keep this enabled and use ForwardedHeaders/UseHttpsRedirection
    /// so that <c>HttpContext.Request.IsHttps</c> reflects the edge scheme.
    /// </summary>
    public bool HstsHttpsOnly { get; set; } = true;

    /// <summary>
    /// Skip CSP / COEP / Permissions-Policy on HEAD and OPTIONS responses.
    /// Default: true. These methods carry no body the browser can interpret, so
    /// emitting body-relevant headers is wasted bytes (notably on CORS preflight).
    /// Transport-level headers (HSTS, X-Frame-Options, X-Content-Type-Options,
    /// Referrer-Policy, COOP) are still applied.
    /// </summary>
    public bool SkipHeadersForPreflight { get; set; } = true;

    /// <summary>
    /// Content Security Policy (CSP) directives.
    /// Default: Strict policy suitable for geospatial APIs.
    /// </summary>
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; media-src 'self'; object-src 'none'; " +
        "frame-ancestors 'none'; form-action 'self'; base-uri 'self'";

    /// <summary>
    /// CSP policy configuration for different environments.
    /// When specified, overrides the ContentSecurityPolicy setting based on environment.
    /// </summary>
    public ContentSecurityPolicyConfig? Csp { get; set; }

    /// <summary>
    /// X-Frame-Options header value.
    /// Default: "DENY" (prevents all framing).
    /// </summary>
    public string XFrameOptions { get; set; } = "DENY";

    /// <summary>
    /// Referrer-Policy header value.
    /// Default: "strict-origin-when-cross-origin" (balanced security and functionality).
    /// </summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>
    /// Enable X-XSS-Protection header (for legacy browser support).
    /// Default: true (provides defense-in-depth).
    /// </summary>
    public bool EnableXssProtection { get; set; } = true;

    /// <summary>
    /// Cross-Origin-Opener-Policy header value.
    /// Default: "same-origin" (isolates browsing context).
    /// </summary>
    public string CrossOriginOpenerPolicy { get; set; } = "same-origin";

    /// <summary>
    /// Cross-Origin-Embedder-Policy header value.
    /// Default: "unsafe-none". <c>require-corp</c> breaks Scalar / Swagger UI and
    /// most map tile providers without explicit CORP headers from those origins,
    /// so we ship the relaxed default and let deployments tighten it as needed.
    /// </summary>
    public string CrossOriginEmbedderPolicy { get; set; } = "unsafe-none";

    /// <summary>
    /// Permissions-Policy header value (restricts browser features).
    /// Default: Restrictive policy disabling unnecessary features.
    /// </summary>
    public string PermissionsPolicy { get; set; } =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=(), " +
        "magnetometer=(), gyroscope=(), accelerometer=(), ambient-light-sensor=(), " +
        "autoplay=(), encrypted-media=(), fullscreen=(), picture-in-picture=()";

    /// <summary>
    /// Additional custom security headers to apply.
    /// Key-value pairs of header names and values.
    /// </summary>
    public Dictionary<string, string>? CustomHeaders { get; set; }

    /// <summary>
    /// Per-route CSP overrides keyed by request path prefix (e.g. <c>/admin</c>,
    /// <c>/docs</c>). The longest matching prefix wins. Use this to loosen CSP for
    /// Scalar/Swagger UI or admin HTML sub-trees without weakening the API default.
    /// </summary>
    public Dictionary<string, SecurityHeadersRouteOverride>? RouteOverrides { get; set; }
}

/// <summary>
/// Route-prefix scoped overrides for security headers. Currently exposes a CSP
/// substitution; additional per-route knobs can be added without changing the
/// middleware signature.
/// </summary>
public sealed class SecurityHeadersRouteOverride
{
    /// <summary>
    /// CSP value to use for responses matching the parent route prefix.
    /// When null/empty the global CSP applies unchanged.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary>
    /// When true, emits the override as <c>Content-Security-Policy-Report-Only</c>
    /// so violations are reported without blocking content. Useful when staging a
    /// stricter policy for a UI sub-tree before enforcing it.
    /// </summary>
    public bool ReportOnly { get; set; }
}

/// <summary>
/// Configuration for Content Security Policy with environment-specific settings.
/// </summary>
public sealed class ContentSecurityPolicyConfig
{
    /// <summary>
    /// CSP policy type to use. Determines the base policy before customizations.
    /// </summary>
    public CspPolicyType PolicyType { get; set; } = CspPolicyType.GeospatialApi;

    /// <summary>
    /// Whether to use development-friendly CSP settings.
    /// When true, allows unsafe-eval and relaxed localhost policies.
    /// </summary>
    public bool AllowDevelopmentFeatures { get; set; }

    /// <summary>
    /// Trusted tile server domains for map rendering.
    /// These will be added to img-src and connect-src directives.
    /// </summary>
    public string[] TrustedTileServers { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Trusted CDN domains for mapping libraries.
    /// These will be added to script-src, style-src, and font-src directives.
    /// </summary>
    public string[] TrustedCdns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Trusted geospatial API domains.
    /// These will be added to connect-src directive.
    /// </summary>
    public string[] TrustedGeospatialApis { get; set; } = Array.Empty<string>();

    /// <summary>
    /// WebSocket URLs for real-time data connections.
    /// These will be added to connect-src directive with ws:// or wss:// protocols.
    /// </summary>
    public string[] WebSocketUrls { get; set; } = Array.Empty<string>();

    /// <summary>
    /// SHA256 hashes of allowed inline styles.
    /// These will be added to style-src directive as 'sha256-{hash}'.
    /// </summary>
    public string[] AllowedStyleHashes { get; set; } = Array.Empty<string>();

    /// <summary>
    /// SHA256 hashes of allowed inline scripts.
    /// These will be added to script-src directive as 'sha256-{hash}'.
    /// </summary>
    public string[] AllowedScriptHashes { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Custom CSP directives to add or override.
    /// Key is the directive name, value is the space-separated list of sources.
    /// </summary>
    public Dictionary<string, string>? CustomDirectives { get; set; }

    /// <summary>
    /// Report URI for CSP violation reporting.
    /// If specified, adds report-uri directive to the policy.
    /// </summary>
    public string? ReportUri { get; set; }

    /// <summary>
    /// Whether to use report-only mode.
    /// When true, uses Content-Security-Policy-Report-Only header instead of enforcing.
    /// </summary>
    public bool ReportOnly { get; set; }
}

/// <summary>
/// Predefined CSP policy types for different use cases.
/// </summary>
public enum CspPolicyType
{
    /// <summary>
    /// Restrictive policy for API-only endpoints with no browser UI.
    /// </summary>
    ApiOnly = 0,

    /// <summary>
    /// Balanced policy for geospatial APIs that may serve web content.
    /// Allows necessary resources for map rendering while maintaining security.
    /// </summary>
    GeospatialApi = 1,

    /// <summary>
    /// Custom policy built entirely from configuration.
    /// Uses only the CustomDirectives specified in config.
    /// </summary>
    Custom = 2
}

/// <summary>
/// Extension methods for registering security headers middleware and services.
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    /// <summary>
    /// Adds security headers services to the dependency injection container.
    /// Configures SecurityHeadersOptions from the "SecurityHeaders" configuration section.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The application configuration</param>
    /// <returns>The service collection for method chaining</returns>
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

    /// <summary>
    /// Adds security headers services to the dependency injection container with custom configuration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Action to configure security headers options</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddSecurityHeaders(
        this IServiceCollection services,
        Action<SecurityHeadersOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);
        return services;
    }

    /// <summary>
    /// Adds the security headers middleware to the application pipeline.
    /// Should be registered early in the pipeline to ensure all responses receive security headers.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for method chaining</returns>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    /// <summary>
    /// Adds the Honua security headers middleware to the application pipeline.
    /// This is the canonical entry point for the audit-fix integration (#1144);
    /// it is functionally identical to <see cref="UseSecurityHeaders"/> but uses
    /// the Honua-namespaced name so the wiring step is self-documenting.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for method chaining</returns>
    public static IApplicationBuilder UseHonuaSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    /// <summary>
    /// Validates security headers configuration for critical security issues.
    /// </summary>
    /// <param name="options">The security headers options to validate</param>
    /// <exception cref="InvalidOperationException">Thrown when critical security misconfigurations are detected</exception>
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

            foreach (var customHeader in options.CustomHeaders.Keys)
            {
                if (securityHeaders.Contains(customHeader))
                {
                    errors.Add($"Custom header '{customHeader}' conflicts with built-in security header");
                }
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

/// <summary>
/// High-performance logging for security headers middleware using source generation for AOT compatibility.
/// </summary>
internal static partial class SecurityHeadersLog
{
    /// <summary>
    /// Logs when security headers are applied to a response (debug level).
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="requestPath">The request path that received security headers</param>
    /// <param name="requestMethod">The HTTP method of the request</param>
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Debug,
        Message = "Security headers applied to response: {RequestMethod} {RequestPath}")]
    public static partial void SecurityHeadersApplied(ILogger logger, string requestPath, string requestMethod);

    /// <summary>
    /// Logs when security headers configuration is validated at startup.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Security headers configuration validated successfully")]
    public static partial void SecurityHeadersConfigurationValidated(ILogger logger);

    /// <summary>
    /// Logs when security headers configuration contains warnings.
    /// </summary>
    /// <param name="logger">The logger instance</param>
    /// <param name="warning">The configuration warning message</param>
    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Warning,
        Message = "Security headers configuration warning: {Warning}")]
    public static partial void SecurityHeadersConfigurationWarning(ILogger logger, string warning);
}
