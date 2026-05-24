// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console;

/// <summary>
/// Helpers that project request DTOs into <see cref="ConsoleContentItem"/>
/// instances and stamp computed actions onto outgoing responses.
/// </summary>
internal static class ConsoleContentMapper
{
    public static ConsoleContentItem FromCreateRequest(CreateConsoleContentItemRequest request, ClaimsPrincipal principal)
    {
        var owner = request.OwnerId ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return new ConsoleContentItem
        {
            Id = string.Empty,
            Name = request.Name,
            Namespace = request.Namespace,
            Title = request.Title,
            Description = request.Description,
            ItemType = request.ItemType,
            Tags = request.Tags ?? Array.Empty<string>(),
            Labels = request.Labels ?? new Dictionary<string, string>(),
            Lifecycle = request.Lifecycle ?? Core.Features.Metadata.Domain.V2.MetadataV2LifecycleStatus.Draft,
            OperationalState = request.OperationalState ?? Core.Features.Metadata.Domain.V2.MetadataV2OperationalState.Unknown,
            Visibility = request.Visibility ?? ConsoleVisibility.Personal,
            OwnerId = owner,
            TeamScopeId = request.TeamScopeId,
            CreatedById = owner,
            UpdatedById = owner,
            Provenance = request.Provenance ?? Array.Empty<ConsoleProvenanceRef>(),
            TypeMetadata = request.TypeMetadata,
        };
    }

    public static ConsoleContentItem FromUpdateRequest(string id, UpdateConsoleContentItemRequest request, ConsoleContentItem existing, ClaimsPrincipal principal)
    {
        // PUT is a full replacement: nullable fields absent from the request clear
        // the stored value, value-type fields fall back to the same defaults the
        // create path applies, and collection fields default to empty. Merge
        // semantics live exclusively on PATCH.
        var updater = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return existing with
        {
            Id = id,
            Name = request.Name,
            Namespace = request.Namespace,
            Title = request.Title,
            Description = request.Description,
            ItemType = request.ItemType,
            Tags = request.Tags ?? Array.Empty<string>(),
            Labels = request.Labels ?? new Dictionary<string, string>(),
            Lifecycle = request.Lifecycle ?? Core.Features.Metadata.Domain.V2.MetadataV2LifecycleStatus.Draft,
            OperationalState = request.OperationalState ?? Core.Features.Metadata.Domain.V2.MetadataV2OperationalState.Unknown,
            Visibility = request.Visibility ?? ConsoleVisibility.Personal,
            OwnerId = request.OwnerId,
            TeamScopeId = request.TeamScopeId,
            UpdatedById = updater,
            Provenance = request.Provenance ?? Array.Empty<ConsoleProvenanceRef>(),
            TypeMetadata = request.TypeMetadata,
            Generation = request.Generation,
        };
    }

    public static ConsoleContentItemPatch FromPatchRequest(PatchConsoleContentItemRequest request, ClaimsPrincipal principal)
    {
        return new ConsoleContentItemPatch
        {
            Title = request.Title,
            Description = request.Description,
            Tags = request.Tags,
            Labels = request.Labels,
            Visibility = request.Visibility,
            UpdatedById = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"),
        };
    }

    public static async Task<ConsoleContentItem> WithComputedActionsAsync(
        ConsoleContentItem item,
        IConsoleActionEvaluator evaluator,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var actions = await evaluator.EvaluateItemActionsAsync(principal, item, Array.Empty<ConsoleContentAction>(), cancellationToken).ConfigureAwait(false);
        return item with { Actions = actions };
    }

    public static async Task<IReadOnlyList<ConsoleContentItem>> WithComputedActionsAsync(
        IReadOnlyList<ConsoleContentItem> items,
        IConsoleActionEvaluator evaluator,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return items;

        var actionMap = await evaluator.EvaluateItemActionsAsync(principal, items, Array.Empty<ConsoleContentAction>(), cancellationToken).ConfigureAwait(false);
        var projected = new List<ConsoleContentItem>(items.Count);
        foreach (var item in items)
        {
            var actions = actionMap.TryGetValue(item.Id, out var resolved) ? resolved : Array.Empty<ConsoleContentAction>();
            projected.Add(item with { Actions = actions });
        }
        return projected;
    }
}
