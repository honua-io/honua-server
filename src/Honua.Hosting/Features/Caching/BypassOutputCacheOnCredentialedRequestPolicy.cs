// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Output cache policy that keeps a request out of the shared output cache — no
/// stored entry is served to it and its response is not stored — when it
/// carries any credential the application defines.
/// </summary>
/// <remarks>
/// <para>
/// The shared output cache keys on the request URL plus the base policy's
/// vary-by values (tenant, license fingerprint). A response that depends on a
/// credential presented in a header or query parameter is therefore not a
/// function of its cache key, and a request that presents one must reach its
/// handler so the handler's own credential, origin, rate and revocation checks
/// run.
/// </para>
/// <para>
/// <see cref="AnonymousOnlyOutputCachePolicy"/> covers credentials that
/// materialise as an authenticated <see cref="HttpContext.User"/>. This policy
/// covers credential families the application validates inside the endpoint
/// handler (embed keys, portal tokens, scene access tokens), which never reach
/// the authentication pipeline.
/// </para>
/// <para>
/// This policy is composed into the base policy, and the middleware runs the
/// endpoint's own policy <em>after</em> the base policies. A named policy (or
/// <c>CacheOutput()</c>) starts with the framework default, which re-enables
/// lookup and storage during <see cref="CacheRequestAsync"/>, and a policy
/// cannot veto a lookup once the middleware has fetched an entry. The policy
/// therefore enforces its decision in two ways that survive that ordering:
/// </para>
/// <list type="bullet">
///   <item><description>
///   Lookup: it adds a per-request vary-by value, so the cache key of a
///   credentialed request never matches a stored entry.
///   </description></item>
///   <item><description>
///   Storage: it clears <see cref="OutputCacheContext.AllowCacheStorage"/> in
///   <see cref="ServeResponseAsync"/>, which the framework default never sets
///   back.
///   </description></item>
/// </list>
/// </remarks>
internal sealed class BypassOutputCacheOnCredentialedRequestPolicy : IOutputCachePolicy
{
    /// <summary>
    /// Vary-by key that partitions credentialed requests away from stored entries.
    /// </summary>
    internal const string CredentialedRequestVaryKey = "honua-credentialed-request";

    /// <summary>
    /// Request headers that carry a caller credential. <c>Authorization</c> and
    /// <c>X-API-Key</c> normally produce an authenticated principal, but they are
    /// listed here too so the decision does not depend on an authentication
    /// handler having run for the matched route.
    /// </summary>
    private static readonly string[] CredentialHeaders =
    [
        HeaderNames.Authorization,
        "X-API-Key",
        "X-Esri-Authorization",
        "X-Honua-Embed-Key",
        "X-Honua-Token",
    ];

    /// <summary>
    /// Query parameters that carry a caller credential: the ArcGIS-compatible
    /// portal/scene <c>token</c> and the embed governance <c>key</c>.
    /// </summary>
    private static readonly string[] CredentialQueryParameters =
    [
        "token",
        "key",
    ];

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

    /// <summary>
    /// Reports whether the request presents any application-defined credential.
    /// Single source of truth for the credential surfaces the shared output
    /// cache must not key on.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns><see langword="true"/> when a credential is present.</returns>
    internal static bool CarriesCredential(HttpContext context)
    {
        var request = context.Request;

        foreach (var header in CredentialHeaders)
        {
            if (!StringValues.IsNullOrEmpty(request.Headers[header]))
            {
                return true;
            }
        }

        foreach (var parameter in CredentialQueryParameters)
        {
            if (request.Query.TryGetValue(parameter, out var values) && !StringValues.IsNullOrEmpty(values))
            {
                return true;
            }
        }

        return false;
    }
}
