// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Output cache policy that disables both lookup and storage when the inbound
/// request includes a <c>Range</c> header. Output caching does not produce
/// <c>206 Partial Content</c> / <c>Content-Range</c> responses; ASP.NET Core's
/// static-file pipeline does. Bypassing the cache for range requests prevents
/// a previously cached full <c>200</c> response from satisfying a later range
/// GET as a full body.
/// </summary>
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
