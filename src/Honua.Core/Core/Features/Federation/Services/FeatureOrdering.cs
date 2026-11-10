// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Federation.Services;

public static class FeatureOrdering
{
    public static ImmutableArray<Feature> Apply(
        ImmutableArray<Feature> features,
        ImmutableArray<OrderByClause> orderBy)
        => Apply(features, orderBy, resource: null);

    public static ImmutableArray<Feature> Apply(
        ImmutableArray<Feature> features,
        ImmutableArray<OrderByClause> orderBy,
        MetadataV2Resource? resource)
    {
        if (features.Length <= 1)
        {
            return features;
        }

        return features
            .OrderBy(
                static feature => feature,
                Comparer<Feature>.Create((left, right) => Compare(left, resource, right, resource, orderBy)))
            .ToImmutableArray();
    }

    public static int Compare(
        in Feature left,
        MetadataV2Resource? leftResource,
        in Feature right,
        MetadataV2Resource? rightResource,
        ImmutableArray<OrderByClause> orderBy)
    {
        foreach (var clause in orderBy)
        {
            var comparison = FederationLocalRefinement.CompareValues(
                ResolveValue(left, leftResource, clause.Field),
                ResolveValue(right, rightResource, clause.Field),
                clause.NullOrdering,
                clause.Ascending);
            if (comparison != 0)
            {
                return clause.Ascending ? comparison : -comparison;
            }
        }

        return left.Id.CompareTo(right.Id);
    }

    public static int CompareResolved(
        in Feature left,
        MetadataV2Resource? leftResource,
        ImmutableArray<OrderByClause> leftOrder,
        in Feature right,
        MetadataV2Resource? rightResource,
        ImmutableArray<OrderByClause> rightOrder)
    {
        var count = Math.Min(leftOrder.Length, rightOrder.Length);
        for (var index = 0; index < count; index++)
        {
            var leftClause = leftOrder[index];
            var rightClause = rightOrder[index];
            var comparison = FederationLocalRefinement.CompareValues(
                ResolveValue(left, leftResource, leftClause.Field),
                ResolveValue(right, rightResource, rightClause.Field),
                leftClause.NullOrdering,
                leftClause.Ascending);
            if (comparison != 0)
            {
                return leftClause.Ascending ? comparison : -comparison;
            }
        }

        return left.Id.CompareTo(right.Id);
    }

    private static object? ResolveValue(
        in Feature feature,
        MetadataV2Resource? resource,
        string requestedField)
    {
        var schemaField = resource?.SchemaFields.FirstOrDefault(field =>
            string.Equals(field.Name, requestedField, StringComparison.OrdinalIgnoreCase));
        if (schemaField is not null)
        {
            var primaryIdField = resource!.FindPrimaryIdField();
            if (primaryIdField is not null &&
                string.Equals(primaryIdField.Name, schemaField.Name, StringComparison.OrdinalIgnoreCase))
            {
                return feature.Id;
            }

            if (feature.Attributes.TryGetValue(schemaField.Name, out var schemaValue))
            {
                return schemaValue;
            }
        }

        if (feature.Attributes.TryGetValue(requestedField, out var value))
        {
            return value;
        }

        foreach (var attribute in (feature.Attributes).Where(attribute => string.Equals(attribute.Key, requestedField, StringComparison.OrdinalIgnoreCase)))
        {
            return attribute.Value;
        }

        return null;
    }
}
