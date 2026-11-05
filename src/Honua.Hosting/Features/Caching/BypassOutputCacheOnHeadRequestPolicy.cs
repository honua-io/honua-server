// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

// The output cache stores a HEAD response with an empty body and stamps Content-Length from that length, so a cached HEAD advertises zero length and GDAL range clients reject the asset.
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
