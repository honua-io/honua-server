// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.OpenData.Abstractions;
using Honua.Core.Features.OpenData.Domain;

namespace Honua.Server.Features.OpenData.Services;

/// <summary>
/// Baseline in-memory open-data store. This matches the current Console content
/// store persistence boundary until the shared persistent Console store lands.
/// </summary>
internal sealed class InMemoryOpenDataStore : IOpenDataStore
{
    private readonly ConcurrentDictionary<string, OpenDataPageRecord> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpenDataStacPublicationRecord> _stacPublications = new(StringComparer.Ordinal);

    public Task<OpenDataPageRecord?> GetPageRecordAsync(string itemId, CancellationToken cancellationToken = default)
    {
        _pages.TryGetValue(itemId, out var record);
        return Task.FromResult(record);
    }

    public Task<OpenDataPageRecord> SetPageRecordAsync(OpenDataPageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _pages[record.ItemId] = record;
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<OpenDataPageRecord>> ListPageRecordsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenDataPageRecord> records = _pages.Values
            .OrderBy(static page => page.ItemId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(records);
    }

    public Task<OpenDataStacPublicationRecord?> GetStacPublicationAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        _stacPublications.TryGetValue(collectionId, out var record);
        return Task.FromResult(record);
    }

    public Task<OpenDataStacPublicationRecord?> GetStacPublicationByItemAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var record = _stacPublications.Values
            .OrderByDescending(static publication => publication.UpdatedAt)
            .FirstOrDefault(publication => string.Equals(publication.ItemId, itemId, StringComparison.Ordinal));
        return Task.FromResult(record);
    }

    public Task<OpenDataStacPublicationRecord> SetStacPublicationAsync(
        OpenDataStacPublicationRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _stacPublications[record.CollectionId] = record;
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<OpenDataStacPublicationRecord>> ListStacPublicationsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenDataStacPublicationRecord> records = _stacPublications.Values
            .OrderBy(static publication => publication.CollectionId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(records);
    }
}
