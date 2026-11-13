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
    private readonly IConfigurationGenerationSource? _generationSource;

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
        IMetadataV2GraphRevisionProbe? revisionProbe,
        IConfigurationGenerationSource? generationSource = null)
        : this(inner, cache, environment, loadScopeFactory, resolveLoader)
    {
        _generationSource = generationSource;
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

    public async ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var cache = _generationSource is null ? _cache : _cache.ForGeneration(_environment,
            await _generationSource.GetGenerationAsync(cancellationToken).ConfigureAwait(false));
        return _revisionProbe is null || !cache.IsEnabled
            ? await cache.GetOrLoadAsync(_environment, _loadCurrent, cancellationToken).ConfigureAwait(false)
            : await GetRevalidatedAsync(cache, _revisionProbe, cancellationToken).ConfigureAwait(false);
    }

    // The probe, not the snapshot lifetime, decides when the catalog is read again: a probe that finds the cached
    // revision still current renews it, so the document is reloaded and parsed only after a revision change.
    private async ValueTask<MetadataV2GraphSnapshot> GetRevalidatedAsync(
        MetadataV2GraphSnapshotCache cache, IMetadataV2GraphRevisionProbe probe,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetExpired(_environment, out var expired))
        {
            await RenewIfCurrentAsync(cache, expired, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await cache.GetOrLoadAsync(_environment, _loadCurrent, cancellationToken).ConfigureAwait(false);

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
            cache.InvalidateIfCurrent(_environment, snapshot.Etag);
            snapshot = await cache.GetOrLoadAsync(_environment, _loadCurrent, cancellationToken).ConfigureAwait(false);
        }
        else if (currentEtag is not null)
        {
            cache.Renew(_environment, currentEtag);
        }

        _validated = snapshot;
        return snapshot;
    }

    // A probe that is not due, or a catalog with no activated revision, answers null: the expired snapshot is then
    // reloaded as before.
    private async ValueTask RenewIfCurrentAsync(
        MetadataV2GraphSnapshotCache cache, MetadataV2GraphSnapshot expired,
        CancellationToken cancellationToken)
    {
        var currentEtag = await _revisionProbe!.GetCurrentEtagAsync(cancellationToken).ConfigureAwait(false);
        if (currentEtag is not null && string.Equals(currentEtag, expired.Etag, StringComparison.Ordinal))
        {
            cache.Renew(_environment, currentEtag);
            _validated = expired;
        }
    }

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
        => _inner.GetByRevisionAsync(revision, cancellationToken);
}
