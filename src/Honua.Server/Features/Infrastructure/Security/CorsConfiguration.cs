// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Honua.Server.Features.Infrastructure.Security;

/// <summary>
/// CORS configuration for Honua Server with security-first approach.
/// Provides separate policies for different environments and use cases.
/// </summary>
public static class CorsConfiguration
{
    public const string DevelopmentPolicy = "DevelopmentCors";
    public const string ProductionPolicy = "ProductionCors";
    public const string RestrictedPolicy = "RestrictedCors";

    /// <summary>
    /// Configures CORS policies for different environments.
    /// </summary>
    public static void AddCorsPolicies(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddCors(options =>
        {
            // Development policy - more permissive for local development
            options.AddPolicy(DevelopmentPolicy, policy =>
            {
                if (environment.IsDevelopment())
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                }
                else
                {
                    // Fall back to production policy in non-dev environments
                    ConfigureProductionPolicy(policy, configuration);
                }
            });

            // Production policy - security-focused with specific allowed origins
            options.AddPolicy(ProductionPolicy, policy =>
            {
                ConfigureProductionPolicy(policy, configuration);
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
    private static void ConfigureProductionPolicy(CorsPolicyBuilder policy, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        var allowCredentials = configuration.GetValue("Cors:AllowCredentials", false);

        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key", "X-Correlation-ID")
                  .SetIsOriginAllowed(origin => IsOriginAllowed(origin, allowedOrigins))
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(10));

            if (allowCredentials)
            {
                policy.AllowCredentials();
            }
        }
        else
        {
            // No origins configured - deny all cross-origin requests
            policy.WithOrigins() // Empty origins list
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    }

    /// <summary>
    /// Configures the most restrictive CORS policy for sensitive operations.
    /// </summary>
    private static void ConfigureRestrictedPolicy(CorsPolicyBuilder policy, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:RestrictedOrigins").Get<string[]>() ?? Array.Empty<string>();

        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST") // Only safe methods
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(5))
                  .DisallowCredentials(); // Never allow credentials in restricted mode
        }
        else
        {
            // No origins configured - deny all
            policy.WithOrigins()
                  .WithMethods()
                  .WithHeaders();
        }
    }

    /// <summary>
    /// Validates if an origin is allowed based on configured patterns.
    /// Supports exact matches and wildcard subdomains.
    /// </summary>
    private static bool IsOriginAllowed(string origin, string[] allowedOrigins)
    {
        if (string.IsNullOrEmpty(origin))
            return false;

        foreach (var allowedOrigin in allowedOrigins)
        {
            // Exact match
            if (string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
                return true;

            // Wildcard subdomain match (*.example.com)
            if (allowedOrigin.StartsWith("*.") && origin.EndsWith(allowedOrigin[1..], StringComparison.OrdinalIgnoreCase))
            {
                var originHost = new Uri(origin).Host;
                var allowedHost = allowedOrigin[2..]; // Remove "*."

                // Ensure it's actually a subdomain, not just ending with the domain
                if (originHost.EndsWith($".{allowedHost}", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
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
