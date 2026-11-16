// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Caching;

namespace Honua.Infrastructure.Caching;

// Bind before the query starts: a concurrent edit must retire the fill along with existing entries.
internal sealed class ExternalRevisionResponseCache(IResponseCache inner, IExternalLayerRevisionSource revisions)
    : IResponseCache
{
    private const string BoundPrefix = "external-bound:";

    public async Task<string> BindKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key.StartsWith(BoundPrefix, StringComparison.Ordinal))
        {
            return key;
        }

        var versioned = await VersionAsync(key, cancellationToken).ConfigureAwait(false);
        return BoundPrefix + await inner.BindKeyAsync(versioned, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        => await inner.GetAsync<T>(await VersionAsync(key, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
        => await inner.SetAsync(await VersionAsync(key, cancellationToken).ConfigureAwait(false), value, expiration, cancellationToken).ConfigureAwait(false);

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default) where T : class
        => await inner.GetOrCreateAsync(await VersionAsync(key, cancellationToken).ConfigureAwait(false), factory, expiration, cancellationToken).ConfigureAwait(false);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => await inner.RemoveAsync(await VersionAsync(key, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
        => inner.RemoveByPatternAsync(pattern, cancellationToken);

    private async Task<string> VersionAsync(string key, CancellationToken cancellationToken)
        => key.StartsWith(BoundPrefix, StringComparison.Ordinal)
            ? key[BoundPrefix.Length..]
            : key + ":external-revision:" + await revisions.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
}
