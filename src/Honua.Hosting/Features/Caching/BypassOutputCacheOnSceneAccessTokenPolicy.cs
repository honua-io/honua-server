// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

internal enum SceneAccessTokenTransport
{
    None,
    Query,
    Header
}

/// <summary>
/// Output cache policy and canonical token-transport detector for scene
/// access envelope tokens (<c>?token=</c> query parameter or
/// <c>X-Honua-Token</c> header). When the policy detects a token it disables
/// both lookup and storage on the output cache; verification call sites use
/// <see cref="TryExtractToken(HttpContext)"/> to read the same value, so the
/// cache bypass and verification paths agree on which requests carry a token.
/// </summary>
/// <remarks>
/// The token authorizes a specific principal for a short window; caching by
/// URL alone would either:
/// <list type="bullet">
///   <item><description>Store unique entries keyed per token, blowing up cache size for browser sessions, or</description></item>
///   <item><description>Replay an authorized payload to a later anonymous client hitting the same URL after expiry.</description></item>
/// </list>
/// </remarks>
internal sealed class BypassOutputCacheOnSceneAccessTokenPolicy : IOutputCachePolicy
{
    /// <summary>Query parameter name for browser-safe token transport.</summary>
    public const string TokenQueryParameter = "token";

    /// <summary>Header name for native-client token transport.</summary>
    public const string TokenHeader = "X-Honua-Token";

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (TryExtractToken(context.HttpContext) is not null)
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
        if (TryExtractToken(context.HttpContext) is not null)
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns the first non-empty access envelope token from
    /// <see cref="TokenQueryParameter"/> (preferred) or
    /// <see cref="TokenHeader"/>, or <c>null</c> if neither is present.
    /// Single source of truth for token transport detection so the cache
    /// policy and the asset-endpoint verifier never disagree.
    /// </summary>
    public static string? TryExtractToken(HttpContext context)
        => TryExtractToken(context, out _);

    /// <summary>
    /// Returns the first non-empty access envelope token and reports whether
    /// it came from the query string or native-client header.
    /// </summary>
    public static string? TryExtractToken(HttpContext context, out SceneAccessTokenTransport transport)
    {
        transport = SceneAccessTokenTransport.None;

        if (context.Request.Query.TryGetValue(TokenQueryParameter, out var queryValues))
        {
            foreach (var value in queryValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    transport = SceneAccessTokenTransport.Query;
                    return value;
                }
            }
        }

        if (context.Request.Headers.TryGetValue(TokenHeader, out var headerValues))
        {
            foreach (var value in headerValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    transport = SceneAccessTokenTransport.Header;
                    return value;
                }
            }
        }

        return null;
    }
}
