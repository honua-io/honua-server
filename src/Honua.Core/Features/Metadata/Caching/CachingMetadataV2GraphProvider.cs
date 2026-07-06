// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

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
    }

    /// <inheritdoc />
    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        => _cache.GetOrLoadAsync(_environment, _inner.GetCurrentAsync, cancellationToken);

    /// <inheritdoc />
    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
        => _inner.GetByRevisionAsync(revision, cancellationToken);
}
