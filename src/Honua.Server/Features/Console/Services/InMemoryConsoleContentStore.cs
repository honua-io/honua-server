// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Baseline in-memory <see cref="IConsoleContentStore"/>. Suitable for tests and
/// early Console wiring; a persistent implementation lands in a follow-on
/// ticket (see <c>#1163</c>).
/// </summary>
internal sealed class InMemoryConsoleContentStore : IConsoleContentStore
{
    private const int MaxLimit = 200;
    private readonly ConcurrentDictionary<string, ConsoleContentItem> _items = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryConsoleContentStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public Task<ConsoleContentListResult> ListAsync(ConsoleContentQuery query, CancellationToken cancellationToken = default)
    {
        var limit = query.Limit <= 0 ? 25 : Math.Min(query.Limit, MaxLimit);
        var filtered = ApplyFilters(query).OrderBy(static i => i.Id, StringComparer.Ordinal).ToList();
        var startIndex = ResolveCursorIndex(query.Cursor, filtered);
        var page = filtered.Skip(startIndex).Take(limit).ToList();
        string? nextCursor = null;
        if (startIndex + page.Count < filtered.Count)
        {
            nextCursor = EncodeCursor(startIndex + page.Count);
        }
        IReadOnlyList<ConsoleContentItem> pageView = page;
        return Task.FromResult(new ConsoleContentListResult
        {
            Items = pageView,
            Total = filtered.Count,
            NextCursor = nextCursor,
        });
    }

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
        _items[id] = updated;
        return Task.FromResult<ConsoleContentItem?>(updated);
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

            foreach (var reference in current.Provenance)
            {
                if (!visited.Add(reference.ItemId))
                    continue;

                if (_items.TryGetValue(reference.ItemId, out var next))
                {
                    ordered.Add(next);
                    frontier.Enqueue((next, depth + 1));
                }
            }
        }

        IReadOnlyList<ConsoleContentItem> result = ordered;
        return Task.FromResult(result);
    }

    private IEnumerable<ConsoleContentItem> ApplyFilters(ConsoleContentQuery query)
    {
        IEnumerable<ConsoleContentItem> source = _items.Values;
        if (query.ItemTypes is { Count: > 0 } types)
        {
            source = source.Where(i => types.Contains(i.ItemType));
        }
        if (query.Visibilities is { Count: > 0 } visibilities)
        {
            source = source.Where(i => visibilities.Contains(i.Visibility));
        }
        if (!string.IsNullOrWhiteSpace(query.OwnerId))
        {
            source = source.Where(i => string.Equals(i.OwnerId, query.OwnerId, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(query.Namespace))
        {
            source = source.Where(i => string.Equals(i.Namespace, query.Namespace, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm.Trim();
            source = source.Where(i => Matches(i, term));
        }
        return source;
    }

    private static bool Matches(ConsoleContentItem item, string term)
    {
        return item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
               || (item.Title is { } title && title.Contains(term, StringComparison.OrdinalIgnoreCase))
               || (item.Description is { } description && description.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveCursorIndex(string? cursor, List<ConsoleContentItem> source)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? Math.Clamp(index, 0, source.Count)
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string EncodeCursor(int index)
    {
        var bytes = Encoding.UTF8.GetBytes(index.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(bytes);
    }
}
