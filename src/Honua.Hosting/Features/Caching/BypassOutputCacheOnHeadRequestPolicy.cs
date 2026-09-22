// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Output cache policy that keeps <c>HEAD</c> requests out of the output cache entirely
/// (no lookup, no storage).
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core's output cache stores a HEAD response as an entry with an empty body and,
/// on replay, stamps <c>Content-Length</c> from the cached body length. The first HEAD on a
/// resource therefore reports the real length while every later HEAD within the TTL reports
/// <c>Content-Length: 0</c>. GDAL's <c>/vsicurl</c> sizes a remote file from that HEAD and,
/// on a zero, reads nothing (QGIS then rejects the PMTiles archive as an invalid data source);
/// ArcGIS Pro and range-aware browsers behave the same way.
/// </para>
/// <para>
/// A HEAD is cheap on this server (the shared HEAD support discards the body), and cached GET
/// entries are keyed separately, so bypassing the cache for HEAD loses nothing.
/// </para>
/// </remarks>
internal sealed class BypassOutputCacheOnHeadRequestPolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsHead(context.HttpContext.Request.Method))
        {
            context.EnableOutputCaching = false;
            context.AllowCacheLookup = false;
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsHead(context.HttpContext.Request.Method))
        {
            context.AllowCacheLookup = false;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (HttpMethods.IsHead(context.HttpContext.Request.Method))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
