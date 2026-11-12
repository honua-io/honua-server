// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Caching;

// After a CDN purge the next miss can reach any task. An entry kept here longer than the CDN keeps the response would
// hand the CDN the old response again with a fresh lifetime, so no entry outlives the CDN lifetime of its route class,
// including per-layer and emergency lifetimes.
internal sealed class CapOutputCacheLifetimeAtCdnLifetimePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        Apply(context);
        return ValueTask.CompletedTask;
    }

    // Also applied by any later policy that re-admits storage, so a re-admitted entry obeys the same limit.
    internal static void Apply(OutputCacheContext context)
    {
        if (SharedCacheResponse.SharedLifetime(context.HttpContext) is not { } cdnLifetime)
        {
            return;
        }

        if (cdnLifetime <= TimeSpan.Zero)
        {
            context.AllowCacheStorage = false;
            return;
        }

        var lifetime = context.ResponseExpirationTimeSpan ?? DefaultLifetime(context.HttpContext);
        if (lifetime > cdnLifetime)
        {
            context.ResponseExpirationTimeSpan = cdnLifetime;
        }
    }

    private static TimeSpan DefaultLifetime(HttpContext context)
        => context.RequestServices?.GetService<IOptions<OutputCacheOptions>>()?.Value.DefaultExpirationTimeSpan
           ?? new OutputCacheOptions().DefaultExpirationTimeSpan;
}
