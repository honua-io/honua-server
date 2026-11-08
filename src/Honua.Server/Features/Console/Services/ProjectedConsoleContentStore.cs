// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

internal sealed class ProjectedConsoleContentStore(
    InMemoryConsoleContentStore authoredContent,
    PublishedConsoleContent publishedContent) : IConsoleContentStore
{
    public async Task<ConsoleContentListResult> ListAsync(ConsoleContentQuery query, CancellationToken cancellationToken = default)
    {
        var published = await publishedContent.ReadAsync(cancellationToken).ConfigureAwait(false);
        var items = published.Items.Values.Concat(authoredContent.SnapshotItems()
            .Where(item => !published.OwnedIds.Contains(item.Id)));
        return ConsoleContentSelection.Page(items, query);
    }

    public async Task<ConsoleContentItem?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var published = await publishedContent.ReadAsync(cancellationToken).ConfigureAwait(false);
        return published.OwnedIds.Contains(id)
            ? published.Items.GetValueOrDefault(id)
            : await authoredContent.GetAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConsoleContentItem> CreateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default)
    {
        var published = await publishedContent.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (published.OwnedIds.Contains(item.Id))
        {
            throw new InvalidOperationException("Published metadata is read-only in the content catalog.");
        }
        return await authoredContent.CreateAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConsoleContentItem?> UpdateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default)
    {
        var published = await publishedContent.ReadAsync(cancellationToken).ConfigureAwait(false);
        return published.OwnedIds.Contains(item.Id)
            ? null
            : await authoredContent.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConsoleContentItem?> PatchAsync(string id, ConsoleContentItemPatch patch, CancellationToken cancellationToken = default)
    {
        var published = await publishedContent.ReadAsync(cancellationToken).ConfigureAwait(false);
        return published.OwnedIds.Contains(id)
            ? null
            : await authoredContent.PatchAsync(id, patch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var published = await publishedContent.ReadAsync(cancellationToken).ConfigureAwait(false);
        return !published.OwnedIds.Contains(id) && await authoredContent.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsoleContentItem>> GetProvenanceChainAsync(
        string id, int maxDepth, CancellationToken cancellationToken = default)
    {
        var published = await publishedContent.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (published.OwnedIds.Contains(id))
        {
            return published.Items.TryGetValue(id, out var item) ? [item] : [];
        }
        var chain = await authoredContent.GetProvenanceChainAsync(id, maxDepth, cancellationToken).ConfigureAwait(false);
        return chain.Select(item => published.OwnedIds.Contains(item.Id)
                ? published.Items.GetValueOrDefault(item.Id) : item)
            .OfType<ConsoleContentItem>().ToArray();
    }
}
