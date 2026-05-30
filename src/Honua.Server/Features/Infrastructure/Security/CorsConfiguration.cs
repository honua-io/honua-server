// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Honua.Infrastructure.Security;

/// <summary>
/// CORS configuration for Honua Server with security-first approach.
/// Provides separate policies for different environments and use cases.
/// </summary>
public static class CorsConfiguration
{
    private static readonly string[] DefaultDevelopmentOrigins =
    [
        "http://localhost:3000",
        "http://localhost:5173",
        "http://localhost:8080"
    ];

    public const string DevelopmentPolicy = "DevelopmentCors";
    public const string ProductionPolicy = "ProductionCors";
    public const string RestrictedPolicy = "RestrictedCors";

    /// <summary>
    /// Configures CORS policies for different environments.
    /// </summary>
    public static void AddCorsPolicies(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger("Honua.Server.CorsConfiguration");

        // Guard against booting a Development profile on a hosted environment.
        // A container or cloud signal (DOTNET_RUNNING_IN_CONTAINER or KUBERNETES_SERVICE_HOST)
        // combined with ASPNETCORE_ENVIRONMENT=Development indicates the dev profile is
        // leaking into a deployed box — collapse the permissive CORS policy and log loudly.
        var runningInManagedHost =
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));
        var permissiveCorsDisabled = environment.IsDevelopment() && runningInManagedHost;
        if (permissiveCorsDisabled)
        {
            CorsLog.DevelopmentCorsDisabledInHostedEnvironment(logger);
        }

        services.AddCors(options =>
        {
            // Development policy - controlled development origins only
            options.AddPolicy(DevelopmentPolicy, policy =>
            {
                if (environment.IsDevelopment() && !permissiveCorsDisabled)
                {
                    // Get development-specific allowed origins from configuration
                    var devOrigins = configuration.GetSection("Cors:DevelopmentOrigins").Get<string[]>()
                        ?? DefaultDevelopmentOrigins;

                    if (devOrigins.Length > 0)
                    {
                        // AllowAnyHeader covers request headers; exposed response
                        // headers must still be enumerated. Mirror the production
                        // policy so the PMTiles RangeProxy strategy (#845) works
                        // for cross-origin browser clients in development.
                        policy.WithOrigins(devOrigins)
                              .AllowAnyMethod()
                              .AllowAnyHeader()
                              .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding",
                                  "Accept-Ranges", "Content-Range", "Content-Length", "ETag", "Last-Modified")
                              .AllowCredentials()
                              .SetPreflightMaxAge(TimeSpan.FromMinutes(5));
                    }
                    else
                    {
                        // No dev origins configured - use production policy
                        ConfigureProductionPolicy(policy, configuration, environment, logger);
                    }
                }
                else
                {
                    // Fall back to production policy in non-dev environments
                    ConfigureProductionPolicy(policy, configuration, environment, logger);
                }
            });

            // Production policy - security-focused with specific allowed origins
            options.AddPolicy(ProductionPolicy, policy =>
            {
                ConfigureProductionPolicy(policy, configuration, environment, logger);
            });

            // Restricted policy - most secure, minimal permissions
            options.AddPolicy(RestrictedPolicy, policy =>
            {
                ConfigureRestrictedPolicy(policy, configuration);
            });
        });
    }

    /// <summary>
    /// Configures the production CORS policy with allowed origins from configuration.
    /// </summary>
    private static void ConfigureProductionPolicy(CorsPolicyBuilder policy, IConfiguration configuration, IWebHostEnvironment environment, ILogger logger)
    {
        var allowedOrigins = ResolveAllowedOrigins(configuration);
        var allowCredentials = configuration.GetValue("Cors:AllowCredentials", false);
        var preflightMaxAgeMinutes = configuration.GetValue("Cors:PreflightMaxAgeMinutes", 10);

        if (allowedOrigins.Length > 0)
        {
            var explicitOrigins = FilterExplicitOrigins(allowedOrigins);
            if (explicitOrigins.Length > 0)
            {
                policy.WithOrigins(explicitOrigins);
            }

            policy.WithMethods("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key", "X-Correlation-ID",
                      "X-Grpc-Web", "X-User-Agent", "Range", "If-Range", "If-None-Match", "If-Modified-Since")
                  .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding",
                      "Accept-Ranges", "Content-Range", "Content-Length", "ETag", "Last-Modified")
                  .SetIsOriginAllowed(origin => IsOriginAllowed(origin, allowedOrigins))
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(preflightMaxAgeMinutes));

            if (allowCredentials)
            {
                policy.AllowCredentials();
            }
        }
        else
        {
            // No origins configured - explicitly deny all cross-origin requests.
            policy.SetIsOriginAllowed(_ => false);

            if (!environment.IsDevelopment())
            {
                CorsLog.NoCorsOriginsConfigured(logger);
            }
        }
    }

    private static string[] ResolveAllowedOrigins(IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        var adminUiOrigins = configuration["HONUA_ADMIN_UI_CORS_ORIGINS"];
        if (string.IsNullOrWhiteSpace(adminUiOrigins))
        {
            return allowedOrigins;
        }

        var merged = allowedOrigins.Concat(
            adminUiOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return merged.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Configures the most restrictive CORS policy for sensitive operations.
    /// </summary>
    private static void ConfigureRestrictedPolicy(CorsPolicyBuilder policy, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:RestrictedOrigins").Get<string[]>() ?? Array.Empty<string>();

        if (allowedOrigins.Length > 0)
        {
            var explicitOrigins = FilterExplicitOrigins(allowedOrigins);
            if (explicitOrigins.Length > 0)
            {
                policy.WithOrigins(explicitOrigins);
            }

            policy.WithMethods("GET", "POST") // Only safe methods
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                  .SetIsOriginAllowed(origin => IsOriginAllowed(origin, allowedOrigins))
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(5))
                  .DisallowCredentials(); // Never allow credentials in restricted mode
        }
        else
        {
            // No origins configured - explicitly deny all.
            policy.SetIsOriginAllowed(_ => false);
        }
    }

    /// <summary>
    /// Validates if an origin is allowed based on configured patterns.
    /// Supports exact matches and wildcard subdomains.
    /// </summary>
    private static bool IsOriginAllowed(string origin, string[] allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        foreach (var allowedOrigin in allowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(allowedOrigin))
            {
                continue;
            }

            var trimmedOrigin = allowedOrigin.Trim();

            // Exact match
            if (!trimmedOrigin.Contains('*') &&
                string.Equals(origin, trimmedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Wildcard subdomain match (*.example.com)
            if (!TryParseWildcardOrigin(trimmedOrigin, out var scheme, out var hostSuffix))
            {
                continue;
            }

            if (scheme != null && !string.Equals(originUri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var originHost = originUri.Host;
            if (originHost.EndsWith("." + hostSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] FilterExplicitOrigins(string[] allowedOrigins)
    {
        if (allowedOrigins.Length == 0)
        {
            return Array.Empty<string>();
        }

        var explicitOrigins = new List<string>();
        foreach (var origin in allowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                continue;
            }

            var trimmed = origin.Trim();
            if (trimmed.Contains('*'))
            {
                continue;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            {
                explicitOrigins.Add(trimmed);
            }
        }

        return explicitOrigins.ToArray();
    }

    private static bool TryParseWildcardOrigin(string allowedOrigin, out string? scheme, out string hostSuffix)
    {
        scheme = null;
        hostSuffix = string.Empty;

        if (allowedOrigin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            allowedOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var schemeEnd = allowedOrigin.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0)
            {
                return false;
            }

            scheme = allowedOrigin[..schemeEnd];
            var hostPattern = allowedOrigin[(schemeEnd + 3)..];
            if (!hostPattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return false;
            }

            hostSuffix = hostPattern[2..];
        }
        else
        {
            if (!allowedOrigin.StartsWith("*.", StringComparison.Ordinal))
            {
                return false;
            }

            hostSuffix = allowedOrigin[2..];
        }

        if (string.IsNullOrWhiteSpace(hostSuffix) || hostSuffix.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Extension method to apply the appropriate CORS policy based on environment.
    /// </summary>
    public static void UseHonuaCors(this IApplicationBuilder app, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            app.UseCors(DevelopmentPolicy);
        }
        else
        {
            app.UseCors(ProductionPolicy);
        }
    }
}

/// <summary>
/// Source-generated logger for CORS configuration (AOT compatible).
/// </summary>
internal static partial class CorsLog
{
    /// <summary>
    /// Log warning when no CORS origins are configured in production.
    /// </summary>
    [LoggerMessage(
        EventId = 4060,
        Level = LogLevel.Warning,
        Message = "No CORS origins configured. All cross-origin requests will be blocked in production.")]
    public static partial void NoCorsOriginsConfigured(ILogger logger);

    /// <summary>
    /// Log when Development CORS is refused because the process appears to be running inside
    /// a container or Kubernetes-managed environment. This prevents a misconfigured
    /// ASPNETCORE_ENVIRONMENT from exposing AllowAnyOrigin on a hosted deployment.
    /// </summary>
    [LoggerMessage(
        EventId = 4061,
        Level = LogLevel.Warning,
        Message = "ASPNETCORE_ENVIRONMENT=Development detected inside a managed host "
                  + "(DOTNET_RUNNING_IN_CONTAINER or KUBERNETES_SERVICE_HOST). Permissive "
                  + "Development CORS policy disabled; falling back to production CORS rules.")]
    public static partial void DevelopmentCorsDisabledInHostedEnvironment(ILogger logger);
}

/// <summary>
/// Configuration options for CORS settings.
/// </summary>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// List of allowed origins for production CORS policy.
    /// Supports exact URLs and wildcard subdomains (*.example.com).
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// List of allowed origins for development CORS policy.
    /// Used only in development environment for local testing.
    /// </summary>
    public string[] DevelopmentOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// List of allowed origins for restricted CORS policy.
    /// Used for sensitive operations requiring tighter security.
    /// </summary>
    public string[] RestrictedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Whether to allow credentials in CORS requests.
    /// Should be false for most API scenarios for security.
    /// </summary>
    public bool AllowCredentials { get; set; }

    /// <summary>
    /// Maximum age for CORS preflight cache in minutes.
    /// </summary>
    public int PreflightMaxAgeMinutes { get; set; } = 10;
}
