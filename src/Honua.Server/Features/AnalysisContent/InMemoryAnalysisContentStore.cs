// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Server.Features.AnalysisContent;

internal sealed class InMemoryAnalysisContentStore : IAnalysisContentStore
{
    private readonly ConcurrentDictionary<string, AnalysisContentItem> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SortedDictionary<int, AnalysisContentVersion>> _versions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResultArtifactRecord> _artifacts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

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
                throw new InvalidOperationException("Analysis content item already exists.");
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
                throw new InvalidOperationException("Analysis content version already exists.");
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
        if (!_versions.TryGetValue(itemId, out var versions) || versions.Count == 0)
        {
            return Task.FromResult<AnalysisContentVersion?>(null);
        }

        AnalysisContentVersion? resolved;
        lock (_gate)
        {
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

    public Task<ResultArtifactRecord> UpsertArtifactAsync(
        ResultArtifactRecord artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        _artifacts[artifact.ArtifactId] = artifact;
        return Task.FromResult(artifact);
    }

    public Task<ResultArtifactRecord?> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        _artifacts.TryGetValue(artifactId, out var artifact);
        return Task.FromResult(artifact);
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
