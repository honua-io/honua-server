// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

// Output caching does not produce 206 or Content-Range; the static-file pipeline does, so range requests bypass cached full responses.
internal sealed class BypassOutputCacheOnRangeRequestPolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (HasRangeHeader(context.HttpContext.Request))
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
        => ValueTask.CompletedTask;

    private static bool HasRangeHeader(HttpRequest request)
        => request.Headers.ContainsKey(HeaderNames.Range);
}
