// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Stops the scene asset cache from varying by the request Host header.
/// ASP.NET Core varies output-cache entries by host unless a policy turns that
/// off. A tileset fetched as <c>honua:5000</c> and the same path fetched through
/// the advertised TLS host were therefore different entries: one could stay 200
/// after generation while the other kept a 404 (#5218).
/// </summary>
internal sealed class IgnoreRequestHostOutputCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        context.CacheVaryByRules.VaryByHost = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
