// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Federation.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

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
        FilterExpression? candidateFilter,
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
        var candidateLimit = checked(itemIds.Length + 1);

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
                Limit = candidateLimit,
                OrderBy = null,
                OutFields = null
            };
            var result = await reader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            if (result.TotalCount > itemIds.Length || result.Features.Length > itemIds.Length)
            {
                throw new InvalidOperationException(
                    $"STAC item id candidate query exceeded the {itemIds.Length}-feature safety limit.");
            }

            foreach (var feature in result.Features)
            {
                candidates.TryAdd(feature.Id, feature);
            }
        }

        var matched = candidates.Values
            .Select(feature => (Feature: feature, ItemId: StacMappingService.ResolveItemId(feature, resource)))
            .Where(candidate => requestedOrder.ContainsKey(candidate.ItemId))
            .ToImmutableArray();

        if (candidateFilter is not null)
        {
            matched = matched
                .Where(candidate => InMemoryFilterEvaluator.Evaluate(
                    candidateFilter,
                    BuildPropertyBag(candidate.Feature, resource)))
                .ToImmutableArray();
        }

        var duplicateItemId = matched
            .GroupBy(static candidate => candidate.ItemId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (duplicateItemId is not null)
        {
            throw new InvalidOperationException(
                $"STAC item id '{duplicateItemId.Key}' resolves to multiple provider features.");
        }

        var features = matched.Select(static candidate => candidate.Feature).ToImmutableArray();
        if (baseQuery.OrderBy is { IsDefaultOrEmpty: false } orderBy)
        {
            return FeatureOrdering.Apply(features, orderBy, resource);
        }

        return matched
            .OrderBy(candidate => requestedOrder[candidate.ItemId])
            .ThenBy(static candidate => candidate.Feature.Id)
            .Select(static candidate => candidate.Feature)
            .ToImmutableArray();
    }

    private static Dictionary<string, JsonElement> BuildPropertyBag(
        Feature feature,
        MetadataV2Resource resource)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in feature.Attributes)
        {
            properties[attribute.Key] = JsonSerializer.SerializeToElement(attribute.Value);
        }

        var primaryIdField = resource.FindPrimaryIdField();
        if (primaryIdField is not null && !properties.ContainsKey(primaryIdField.Name))
        {
            properties[primaryIdField.Name] = JsonSerializer.SerializeToElement(feature.Id);
        }

        return properties;
    }
}
