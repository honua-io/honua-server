// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Caching;

namespace Honua.Infrastructure.SharedCaching;

// Tells the shared-cache policy when a response came from this task's response cache, which another task's edit does
// not reach, so that response leaves with the ordinary CDN lifetime rather than a purge-backed one.
internal sealed class ProcessCacheHitResponseCache(IResponseCache inner, IHttpContextAccessor accessor) : IResponseCache
{
    public Task<string> BindKeyAsync(string key, CancellationToken cancellationToken = default)
        => inner.BindKeyAsync(key, cancellationToken);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var cached = await inner.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            MarkHit();
        }

        return cached;
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
        => inner.SetAsync(key, value, expiration, cancellationToken);

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
    {
        var created = false;
        var value = await inner.GetOrCreateAsync(
            key,
            () =>
            {
                created = true;
                return factory();
            },
            expiration,
            cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            MarkHit();
        }

        return value;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => inner.RemoveAsync(key, cancellationToken);

    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
        => inner.RemoveByPatternAsync(pattern, cancellationToken);

    private void MarkHit()
    {
        if (accessor.HttpContext is { } context)
        {
            SharedCacheResponse.MarkServedFromProcessCache(context);
        }
    }
}
