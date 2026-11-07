// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

internal sealed class InMemoryConsoleContentStore : IConsoleContentStore
{
    private readonly ConcurrentDictionary<string, ConsoleContentItem> _items = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryConsoleContentStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<ConsoleContentListResult> ListAsync(ConsoleContentQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(ConsoleContentSelection.Page(_items.Values, query));

    public Task<ConsoleContentItem?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        _items.TryGetValue(id, out var item);
        return Task.FromResult(item);
    }

    public Task<ConsoleContentItem> CreateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
        var stored = item with
        {
            Id = id,
            CreatedAt = item.CreatedAt ?? now,
            UpdatedAt = now,
            Generation = 1,
            Actions = Array.Empty<ConsoleContentAction>(),
        };
        if (!_items.TryAdd(id, stored))
        {
            throw new InvalidOperationException($"Console content item '{id}' already exists.");
        }
        return Task.FromResult(stored);
    }

    public Task<ConsoleContentItem?> UpdateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default)
    {
        // Optimistic concurrency: the supplied generation, when present, must
        // exactly match the stored value. The swap uses TryUpdate so two
        // concurrent updates reading the same generation cannot both succeed —
        // the loser observes a mismatched generation on retry and raises the
        // conflict path.
        while (true)
        {
            if (!_items.TryGetValue(item.Id, out var existing))
                return Task.FromResult<ConsoleContentItem?>(null);

            if (item.Generation is { } requested && existing.Generation is { } current && requested != current)
            {
                throw new InvalidOperationException("Stale generation; refresh and retry.");
            }

            var nextGeneration = (existing.Generation ?? 0) + 1;
            var now = _timeProvider.GetUtcNow();
            var updated = item with
            {
                Id = existing.Id,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
                Generation = nextGeneration,
                Actions = Array.Empty<ConsoleContentAction>(),
            };
            if (_items.TryUpdate(item.Id, updated, existing))
            {
                return Task.FromResult<ConsoleContentItem?>(updated);
            }
        }
    }

    public Task<ConsoleContentItem?> PatchAsync(string id, ConsoleContentItemPatch patch, CancellationToken cancellationToken = default)
    {
        // Compare-and-swap loop: a plain read-modify-write here could clobber a
        // concurrent PUT (reverting Generation and any PUT-only fields). Each
        // iteration re-reads the latest snapshot, re-applies the patch on top,
        // and only commits via TryUpdate so the loser refreshes.
        while (true)
        {
            if (!_items.TryGetValue(id, out var existing))
                return Task.FromResult<ConsoleContentItem?>(null);

            var now = _timeProvider.GetUtcNow();
            var updated = existing with
            {
                Title = patch.Title ?? existing.Title,
                Description = patch.Description ?? existing.Description,
                Tags = patch.Tags ?? existing.Tags,
                Labels = patch.Labels ?? existing.Labels,
                Visibility = patch.Visibility ?? existing.Visibility,
                UpdatedById = patch.UpdatedById ?? existing.UpdatedById,
                UpdatedAt = now,
                Actions = Array.Empty<ConsoleContentAction>(),
            };

            // Invariant: visibility 'team' requires a non-empty teamScopeId.
            // PATCH cannot carry teamScopeId (it is intentionally excluded from
            // ConsoleContentItemPatch), so this check fires when the patch sets
            // visibility=team and the latest stored snapshot has no team scope —
            // covering the TOCTOU window where a concurrent PUT cleared the
            // scope between the endpoint's pre-flight check and the swap here.
            if (updated.Visibility == ConsoleVisibility.Team && string.IsNullOrWhiteSpace(updated.TeamScopeId))
            {
                throw new InvalidOperationException(
                    "visibility 'team' requires a non-empty teamScopeId; use PUT to set the team scope before patching visibility.");
            }

            if (_items.TryUpdate(id, updated, existing))
            {
                return Task.FromResult<ConsoleContentItem?>(updated);
            }
        }
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_items.TryRemove(id, out _));
    }

    public Task<IReadOnlyList<ConsoleContentItem>> GetProvenanceChainAsync(string id, int maxDepth, CancellationToken cancellationToken = default)
    {
        if (!_items.TryGetValue(id, out var anchor))
            return Task.FromResult<IReadOnlyList<ConsoleContentItem>>(Array.Empty<ConsoleContentItem>());

        var visited = new HashSet<string>(StringComparer.Ordinal) { anchor.Id };
        var ordered = new List<ConsoleContentItem> { anchor };
        var frontier = new Queue<(ConsoleContentItem Item, int Depth)>();
        frontier.Enqueue((anchor, 0));

        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            if (depth >= maxDepth)
                continue;

            var referenceIndex = 0;
            while (referenceIndex < current.Provenance.Count)
            {
                var reference = current.Provenance[referenceIndex];
                if (reference is not null &&
                    !string.IsNullOrWhiteSpace(reference.ItemId) &&
                    visited.Add(reference.ItemId) &&
                    _items.TryGetValue(reference.ItemId, out var next))
                {
                    ordered.Add(next);
                    frontier.Enqueue((next, depth + 1));
                }

                referenceIndex++;
            }
        }

        IReadOnlyList<ConsoleContentItem> result = ordered;
        return Task.FromResult(result);
    }

}
