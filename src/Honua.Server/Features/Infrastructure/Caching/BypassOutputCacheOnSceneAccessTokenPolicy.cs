// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Output cache policy that disables both lookup and storage when the request
/// carries a scene access envelope token (<c>?token=</c> query parameter or
/// <c>X-Honua-Token</c> header). The token authorizes a specific principal
/// for a short window; caching by URL alone would either:
/// <list type="bullet">
///   <item><description>Store unique entries keyed per token, blowing up cache size for browser sessions, or</description></item>
///   <item><description>Replay an authorized payload to a later anonymous client hitting the same URL after expiry.</description></item>
/// </list>
/// </summary>
internal sealed class BypassOutputCacheOnSceneAccessTokenPolicy : IOutputCachePolicy
{
    /// <summary>
    /// Query parameter name for browser-safe token transport. Mirrored on
    /// the asset endpoint extraction path; keep in lockstep.
    /// </summary>
    public const string TokenQueryParameter = "token";

    /// <summary>Header name for native-client token transport.</summary>
    public const string TokenHeader = "X-Honua-Token";

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (HasToken(context.HttpContext))
        {
            context.EnableOutputCaching = false;
            context.AllowCacheLookup = false;
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (HasToken(context.HttpContext))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    private static bool HasToken(HttpContext context)
    {
        if (context.Request.Query.TryGetValue(TokenQueryParameter, out var queryValues))
        {
            foreach (var value in queryValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return true;
                }
            }
        }

        if (context.Request.Headers.TryGetValue(TokenHeader, out var headerValues))
        {
            foreach (var value in headerValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
