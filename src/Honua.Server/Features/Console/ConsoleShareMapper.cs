// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console;

internal static class ConsoleShareMapper
{
    public static ConsoleShareAccessTier ResolveEffectiveTier(ConsoleShareState? state, ConsoleContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var tier = state is not null
            ? state.AccessTier
            : item.Visibility switch
            {
                ConsoleVisibility.Public => ConsoleShareAccessTier.PublicIndexed,
                ConsoleVisibility.Organization => ConsoleShareAccessTier.Organization,
                _ => ConsoleShareAccessTier.Private,
            };
        return CapToVisibility(tier, item.Visibility);
    }

    public static ConsoleShareAccessTier CapToVisibility(ConsoleShareAccessTier tier, ConsoleVisibility visibility)
    {
        if (visibility == ConsoleVisibility.Public || tier != ConsoleShareAccessTier.PublicIndexed)
        {
            return tier;
        }

        return visibility == ConsoleVisibility.Organization
            ? ConsoleShareAccessTier.Organization
            : ConsoleShareAccessTier.Private;
    }

    // The caller must already pass redacted tokens with effective expiry applied.
    public static ConsoleShareProjection BuildProjection(
        ConsoleContentItem item,
        ConsoleShareState? state,
        IReadOnlyList<ConsolePublicLinkToken> publicLinkTokens,
        IReadOnlyList<ConsoleContentAction> callerActions)
    {
        var effectiveTier = ResolveEffectiveTier(state, item);
        var publicLinkTierEnabled = effectiveTier is ConsoleShareAccessTier.PublicLink or ConsoleShareAccessTier.PublicIndexed;
        var publicLinkEnabled = publicLinkTierEnabled && publicLinkTokens.Any(token => !token.IsExpired);

        var embedEnabled = state?.EmbedEnabled ?? false;
        var openDataEligible = IsDistributableType(item.ItemType)
            && effectiveTier == ConsoleShareAccessTier.PublicIndexed;
        var anonymousEligible = effectiveTier == ConsoleShareAccessTier.PublicIndexed
            || publicLinkEnabled
            || embedEnabled;

        return new ConsoleShareProjection
        {
            ItemId = item.Id,
            ItemName = item.Name,
            ItemTitle = item.Title,
            ItemType = item.ItemType,
            OwnerId = item.OwnerId,
            AccessTier = effectiveTier,
            PublicLinkEnabled = publicLinkEnabled,
            PublicLinkTokens = publicLinkTokens,
            EmbedEnabled = embedEnabled,
            EmbedAudience = embedEnabled ? state?.EmbedAudience : null,
            OpenDataEligible = openDataEligible,
            CallerPermissions = callerActions,
            AnonymousEligible = anonymousEligible,
            Endpoints = BuildEndpoints(item.Id),
            UpdatedAt = state?.UpdatedAt,
            UpdatedById = state?.UpdatedById,
        };
    }

    public static IReadOnlyDictionary<string, string> BuildEndpoints(string itemId)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["self"] = $"/api/v1/console/content/{itemId}",
        };

    private static bool IsDistributableType(ConsoleContentItemType itemType) => itemType switch
    {
        ConsoleContentItemType.Layer
            or ConsoleContentItemType.Service
            or ConsoleContentItemType.OpenData => true,
        _ => false,
    };
}
