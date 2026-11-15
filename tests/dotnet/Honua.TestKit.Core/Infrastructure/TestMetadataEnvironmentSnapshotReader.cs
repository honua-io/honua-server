// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.TestKit;

public sealed class TestMetadataEnvironmentSnapshotReader(IEnumerable<MetadataV2Graph> graphs, DateTimeOffset generatedAt) : IMetadataV2EnvironmentSnapshotReader
{
    private readonly Dictionary<string, MetadataV2GraphSnapshot> _snapshots = graphs.ToDictionary(
        static graph => graph.Environment,
        graph => new MetadataV2GraphSnapshot(
            graph,
            $"etag-{graph.Environment}-{graph.Revision}",
            generatedAt),
        StringComparer.OrdinalIgnoreCase);

    public ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
        string environment,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(
            _snapshots.TryGetValue(environment, out var snapshot) ? snapshot : null);

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
        string environment,
        long revision,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshots.TryGetValue(environment, out var current) && current.Revision == revision
            ? current
            : null;
        return ValueTask.FromResult(snapshot);
    }

    public async ValueTask<MetadataV2GraphSnapshot?> GetByEtagAsync(
        string environment,
        string etag,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetCurrentAsync(environment, cancellationToken).ConfigureAwait(false);
        return snapshot is not null && string.Equals(snapshot.Etag, etag, StringComparison.Ordinal) ? snapshot : null;
    }

    public async IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
        IReadOnlyList<string> environments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var snapshot in environments.Select(_snapshots.GetValueOrDefault).OfType<MetadataV2GraphSnapshot>())
        {
            yield return new MetadataV2EnvironmentRevision
            {
                Environment = snapshot.Graph.Environment,
                Revision = snapshot.Revision,
                ETag = snapshot.Etag,
                ActivatedAt = snapshot.LoadedAt,
            };
        }
    }
}
