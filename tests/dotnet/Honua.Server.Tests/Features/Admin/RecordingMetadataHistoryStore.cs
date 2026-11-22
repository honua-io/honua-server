// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Admin;

// Stands in for the Postgres history trigger on the in-memory graph: records resource title edits
// with the actor the request-scoped provider reports at save time, and serves them back filtered.
internal sealed class RecordingMetadataHistoryStore(
    TestMetadataV2GraphProvider inner,
    IMetadataActorProvider actorProvider) : IMetadataV2GraphStore, IMetadataFieldHistoryStore
{
    private readonly ConcurrentQueue<MetadataFieldHistoryEntry> _entries = new();

    public ConcurrentQueue<MetadataFieldHistoryQuery> Queries { get; } = new();

    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        inner.GetCurrentAsync(cancellationToken);

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default) =>
        inner.GetByRevisionAsync(revision, cancellationToken);

    public Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(long revision, string? expectedCurrentEtag, CancellationToken cancellationToken = default) =>
        inner.ActivateRevisionAsync(revision, expectedCurrentEtag, cancellationToken);

    public async Task<MetadataV2GraphSnapshot> SaveAsync(MetadataV2Graph graph, string? expectedEtag, CancellationToken cancellationToken = default)
    {
        var before = await inner.GetCurrentAsync(cancellationToken);
        var saved = await inner.SaveAsync(graph, expectedEtag, cancellationToken);
        foreach (var resource in saved.Graph.Resources)
        {
            var previous = before.Graph.Resources.FirstOrDefault(r => r.Metadata.Id == resource.Metadata.Id);
            if (previous is not null && previous.Metadata.Title != resource.Metadata.Title)
            {
                _entries.Enqueue(new MetadataFieldHistoryEntry
                {
                    Id = _entries.Count + 1,
                    Revision = saved.Graph.Revision,
                    ItemKind = "resources",
                    ItemId = resource.Metadata.Id,
                    FieldPath = ["title"],
                    OldValue = JsonSerializer.SerializeToElement(previous.Metadata.Title),
                    NewValue = JsonSerializer.SerializeToElement(resource.Metadata.Title),
                    Actor = actorProvider.GetCurrentActor() ?? "system:metadata-publisher",
                });
            }
        }
        return saved;
    }

    public Task<MetadataFieldHistoryPage> ListAsync(MetadataFieldHistoryQuery query, CancellationToken cancellationToken = default)
    {
        Queries.Enqueue(query);
        var items = _entries
            .Where(entry => query.ItemId is null || entry.ItemId == query.ItemId)
            .Where(entry => query.FieldPrefix is null || string.Join('.', entry.FieldPath).StartsWith(query.FieldPrefix, StringComparison.Ordinal))
            .Reverse()
            .Take(query.Limit)
            .ToArray();
        return Task.FromResult(new MetadataFieldHistoryPage { Items = items, HasMore = false });
    }
}
