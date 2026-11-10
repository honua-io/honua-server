// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Caching;

// A credentialed request must miss the shared output cache because the cache key omits header and query credentials, and a later policy cannot veto a lookup once an entry was fetched.
internal sealed class BypassOutputCacheOnCredentialedRequestPolicy : IOutputCachePolicy
{
    internal const string CredentialedRequestVaryKey = "honua-credentialed-request";

    private static readonly string[] CredentialCookies =
    [
        AdminAuthSessionStore.AuthSessionCookieName,
        AdminAuthSessionStore.PendingSessionCookieName,
    ];

    private const string DefaultForwardedClientCertHeader = "X-Forwarded-Client-Cert";

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (CarriesCredential(context.HttpContext))
        {
            context.EnableOutputCaching = false;
            context.AllowCacheLookup = false;
            context.AllowCacheStorage = false;

            // A later endpoint policy may re-enable lookup; a key unique to this
            // request guarantees the lookup (and request coalescing) cannot match.
            context.CacheVaryByRules.VaryByValues[CredentialedRequestVaryKey] =
                context.HttpContext.TraceIdentifier;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (CarriesCredential(context.HttpContext))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    internal static bool CarriesCredential(HttpContext context)
    {
        var request = context.Request;

        if (RequestCredentialCatalog.HasNonEmptyCredential(request)) return true;

        foreach (var cookieName in CredentialCookies)
        {
            if (!string.IsNullOrEmpty(request.Cookies[cookieName]))
            {
                return true;
            }
        }

        if (context.Connection.ClientCertificate is not null)
        {
            return true;
        }

        if (HasForwardedClientCertificate(context))
        {
            return true;
        }

        return false;
    }

    private static bool HasForwardedClientCertificate(HttpContext context)
    {
        if (!StringValues.IsNullOrEmpty(context.Request.Headers[DefaultForwardedClientCertHeader]))
        {
            return true;
        }

        var configured = ResolveForwardedClientCertHeader(context);
        if (configured is null ||
            string.Equals(configured, DefaultForwardedClientCertHeader, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !StringValues.IsNullOrEmpty(context.Request.Headers[configured]);
    }

    private static string? ResolveForwardedClientCertHeader(HttpContext context)
    {
        if (!TryGetRequestServices(context, out var services))
        {
            return null;
        }

        var monitored = services.GetService<IOptionsMonitor<ClientCertificateAuthenticationOptions>>()
            ?.CurrentValue.ForwardedCertificate?.HeaderName;
        if (!string.IsNullOrWhiteSpace(monitored))
        {
            return monitored;
        }

        var configured = services.GetService<IOptions<ClientCertificateAuthenticationOptions>>()
            ?.Value.ForwardedCertificate?.HeaderName;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var configuration = services.GetService<IConfiguration>();
        var fromConfig = configuration?[$"{ClientCertificateAuthenticationOptions.SectionName}:ForwardedCertificate:HeaderName"];
        return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig;
    }

    private static bool TryGetRequestServices(HttpContext context, out IServiceProvider services)
    {
        try
        {
            services = context.RequestServices;
        }
        catch (InvalidOperationException)
        {
            services = null!;
            return false;
        }

        return services is not null;
    }
}
