// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

internal sealed class BypassOutputCacheOnNoStoreResponsePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var cacheControl = context.HttpContext.Response.Headers[HeaderNames.CacheControl].ToString();
        if (cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
