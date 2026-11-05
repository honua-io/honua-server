// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

internal enum SceneAccessTokenTransport
{
    None,
    Query,
    Header
}

internal sealed class BypassOutputCacheOnSceneAccessTokenPolicy : IOutputCachePolicy
{
    public const string TokenQueryParameter = "token";

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

    public static string? TryExtractToken(HttpContext context)
        => TryExtractToken(context, out _);

    public static string? TryExtractToken(HttpContext context, out SceneAccessTokenTransport transport)
    {
        transport = SceneAccessTokenTransport.None;

        if (context.Request.Query.TryGetValue(TokenQueryParameter, out var queryValues))
        {
            var value = queryValues.FirstOrDefault(v => !string.IsNullOrEmpty(v));
            if (value is not null)
            {
                transport = SceneAccessTokenTransport.Query;
                return value;
            }
        }

        if (context.Request.Headers.TryGetValue(TokenHeader, out var headerValues))
        {
            var value = headerValues.FirstOrDefault(v => !string.IsNullOrEmpty(v));
            if (value is not null)
            {
                transport = SceneAccessTokenTransport.Header;
                return value;
            }
        }

        return null;
    }
}
