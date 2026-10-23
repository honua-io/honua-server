// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Resolves a protocol-facing GeoServices object id to the stored feature row.
/// </summary>
internal static class GeoServicesFeatureObjectIdResolver
{
    public static async Task<Feature?> ResolveAsync(
        IFeatureReader featureReader,
        IFilterExpressionService filterExpressionService,
        MetadataV2Resource resource,
        int storageLayerId,
        long objectId,
        CancellationToken cancellationToken)
    {
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource);
        if (objectIdField is null || objectIdField.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return await featureReader.GetAsync(storageLayerId, objectId, cancellationToken).ConfigureAwait(false);
        }

        var expression = new BinaryExpression(
            new PropertyReference(objectIdField.Name),
            BinaryOperator.In,
            new ValueList([new Literal(objectId, LiteralType.Number)]));
        var translation = filterExpressionService.Translate(expression, resource);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid ObjectId field.");
        }

        var result = await featureReader.QueryAsync(
            storageLayerId,
            new FeatureQuery
            {
                SqlFilter = translation.SqlFilter,
                Limit = 2,
            },
            cancellationToken).ConfigureAwait(false);

        // Feature is a value type: SingleOrDefault on Feature would turn an
        // absent row into a non-null default feature at this nullable boundary.
        return result.Items
            .Where(feature => TryReadPublicObjectId(feature, objectIdField.Name, out var resolvedObjectId)
                && resolvedObjectId == objectId)
            .Select(static feature => (Feature?)feature)
            .SingleOrDefault();
    }

    /// <summary>
    /// Maps protocol object ids to storage row ids. The storage objectid fast path is an
    /// identity map. A custom object-id field is resolved with one IN query per batch so
    /// attachment and edit callers do not treat the public id as the storage id.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, long>> ResolveStorageIdsAsync(
        IFeatureReader featureReader,
        IFilterExpressionService filterExpressionService,
        MetadataV2Resource resource,
        int storageLayerId,
        IReadOnlyList<long> objectIds,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<long, long>();
        if (objectIds.Count == 0)
        {
            return resolved;
        }

        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource);
        if (objectIdField is null || objectIdField.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var objectId in objectIds.Distinct())
            {
                resolved[objectId] = objectId;
            }

            return resolved;
        }

        const int batchSize = 500;
        foreach (var batch in objectIds.Distinct().Chunk(batchSize))
        {
            var expression = new BinaryExpression(
                new PropertyReference(objectIdField.Name),
                BinaryOperator.In,
                new ValueList(batch.Select(static objectId => (FilterExpression)new Literal(objectId, LiteralType.Number)).ToArray()));
            var translation = filterExpressionService.Translate(expression, resource);
            if (!translation.IsSuccess)
            {
                throw new ArgumentException(translation.ErrorMessage ?? "Invalid ObjectId field.");
            }

            var pageOffset = 0;
            var query = new FeatureQuery
            {
                SqlFilter = translation.SqlFilter,
                Limit = batch.Length
            };
            while (true)
            {
                var page = await featureReader.QueryAsync(
                    storageLayerId,
                    pageOffset == 0 ? query : query with { Offset = pageOffset },
                    cancellationToken).ConfigureAwait(false);
                foreach (var feature in page.Items)
                {
                    if (TryReadPublicObjectId(feature, objectIdField.Name, out var publicObjectId))
                    {
                        resolved[publicObjectId] = feature.Id;
                    }
                }

                if (!page.HasMoreResults || page.Items.IsDefaultOrEmpty)
                {
                    break;
                }

                var nextOffset = pageOffset + page.Items.Length;
                if (nextOffset <= pageOffset)
                {
                    break;
                }

                pageOffset = nextOffset;
            }
        }

        return resolved;
    }

    private static bool TryReadPublicObjectId(Feature feature, string fieldName, out long objectId)
    {
        if (feature.Attributes.TryGetValue(fieldName, out var value) &&
            FeatureServerValueParser.TryConvertToLong(value, out objectId))
        {
            return true;
        }

        foreach (var pair in feature.Attributes)
        {
            if (string.Equals(pair.Key, fieldName, StringComparison.OrdinalIgnoreCase) &&
                FeatureServerValueParser.TryConvertToLong(pair.Value, out objectId))
            {
                return true;
            }
        }

        objectId = 0;
        return false;
    }
}
