// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

internal static class ConsoleProvenanceWalk
{
    // Breadth-first from the anchor; each item appears once, so a reference cycle ends.
    public static IReadOnlyList<ConsoleContentItem> Collect(
        IReadOnlyDictionary<string, ConsoleContentItem> items, string id, int maxDepth)
    {
        if (!items.TryGetValue(id, out var anchor))
        {
            return [];
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { anchor.Id };
        var ordered = new List<ConsoleContentItem> { anchor };
        var frontier = new Queue<(ConsoleContentItem Item, int Depth)>();
        frontier.Enqueue((anchor, 0));
        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }
            foreach (var reference in current.Provenance)
            {
                if (reference is not null
                    && !string.IsNullOrWhiteSpace(reference.ItemId)
                    && visited.Add(reference.ItemId)
                    && items.TryGetValue(reference.ItemId, out var next))
                {
                    ordered.Add(next);
                    frontier.Enqueue((next, depth + 1));
                }
            }
        }
        return ordered;
    }
}
