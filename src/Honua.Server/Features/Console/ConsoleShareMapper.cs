// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console;

/// <summary>
/// Builds the read-only <see cref="ConsoleShareProjection"/> from a content item
/// and its share state, and resolves the effective access tier shared by the
/// projection and the dependency-closure validator.
/// </summary>
internal static class ConsoleShareMapper
{
    /// <summary>
    /// Resolves the effective Share access tier. Explicit share state is
    /// authoritative; when none exists the tier is derived from the item's
    /// membership visibility for backward compatibility (public =&gt;
    /// public-indexed, organization =&gt; organization, otherwise private).
    /// Public-link and embed remain disabled until explicitly configured.
    /// </summary>
    public static ConsoleShareAccessTier ResolveEffectiveTier(ConsoleShareState? state, ConsoleContentItem item)
    {
        if (state is not null)
        {
            return state.AccessTier;
        }

        return item.Visibility switch
        {
            ConsoleVisibility.Public => ConsoleShareAccessTier.PublicIndexed,
            ConsoleVisibility.Organization => ConsoleShareAccessTier.Organization,
            _ => ConsoleShareAccessTier.Private,
        };
    }

    /// <summary>
    /// Projects a content item plus its share state and store-projected public
    /// link tokens into the wire share projection. <paramref name="publicLinkTokens"/>
    /// must already be redacted (no token values) with effective expiry applied.
    /// </summary>
    public static ConsoleShareProjection BuildProjection(
        ConsoleContentItem item,
        ConsoleShareState? state,
        IReadOnlyList<ConsolePublicLinkToken> publicLinkTokens,
        IReadOnlyList<ConsoleContentAction> callerActions)
    {
        var effectiveTier = ResolveEffectiveTier(state, item);
        var publicLinkTierEnabled = effectiveTier is ConsoleShareAccessTier.PublicLink or ConsoleShareAccessTier.PublicIndexed;
        var publicLinkEnabled = false;
        if (publicLinkTierEnabled)
        {
            foreach (var token in publicLinkTokens)
            {
                if (!token.IsExpired)
                {
                    publicLinkEnabled = true;
                    break;
                }
            }
        }

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

    /// <summary>
    /// Logical access endpoints surfaced for an item. The canonical self link is
    /// the authenticated content detail route; protocol distribution links are
    /// owned by the publication workflow tracked separately.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEndpoints(string itemId)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["self"] = $"/api/v1/console/content/{itemId}",
        };

    /// <summary>
    /// Item types that can be distributed as open data. Compositions (saved maps,
    /// dashboards, reports, generated apps) are not open-data datasets.
    /// </summary>
    private static bool IsDistributableType(ConsoleContentItemType itemType) => itemType switch
    {
        ConsoleContentItemType.Layer
            or ConsoleContentItemType.Service
            or ConsoleContentItemType.OpenData => true,
        _ => false,
    };
}
