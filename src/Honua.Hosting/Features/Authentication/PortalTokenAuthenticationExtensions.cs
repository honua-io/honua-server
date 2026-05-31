// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Wiring extensions for the ArcGIS-compatible portal-token authentication scheme.
/// </summary>
/// <remarks>
/// The scheme is registered alongside the existing API-key authentication so that
/// FeatureServer/MapServer endpoints (which use <c>AllowAnonymous</c> route metadata
/// and authorize per-resource) can observe portal-token credentials carried as
/// <c>?token=</c>, <c>Authorization: Bearer ...</c>, or <c>X-Esri-Authorization: Bearer ...</c>.
/// A small middleware bridges anonymous requests by authenticating the scheme on
/// demand and projecting the resulting principal onto <see cref="HttpContext.User"/>.
/// </remarks>
public static class PortalTokenAuthenticationExtensions
{
    /// <summary>
    /// Authentication scheme name for ArcGIS-compatible portal tokens.
    /// </summary>
    public const string PortalTokenScheme = PortalTokenIssuer.AuthSchemeName;

    /// <summary>
    /// Registers the portal-token issuer, options, default credential verifier, and
    /// authentication scheme.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPortalTokenAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<PortalTokenAuthenticationOptions>()
            .Bind(configuration.GetSection(PortalTokenAuthenticationOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingletonPortalTokenIssuer();
        services.TryAddDefaultPortalCredentialVerifier();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
                PortalTokenScheme,
                static _ => { });

        return services;
    }

    /// <summary>
    /// Inserts the portal-token bridge middleware into the request pipeline.
    /// Must run after <c>UseAuthentication</c>/<c>UseApiKeyAuthentication</c> so it can
    /// observe the default-scheme authentication result, and before the tenant-context
    /// middleware so downstream handlers see the hydrated principal.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UsePortalTokenAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<PortalTokenAuthenticationMiddleware>();
    }

    private static void TryAddSingletonPortalTokenIssuer(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IPortalTokenIssuer)))
        {
            services.AddSingleton<IPortalTokenIssuer, PortalTokenIssuer>();
        }
    }

    private static void TryAddDefaultPortalCredentialVerifier(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IPortalCredentialVerifier)))
        {
            services.AddScoped<IPortalCredentialVerifier, AdminPortalCredentialVerifier>();
        }
    }
}

/// <summary>
/// Bridges anonymous requests that carry an ArcGIS-style portal token into the
/// default authentication pipeline by attempting <see cref="PortalTokenAuthenticationHandler"/>
/// authentication and projecting any resulting principal onto <see cref="HttpContext.User"/>.
/// </summary>
internal sealed class PortalTokenAuthenticationMiddleware(
    RequestDelegate next,
    IOptionsMonitor<PortalTokenAuthenticationOptions> options)
{
    private const string EsriAuthorizationHeader = "X-Esri-Authorization";

    private readonly RequestDelegate _next = next;
    private readonly IOptionsMonitor<PortalTokenAuthenticationOptions> _options = options;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.CurrentValue.Enabled &&
            !IsAuthenticated(context) &&
            CarriesPortalTokenIndicator(context))
        {
            var result = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme).ConfigureAwait(false);
            if (result.Succeeded && result.Principal is not null)
            {
                context.User = result.Principal;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsAuthenticated(HttpContext context)
        => context.User?.Identity?.IsAuthenticated == true;

    private static bool CarriesPortalTokenIndicator(HttpContext context)
    {
        if (context.Request.Query.ContainsKey(PortalTokenAuthenticationHandler.TokenQueryParameter))
        {
            return true;
        }

        if (context.Request.Headers.TryGetValue(EsriAuthorizationHeader, out var esri) &&
            HasBearerPrefix(esri))
        {
            return true;
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var auth) && HasBearerPrefix(auth))
        {
            // Defer to PortalToken only when no JWT-Bearer scheme is configured —
            // here we accept the cooperative attempt because a portal-token value
            // is opaque hex and never validates as a JWT, so dual schemes can
            // safely coexist.
            return true;
        }

        return false;
    }

    private static bool HasBearerPrefix(Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                value.StartsWith(PortalTokenAuthenticationHandler.BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
