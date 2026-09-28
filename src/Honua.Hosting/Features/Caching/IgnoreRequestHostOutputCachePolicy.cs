// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Stops the scene asset cache from varying by the request Host header.
/// ASP.NET Core varies output-cache entries by host unless a policy turns that
/// off. Public scene assets share an entry across internal and advertised Host
/// values within the same request scheme. The framework still separates HTTP
/// and HTTPS entries and excludes non-200 responses from cache storage.
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
