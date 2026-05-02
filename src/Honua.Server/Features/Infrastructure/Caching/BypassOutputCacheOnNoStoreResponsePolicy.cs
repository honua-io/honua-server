// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Output cache policy that disables server-side cache storage when the
/// outgoing response carries <c>Cache-Control: no-store</c>. The handler may
/// only learn that the response is non-cacheable after resolving the
/// downstream resource (e.g. a scene dataset whose registry record sets
/// <c>cachePolicy.noStore = true</c>); without this hook the response would
/// still be eligible for the named output-cache policy until TTL/invalidation.
/// </summary>
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
