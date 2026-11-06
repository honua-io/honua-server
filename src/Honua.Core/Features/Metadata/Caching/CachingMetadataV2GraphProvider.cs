// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Metadata.Caching;

public sealed class CachingMetadataV2GraphProvider : IMetadataV2GraphProvider
{
    private readonly IMetadataV2GraphProvider _inner;
    private readonly MetadataV2GraphSnapshotCache _cache;
    private readonly string _environment;
    private readonly Func<CancellationToken, ValueTask<MetadataV2GraphSnapshot>> _loadCurrent;
    private readonly IMetadataV2GraphRevisionProbe? _revisionProbe;
    private MetadataV2GraphSnapshot? _validated;

    public CachingMetadataV2GraphProvider(
        IMetadataV2GraphProvider inner,
        MetadataV2GraphSnapshotCache cache,
        string environment)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _environment = string.IsNullOrWhiteSpace(environment)
            ? throw new ArgumentException("Environment must be set.", nameof(environment))
            : environment;
        _loadCurrent = _inner.GetCurrentAsync;
    }

    // A coalesced load outlives the caller, so it opens its own scope instead of using a request-scoped connection.
    public CachingMetadataV2GraphProvider(
        IMetadataV2GraphProvider inner,
        MetadataV2GraphSnapshotCache cache,
        string environment,
        IServiceScopeFactory loadScopeFactory,
        Func<IServiceProvider, IMetadataV2GraphProvider> resolveLoader)
        : this(inner, cache, environment)
    {
        ArgumentNullException.ThrowIfNull(loadScopeFactory);
        ArgumentNullException.ThrowIfNull(resolveLoader);
        _loadCurrent = async cancellationToken =>
        {
            await using var scope = loadScopeFactory.CreateAsyncScope();
            return await resolveLoader(scope.ServiceProvider)
                .GetCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
        };
    }

    public CachingMetadataV2GraphProvider(
        IMetadataV2GraphProvider inner,
        MetadataV2GraphSnapshotCache cache,
        string environment,
        IServiceScopeFactory loadScopeFactory,
        Func<IServiceProvider, IMetadataV2GraphProvider> resolveLoader,
        IMetadataV2GraphRevisionProbe? revisionProbe)
        : this(inner, cache, environment, loadScopeFactory, resolveLoader)
    {
        _revisionProbe = revisionProbe;
    }

    public CachingMetadataV2GraphProvider(
        IMetadataV2GraphProvider inner,
        MetadataV2GraphSnapshotCache cache,
        string environment,
        IMetadataV2GraphRevisionProbe? revisionProbe)
        : this(inner, cache, environment)
    {
        _revisionProbe = revisionProbe;
    }

    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        => _revisionProbe is null || !_cache.IsEnabled
            ? _cache.GetOrLoadAsync(_environment, _loadCurrent, cancellationToken)
            : GetRevalidatedAsync(_revisionProbe, cancellationToken);

    private async ValueTask<MetadataV2GraphSnapshot> GetRevalidatedAsync(
        IMetadataV2GraphRevisionProbe probe,
        CancellationToken cancellationToken)
    {
        var snapshot = await _cache.GetOrLoadAsync(_environment, _loadCurrent, cancellationToken).ConfigureAwait(false);

        // One probe per request scope: repeat reads in the same request reuse the validated
        // snapshot. A same-scope write invalidates the cache and the next read returns a different
        // snapshot object, which is probed again rather than trusting the memo.
        if (ReferenceEquals(snapshot, _validated))
        {
            return snapshot;
        }

        var currentEtag = await probe.GetCurrentEtagAsync(cancellationToken).ConfigureAwait(false);
        if (currentEtag is not null && !string.Equals(currentEtag, snapshot.Etag, StringComparison.Ordinal))
        {
            // Another instance activated a newer revision. Drop the superseded snapshot (only if
            // it is still the cached one) and reload once; a concurrent reload is shared.
            _cache.InvalidateIfCurrent(_environment, snapshot.Etag);
            snapshot = await _cache.GetOrLoadAsync(_environment, _loadCurrent, cancellationToken).ConfigureAwait(false);
        }

        _validated = snapshot;
        return snapshot;
    }

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
        => _inner.GetByRevisionAsync(revision, cancellationToken);
}
