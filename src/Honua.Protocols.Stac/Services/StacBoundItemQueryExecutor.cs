// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac.Models;

namespace Honua.Protocols.Stac.Services;

/// <summary>
/// Executes bounded, provider-neutral candidate queries for storage-bound STAC items.
/// </summary>
internal static class StacBoundItemQueryExecutor
{
    public static async Task<ImmutableArray<Feature>> QueryAsync(
        IFeatureReader reader,
        int layerId,
        MetadataV2Resource resource,
        FeatureQuery baseQuery,
        ImmutableArray<string> itemIds,
        CancellationToken cancellationToken)
    {
        if (baseQuery.SqlFilter is not null)
        {
            throw new NotSupportedException(
                "Combining STAC ids with a provider-specific SQL filter is not supported for storage-bound layers.");
        }

        var requestedOrder = itemIds
            .Select((itemId, index) => (itemId, index))
            .GroupBy(static pair => pair.itemId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().index, StringComparer.Ordinal);
        var candidates = new Dictionary<long, Feature>();

        foreach (var field in StacItemIdWhereBuilder.GetCandidateFields(resource))
        {
            if (!StacItemIdWhereBuilder.TryBuildFieldMatch(field, itemIds, out var where))
            {
                continue;
            }

            var query = baseQuery with
            {
                Where = StacItemIdWhereBuilder.Combine(baseQuery.Where, where),
                Offset = null,
                Limit = StacConstants.MaxSearchLimit
            };
            var result = await reader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            if (result.TotalCount > result.Features.Length)
            {
                throw new InvalidOperationException(
                    $"STAC item id candidate query exceeded the {StacConstants.MaxSearchLimit} feature safety limit.");
            }

            foreach (var feature in result.Features)
            {
                candidates.TryAdd(feature.Id, feature);
            }
        }

        return candidates.Values
            .Select(feature => (Feature: feature, ItemId: StacMappingService.ResolveItemId(feature)))
            .Where(candidate => requestedOrder.ContainsKey(candidate.ItemId))
            .OrderBy(candidate => requestedOrder[candidate.ItemId])
            .ThenBy(static candidate => candidate.Feature.Id)
            .Select(static candidate => candidate.Feature)
            .ToImmutableArray();
    }
}
