// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Authentication;

public static class PortalTokenAuthenticationExtensions
{
    internal static readonly object AuthenticationFailureKey = new();

    internal static readonly object QueryCredentialKey = new();
    public const string PortalTokenScheme = PortalTokenIssuer.AuthSchemeName;

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

        services
            .AddOptions<PortalCredentialVerifierOptions>()
            .Bind(configuration.GetSection(PortalCredentialVerifierOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingletonPortalTokenIssuer();
        services.TryAddSingletonScopedJobTokenIssuer();
        services.TryAddPortalCredentialVerifier();
        services.TryAddPortalAccessProjection(configuration);
        services.TryAddPortalOAuthBridge();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
                PortalTokenScheme,
                static _ => { });

        return services;
    }

    // This middleware runs after authentication and before tenant resolution so downstream handlers see the hydrated principal.
    public static IApplicationBuilder UsePortalTokenAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<PortalTokenAuthenticationMiddleware>();
    }

    // An invalid portal token is rejected after the shared rate limiter has entered the request, so repeated bad credentials still consume the source-IP bucket, and the response stays the GeoServices 498.
    public static IApplicationBuilder UsePortalTokenAuthenticationRejection(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (!context.Items.TryGetValue(AuthenticationFailureKey, out var failure) || failure is not true)
            {
                await next().ConfigureAwait(false);
                return;
            }

            context.Response.Headers.Append("WWW-Authenticate", "Bearer");
            await StandardErrorHelpers.CreateInvalidToken(context).ExecuteAsync(context).ConfigureAwait(false);
        });
    }

    private static void TryAddSingletonPortalTokenIssuer(this IServiceCollection services)
    {
        // Re-checks the credential a stored token was minted from on every restore (SEC-9).
        // Registered unconditionally, including when an operator supplies a custom issuer, so
        // the binding is never silently absent.
        services.TryAddSingleton<IPortalTokenSourceValidator>(sp => new PortalTokenSourceValidator(
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<IOptions<PortalTokenAuthenticationOptions>>(),
            sp,
            sp.GetService<TimeProvider>()));

        if (!services.Any(d => d.ServiceType == typeof(IPortalTokenIssuer)))
        {
            services.AddSingleton<IPortalTokenIssuer>(sp => new PortalTokenIssuer(
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ILogger<PortalTokenIssuer>>(),
                sp.GetService<IDistributedCache>(),
                sp));
        }
    }

    // The scoped-job issuer is a separate singleton so a portal token is never asked to carry a job-bound attenuation.
    private static void TryAddSingletonScopedJobTokenIssuer(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IScopedJobTokenIssuer)))
        {
            services.AddSingleton<IScopedJobTokenIssuer, ScopedJobTokenIssuer>();
        }
    }

    private static void TryAddPortalCredentialVerifier(this IServiceCollection services)
    {
        // The OAuth bridge constructs OidcPortalCredentialVerifier directly. Register
        // that concrete type and its cache even when an operator already supplied
        // IPortalCredentialVerifier; skipping them makes /sharing/rest/oauth2/authorize
        // fail during activation. The custom verifier still wins the interface.
        services.TryAddSingleton<OidcConfigurationManagerCache>();
        services.TryAddScoped<AdminPortalCredentialVerifier>();
        services.TryAddScoped<OidcPortalCredentialVerifier>();

        if (services.Any(d => d.ServiceType == typeof(IPortalCredentialVerifier)))
        {
            return;
        }

        // The admin verifier (admin password / admin API key) remains the default
        // and community option. The OIDC-backed verifier is opt-in and, when
        // enabled, may fall back to the admin verifier so admin/API-key access
        // keeps working alongside named-user login.
        services.AddScoped<IPortalCredentialVerifier>(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<PortalCredentialVerifierOptions>>().Value;
            var admin = provider.GetRequiredService<AdminPortalCredentialVerifier>();

            if (!options.UseOidc)
            {
                return admin;
            }

            var oidc = provider.GetRequiredService<OidcPortalCredentialVerifier>();
            return options.FallBackToAdminVerifier
                ? new CompositePortalCredentialVerifier(oidc, admin)
                : oidc;
        });
    }

    private static void TryAddPortalOAuthBridge(this IServiceCollection services)
    {
        // The ArcGIS OAuth2 named-user bridge (#1242) composes the existing OIDC
        // identity core and IPortalTokenIssuer rather than introducing a parallel
        // auth subsystem (ADR-0049). The store/broker/token-service are scoped to
        // the request like the credential verifier; the store is registered as a
        // singleton because it is stateless over the shared distributed/memory cache.
        services.TryAddSingleton<OidcRuntimeProviderRegistry>();
        services.TryAddSingleton<IOidcRuntimeProviderSource>(static provider =>
            provider.GetRequiredService<OidcRuntimeProviderRegistry>());
        services.TryAddSingleton<PortalOAuthStore>();

        // First-class OAuth2 client registry + scope catalogue (ADR-0053 Increment 2,
        // #1888). Singletons mirror the in-memory IAdminApiKeyStore pattern: no
        // parallel durable token store (ADR-0049). They are additive — the flag-gated
        // Increment-1 client_credentials path keeps working via the API-key fallback.
        services.TryAddSingleton<IOAuthClientStore>(sp =>
            sp.GetService<StackExchange.Redis.IConnectionMultiplexer>() is { } redis
                ? new RedisOAuthClientStore(redis, sp.GetService<TimeProvider>())
                : new InMemoryOAuthClientStore(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IOAuthScopeCatalogue, InMemoryOAuthScopeCatalogue>();

        // Process-local defaults for non-Postgres providers. The server's Postgres
        // composition registers durable stores first, so TryAdd preserves them.
        // Both profiles compose the shared identity/role model.
        services.TryAddSingleton<IPortalGroupStore>(
            sp => new InMemoryPortalGroupStore(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IPortalItemSharingStore, InMemoryPortalItemSharingStore>();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<Honua.Core.Features.Security.Abstractions.IPortalResourceReadAuthorizer, PortalResourceReadAuthorizer>();

        // Optional JWT access-token format + RFC 7662 introspection (ADR-0068, #1890)
        // and pluggable IdP/OIDC federation for client_credentials (ADR-0053
        // Increment 3, #1889). All strictly opt-in via options; registering the
        // services unconditionally keeps the flag-off path a no-op (the services
        // report disabled and the token service falls back to the opaque/in-tree
        // paths) so there is no behaviour change by default.
        services.TryAddSingleton<PortalJwtAccessTokenService>();
        services.TryAddSingleton<ClientCredentialsFederationService>();
        services.AddScoped<PortalTokenIntrospectionService>();
        services.AddScoped<PortalOAuthRevocationService>();

        services.AddScoped<PortalOAuthBroker>();
        services.AddScoped<PortalOAuthTokenService>();

        if (!services.Any(d => d.ServiceType == typeof(IHttpClientFactory)))
        {
            services.AddHttpClient();
        }

        services.AddHttpClient(PortalOAuthBroker.HttpClientName);
        services.AddHttpClient(ClientCredentialsFederationService.HttpClientName);
    }

    private static void TryAddPortalAccessProjection(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IPortalAccessProjection>(_ =>
        {
            // The Portal access/visibility projection is off-by-default so the
            // facade never exposes a discovery surface unless an operator opts in,
            // consistent with the entitlement-gated generateToken surface (#1370).
            var enabled = configuration.GetValue(
                $"{PortalAccessProjectionConfiguration.SectionName}:Enabled",
                defaultValue: false);
            return new PortalAccessProjection(enabled);
        });
    }
}

public static class PortalAccessProjectionConfiguration
{
    public const string SectionName = "Authentication:PortalAccessProjection";
}

internal sealed class PortalTokenAuthenticationMiddleware(
    RequestDelegate next,
    IOptionsMonitor<PortalTokenAuthenticationOptions> options)
{
    private const string GeoServicesAuthorizationHeader = "X-Esri-Authorization";

    private readonly RequestDelegate _next = next;
    private readonly IOptionsMonitor<PortalTokenAuthenticationOptions> _options = options;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ScopedJobAuthentication.HasCandidate(context) && _options.CurrentValue.Enabled &&
            !IsAuthenticated(context) &&
            CarriesPortalTokenIndicator(context))
        {
            var result = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme).ConfigureAwait(false);
            if (result.Succeeded && result.Principal is not null)
            {
                context.User = result.Principal;
                if (context.Items.ContainsKey(PortalTokenAuthenticationExtensions.QueryCredentialKey))
                {
                    // RFC 6750 §2.3: a response to a request whose credential was in the URI
                    // must be Cache-Control: private. OnStarting runs after the endpoint (and
                    // any endpoint-registered callback) has set its own policy, so the merge
                    // sees the final value on every route, tiles and OGC included.
                    context.Response.OnStarting(static state =>
                    {
                        Honua.Infrastructure.Caching.UriCredentialResponseCachePolicy.Apply(((HttpContext)state).Response);
                        return Task.CompletedTask;
                    }, context);
                }
            }
            else if (!result.Succeeded && result.Failure is not null && IsGeoServicesPortalRequest(context))
            {
                context.Items[PortalTokenAuthenticationExtensions.AuthenticationFailureKey] = true;
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

        if (context.Request.Headers.TryGetValue(GeoServicesAuthorizationHeader, out var esri) &&
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

        // Only standard URL-encoded forms carry the ArcGIS POST token transport.
        // OAuth endpoints use form fields such as RFC 7009's `token` as operation
        // operands, not request credentials, so they must retain anonymous semantics.
        // Multipart and arbitrary request bodies must never trigger credential parsing.
        if (!context.Request.Path.StartsWithSegments("/sharing/rest/oauth2", StringComparison.OrdinalIgnoreCase) &&
            PortalTokenAuthenticationHandler.HasFormUrlEncodedContentType(context.Request))
        {
            return true;
        }

        return false;
    }

    private static bool IsGeoServicesPortalRequest(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/rest"))
        {
            return true;
        }

        // OAuth2 uses `token` as an operation operand on revoke/introspect, so an
        // unknown operand must not be mistaken for an invalid request credential.
        return context.Request.Path.StartsWithSegments("/sharing/rest") &&
            !context.Request.Path.StartsWithSegments("/sharing/rest/oauth2");
    }

    private static bool HasBearerPrefix(Microsoft.Extensions.Primitives.StringValues values)
    {
        return values.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(PortalTokenAuthenticationHandler.BearerPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
