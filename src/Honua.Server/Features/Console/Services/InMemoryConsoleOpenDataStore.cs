// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Baseline in-memory <see cref="IConsoleOpenDataStore"/>. Suitable for tests and
/// early Console wiring; a persistent implementation lands alongside the
/// persistent content/share stores (#1163).
/// </summary>
internal sealed class InMemoryConsoleOpenDataStore : IConsoleOpenDataStore
{
    private readonly ConcurrentDictionary<string, ConsoleOpenDataPage> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConsoleStacPublicationState> _publications = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryConsoleOpenDataStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<ConsoleOpenDataPage?> GetPageAsync(string itemId, CancellationToken cancellationToken = default)
    {
        _pages.TryGetValue(itemId, out var page);
        return Task.FromResult(page);
    }

    public Task<ConsoleOpenDataPage> SavePageAsync(ConsoleOpenDataPage page, string? principalId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var stored = page with
        {
            UpdatedAt = now,
            UpdatedById = principalId,
        };
        _pages[page.ItemId] = stored;
        return Task.FromResult(stored);
    }

    public Task<ConsoleStacPublicationState> GetStacPublicationAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (_publications.TryGetValue(itemId, out var existing))
        {
            return Task.FromResult(existing);
        }

        return Task.FromResult(new ConsoleStacPublicationState { ItemId = itemId });
    }

    public Task<ConsoleStacPublicationState> PublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default)
    {
        // CAS loop so a concurrent publish/unpublish cannot clobber the transition.
        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            if (_publications.TryGetValue(itemId, out var existing))
            {
                var updated = existing with
                {
                    Status = ConsoleStacPublicationStatus.Published,
                    CollectionId = existing.CollectionId ?? NewCollectionId(itemId),
                    Revision = existing.Revision + 1,
                    FirstPublishedAt = existing.FirstPublishedAt ?? now,
                    UpdatedAt = now,
                    UpdatedById = principalId,
                };
                if (_publications.TryUpdate(itemId, updated, existing))
                {
                    return Task.FromResult(updated);
                }

                continue;
            }

            var seeded = new ConsoleStacPublicationState
            {
                ItemId = itemId,
                Status = ConsoleStacPublicationStatus.Published,
                CollectionId = NewCollectionId(itemId),
                Revision = 1,
                FirstPublishedAt = now,
                UpdatedAt = now,
                UpdatedById = principalId,
            };
            if (_publications.TryAdd(itemId, seeded))
            {
                return Task.FromResult(seeded);
            }
        }
    }

    public Task<ConsoleStacPublicationState?> UnpublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (!_publications.TryGetValue(itemId, out var existing) || existing.Status != ConsoleStacPublicationStatus.Published)
            {
                return Task.FromResult<ConsoleStacPublicationState?>(null);
            }

            var updated = existing with
            {
                Status = ConsoleStacPublicationStatus.Unpublished,
                Revision = existing.Revision + 1,
                UpdatedAt = _timeProvider.GetUtcNow(),
                UpdatedById = principalId,
            };
            if (_publications.TryUpdate(itemId, updated, existing))
            {
                return Task.FromResult<ConsoleStacPublicationState?>(updated);
            }
        }
    }

    public Task<IReadOnlyList<string>> ListPublishedStacItemIdsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> ids = _publications.Values
            .Where(state => state.Status == ConsoleStacPublicationStatus.Published)
            .Select(state => state.ItemId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(ids);
    }

    // Stable, opaque collection id derived from the item id. The persistent store
    // should keep this assignment durable so re-publish reuses the same id.
    private static string NewCollectionId(string itemId) => $"honua-{itemId}";
}
