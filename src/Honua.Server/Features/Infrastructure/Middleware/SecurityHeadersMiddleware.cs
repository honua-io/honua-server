// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
        // Apply security headers to all responses
        ApplySecurityHeaders(context.Response.Headers);

        // Log security headers application for debugging in development
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            SecurityHeadersLog.SecurityHeadersApplied(_logger, context.Request.Path, context.Request.Method);
        }

        // Continue to next middleware
        await _next(context);
    }

    /// <summary>
    /// Applies comprehensive security headers to the HTTP response.
    /// </summary>
    /// <param name="headers">The response headers collection</param>
    private void ApplySecurityHeaders(IHeaderDictionary headers)
    {
        // Strict-Transport-Security (HSTS) - Force HTTPS for specified duration
        if (_options.EnableHsts)
        {
            var hstsValue = $"max-age={_options.HstsMaxAge}";
            if (_options.HstsIncludeSubdomains)
                hstsValue += "; includeSubDomains";
            if (_options.HstsPreload)
                hstsValue += "; preload";

            headers["Strict-Transport-Security"] = hstsValue;
        }

        // Content-Security-Policy - Prevent XSS and injection attacks
        if (!string.IsNullOrEmpty(_options.ContentSecurityPolicy))
        {
            headers["Content-Security-Policy"] = _options.ContentSecurityPolicy;
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
        if (!string.IsNullOrEmpty(_options.CrossOriginEmbedderPolicy))
        {
            headers["Cross-Origin-Embedder-Policy"] = _options.CrossOriginEmbedderPolicy;
        }

        // Permissions-Policy - Restrict access to browser features
        if (!string.IsNullOrEmpty(_options.PermissionsPolicy))
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
    /// Default: 31536000 (1 year).
    /// </summary>
    public int HstsMaxAge { get; set; } = 31536000; // 1 year

    /// <summary>
    /// Include subdomains in HSTS policy.
    /// Default: true (recommended for complete coverage).
    /// </summary>
    public bool HstsIncludeSubdomains { get; set; } = true;

    /// <summary>
    /// Enable HSTS preload (requires submission to browser preload lists).
    /// Default: false (requires manual submission).
    /// </summary>
    public bool HstsPreload { get; set; }

    /// <summary>
    /// Content Security Policy (CSP) directives.
    /// Default: Strict policy suitable for APIs.
    /// </summary>
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; media-src 'self'; object-src 'none'; " +
        "frame-ancestors 'none'; form-action 'self'; base-uri 'self'";

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
    /// Default: "require-corp" (requires explicit cross-origin permissions).
    /// </summary>
    public string CrossOriginEmbedderPolicy { get; set; } = "require-corp";

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
