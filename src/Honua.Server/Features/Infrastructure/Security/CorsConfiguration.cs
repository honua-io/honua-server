// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Honua.Infrastructure.Security;

public static class CorsConfiguration
{
    private const string LoggerCategory = "Honua.Server.CorsConfiguration";

    private static readonly string[] DefaultDevelopmentOrigins =
    [
        "http://localhost:3000",
        "http://localhost:5173",
        "http://localhost:8080"
    ];

    private static readonly string[] DefaultAllowedHeaders =
    [
        "X-Honua-CSRF", "Content-Type", "Authorization", "X-Esri-Authorization", "X-API-Key", "X-Correlation-ID",
        "X-Grpc-Web", "X-User-Agent", "Range", "If-Range",
        "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since",
        "Prefer", "X-Honua-Embed-Key", "MCP-Protocol-Version", "Mcp-Method", "Mcp-Name", "Mcp-Session-Id",
        "Idempotency-Key"
    ];

    private static readonly string[] BrowserExposedHeaders =
    [
        "Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding",
        "Accept-Ranges", "Content-Range", "Content-Length", "ETag", "Last-Modified", "Mcp-Session-Id"
    ];

    public const string DevelopmentPolicy = HttpCorsPolicyNames.Development;
    public const string ProductionPolicy = HttpCorsPolicyNames.Production;
    public const string RestrictedPolicy = "RestrictedCors";

    public const string EmbedPolicy = "EmbedCors";

    public static void AddCorsPolicies(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
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
            // The host's logging pipeline does not exist yet; dispose the bootstrap
            // factory so its console queue thread does not outlive this call.
            using var bootstrapLoggerFactory = LoggerFactory.Create(static builder => builder.AddConsole());
            CorsLog.DevelopmentCorsDisabledInHostedEnvironment(bootstrapLoggerFactory.CreateLogger(LoggerCategory));
        }

        services.AddCors();
        // Policies are built when CorsOptions is first resolved, so they log through the
        // host's ILoggerFactory instead of a factory that would outlive the host.
        services.AddOptions<CorsOptions>().Configure<ILoggerFactory>((options, loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(LoggerCategory);
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
                        policy.WithOrigins(FilterExplicitOrigins(devOrigins))
                              .AllowAnyMethod()
                              .AllowAnyHeader()
                              .WithExposedHeaders(BrowserExposedHeaders)
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

            // Embed policy - public embed governance endpoints reachable cross-origin
            // from arbitrary ISV sites; the per-embed-key origin allow-list is the real gate.
            options.AddPolicy(EmbedPolicy, policy =>
            {
                ConfigureEmbedPolicy(policy, configuration);
            });
        });
    }

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
                  .WithHeaders(ResolveAllowedHeaders(configuration))
                  .WithExposedHeaders(BrowserExposedHeaders)
                  .SetIsOriginAllowed(origin => allowCredentials
                      ? explicitOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                      : IsOriginAllowed(origin, allowedOrigins))
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

    private static string[] ResolveAllowedHeaders(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Cors:AllowedHeaders").Get<string[]>();
        if (configured is null || configured.Length == 0)
        {
            return DefaultAllowedHeaders;
        }

        return DefaultAllowedHeaders
            .Concat(configured.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsExplicitOriginAllowed(IConfiguration configuration, string origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            && FilterExplicitOrigins(ResolveAllowedOrigins(configuration)).Contains(origin, StringComparer.OrdinalIgnoreCase);
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

    private static void ConfigureEmbedPolicy(CorsPolicyBuilder policy, IConfiguration configuration)
    {
        // Reflect the request Origin instead of AllowAnyOrigin(): the preflight must still
        // succeed from ANY origin (embed widgets load on arbitrary ISV pages), but the CORS
        // result must echo the SPECIFIC request origin in Access-Control-Allow-Origin, not the
        // "*" wildcard. AllowAnyOrigin() emits "*", and the CORS middleware applies that result
        // via Response.OnStarting AFTER the handler runs — overwriting the handler's per-origin
        // echo with "*" (a wildcard ACAO on the embed policy surface). SetIsOriginAllowed(_ => true)
        // makes the middleware itself echo the request origin and add Vary: Origin. The per-embed-
        // key origin allow-list remains the authoritative gate inside the handler (it returns 403
        // for origins not approved for the presented key); credentials stay disabled (#1191).
        policy.SetIsOriginAllowed(_ => true)
              .WithMethods("GET", "POST", "OPTIONS")
              .WithHeaders(ResolveAllowedHeaders(configuration))
              .WithExposedHeaders("Content-Length", "ETag")
              .SetPreflightMaxAge(TimeSpan.FromMinutes(5));
    }

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

internal static partial class CorsLog
{
    [LoggerMessage(
        EventId = 4060,
        Level = LogLevel.Warning,
        Message = "No CORS origins configured. All cross-origin requests will be blocked in production.")]
    public static partial void NoCorsOriginsConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 4061,
        Level = LogLevel.Warning,
        Message = "ASPNETCORE_ENVIRONMENT=Development detected inside a managed host "
                  + "(DOTNET_RUNNING_IN_CONTAINER or KUBERNETES_SERVICE_HOST). Permissive "
                  + "Development CORS policy disabled; falling back to production CORS rules.")]
    public static partial void DevelopmentCorsDisabledInHostedEnvironment(ILogger logger);
}
