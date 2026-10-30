// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Metadata.Caching;

/// <summary>
/// Read-through caching decorator over <see cref="IMetadataV2GraphProvider"/>. Serves the current
/// graph snapshot from a shared, per-instance <see cref="MetadataV2GraphSnapshotCache"/> so repeated
/// catalog resolutions (every MCP tool call, every REST/OGC metadata read) reuse one materialized
/// snapshot instead of re-reading the full catalog document from the backing store on each call.
/// </summary>
/// <remarks>
/// Only the read surface (<see cref="IMetadataV2GraphProvider"/>) is decorated. Admin/write paths
/// resolve the concrete store's persisted-snapshot reader directly and are never routed through this
/// cache, so read-modify-write always sees a fresh snapshot. Historical revision lookups
/// (<see cref="GetByRevisionAsync"/>) are rare and revision-specific, so they pass straight through.
/// </remarks>
public sealed class CachingMetadataV2GraphProvider : IMetadataV2GraphProvider
{
    private readonly IMetadataV2GraphProvider _inner;
    private readonly MetadataV2GraphSnapshotCache _cache;
    private readonly string _environment;
    private readonly Func<CancellationToken, ValueTask<MetadataV2GraphSnapshot>> _loadCurrent;
    private readonly IMetadataV2GraphRevisionProbe? _revisionProbe;
    private MetadataV2GraphSnapshot? _validated;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingMetadataV2GraphProvider"/> class.
    /// </summary>
    /// <param name="inner">The provider that reads the snapshot from the backing store.</param>
    /// <param name="cache">The shared per-instance snapshot cache.</param>
    /// <param name="environment">The metadata environment this provider resolves (the cache key).</param>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingMetadataV2GraphProvider"/> class whose
    /// cache-miss loads run in a scope owned by the load itself rather than the calling request.
    /// </summary>
    /// <remarks>
    /// A coalesced load outlives any individual caller (a cancelled caller stops waiting, the load
    /// continues for the other waiters), so it must not use the caller's scoped, disposable
    /// infrastructure — e.g. a scoped connection provider whose disposal releases its
    /// query-concurrency slots. Each load creates and disposes its own async scope.
    /// </remarks>
    /// <param name="inner">The request-scoped provider (used for revision lookups).</param>
    /// <param name="cache">The shared per-instance snapshot cache.</param>
    /// <param name="environment">The metadata environment this provider resolves (the cache key).</param>
    /// <param name="loadScopeFactory">Creates the scope each cache-miss load runs in.</param>
    /// <param name="resolveLoader">Resolves the uncached backing provider from the load scope.</param>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingMetadataV2GraphProvider"/> class that
    /// revalidates the cached snapshot against the database once per instance of this provider
    /// (one request scope) before serving it.
    /// </summary>
    /// <remarks>
    /// For nodes with no cross-node invalidation channel, such as Lambda instances without Redis
    /// (honua-server#6762). Each instance caches its own snapshot and nothing tells it about a
    /// publish on another instance, so without this a just-published service 404s until the TTL
    /// expires. The probe is one primary-key read of the current-pointer row; the full catalog is
    /// reloaded (single-flight, through the shared cache) only when the active revision changed.
    /// It needs no LISTEN/NOTIFY or long-lived connection, so it holds across frozen/thawed instances.
    /// </remarks>
    /// <param name="inner">The request-scoped provider (used for revision lookups).</param>
    /// <param name="cache">The shared per-instance snapshot cache.</param>
    /// <param name="environment">The metadata environment this provider resolves (the cache key).</param>
    /// <param name="loadScopeFactory">Creates the scope each cache-miss load runs in.</param>
    /// <param name="resolveLoader">Resolves the uncached backing provider from the load scope.</param>
    /// <param name="revisionProbe">Reads the active revision ETag; <c>null</c> disables revalidation.</param>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingMetadataV2GraphProvider"/> class whose
    /// loads run on <paramref name="inner"/> and which revalidates with <paramref name="revisionProbe"/>.
    /// </summary>
    /// <param name="inner">The provider that reads the snapshot from the backing store.</param>
    /// <param name="cache">The shared per-instance snapshot cache.</param>
    /// <param name="environment">The metadata environment this provider resolves (the cache key).</param>
    /// <param name="revisionProbe">Reads the active revision ETag; <c>null</c> disables revalidation.</param>
    public CachingMetadataV2GraphProvider(
        IMetadataV2GraphProvider inner,
        MetadataV2GraphSnapshotCache cache,
        string environment,
        IMetadataV2GraphRevisionProbe? revisionProbe)
        : this(inner, cache, environment)
    {
        _revisionProbe = revisionProbe;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
        => _inner.GetByRevisionAsync(revision, cancellationToken);
}
