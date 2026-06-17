// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Ai.AnalysisContent;

internal sealed class InMemoryAnalysisContentStore : IAnalysisContentStore
{
    private readonly ConcurrentDictionary<string, AnalysisContentItem> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SortedDictionary<int, AnalysisContentVersion>> _versions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResultArtifactRecord> _artifacts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryAnalysisContentStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<AnalysisContentItem> CreateItemAsync(
        AnalysisContentItem item,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(version);

        lock (_gate)
        {
            if (_items.ContainsKey(item.ItemId))
            {
                throw new AnalysisContentStoreConflictException("Analysis content item already exists.");
            }

            _items[item.ItemId] = item;
            _versions[item.ItemId] = new SortedDictionary<int, AnalysisContentVersion>
            {
                [version.Version] = version
            };
        }

        return Task.FromResult(item);
    }

    public Task<AnalysisContentItem?> GetItemAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        _items.TryGetValue(itemId, out var item);
        return Task.FromResult(item);
    }

    public Task<AnalysisContentVersion> AddVersionAsync(
        string itemId,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item))
            {
                throw new KeyNotFoundException("Analysis content item was not found.");
            }

            if (!_versions.TryGetValue(itemId, out var versions))
            {
                versions = new SortedDictionary<int, AnalysisContentVersion>();
                _versions[itemId] = versions;
            }

            if (versions.ContainsKey(version.Version))
            {
                throw new AnalysisContentStoreConflictException("Analysis content version already exists.");
            }

            versions[version.Version] = version;
            _items[itemId] = item with
            {
                CurrentVersion = version.Version,
                CurrentVersionId = version.VersionId,
                UpdatedAt = version.CreatedAt
            };
        }

        return Task.FromResult(version);
    }

    public Task<AnalysisContentVersion?> GetVersionAsync(
        string itemId,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        if (!_versions.TryGetValue(itemId, out var versions))
        {
            return Task.FromResult<AnalysisContentVersion?>(null);
        }

        AnalysisContentVersion? resolved;
        lock (_gate)
        {
            if (versions.Count == 0)
            {
                return Task.FromResult<AnalysisContentVersion?>(null);
            }

            resolved = version.HasValue
                ? versions.GetValueOrDefault(version.Value)
                : versions.LastOrDefault().Value;
        }

        return Task.FromResult(resolved);
    }

    public Task<IReadOnlyList<AnalysisContentVersion>> ListVersionsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        if (!_versions.TryGetValue(itemId, out var versions))
        {
            return Task.FromResult<IReadOnlyList<AnalysisContentVersion>>(Array.Empty<AnalysisContentVersion>());
        }

        AnalysisContentVersion[] snapshot;
        lock (_gate)
        {
            snapshot = versions.Values.ToArray();
        }

        return Task.FromResult<IReadOnlyList<AnalysisContentVersion>>(snapshot);
    }

    public Task<AnalysisContentItemPage> ListItemsAsync(
        AnalysisContentItemQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        AnalysisContentItem[] snapshot;
        lock (_gate)
        {
            snapshot = _items.Values.ToArray();
        }

        var lifecycle = query.Lifecycle ?? AnalysisContentLifecycle.Active;
        var filtered = snapshot
            .Where(item => item.Lifecycle == lifecycle)
            .Where(item => query.Kind is null || item.Kind == query.Kind.Value)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToArray();

        var page = filtered
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();

        return Task.FromResult(new AnalysisContentItemPage
        {
            Items = page,
            TotalCount = filtered.Length
        });
    }

    public Task<ResultArtifactRecord> UpsertArtifactAsync(
        ResultArtifactRecord artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        _artifacts[artifact.ArtifactId] = artifact;

        // Opportunistic sweep: remove entries whose ExpiresAt has passed so the
        // dictionary does not grow without bound across preview calls.
        var now = _timeProvider.GetUtcNow();
        foreach (var key in _artifacts.Keys)
        {
            if (_artifacts.TryGetValue(key, out var candidate)
                && candidate.ExpiresAt.HasValue
                && candidate.ExpiresAt.Value <= now)
            {
                // Value-conditional remove: only drop the exact stale entry we just
                // inspected, so a concurrent refresh between the check and the remove
                // is not lost.
                _artifacts.TryRemove(new KeyValuePair<string, ResultArtifactRecord>(key, candidate));
            }
        }

        return Task.FromResult(artifact);
    }

    public Task<ResultArtifactRecord?> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        if (!_artifacts.TryGetValue(artifactId, out var artifact))
        {
            return Task.FromResult<ResultArtifactRecord?>(null);
        }

        // Honor the expiry: treat an expired artifact as not-found.
        if (artifact.ExpiresAt.HasValue && artifact.ExpiresAt.Value <= _timeProvider.GetUtcNow())
        {
            _artifacts.TryRemove(artifactId, out _);
            return Task.FromResult<ResultArtifactRecord?>(null);
        }

        return Task.FromResult<ResultArtifactRecord?>(artifact);
    }

    public Task<IReadOnlyList<ResultArtifactRecord>> ListArtifactsForJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var artifacts = _artifacts.Values
            .Where(artifact => string.Equals(artifact.JobId, jobId, StringComparison.Ordinal))
            .OrderBy(artifact => artifact.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ResultArtifactRecord>>(artifacts);
    }
}
