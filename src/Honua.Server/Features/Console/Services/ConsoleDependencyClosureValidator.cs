// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Default <see cref="IConsoleDependencyClosureValidator"/>. Walks an item's
/// provenance graph breadth-first and reports dependencies that are not
/// shareable by the requested target audience.
/// </summary>
/// <remarks>
/// The walk is capped at the supplied depth (callers pass a hard cap of 10). A
/// provenance edge that does not resolve to a Console content item — an external
/// catalog resource or a published service — is not a closure blocker and is
/// skipped: only Console-owned dependencies carry a share tier to validate.
/// </remarks>
internal sealed class ConsoleDependencyClosureValidator(
    IConsoleContentStore contentStore,
    IConsoleShareStore shareStore,
    ILogger<ConsoleDependencyClosureValidator> logger) : IConsoleDependencyClosureValidator
{
    public async Task<IReadOnlyList<ConsoleShareDependencyConflict>> ValidateAsync(
        string itemId,
        ConsoleShareAccessTier targetTier,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        // Private sharing exposes nothing beyond membership scope, so the closure
        // is always compatible — short-circuit before touching the store.
        if (targetTier == ConsoleShareAccessTier.Private)
        {
            return Array.Empty<ConsoleShareDependencyConflict>();
        }

        var root = await contentStore.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return Array.Empty<ConsoleShareDependencyConflict>();
        }

        var cap = Math.Max(1, maxDepth);
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var conflicts = new List<ConsoleShareDependencyConflict>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<(ConsoleContentItem Item, int Depth)>();
        frontier.Enqueue((root, 0));

        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            if (depth >= cap)
            {
                ConsoleShareEndpointsLog.DependencyClosureDepthCapped(logger, itemId, cap);
                continue;
            }

            foreach (var reference in current.Provenance)
            {
                if (reference is null || string.IsNullOrWhiteSpace(reference.ItemId))
                {
                    continue;
                }

                if (!visited.Add(reference.ItemId))
                {
                    continue;
                }

                var dependency = await contentStore.GetAsync(reference.ItemId, cancellationToken).ConfigureAwait(false);
                if (dependency is null)
                {
                    // Dangling / external provenance edge — not a Console-owned
                    // dependency, so it carries no share tier to enforce.
                    continue;
                }

                var dependencyState = await shareStore.GetAsync(dependency.Id, cancellationToken).ConfigureAwait(false);
                if (!IsCompatible(dependency, dependencyState, targetTier, out var reason)
                    && reported.Add(dependency.Id))
                {
                    conflicts.Add(new ConsoleShareDependencyConflict
                    {
                        ItemId = dependency.Id,
                        ItemType = dependency.ItemType,
                        ItemName = dependency.Name,
                        BlockingReason = reason,
                    });
                }

                frontier.Enqueue((dependency, depth + 1));
            }
        }

        return conflicts;
    }

    private static bool IsCompatible(
        ConsoleContentItem dependency,
        ConsoleShareState? dependencyState,
        ConsoleShareAccessTier targetTier,
        out string blockingReason)
    {
        var effectiveTier = ConsoleShareMapper.ResolveEffectiveTier(dependencyState, dependency);

        switch (targetTier)
        {
            case ConsoleShareAccessTier.PublicIndexed:
                if (dependency.Visibility == ConsoleVisibility.Public)
                {
                    blockingReason = string.Empty;
                    return true;
                }
                blockingReason = $"dependency visibility is '{VisibilityName(dependency.Visibility)}'; public-indexed sharing requires public dependencies";
                return false;

            case ConsoleShareAccessTier.PublicLink:
                if (dependency.Visibility == ConsoleVisibility.Public
                    || effectiveTier is ConsoleShareAccessTier.PublicLink or ConsoleShareAccessTier.PublicIndexed)
                {
                    blockingReason = string.Empty;
                    return true;
                }
                blockingReason = $"dependency visibility is '{VisibilityName(dependency.Visibility)}' and it has no public-link access";
                return false;

            case ConsoleShareAccessTier.Organization:
                if (dependency.Visibility is ConsoleVisibility.Organization or ConsoleVisibility.Public)
                {
                    blockingReason = string.Empty;
                    return true;
                }
                blockingReason = $"dependency visibility is '{VisibilityName(dependency.Visibility)}'; organization sharing requires organization or public dependencies";
                return false;

            default:
                blockingReason = string.Empty;
                return true;
        }
    }

    private static string VisibilityName(ConsoleVisibility visibility) => visibility switch
    {
        ConsoleVisibility.Personal => "personal",
        ConsoleVisibility.Team => "team",
        ConsoleVisibility.Organization => "organization",
        ConsoleVisibility.Public => "public",
        _ => "unknown",
    };
}
